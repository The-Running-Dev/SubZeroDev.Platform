using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;

namespace SubZeroDev.Platform.Identity;

/// <summary>One generic-path provider's settings, after validation. Internal on purpose: the
/// schema is the configuration keys, and no public type may carry it (<c>20-contract.md</c> § 12).</summary>
internal sealed record BearerSettings(
    string Name,
    string? Issuer,
    JsonWebKeySet? SigningKeys,
    IReadOnlyList<string> Audiences,
    IReadOnlyList<string> Algorithms,
    TimeSpan ClockTolerance,
    string SubjectClaim,
    string? DisplayNameClaim,
    Uri? Discovery = null,
    TimeSpan KeyRefreshInterval = default,
    string? IssuerPattern = null)
{
    internal const string SectionPath = "Platform:Identity:Bearer";

    /// <summary>The placeholder an <c>IssuerPattern</c> carries exactly once. It stands for one or
    /// more characters, none of them <c>/</c>.</summary>
    internal const string TenantPlaceholder = "{tenantid}";

    private const string Source = "the Platform:Identity:Bearer section of configuration";
    private static readonly TimeSpan MaximumTolerance = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MinimumRefresh = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaximumRefresh = TimeSpan.FromDays(1);
    private static readonly TimeSpan DefaultRefresh = TimeSpan.FromMinutes(5);

    private static readonly string[] PermittedAlgorithms =
    [
        "RS256", "RS384", "RS512", "PS256", "PS384", "PS512", "ES256", "ES384", "ES512",
    ];

    // An unrecognised key fails startup rather than being ignored.
    private static readonly HashSet<string> Recognised = new(StringComparer.OrdinalIgnoreCase)
    {
        "Issuer", "IssuerPattern", "SigningKeys", "Audiences", "Algorithms", "ClockTolerance", "SubjectClaim", "DisplayNameClaim",
        "Discovery", "KeyRefreshInterval",
    };

    /// <summary>Reads and validates every child section of <c>Platform:Identity:Bearer</c>, before
    /// anything is registered or any key is used.</summary>
    internal static Result<IReadOnlyList<BearerSettings>, ConfigurationError> ReadAll(IConfiguration configuration)
    {
        var providers = new List<BearerSettings>();

        foreach (var section in configuration.GetSection(SectionPath).GetChildren())
        {
            var read = Read(section);
            if (!read.IsSuccess)
            {
                return Result<IReadOnlyList<BearerSettings>, ConfigurationError>.Failure(read.Error);
            }

            providers.Add(read.Value);
        }

        return Result<IReadOnlyList<BearerSettings>, ConfigurationError>.Success(providers);
    }

    private static Result<BearerSettings, ConfigurationError> Read(IConfigurationSection section)
    {
        var name = section.Key;
        string Key(string setting) => $"{SectionPath}:{name}:{setting}";
        static Result<BearerSettings, ConfigurationError> Fail(ConfigurationError error) =>
            Result<BearerSettings, ConfigurationError>.Failure(error);

        if (section.Value is not null)
        {
            return Fail(ConfigurationError.InvalidSetting(
                $"{SectionPath}:{name}", "it must be a section holding the provider's settings, not a value"));
        }

        foreach (var child in section.GetChildren())
        {
            if (!Recognised.Contains(child.Key))
            {
                return Fail(ConfigurationError.InvalidSetting(
                    Key(child.Key), "it is not a setting the generic bearer path recognises"));
            }
        }

        var issuer = Scalar(section, "Issuer", Key("Issuer"), out var issuerError);
        if (issuerError is not null)
        {
            return Fail(issuerError);
        }

        var issuerPattern = Scalar(section, "IssuerPattern", Key("IssuerPattern"), out var patternError);
        if (patternError is not null)
        {
            return Fail(patternError);
        }

        if (issuer is not null && issuerPattern is not null)
        {
            return Fail(ConfigurationError.InconsistentSettings(
                Key("Issuer"), Key("IssuerPattern"), "a provider trusts one issuer or a pattern of issuers, not both"));
        }

        if (issuer is null && issuerPattern is null)
        {
            return Fail(ConfigurationError.MissingRequiredSetting(
                $"{Key("Issuer")} or {Key("IssuerPattern")}", Source));
        }

        if (issuer is not null && !Uri.TryCreate(issuer, UriKind.Absolute, out _))
        {
            return Fail(ConfigurationError.InvalidSetting(Key("Issuer"), "it must be an absolute URI"));
        }

        if (issuerPattern is not null && CountOccurrences(issuerPattern, TenantPlaceholder) != 1)
        {
            return Fail(ConfigurationError.InvalidSetting(
                Key("IssuerPattern"), $"it must contain {TenantPlaceholder} exactly once"));
        }

        var signingKeysJson = Scalar(section, "SigningKeys", Key("SigningKeys"), out var keysError);
        if (keysError is not null)
        {
            return Fail(keysError);
        }

        var discoveryText = Scalar(section, "Discovery", Key("Discovery"), out var discoveryError);
        if (discoveryError is not null)
        {
            return Fail(discoveryError);
        }

        var refreshText = Scalar(section, "KeyRefreshInterval", Key("KeyRefreshInterval"), out var refreshError);
        if (refreshError is not null)
        {
            return Fail(refreshError);
        }

        if (discoveryText is not null && signingKeysJson is not null)
        {
            return Fail(ConfigurationError.InconsistentSettings(
                Key("Discovery"), Key("SigningKeys"), "a provider finds its keys one way, not both"));
        }

        if (refreshText is not null && discoveryText is null)
        {
            return Fail(signingKeysJson is not null
                ? ConfigurationError.InconsistentSettings(
                    Key("KeyRefreshInterval"), Key("SigningKeys"), "fixed keys are never refreshed")
                : ConfigurationError.MissingRequiredSetting(Key("Discovery"), Source));
        }

        if (discoveryText is null && signingKeysJson is null)
        {
            return Fail(ConfigurationError.MissingRequiredSetting(
                $"{Key("Discovery")} or {Key("SigningKeys")}", Source));
        }

        JsonWebKeySet? keySet = null;
        if (signingKeysJson is not null)
        {
            keySet = ParseKeySet(signingKeysJson, out var keySetProblem);
            if (keySet is null)
            {
                return Fail(ConfigurationError.InvalidSetting(Key("SigningKeys"), keySetProblem));
            }
        }

        Uri? discovery = null;
        if (discoveryText is not null
            && (!Uri.TryCreate(discoveryText, UriKind.Absolute, out discovery) || discovery.Scheme != Uri.UriSchemeHttps))
        {
            return Fail(ConfigurationError.InvalidSetting(Key("Discovery"), "it must be an absolute https address"));
        }

        var refresh = DefaultRefresh;
        if (refreshText is not null
            && (!TimeSpan.TryParse(refreshText, CultureInfo.InvariantCulture, out refresh)
                || refresh < MinimumRefresh
                || refresh > MaximumRefresh))
        {
            return Fail(ConfigurationError.InvalidSetting(
                Key("KeyRefreshInterval"), "it must be a duration between 00:00:30 and 1.00:00:00"));
        }

        var audiences = StringArray(section, "Audiences", Key("Audiences"), out var audiencesError);
        if (audiencesError is not null)
        {
            return Fail(audiencesError);
        }

        if (audiences is null)
        {
            return Fail(ConfigurationError.MissingRequiredSetting(Key("Audiences"), Source));
        }

        if (audiences.Any(string.IsNullOrEmpty))
        {
            return Fail(ConfigurationError.InvalidSetting(Key("Audiences"), "every audience must be a non-empty string"));
        }

        var algorithms = StringArray(section, "Algorithms", Key("Algorithms"), out var algorithmsError);
        if (algorithmsError is not null)
        {
            return Fail(algorithmsError);
        }

        algorithms ??= ["RS256"];
        var rejected = algorithms.FirstOrDefault(
            algorithm => !PermittedAlgorithms.Contains(algorithm, StringComparer.Ordinal));
        if (rejected is not null)
        {
            return Fail(ConfigurationError.InvalidSetting(
                Key("Algorithms"),
                $"'{rejected}' is not permitted; only RS256, RS384, RS512, PS256, PS384, PS512, ES256, ES384 and ES512 are"));
        }

        var tolerance = TimeSpan.FromMinutes(1);
        var toleranceText = Scalar(section, "ClockTolerance", Key("ClockTolerance"), out var toleranceError);
        if (toleranceError is not null)
        {
            return Fail(toleranceError);
        }

        if (toleranceText is not null
            && (!TimeSpan.TryParse(toleranceText, CultureInfo.InvariantCulture, out tolerance)
                || tolerance < TimeSpan.Zero
                || tolerance > MaximumTolerance))
        {
            return Fail(ConfigurationError.InvalidSetting(
                Key("ClockTolerance"), "it must be a duration between 00:00:00 and 00:05:00"));
        }

        var subjectClaim = Scalar(section, "SubjectClaim", Key("SubjectClaim"), out var subjectError);
        if (subjectError is not null)
        {
            return Fail(subjectError);
        }

        if (subjectClaim is { Length: 0 })
        {
            return Fail(ConfigurationError.InvalidSetting(Key("SubjectClaim"), "it must name a claim"));
        }

        var displayNameClaim = Scalar(section, "DisplayNameClaim", Key("DisplayNameClaim"), out var displayError);
        if (displayError is not null)
        {
            return Fail(displayError);
        }

        if (displayNameClaim is { Length: 0 })
        {
            return Fail(ConfigurationError.InvalidSetting(Key("DisplayNameClaim"), "it must name a claim"));
        }

        return Result<BearerSettings, ConfigurationError>.Success(new BearerSettings(
            name, issuer, keySet, audiences, algorithms, tolerance, subjectClaim ?? "sub", displayNameClaim,
            discovery, refresh, issuerPattern));
    }

    /// <summary>Whether <paramref name="issuer"/> is one this provider trusts: the configured
    /// <c>Issuer</c> exactly, or an issuer the <c>IssuerPattern</c> matches. Everything outside the
    /// placeholder is compared ordinally, and the placeholder matches one or more characters, none
    /// of them <c>/</c>.</summary>
    internal bool Claims(string issuer)
    {
        if (IssuerPattern is null)
        {
            return string.Equals(issuer, Issuer, StringComparison.Ordinal);
        }

        var at = IssuerPattern.IndexOf(TenantPlaceholder, StringComparison.Ordinal);
        var prefix = IssuerPattern.AsSpan(0, at);
        var suffix = IssuerPattern.AsSpan(at + TenantPlaceholder.Length);

        if (issuer.Length < prefix.Length + suffix.Length + 1
            || !issuer.AsSpan().StartsWith(prefix, StringComparison.Ordinal)
            || !issuer.AsSpan().EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        return !issuer.AsSpan(prefix.Length, issuer.Length - prefix.Length - suffix.Length).Contains('/');
    }

    /// <summary>What a discovery document's <c>issuer</c> must equal ordinally: the configured
    /// <c>Issuer</c>, or for a pattern provider the pattern string itself.</summary>
    internal string DiscoveryIssuer => Issuer ?? IssuerPattern!;

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var at = text.IndexOf(value, StringComparison.Ordinal);
             at >= 0;
             at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>A setting that holds a value. Absent is <see langword="null"/>; a section where the
    /// value belongs is an error.</summary>
    private static string? Scalar(IConfigurationSection section, string setting, string key, out ConfigurationError? error)
    {
        error = null;
        var child = section.GetSection(setting);
        if (child.GetChildren().Any())
        {
            error = ConfigurationError.InvalidSetting(key, "it must be a value, not a section");
            return null;
        }

        return child.Value;
    }

    /// <summary>A setting that holds an array. Absent or empty is <see langword="null"/>; a value
    /// where the array belongs is an error.</summary>
    private static List<string>? StringArray(IConfigurationSection section, string setting, string key, out ConfigurationError? error)
    {
        error = null;
        var child = section.GetSection(setting);
        var items = child.GetChildren().ToList();
        if (items.Count == 0)
        {
            if (child.Value is not null)
            {
                error = ConfigurationError.InvalidSetting(key, "it must be an array of strings, not a single value");
            }

            return null;
        }

        var values = new List<string>(items.Count);
        foreach (var item in items)
        {
            if (item.GetChildren().Any())
            {
                error = ConfigurationError.InvalidSetting(key, "every element must be a string, not a section");
                return null;
            }

            values.Add(item.Value ?? "");
        }

        return values;
    }

    internal static JsonWebKeySet? ParseKeySet(string json, out string problem)
    {
        problem = "";
        JsonWebKeySet keySet;
        try
        {
            keySet = new JsonWebKeySet(json);
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException or InvalidOperationException)
        {
            problem = "it must be a JSON Web Key Set document";
            return null;
        }

        if (keySet.Keys.Count == 0)
        {
            problem = "the key set holds no keys";
            return null;
        }

        foreach (var key in keySet.Keys)
        {
            if (key.Kty is not (JsonWebAlgorithmsKeyTypes.RSA or JsonWebAlgorithmsKeyTypes.EllipticCurve))
            {
                problem = "only RSA and EC public keys are permitted";
                return null;
            }

            if (key.HasPrivateKey)
            {
                problem = "it must hold public keys only; a key carries private members";
                return null;
            }
        }

        return keySet;
    }
}
