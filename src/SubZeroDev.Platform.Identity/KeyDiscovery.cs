using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using SubZeroDev.Platform.Abstractions;

namespace SubZeroDev.Platform.Identity;

/// <summary>Where a discovery fetch gets its connection from. A seam so a test observes every
/// outbound request; the default is a plain handler that follows no redirect.</summary>
internal sealed class DiscoveryTransport(Func<HttpMessageHandler> create)
{
    internal static DiscoveryTransport Default { get; } =
        new(() => new SocketsHttpHandler { AllowAutoRedirect = false });

    internal HttpMessageHandler Create() => create();
}

/// <summary>One discovery provider's cached key set. Replaced whole, never edited, so a reader sees
/// either the previous set or the next and never a mixture (I-I9).</summary>
internal sealed class KeySetCache
{
    private volatile JsonWebKeySet? _current;
    private volatile bool _lastFetchFailed;

    internal JsonWebKeySet? Current => _current;

    internal bool LastFetchFailed => _lastFetchFailed;

    internal void Replace(JsonWebKeySet keys)
    {
        _current = keys;
        _lastFetchFailed = false;
    }

    internal void RecordFailure() => _lastFetchFailed = true;
}

/// <summary>Fetches one issuer's key set through its discovery document. Only ever called from
/// startup and from the refresh work item, never while a request is authenticated (I-I8).</summary>
internal static class KeyFetcher
{
    /// <summary>The bound on one fetch, discovery document and key set together. Fixed, not a setting.</summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private const int MaximumResponseBytes = 1024 * 1024;

    /// <summary>Fetches and, on success, replaces the cached set. Any failure keeps the previous set
    /// and is recorded; only cancellation of <paramref name="cancellationToken"/> escapes.</summary>
    internal static async Task RefreshAsync(
        BearerSettings settings,
        KeySetCache cache,
        DiscoveryTransport transport,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bound.CancelAfter(timeout ?? Timeout);

        try
        {
            using var client = new HttpClient(transport.Create(), disposeHandler: true)
            {
                Timeout = System.Threading.Timeout.InfiniteTimeSpan,
                MaxResponseContentBufferSize = MaximumResponseBytes,
            };

            var document = await client.GetStringAsync(settings.Discovery, bound.Token).ConfigureAwait(false);
            using var parsed = JsonDocument.Parse(document);
            var root = parsed.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("issuer", out var issuer)
                || issuer.ValueKind != JsonValueKind.String
                || !string.Equals(issuer.GetString(), settings.Issuer, StringComparison.Ordinal)
                || !root.TryGetProperty("jwks_uri", out var jwksUri)
                || jwksUri.ValueKind != JsonValueKind.String
                || !Uri.TryCreate(jwksUri.GetString(), UriKind.Absolute, out var jwks)
                || jwks.Scheme != Uri.UriSchemeHttps)
            {
                cache.RecordFailure();
                return;
            }

            var keys = BearerSettings.ParseKeySet(
                await client.GetStringAsync(jwks, bound.Token).ConfigureAwait(false), out _);
            if (keys is null)
            {
                cache.RecordFailure();
                return;
            }

            cache.Replace(keys);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            cache.RecordFailure();
        }
    }
}

/// <summary>The recurring refresh of one discovery provider's key set. Takes no lease and writes
/// nothing durable: every instance holds its own cache (I-I9).</summary>
internal sealed class KeyRefreshWork(BearerSettings settings, KeySetCache cache, DiscoveryTransport transport)
    : IBackgroundWork
{
    public BackgroundWorkName Name { get; } = new($"platform.identity.key-refresh:{settings.Name}");

    public HostRoles Roles => HostRoles.Both;

    public TimeSpan Interval => settings.KeyRefreshInterval;

    public bool RequiresLease => false;

    public Task TickAsync(CancellationToken cancellationToken) =>
        KeyFetcher.RefreshAsync(settings, cache, transport, cancellationToken);
}

/// <summary>Readiness of one provider's key set: Unhealthy with none, Degraded when the last fetch
/// failed, Healthy otherwise. A fixed-key provider has no cache and is always healthy.</summary>
internal sealed class KeySetHealthCheck(string provider, KeySetCache? cache) : IHealthCheck
{
    public HealthCheckName Name { get; } = new($"platform.identity.key-set:{provider}");

    public HealthCheckKind Kind => HealthCheckKind.Readiness;

    public HealthCheckCriticality Criticality => HealthCheckCriticality.Required;

    public TimeSpan Timeout => TimeSpan.FromSeconds(5);

    public bool TouchesExternalDependency => false;

    public Task<HealthCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        var (status, detail) = cache switch
        {
            null => (HealthStatus.Healthy, (string?)null),
            { Current: null } => (HealthStatus.Unhealthy, "no key set has been fetched"),
            { LastFetchFailed: true } => (HealthStatus.Degraded, "the last key fetch failed; the previous set is in use"),
            _ => (HealthStatus.Healthy, null),
        };

        return Task.FromResult(new HealthCheckResult(status, detail, new Dictionary<string, string>()));
    }
}

/// <summary>Startup step 8's key fetch: every discovery provider fetches once, in parallel, bounded
/// by <see cref="KeyFetcher.Timeout"/>, and a failure never fails startup (I-I10).</summary>
internal sealed class KeyDiscoveryStartup(
    IReadOnlyList<(BearerSettings Settings, KeySetCache Cache)> sources,
    DiscoveryTransport transport) : IHostedLifecycleService
{
    public Task StartingAsync(CancellationToken cancellationToken) =>
        Task.WhenAll(sources.Select(source =>
            KeyFetcher.RefreshAsync(source.Settings, source.Cache, transport, cancellationToken)));

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
