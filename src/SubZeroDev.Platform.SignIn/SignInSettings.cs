using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;

namespace SubZeroDev.Platform.SignIn;

/// <summary>One sign-in method's settings, after validation. Internal on purpose: the schema is the
/// configuration keys, and no public type may carry it (<c>20-contract.md</c> § 13).</summary>
internal sealed record SignInSettings(
    string Name,
    string Issuer,
    string ClientId,
    string RedirectPath,
    string Scopes,
    string? EndSessionTemplate,
    IReadOnlyDictionary<string, string> ExtraAuthorizeParameters,
    string PostSignOutPath)
{
    internal const string SectionPath = "SubZeroDev:SignIn";

    internal const string BearerSectionPath = "Platform:Identity:Bearer";

    internal const string ClientIdPlaceholder = "{client_id}";

    internal const string ReturnToPlaceholder = "{return_to}";

    internal const string IdTokenHintPlaceholder = "{id_token_hint}";

    private const string Source = "the SubZeroDev:SignIn section of configuration";

    // The parameters the module owns. A configuration setting that names one fails startup; a hook
    // that returns one has it overwritten with the module's value.
    private static readonly string[] ProtectedParameters =
    [
        "client_id", "redirect_uri", "response_type", "scope", "state", "code_challenge", "code_challenge_method",
    ];

    // An unrecognised key fails startup rather than being ignored. A client-secret key is deliberately
    // not among them: this is a public client.
    private static readonly HashSet<string> Recognised = new(StringComparer.OrdinalIgnoreCase)
    {
        "Issuer", "ClientId", "RedirectPath", "Scopes", "EndSessionTemplate", "ExtraAuthorizeParameters", "PostSignOutPath",
    };

    private static readonly Regex MethodName = new("^[A-Za-z0-9_-]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Every brace and whatever follows it up to the next brace, so a stray brace is reported too.
    private static readonly Regex Placeholder = new("[{}][^{}]*[{}]?", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Whether <paramref name="name"/> is one the module owns.</summary>
    internal static bool IsProtected(string name) =>
        ProtectedParameters.Contains(name, StringComparer.OrdinalIgnoreCase)
        || string.Equals(name, "nonce", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads and validates every child section of <c>SubZeroDev:SignIn</c>, before anything
    /// is registered or any address is built.</summary>
    internal static Result<IReadOnlyList<SignInSettings>, ConfigurationError> ReadAll(IConfiguration configuration)
    {
        var methods = new List<SignInSettings>();
        var issuers = configuration.GetSection(BearerSectionPath).GetChildren()
            .Select(section => section.GetSection("Issuer").Value)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

        foreach (var section in configuration.GetSection(SectionPath).GetChildren())
        {
            var read = Read(section, issuers);
            if (!read.IsSuccess)
            {
                return Result<IReadOnlyList<SignInSettings>, ConfigurationError>.Failure(read.Error);
            }

            methods.Add(read.Value);
        }

        foreach (var group in methods.GroupBy(method => method.RedirectPath, StringComparer.Ordinal))
        {
            var clash = group.ToList();
            if (clash.Count > 1)
            {
                return Result<IReadOnlyList<SignInSettings>, ConfigurationError>.Failure(
                    ConfigurationError.InconsistentSettings(
                        $"{SectionPath}:{clash[0].Name}:RedirectPath",
                        $"{SectionPath}:{clash[1].Name}:RedirectPath",
                        "two sign-in methods cannot share a callback path"));
            }
        }

        return Result<IReadOnlyList<SignInSettings>, ConfigurationError>.Success(methods);
    }

    /// <summary>Builds the end-session address from the template, url-encoding every value.</summary>
    internal static Uri ExpandTemplate(string template, string clientId, Uri returnTo) =>
        new(
            template
                .Replace(ClientIdPlaceholder, Uri.EscapeDataString(clientId), StringComparison.Ordinal)
                .Replace(ReturnToPlaceholder, Uri.EscapeDataString(returnTo.AbsoluteUri), StringComparison.Ordinal)

                // The session holds no id token (I-S1), so there is never a hint to substitute.
                .Replace(IdTokenHintPlaceholder, string.Empty, StringComparison.Ordinal),
            UriKind.Absolute);

    private static Result<SignInSettings, ConfigurationError> Read(IConfigurationSection section, HashSet<string> issuers)
    {
        var name = section.Key;
        string Key(string setting) => $"{SectionPath}:{name}:{setting}";
        static Result<SignInSettings, ConfigurationError> Fail(ConfigurationError error) =>
            Result<SignInSettings, ConfigurationError>.Failure(error);

        if (section.Value is not null)
        {
            return Fail(ConfigurationError.InvalidSetting(
                $"{SectionPath}:{name}", "it must be a section holding the sign-in method's settings, not a value"));
        }

        if (!MethodName.IsMatch(name))
        {
            return Fail(ConfigurationError.InvalidSetting(
                $"{SectionPath}:{name}",
                "a sign-in method's name may contain only letters, digits, '-' and '_', because it appears in a route and a cookie name"));
        }

        foreach (var child in section.GetChildren())
        {
            if (!Recognised.Contains(child.Key))
            {
                return Fail(ConfigurationError.InvalidSetting(
                    Key(child.Key),
                    "it is not a setting the sign-in module recognises; the module is a public client and has no client secret"));
            }
        }

        var issuer = Scalar(section, "Issuer", Key("Issuer"), out var error);
        if (error is not null)
        {
            return Fail(error);
        }

        if (string.IsNullOrWhiteSpace(issuer))
        {
            return Fail(ConfigurationError.MissingRequiredSetting(Key("Issuer"), Source));
        }

        if (!Uri.TryCreate(issuer, UriKind.Absolute, out var issuerUri)
            || !(issuerUri.Scheme == Uri.UriSchemeHttps || (issuerUri.Scheme == Uri.UriSchemeHttp && issuerUri.IsLoopback)))
        {
            return Fail(ConfigurationError.InvalidSetting(
                Key("Issuer"), "it must be an absolute https address (http only for a loopback host)"));
        }

        if (!issuers.Contains(issuer))
        {
            return Fail(ConfigurationError.InvalidSetting(
                Key("Issuer"), $"it must equal the Issuer of a provider configured under {BearerSectionPath}; none does"));
        }

        var clientId = Scalar(section, "ClientId", Key("ClientId"), out error);
        if (error is not null)
        {
            return Fail(error);
        }

        if (string.IsNullOrWhiteSpace(clientId))
        {
            return Fail(ConfigurationError.MissingRequiredSetting(Key("ClientId"), Source));
        }

        var redirectPath = Scalar(section, "RedirectPath", Key("RedirectPath"), out error);
        if (error is not null)
        {
            return Fail(error);
        }

        if (string.IsNullOrWhiteSpace(redirectPath))
        {
            return Fail(ConfigurationError.MissingRequiredSetting(Key("RedirectPath"), Source));
        }

        if (!IsRelativePath(redirectPath) || redirectPath.Contains('{', StringComparison.Ordinal) || redirectPath.Contains('}', StringComparison.Ordinal))
        {
            return Fail(ConfigurationError.InvalidSetting(
                Key("RedirectPath"), "it must be a relative path beginning with '/' and holding no query, fragment or route parameter"));
        }

        var scopes = Scalar(section, "Scopes", Key("Scopes"), out error);
        if (error is not null)
        {
            return Fail(error);
        }

        scopes = string.IsNullOrWhiteSpace(scopes) ? "openid" : scopes.Trim();
        if (!scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("openid", StringComparer.Ordinal))
        {
            return Fail(ConfigurationError.InvalidSetting(
                Key("Scopes"), "it must include 'openid', because the session's subject is read from the id token"));
        }

        var template = Scalar(section, "EndSessionTemplate", Key("EndSessionTemplate"), out error);
        if (error is not null)
        {
            return Fail(error);
        }

        if (string.IsNullOrWhiteSpace(template))
        {
            template = null;
        }
        else
        {
            var templateError = ValidateTemplate(template, Key("EndSessionTemplate"));
            if (templateError is not null)
            {
                return Fail(templateError);
            }
        }

        var extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var extraSection = section.GetSection("ExtraAuthorizeParameters");
        if (extraSection.Value is not null)
        {
            return Fail(ConfigurationError.InvalidSetting(
                Key("ExtraAuthorizeParameters"), "it must be a section of name and value pairs, not a single value"));
        }

        foreach (var parameter in extraSection.GetChildren())
        {
            var parameterKey = $"{Key("ExtraAuthorizeParameters")}:{parameter.Key}";
            if (parameter.GetChildren().Any())
            {
                return Fail(ConfigurationError.InvalidSetting(parameterKey, "it must be a value, not a section"));
            }

            if (IsProtected(parameter.Key))
            {
                return Fail(ConfigurationError.InvalidSetting(
                    parameterKey, "it names a parameter the module owns and a configuration setting cannot replace"));
            }

            extra[parameter.Key] = parameter.Value ?? string.Empty;
        }

        var postSignOutPath = Scalar(section, "PostSignOutPath", Key("PostSignOutPath"), out error);
        if (error is not null)
        {
            return Fail(error);
        }

        postSignOutPath = string.IsNullOrWhiteSpace(postSignOutPath) ? "/" : postSignOutPath;
        if (!IsRelativePath(postSignOutPath))
        {
            return Fail(ConfigurationError.InvalidSetting(
                Key("PostSignOutPath"), "it must be a relative path beginning with '/'"));
        }

        return Result<SignInSettings, ConfigurationError>.Success(
            new SignInSettings(name, issuer, clientId, redirectPath, scopes, template, extra, postSignOutPath));
    }

    private static ConfigurationError? ValidateTemplate(string template, string key)
    {
        foreach (Match match in Placeholder.Matches(template))
        {
            if (match.Value is not (ClientIdPlaceholder or ReturnToPlaceholder or IdTokenHintPlaceholder))
            {
                return ConfigurationError.InvalidSetting(
                    key,
                    $"'{match.Value}' is not a placeholder it supports; use {ClientIdPlaceholder}, {ReturnToPlaceholder} or {IdTokenHintPlaceholder}");
            }
        }

        var scheme = template.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "https://"
            : template.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "http://"
            : null;
        if (scheme is null)
        {
            return ConfigurationError.InvalidSetting(key, "it must begin with 'https://' (or 'http://' for a loopback host)");
        }

        // A placeholder in the authority would let a value choose the host the person is sent to.
        var rest = template[scheme.Length..];
        var authorityEnd = rest.IndexOfAny(['/', '?', '#']);
        var authority = authorityEnd < 0 ? rest : rest[..authorityEnd];
        if (authority.Contains('{', StringComparison.Ordinal))
        {
            return ConfigurationError.InvalidSetting(key, "a placeholder cannot appear in the host");
        }

        if (!Uri.TryCreate(
                ExpandTemplate(template, "client", new Uri("https://app.invalid/")).AbsoluteUri,
                UriKind.Absolute,
                out var sample))
        {
            return ConfigurationError.InvalidSetting(key, "it does not produce an absolute address");
        }

        if (sample.Scheme == Uri.UriSchemeHttp && !sample.IsLoopback)
        {
            return ConfigurationError.InvalidSetting(key, "it must use https unless the host is loopback");
        }

        return null;
    }

    private static bool IsRelativePath(string path) =>
        path.Length > 0
        && path[0] == '/'
        && !path.StartsWith("//", StringComparison.Ordinal)
        && !path.Contains('\\', StringComparison.Ordinal)
        && !path.Contains('?', StringComparison.Ordinal)
        && !path.Contains('#', StringComparison.Ordinal)
        && !path.Any(char.IsControl);

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
}
