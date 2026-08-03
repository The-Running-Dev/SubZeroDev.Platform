using System.Reflection;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;

namespace SubZeroDev.Platform.Tests;

/// <summary>Unresolved #1, settled ahead of S3: the settings fingerprint's canonical form and hash.
/// Specified to the byte in <c>design/20-contract.md</c>, so it is tested to the byte here, not
/// just for "changes" versus "does not change".</summary>
public sealed class SettingsFingerprintTests
{
    [Fact]
    public void Two_independently_constructed_instances_fingerprint_identical_options_identically()
    {
        // The practical equivalent of "two separate processes" — two fresh implementation
        // instances, over two independently constructed but equal option trees, sharing no state.
        var first = ((ISettingsFingerprint)new SettingsFingerprint()).Compute(Baseline());
        var second = ((ISettingsFingerprint)new SettingsFingerprint()).Compute(Baseline());

        Assert.Equal(first, second);
    }

    [Fact]
    public void Matches_the_byte_exact_specification_against_a_hand_computed_vector()
    {
        // Computed independently in PowerShell against System.Security.Cryptography.SHA256 over
        // the exact byte layout design/20-contract.md specifies: "szdfp1", then each of the nine
        // currently-[Fingerprinted] entries, path-sorted ordinally, length-prefixed.
        var fingerprint = ((ISettingsFingerprint)new SettingsFingerprint()).Compute(Baseline());

        Assert.Equal("1934034ec1574bdaa67759cac02935ce61104d952e7e0b25fe36ed6ed89fb15a", fingerprint);
    }

    [Fact]
    public void Every_Fingerprinted_property_changes_the_hash_and_every_other_property_does_not()
    {
        ISettingsFingerprint fingerprint = new SettingsFingerprint();
        var baselineHash = fingerprint.Compute(Baseline());

        var leaves = Leaves().ToList();
        Assert.NotEmpty(leaves);

        foreach (var (path, property, owner) in leaves)
        {
            var options = Baseline();
            var target = owner(options);
            var original = property.GetValue(target);
            property.SetValue(target, Perturb(original));

            var perturbedHash = fingerprint.Compute(options);
            var isFingerprinted = property.GetCustomAttribute<FingerprintedAttribute>() is not null;
            var changed = perturbedHash != baselineHash;

            Assert.True(
                changed == isFingerprinted,
                isFingerprinted
                    ? $"'{path}' is [Fingerprinted] but changing it left the fingerprint unchanged."
                    : $"'{path}' is not [Fingerprinted] but changing it changed the fingerprint.");
        }
    }

    /// <summary>Every property reachable from <see cref="PlatformOptions"/>, one level of recursion
    /// into a nested settings record — the same shape <see cref="ISettingsFingerprint"/> itself
    /// walks, discovered independently here via reflection rather than hand-listed, so this test
    /// does not silently stop covering a property a future settings record adds.</summary>
    private static IEnumerable<(string Path, PropertyInfo Property, Func<PlatformOptions, object> Owner)> Leaves()
    {
        foreach (var top in typeof(PlatformOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!top.CanWrite)
            {
                continue;
            }

            if (top.PropertyType.IsClass && top.PropertyType != typeof(string))
            {
                foreach (var leaf in top.PropertyType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!leaf.CanWrite)
                    {
                        continue;
                    }

                    var section = top;
                    yield return ($"{top.Name}:{leaf.Name}", leaf, options => section.GetValue(options)!);
                }

                continue;
            }

            yield return (top.Name, top, options => options);
        }
    }

    private static object Perturb(object? value) => value switch
    {
        TimeSpan span => span + TimeSpan.FromTicks(1),
        double number => number + 1,
        long number => number + 1,
        int number => number + 1,
        bool flag => !flag,
        string text => text + "-changed",
        Enum named => NextEnumValue(named),
        _ => throw new NotSupportedException($"No perturbation known for '{value?.GetType()}'."),
    };

    private static object NextEnumValue(Enum value) =>
        Enum.GetValues(value.GetType())
            .Cast<Enum>()
            .FirstOrDefault(candidate => !candidate.Equals(value))
        ?? throw new NotSupportedException($"Enum '{value.GetType()}' has only one value.");

    private static PlatformOptions Baseline() => new()
    {
        ServiceName = "settings-fingerprint-tests",
        ServiceVersion = "1.0.0",
        Persistence = new PersistenceOptions
        {
            Provider = PersistenceProvider.Sqlite,
            ConnectionString = "Data Source=:memory:",
        },
        Outbox = new OutboxOptions
        {
            ProcessedRetention = TimeSpan.FromDays(1),
            PoisonedRetention = TimeSpan.FromDays(7),
            ClaimWindow = TimeSpan.FromMinutes(5),
            PoisonAttemptCount = 12,
            RetryBackoffBase = TimeSpan.FromSeconds(30),
            RetryBackoffFactor = 2,
            RetryBackoffCap = TimeSpan.FromHours(6),
            DeferralAge = TimeSpan.FromHours(24),
        },
        Lease = new LeaseOptions { Duration = TimeSpan.FromMinutes(5) },
    };
}
