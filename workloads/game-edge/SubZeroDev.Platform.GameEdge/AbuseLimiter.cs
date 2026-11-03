using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using SubZeroDev.Platform.Abstractions;

namespace SubZeroDev.Platform.GameEdge;

/// <summary>Counts requests per caller in a fixed window and refuses the ones over the permit count
/// (invariant 121). Per instance and in memory: a restart resets every count, and a second edge
/// instance counts alone. A caller's window opens at its first request after the previous one closed;
/// a refused request is not counted, so the delay it is told is the window's remaining time.</summary>
internal sealed class AbuseLimiter
{
    private readonly int permitCount;
    private readonly long windowTicks;
    private readonly TimeProvider time;
    private readonly ConcurrentDictionary<string, Counter> counters = new(StringComparer.Ordinal);
    private long nextSweep;

    public AbuseLimiter(GameEdgeOptions options, TimeProvider time)
    {
        permitCount = options.AbuseLimits.PermitCount;
        this.time = time;
        windowTicks = (long)(options.AbuseLimits.Window.TotalSeconds * time.TimestampFrequency);
        nextSweep = time.GetTimestamp() + windowTicks;
    }

    /// <summary>Counts a request against <paramref name="key"/>.</summary>
    /// <param name="key">The caller: a principal or a client address, from <see cref="Key"/>.</param>
    /// <returns><see langword="null"/> when the request is admitted, otherwise how long the caller must
    /// wait for its window to close, at least one second and in whole seconds.</returns>
    public TimeSpan? TryAdmit(string key)
    {
        var now = time.GetTimestamp();
        Sweep(now);

        while (true)
        {
            var counter = counters.GetOrAdd(key, static _ => new Counter());
            lock (counter)
            {
                // The sweep took this counter out while we held it: take a fresh one.
                if (counter.Removed)
                {
                    continue;
                }

                if (counter.Count == 0 || now - counter.Start >= windowTicks)
                {
                    counter.Start = now;
                    counter.Count = 0;
                }

                if (counter.Count < permitCount)
                {
                    counter.Count++;
                    return null;
                }

                var remaining = counter.Start + windowTicks - now;
                var seconds = Math.Ceiling((double)remaining / time.TimestampFrequency);
                return TimeSpan.FromSeconds(Math.Max(1, seconds));
            }
        }
    }

    /// <summary>The key a request is counted under: its principal when the bearer chain established
    /// one, its client address otherwise.</summary>
    /// <param name="principal">The principal the request carries, or <see langword="null"/> when no
    /// account was established.</param>
    /// <param name="clientAddress">The address <see cref="HeaderPolicy.ClientAddress"/> found.</param>
    public static string Key(PrincipalId? principal, string clientAddress) =>
        principal is { } id
            ? $"principal\n{id.Issuer}\n{id.Subject}"
            : $"address\n{clientAddress}";

    // Without this the table would hold a key for every address that ever called. Run at most once per
    // window, by whichever request notices first; a counter whose window has closed holds nothing a
    // later request needs.
    private void Sweep(long now)
    {
        var due = Volatile.Read(ref nextSweep);
        if (now < due || Interlocked.CompareExchange(ref nextSweep, now + windowTicks, due) != due)
        {
            return;
        }

        foreach (var (key, counter) in counters)
        {
            lock (counter)
            {
                if (now - counter.Start >= windowTicks)
                {
                    counter.Removed = true;
                    counters.TryRemove(new KeyValuePair<string, Counter>(key, counter));
                }
            }
        }
    }

    private sealed class Counter
    {
        public long Start;
        public int Count;
        public bool Removed;
    }
}

/// <summary>The startup check on <c>GameEdge:AbuseLimits</c>. Absent, the default of 60 requests in 60
/// seconds applies; present, both fields are required, because the binder would otherwise fail on a
/// constructor parameter instead of naming the setting.</summary>
internal static class AbuseLimitSettings
{
    private const string Key = "GameEdge:AbuseLimits";

    /// <summary>Every breach, each naming its full configuration key. Empty when the section is absent or
    /// valid.</summary>
    public static IReadOnlyList<string> Validate(IConfigurationSection edge)
    {
        var limits = edge.GetSection("AbuseLimits");
        var errors = new List<string>();
        if (!limits.Exists())
        {
            return errors;
        }

        var permitKey = $"{Key}:{nameof(AbuseLimitOptions.PermitCount)}";
        if (!int.TryParse(limits[nameof(AbuseLimitOptions.PermitCount)], NumberStyles.None, CultureInfo.InvariantCulture, out var permits)
            || permits < 1)
        {
            errors.Add($"'{permitKey}' is required and must be a whole number of at least one.");
        }

        // Whole seconds on the wire, since Retry-After has no fractions.
        var windowKey = $"{Key}:{nameof(AbuseLimitOptions.Window)}";
        if (!TimeSpan.TryParse(limits[nameof(AbuseLimitOptions.Window)], CultureInfo.InvariantCulture, out var window))
        {
            errors.Add($"'{windowKey}' is required and must be a time span.");
        }
        else if (window < TimeSpan.FromSeconds(1))
        {
            errors.Add($"'{windowKey}' must be at least one second.");
        }

        return errors;
    }
}
