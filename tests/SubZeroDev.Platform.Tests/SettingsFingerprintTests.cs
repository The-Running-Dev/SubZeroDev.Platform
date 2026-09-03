using System.Reflection;
using System.Text;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;

namespace SubZeroDev.Platform.Tests;

/// <summary>Unresolved #1, settled ahead of S3: the settings fingerprint's canonical form and hash.
/// Specified to the byte in <c>design/d3/20-contract.md</c>, so it is tested to the byte here, not
/// just for "changes" versus "does not change".</summary>
public sealed class SettingsFingerprintTests
{
    [Fact]
    public void Two_independently_constructed_instances_fingerprint_identical_options_identically()
    {
        // The practical equivalent of "two separate processes" — two fresh implementation
        // instances, over two independently constructed but equal option trees, sharing no state.
        var first = ((ISettingsFingerprint)new SettingsFingerprint()).Compute(Baseline(), []);
        var second = ((ISettingsFingerprint)new SettingsFingerprint()).Compute(Baseline(), []);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Matches_the_byte_exact_specification_against_a_hand_computed_vector()
    {
        // Computed independently against System.Security.Cryptography.SHA256 over the exact byte
        // layout design/20-contract.md specifies: "szdfp3", then each of the ten
        // currently-[Fingerprinted] entries (D5-S1 adds CompositionProfile), path-sorted
        // ordinally, length-prefixed. D5-S8 bumped the version when the entitlement contributor set
        // joined the input; with no contributor registered it contributes no entry, so this vector
        // differs from the szdfp2 one in the six version bytes and nothing else.
        var fingerprint = ((ISettingsFingerprint)new SettingsFingerprint()).Compute(Baseline(), []);

        Assert.Equal("82d01318ad2ded8fb954090f8876bc8a99c221b6928b8d565af403da03425dbe", fingerprint);
    }

    [Fact]
    public void Two_hosts_differing_only_in_composition_profile_fingerprint_differently()
    {
        var operated = Baseline() with { CompositionProfile = CompositionProfile.Operated };
        var local = Baseline() with { CompositionProfile = CompositionProfile.Local };
        var fingerprint = new SettingsFingerprint();

        Assert.NotEqual(fingerprint.Compute(operated, []), fingerprint.Compute(local, []));
    }

    /// <summary>S8.8 — two hosts differing only in their frozen entitlement contributor set compute
    /// different fingerprints. The contributor set is what bounds the union's risk of one wrong
    /// contributor granting everything, so two instances that disagree about who may grant must be
    /// visible through <c>platform.settings-fingerprint</c> rather than by behaving differently in
    /// silence.</summary>
    [Fact]
    public void Two_hosts_differing_only_in_their_contributor_set_fingerprint_differently()
    {
        ISettingsFingerprint fingerprint = new SettingsFingerprint();
        var baseline = new EntitlementContributorName("Platform.Entitlement.CommunityBaseline");
        var licensing = new EntitlementContributorName("Platform.Licensing");

        var withBaselineOnly = fingerprint.Compute(Baseline(), [baseline]);
        var withLicensing = fingerprint.Compute(Baseline(), [baseline, licensing]);
        var withNone = fingerprint.Compute(Baseline(), []);

        Assert.NotEqual(withBaselineOnly, withLicensing);
        Assert.NotEqual(withBaselineOnly, withNone);
        Assert.NotEqual(withLicensing, withNone);
    }

    /// <summary>S8.8, the other half of "differing only in" — registration <em>order</em> is not a
    /// disagreement. The evaluator takes a union, which is order-independent, so two hosts that
    /// registered the same contributors in different orders agree about who may grant and must not
    /// be reported as disagreeing. A permanent false mismatch trains an operator to ignore the one
    /// check that would have caught a real one.</summary>
    [Fact]
    public void The_contributor_set_is_a_set__registration_order_is_not_a_disagreement()
    {
        ISettingsFingerprint fingerprint = new SettingsFingerprint();
        var first = new EntitlementContributorName("Platform.Billing");
        var second = new EntitlementContributorName("Platform.Licensing");

        Assert.Equal(
            fingerprint.Compute(Baseline(), [first, second]),
            fingerprint.Compute(Baseline(), [second, first]));
    }

    /// <summary>S8.8 — the format version changes in the same commit the encoding does. An encoding
    /// change that is not versioned is the silent break the field exists to prevent, so this asserts
    /// the version is inside the hashed input rather than beside it.</summary>
    [Fact]
    public void The_format_version_is_inside_the_hashed_input_and_is_the_D5_S8_one()
    {
        var version = typeof(SettingsFingerprint)
            .GetField("FormatVersion", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null);

        Assert.Equal("szdfp3", Encoding.UTF8.GetString((byte[])version!));
    }

    [Fact]
    public void Every_Fingerprinted_property_changes_the_hash_and_every_other_property_does_not()
    {
        ISettingsFingerprint fingerprint = new SettingsFingerprint();
        var baselineHash = fingerprint.Compute(Baseline(), []);

        var leaves = Leaves().ToList();
        Assert.NotEmpty(leaves);

        foreach (var (path, property, owner) in leaves)
        {
            var options = Baseline();
            var target = owner(options);
            var original = property.GetValue(target);
            property.SetValue(target, Perturb(original, property.PropertyType));

            var perturbedHash = fingerprint.Compute(options, []);
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

    private static object Perturb(object? value, Type type)
    {
        // Uri is nullable and reference-typed, so it needs the leaf's declared type rather than the
        // (possibly null) value to pick a perturbation.
        if (type == typeof(Uri))
        {
            return value is Uri current ? new Uri(current, "changed") : new Uri("https://changed.example/");
        }

        return value switch
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
    }

    private static object NextEnumValue(Enum value) =>
        Enum.GetValues(value.GetType())
            .Cast<Enum>()
            .FirstOrDefault(candidate => !candidate.Equals(value))
        ?? throw new NotSupportedException($"Enum '{value.GetType()}' has only one value.");

    private static PlatformOptions Baseline() => new()
    {
        ServiceName = "settings-fingerprint-tests",
        ServiceVersion = "1.0.0",
        CompositionProfile = CompositionProfile.Operated,
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
