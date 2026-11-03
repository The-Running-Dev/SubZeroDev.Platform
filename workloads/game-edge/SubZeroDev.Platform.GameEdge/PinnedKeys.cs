using Microsoft.Extensions.Configuration;

namespace SubZeroDev.Platform.GameEdge;

/// <summary>The terminate-mode rule of invariant 125: the edge validates bearer tokens only against
/// issuer keys pinned in its configuration, so a provider carries <c>SigningKeys</c> and no
/// <c>Discovery</c>. It reads the raw <c>Platform:Identity:Bearer</c> section, as Identity's own
/// settings do, and runs before Identity is registered: Identity's discovery fetches at startup, so a
/// check made after registration would come after the connection it exists to prevent. Whether a
/// pinned key set is a usable one (a JSON Web Key Set of public RSA or EC keys, none missing) stays
/// Identity's own validation, which also runs before any key is used.</summary>
internal static class PinnedKeys
{
    private const string SectionPath = "Platform:Identity:Bearer";
    private const string ModeKey = "GameEdge:CredentialMode";

    /// <summary>Every breach of the pinning rule, each naming the provider's full configuration key.
    /// Empty when every configured provider is pinned. A host with no provider is the composition
    /// profile's to refuse, not this check's.</summary>
    public static IReadOnlyList<string> Validate(IConfiguration configuration)
    {
        var errors = new List<string>();

        foreach (var provider in configuration.GetSection(SectionPath).GetChildren())
        {
            var path = $"{SectionPath}:{provider.Key}";

            // A key with nothing beneath it is not a provider and carries no discovery address; a bare
            // value there is Identity's to report.
            if (!provider.GetChildren().Any())
            {
                continue;
            }

            if (provider.GetSection("Discovery").Exists())
            {
                errors.Add(
                    $"'{path}:Discovery' must not be set when '{ModeKey}' is '{CredentialMode.Terminate}': "
                    + "the edge validates tokens only against pinned keys and opens no connection to an issuer.");
            }

            if (string.IsNullOrWhiteSpace(provider["SigningKeys"]))
            {
                errors.Add(
                    $"'{path}:SigningKeys' is required when '{ModeKey}' is '{CredentialMode.Terminate}': "
                    + "the issuer's public keys are pinned in the edge's configuration.");
            }
        }

        return errors;
    }
}
