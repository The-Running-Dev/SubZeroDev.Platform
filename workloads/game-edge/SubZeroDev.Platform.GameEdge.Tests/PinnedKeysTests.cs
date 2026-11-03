using Microsoft.Extensions.Configuration;

namespace SubZeroDev.Platform.GameEdge.Tests;

/// <summary>Invariant 125 — terminate mode validates tokens only against pinned keys, so a provider
/// carries <c>SigningKeys</c> and no <c>Discovery</c>. The rule is checked on the raw configuration,
/// before Identity is registered, because Identity's discovery fetches at startup.</summary>
public sealed class PinnedKeysTests
{
    private const string Jwks = """{"keys":[]}""";

    [Fact]
    public void A_provider_with_pinned_keys_and_no_discovery_is_accepted()
    {
        var errors = PinnedKeys.Validate(Configure(
            ("game:Issuer", "https://issuer.test"),
            ("game:SigningKeys", Jwks)));

        Assert.Empty(errors);
    }

    [Fact]
    public void Several_pinned_providers_are_accepted()
    {
        var errors = PinnedKeys.Validate(Configure(
            ("a:Issuer", "https://a.test"),
            ("a:SigningKeys", Jwks),
            ("b:IssuerPattern", "https://*.b.test"),
            ("b:SigningKeys", Jwks)));

        Assert.Empty(errors);
    }

    [Fact]
    public void No_provider_is_not_this_checks_to_refuse()
    {
        Assert.Empty(PinnedKeys.Validate(Configure()));
    }

    [Fact]
    public void A_discovery_only_provider_is_refused_for_the_address_and_for_the_missing_keys()
    {
        var errors = PinnedKeys.Validate(Configure(
            ("game:Issuer", "https://issuer.test"),
            ("game:Discovery", "https://issuer.test/.well-known/jwks.json")));

        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, e => e.StartsWith("'Platform:Identity:Bearer:game:Discovery'", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.StartsWith("'Platform:Identity:Bearer:game:SigningKeys'", StringComparison.Ordinal));
    }

    [Fact]
    public void A_discovery_provider_beside_a_pinned_one_is_refused_and_only_it_is_named()
    {
        var errors = PinnedKeys.Validate(Configure(
            ("pinned:Issuer", "https://a.test"),
            ("pinned:SigningKeys", Jwks),
            ("remote:Issuer", "https://b.test"),
            ("remote:SigningKeys", Jwks),
            ("remote:Discovery", "https://b.test/jwks")));

        var error = Assert.Single(errors);
        Assert.StartsWith("'Platform:Identity:Bearer:remote:Discovery'", error, StringComparison.Ordinal);
        Assert.DoesNotContain("pinned:", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_provider_without_pinned_keys_is_refused_naming_the_setting(string? signingKeys)
    {
        var errors = signingKeys is null
            ? PinnedKeys.Validate(Configure(("game:Issuer", "https://issuer.test")))
            : PinnedKeys.Validate(Configure(("game:Issuer", "https://issuer.test"), ("game:SigningKeys", signingKeys)));

        var error = Assert.Single(errors);
        Assert.StartsWith("'Platform:Identity:Bearer:game:SigningKeys'", error, StringComparison.Ordinal);
    }

    private static IConfiguration Configure(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>($"Platform:Identity:Bearer:{s.Key}", s.Value)))
            .Build();
}
