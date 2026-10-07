using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Hosting;
using SubZeroDev.Platform.Identity;
using Vendor.ConfigurationFixture;

namespace SubZeroDev.Platform.Tests;

/// <summary>D5-S23: a vendor's settings arrive as a configuration source. The fixture project is built
/// the way a vendor package would be, and Identity reads what it writes exactly as it reads a settings
/// file (I-I14).</summary>
public sealed class VendorConfigurationSourceTests
{
    private const string Discovery = "https://issuer.test/.well-known/openid-configuration";

    private static readonly RSA Rsa = ConfiguredBearerTests.Rsa;

    // S23.1 -------------------------------------------------------------------------------------

    /// <summary>S23.1 — the fixture contributes <c>Platform:Identity:Bearer:&lt;name&gt;:*</c> keys
    /// through a standard configuration source, under whatever name the caller chooses.</summary>
    [Fact]
    public void S23_1_The_fixture_contributes_keys_under_the_callers_provider_name()
    {
        var configuration = new ConfigurationBuilder()
            .AddFixtureFixedKeyIssuer("hosted", ConfiguredBearerTests.Issuer, ConfiguredBearerTests.Jwks, ConfiguredBearerTests.Audience)
            .AddFixtureFixedKeyIssuer("self", "https://self.test", ConfiguredBearerTests.Jwks, ConfiguredBearerTests.Audience)
            .Build();

        var read = BearerSettings.ReadAll(configuration);

        Assert.True(read.IsSuccess);
        Assert.Equal(["hosted", "self"], read.Value.Select(s => s.Name).Order());
    }

    // S23.3 -------------------------------------------------------------------------------------

    /// <summary>S23.3 — the same keys and values from the fixture's source and from an in-memory settings
    /// file give equal validated settings, for each protocol surface the fixture offers.</summary>
    [Fact]
    public void S23_3_Equal_settings_from_a_vendor_source_and_a_settings_file()
    {
        var jwks = ConfiguredBearerTests.Jwks;
        var cases = new (IConfiguration Vendor, Dictionary<string, string?> File)[]
        {
            (
                new ConfigurationBuilder().AddFixtureFixedKeyIssuer("v", ConfiguredBearerTests.Issuer, jwks, ConfiguredBearerTests.Audience).Build(),
                ConfiguredBearerTests.Valid("v")),
            (
                new ConfigurationBuilder().AddFixtureDiscoveredIssuer("v", ConfiguredBearerTests.Issuer, Discovery, ConfiguredBearerTests.Audience).Build(),
                new()
                {
                    ["Platform:Identity:Bearer:v:Issuer"] = ConfiguredBearerTests.Issuer,
                    ["Platform:Identity:Bearer:v:Discovery"] = Discovery,
                    ["Platform:Identity:Bearer:v:Audiences:0"] = ConfiguredBearerTests.Audience,
                }),
            (
                new ConfigurationBuilder().AddFixtureTenantedIssuer("v", IssuerPatternTests.Pattern, jwks, ConfiguredBearerTests.Audience).Build(),
                new()
                {
                    ["Platform:Identity:Bearer:v:IssuerPattern"] = IssuerPatternTests.Pattern,
                    ["Platform:Identity:Bearer:v:SigningKeys"] = jwks,
                    ["Platform:Identity:Bearer:v:Audiences:0"] = ConfiguredBearerTests.Audience,
                }),
        };

        foreach (var (vendor, file) in cases)
        {
            var fromVendor = BearerSettings.ReadAll(vendor);
            var fromFile = BearerSettings.ReadAll(ConfiguredBearerTests.Configure(file));

            Assert.True(fromVendor.IsSuccess);
            Assert.True(fromFile.IsSuccess);
            Assert.Equal(Describe(fromFile.Value.Single()), Describe(fromVendor.Value.Single()));
        }
    }

    /// <summary>S23.3 — providers built from the two sources accept and reject the same tokens.</summary>
    [Fact]
    public async Task S23_3_Providers_from_either_source_accept_and_reject_the_same_tokens()
    {
        var vendor = BearerSettings.ReadAll(new ConfigurationBuilder()
            .AddFixtureTenantedIssuer("v", IssuerPatternTests.Pattern, ConfiguredBearerTests.Jwks, ConfiguredBearerTests.Audience)
            .Build()).Value.Single();
        var file = BearerSettings.ReadAll(ConfiguredBearerTests.Configure(new Dictionary<string, string?>
        {
            ["Platform:Identity:Bearer:v:IssuerPattern"] = IssuerPatternTests.Pattern,
            ["Platform:Identity:Bearer:v:SigningKeys"] = ConfiguredBearerTests.Jwks,
            ["Platform:Identity:Bearer:v:Audiences:0"] = ConfiguredBearerTests.Audience,
        })).Value.Single();

        var tokens = new[]
        {
            ConfiguredBearerTests.Mint(Rsa, issuer: "https://acme.issuer.test"),
            ConfiguredBearerTests.Mint(Rsa, issuer: "https://acme.elsewhere.test"),
            ConfiguredBearerTests.Mint(Rsa, issuer: "https://acme.issuer.test", audience: "someone-else"),
            ConfiguredBearerTests.Mint(Rsa, issuer: "https://acme.issuer.test", expires: DateTimeOffset.UtcNow.AddHours(-1)),
            ConfiguredBearerTests.Mint(Rsa, issuer: "https://acme.issuer.test", kid: "unknown"),
        };

        var accepted = 0;
        foreach (var token in tokens)
        {
            var a = await new ConfiguredBearerAuthenticationProvider(vendor)
                .AuthenticateAsync(ConfiguredBearerTests.Request(token), default);
            var b = await new ConfiguredBearerAuthenticationProvider(file)
                .AuthenticateAsync(ConfiguredBearerTests.Request(token), default);

            Assert.Equal(a.IsSuccess, b.IsSuccess);
            if (a.IsSuccess)
            {
                accepted++;
                Assert.Equal(a.Value.Id, b.Value.Id);
            }
            else
            {
                Assert.Equal(a.Error.Code, b.Error.Code);
            }
        }

        Assert.Equal(1, accepted);
    }

    // S23.4 -------------------------------------------------------------------------------------

    /// <summary>S23.4 — a key the fixture writes that Identity does not recognise fails startup as
    /// <c>InvalidSetting</c>, naming the full key, exactly as it would from a settings file.</summary>
    [Fact]
    public void S23_4_An_unrecognised_vendor_key_fails_startup_like_one_from_a_settings_file()
    {
        const string key = "Platform:Identity:Bearer:quirky:VendorQuirk";

        var vendorServices = new ServiceCollection();
        vendorServices.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddFixtureIssuerWithQuirk("quirky", ConfiguredBearerTests.Issuer, ConfiguredBearerTests.Jwks, ConfiguredBearerTests.Audience)
            .Build());

        var fromFile = ConfiguredBearerTests.Valid("quirky");
        fromFile[key] = "on";
        var fileServices = new ServiceCollection();
        fileServices.AddSingleton(ConfiguredBearerTests.Configure(fromFile));

        var vendorError = StartupFailure(vendorServices);
        var fileError = StartupFailure(fileServices);

        Assert.Equal(nameof(ConfigurationError.InvalidSetting), vendorError.Code);
        Assert.Contains(key, vendorError.Detail, StringComparison.Ordinal);
        Assert.Equal(fileError.Code, vendorError.Code);
        Assert.Equal(fileError.Detail, vendorError.Detail);
    }

    // helpers -----------------------------------------------------------------------------------

    private static ConfigurationError StartupFailure(ServiceCollection services)
    {
        var exception = Assert.Throws<PlatformStartupException>(() => new IdentityModule().Register(services));
        var error = Assert.IsType<HostStartupError>(exception.Error);
        return Assert.IsType<ConfigurationError>(error.Inner);
    }

    private static string Describe(BearerSettings s) => string.Join(
        "|",
        s.Name, s.Issuer, s.IssuerPattern, s.Discovery, s.KeyRefreshInterval, s.ClockTolerance, s.SubjectClaim,
        s.DisplayNameClaim, string.Join(",", s.Audiences), string.Join(",", s.Algorithms),
        s.SigningKeys is null ? "-" : string.Join(",", s.SigningKeys.Keys.Select(k => k.Kid)));
}
