using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

namespace Vendor.ConfigurationFixture;

/// <summary>A stand-in for a vendor's configuration package: one extension method per protocol surface
/// the vendor offers, each writing keys under <c>Platform:Identity:Bearer:&lt;name&gt;</c>. It is a
/// configuration source and nothing more, so it references no Platform package.</summary>
public static class VendorConfigurationExtensions
{
    private const string Section = "Platform:Identity:Bearer";

    /// <summary>The vendor's issuer, trusted with keys the operator already holds.</summary>
    public static IConfigurationBuilder AddFixtureFixedKeyIssuer(
        this IConfigurationBuilder builder, string name, string issuer, string signingKeys, string audience) =>
        builder.AddFixtureSource(new Dictionary<string, string?>
        {
            [$"{Section}:{name}:Issuer"] = issuer,
            [$"{Section}:{name}:SigningKeys"] = signingKeys,
            [$"{Section}:{name}:Audiences:0"] = audience,
        });

    /// <summary>The vendor's issuer, whose keys are found through its discovery document.</summary>
    public static IConfigurationBuilder AddFixtureDiscoveredIssuer(
        this IConfigurationBuilder builder, string name, string issuer, string discovery, string audience) =>
        builder.AddFixtureSource(new Dictionary<string, string?>
        {
            [$"{Section}:{name}:Issuer"] = issuer,
            [$"{Section}:{name}:Discovery"] = discovery,
            [$"{Section}:{name}:Audiences:0"] = audience,
        });

    /// <summary>The vendor's multi-tenant issuer, one entry for every customer tenant.</summary>
    public static IConfigurationBuilder AddFixtureTenantedIssuer(
        this IConfigurationBuilder builder, string name, string issuerPattern, string signingKeys, string audience) =>
        builder.AddFixtureSource(new Dictionary<string, string?>
        {
            [$"{Section}:{name}:IssuerPattern"] = issuerPattern,
            [$"{Section}:{name}:SigningKeys"] = signingKeys,
            [$"{Section}:{name}:Audiences:0"] = audience,
        });

    /// <summary>A deliberately defective method: it writes a key the generic path does not name.</summary>
    public static IConfigurationBuilder AddFixtureIssuerWithQuirk(
        this IConfigurationBuilder builder, string name, string issuer, string signingKeys, string audience) =>
        builder.AddFixtureSource(new Dictionary<string, string?>
        {
            [$"{Section}:{name}:Issuer"] = issuer,
            [$"{Section}:{name}:SigningKeys"] = signingKeys,
            [$"{Section}:{name}:Audiences:0"] = audience,
            [$"{Section}:{name}:VendorQuirk"] = "on",
        });

    private static IConfigurationBuilder AddFixtureSource(
        this IConfigurationBuilder builder, IReadOnlyDictionary<string, string?> values) =>
        builder.Add(new FixtureSource(values));

    private sealed class NeverChanges : IChangeToken
    {
        internal static readonly NeverChanges Instance = new();

        public bool HasChanged => false;

        public bool ActiveChangeCallbacks => false;

        public IDisposable RegisterChangeCallback(Action<object?> callback, object? state) => new Noop();

        private sealed class Noop : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    private sealed class FixtureSource(IReadOnlyDictionary<string, string?> values) : IConfigurationSource
    {
        public IConfigurationProvider Build(IConfigurationBuilder builder) => new FixtureProvider(values);
    }

    private sealed class FixtureProvider(IReadOnlyDictionary<string, string?> values) : IConfigurationProvider
    {
        private readonly Dictionary<string, string?> _data =
            new(values, StringComparer.OrdinalIgnoreCase);

        public bool TryGet(string key, out string? value) => _data.TryGetValue(key, out value);

        public void Set(string key, string? value) => _data[key] = value;

        public void Load()
        {
        }

        public IChangeToken GetReloadToken() => NeverChanges.Instance;

        public IEnumerable<string> GetChildKeys(IEnumerable<string> earlierKeys, string? parentPath)
        {
            var prefix = parentPath is null ? string.Empty : parentPath + ConfigurationPath.KeyDelimiter;
            var children = new List<string>();
            foreach (var key in _data.Keys)
            {
                if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var rest = key[prefix.Length..];
                    var end = rest.IndexOf(ConfigurationPath.KeyDelimiter, StringComparison.Ordinal);
                    children.Add(end < 0 ? rest : rest[..end]);
                }
            }

            children.AddRange(earlierKeys);
            children.Sort(StringComparer.OrdinalIgnoreCase);
            return children;
        }
    }
}
