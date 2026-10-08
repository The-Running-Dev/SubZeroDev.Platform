using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Hosting;
using SubZeroDev.Platform.Identity;

namespace SubZeroDev.Platform.Tests;

/// <summary>D5-S22: one issuer serves many customer tenants. A provider configured with an
/// <c>IssuerPattern</c> claims every issuer string the pattern matches and authenticates each as its
/// own principal (I-I11).</summary>
public sealed class IssuerPatternTests
{
    internal const string Pattern = "https://{tenantid}.issuer.test";

    private const string Section = "Platform:Identity:Bearer:corp";

    private static readonly RSA Rsa = ConfiguredBearerTests.Rsa;

    // S22.1 -------------------------------------------------------------------------------------

    /// <summary>S22.1 — <c>IssuerPattern</c> is a recognised key: a provider configured with it and
    /// no <c>Issuer</c> starts and claims a matching issuer.</summary>
    [Fact]
    public async Task S22_1_IssuerPattern_is_a_recognised_key()
    {
        var provider = new ConfiguredBearerAuthenticationProvider(ConfiguredBearerTests.ReadSettings(Patterned()));

        var result = await provider.AuthenticateAsync(
            ConfiguredBearerTests.Request(ConfiguredBearerTests.Mint(Rsa, issuer: "https://acme.issuer.test")), default);

        Assert.True(result.IsSuccess);
    }

    /// <summary>S22.1 — <c>Issuer</c> with <c>IssuerPattern</c> is <c>InconsistentSettings</c> and
    /// neither is <c>MissingRequiredSetting</c>; each names the full keys.</summary>
    [Theory]
    [InlineData(true, true, nameof(ConfigurationError.InconsistentSettings))]
    [InlineData(false, false, nameof(ConfigurationError.MissingRequiredSetting))]
    public void S22_1_Exactly_one_of_Issuer_and_IssuerPattern_is_required(bool issuer, bool pattern, string code)
    {
        var settings = Patterned();
        if (issuer)
        {
            settings[$"{Section}:Issuer"] = ConfiguredBearerTests.Issuer;
        }

        if (!pattern)
        {
            settings.Remove($"{Section}:IssuerPattern");
        }

        var inner = StartupFailure(settings);

        Assert.Equal(code, inner.Code);
        Assert.Contains($"{Section}:Issuer", inner.Detail, StringComparison.Ordinal);
        Assert.Contains($"{Section}:IssuerPattern", inner.Detail, StringComparison.Ordinal);
    }

    // S22.2 -------------------------------------------------------------------------------------

    /// <summary>S22.2 — a pattern that does not carry <c>{tenantid}</c> exactly once is
    /// <c>InvalidSetting</c>, naming the full key.</summary>
    [Theory]
    [InlineData("https://issuer.test")]
    [InlineData("https://{tenantid}.{tenantid}.issuer.test")]
    [InlineData("https://{TenantId}.issuer.test")]
    [InlineData("https://{tenantid.issuer.test")]
    [InlineData("")]
    public void S22_2_A_pattern_without_the_placeholder_exactly_once_is_invalid(string pattern)
    {
        var settings = Patterned();
        settings[$"{Section}:IssuerPattern"] = pattern;

        var inner = StartupFailure(settings);

        Assert.Equal(nameof(ConfigurationError.InvalidSetting), inner.Code);
        Assert.Contains($"{Section}:IssuerPattern", inner.Detail, StringComparison.Ordinal);
    }

    // S22.3 -------------------------------------------------------------------------------------

    public static TheoryData<string, string, bool> Matches => new()
    {
        { Pattern, "https://acme.issuer.test", true },
        { Pattern, "https://ACME-Corp.issuer.test", true },
        { Pattern, "https://a.b.issuer.test", true },
        { Pattern, "https://.issuer.test", false },
        { Pattern, "https://a/b.issuer.test", false },
        { Pattern, "https://acme.issuer.test/", false },
        { Pattern, "HTTPS://acme.issuer.test", false },
        { Pattern, "https://acme.ISSUER.test", false },
        { Pattern, "https://acme.issuer.testing", false },
        { Pattern, "https://issuer.test", false },
        { "{tenantid}.example/issuer", "acme.example/issuer", true },
        { "{tenantid}.example/issuer", ".example/issuer", false },
        { "{tenantid}.example/issuer", "a/b.example/issuer", false },
        { "{tenantid}.example/issuer", "acme.EXAMPLE/issuer", false },
        { "https://issuer.test/{tenantid}", "https://issuer.test/acme", true },
        { "https://issuer.test/{tenantid}", "https://issuer.test/", false },
        { "https://issuer.test/{tenantid}", "https://issuer.test/a/b", false },
        { "https://issuer.test/{tenantid}", "https://ISSUER.test/acme", false },
        { "a{tenantid}a", "aba", true },
        { "a{tenantid}a", "aa", false },
    };

    /// <summary>S22.3 — the placeholder matches one or more characters, none of them <c>/</c>, and
    /// everything else is ordinal. A matching issuer is claimed; any other is
    /// <c>CredentialNotClaimed</c>.</summary>
    [Theory]
    [MemberData(nameof(Matches))]
    public async Task S22_3_The_placeholder_matches_one_or_more_non_slash_characters(
        string pattern, string issuer, bool claimed)
    {
        var settings = Patterned();
        settings[$"{Section}:IssuerPattern"] = pattern;
        var provider = new ConfiguredBearerAuthenticationProvider(ConfiguredBearerTests.ReadSettings(settings));

        var result = await provider.AuthenticateAsync(
            ConfiguredBearerTests.Request(ConfiguredBearerTests.Mint(Rsa, issuer: issuer)), default);

        if (claimed)
        {
            Assert.True(result.IsSuccess);
        }
        else
        {
            Assert.Equal(nameof(AuthenticationError.CredentialNotClaimed), result.Error.Code);
        }
    }

    // S22.4 -------------------------------------------------------------------------------------

    /// <summary>S22.4 — the principal's issuer is the concrete validated <c>iss</c>, never the
    /// pattern, so two tenants presenting one subject are two principals (I-I11).</summary>
    [Fact]
    public async Task S22_4_Two_tenants_with_one_subject_are_two_principals()
    {
        var provider = new ConfiguredBearerAuthenticationProvider(ConfiguredBearerTests.ReadSettings(Patterned()));

        var acme = await provider.AuthenticateAsync(ConfiguredBearerTests.Request(
            ConfiguredBearerTests.Mint(Rsa, issuer: "https://acme.issuer.test", subject: "alice")), default);
        var globex = await provider.AuthenticateAsync(ConfiguredBearerTests.Request(
            ConfiguredBearerTests.Mint(Rsa, issuer: "https://globex.issuer.test", subject: "alice")), default);

        Assert.Equal(new PrincipalId("https://acme.issuer.test", "alice"), acme.Value.Id);
        Assert.Equal(new PrincipalId("https://globex.issuer.test", "alice"), globex.Value.Id);
        Assert.NotEqual(acme.Value.Id, globex.Value.Id);
        Assert.Equal(PrincipalKind.Account, acme.Value.Kind);
        Assert.DoesNotContain("{tenantid}", acme.Value.Id.Issuer, StringComparison.Ordinal);
    }

    // S22.6 -------------------------------------------------------------------------------------

    /// <summary>S22.6 — through an operated host, tokens from two tenants of one configured pattern
    /// authenticate as distinct principals, and a token from an issuer outside the pattern is
    /// refused as unauthenticated.</summary>
    [Fact]
    public async Task S22_6_An_operated_host_authenticates_each_tenant_as_its_own_principal()
    {
        var (app, client) = await WebHostUnderTest.StartAsync(
            services => services.AddSingleton<IPlatformModule>(new IdentityModule()),
            Patterned(),
            composeOperatedDefaults: false,
            postCompose: services => services.AddSingleton<IAuditSink>(
                new RecordingAuditSink("test-durable", isDurable: true)),
            mapEndpoints: web => web.MapGet("/who", (ICurrentPrincipal principal) => principal.Current.Id.ToString())
                .ExemptFromPlatformAuthorization("Test-harness endpoint reporting the authenticated principal."));

        try
        {
            var acme = await WhoAsync(client, "https://acme.issuer.test");
            var globex = await WhoAsync(client, "https://globex.issuer.test");

            Assert.Equal(HttpStatusCode.OK, acme.Status);
            Assert.Equal(HttpStatusCode.OK, globex.Status);
            Assert.Equal("https://acme.issuer.test:alice", acme.Body);
            Assert.Equal("https://globex.issuer.test:alice", globex.Body);

            var foreign = await WhoAsync(client, "https://acme.elsewhere.test");
            Assert.Equal(HttpStatusCode.Unauthorized, foreign.Status);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    // helpers -----------------------------------------------------------------------------------

    internal static Dictionary<string, string?> Patterned()
    {
        var settings = ConfiguredBearerTests.Valid("corp");
        settings.Remove($"{Section}:Issuer");
        settings[$"{Section}:IssuerPattern"] = Pattern;
        return settings;
    }

    private static ConfigurationError StartupFailure(Dictionary<string, string?> settings)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(ConfiguredBearerTests.Configure(settings));

        var exception = Assert.Throws<PlatformStartupException>(() => new IdentityModule().Register(services));

        var error = Assert.IsType<HostStartupError>(exception.Error);
        Assert.Equal(nameof(HostStartupError.Configuration), error.Code);
        return Assert.IsType<ConfigurationError>(error.Inner);
    }

    private static async Task<(HttpStatusCode Status, string Body)> WhoAsync(HttpClient client, string issuer)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/who");
        request.Headers.Add("Authorization", $"Bearer {ConfiguredBearerTests.Mint(Rsa, issuer: issuer)}");
        using var response = await client.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }
}
