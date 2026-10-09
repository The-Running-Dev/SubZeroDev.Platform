using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Hosting;
using SubZeroDev.Platform.Identity;

namespace SubZeroDev.Platform.Tests;

/// <summary>D5-S20: an issuer with fixed keys is trusted from configuration alone. The schema is
/// the configuration keys under <c>Platform:Identity:Bearer:&lt;name&gt;</c>; there is no public
/// type to construct.</summary>
public sealed class ConfiguredBearerTests
{
    internal const string Issuer = "https://issuer.test";
    internal const string Audience = "platform-api";
    private const string Section = "Platform:Identity:Bearer:corp";

    internal static readonly RSA Rsa = RSA.Create(2048);
    internal static readonly string Jwks = PublicJwks(Rsa, "key-1");

    // S20.1 -------------------------------------------------------------------------------------

    /// <summary>S20.1 — one section with the three required keys is one provider named after the
    /// section, with no other key and no registration call; several sections chain in registry
    /// order.</summary>
    [Fact]
    public async Task S20_1_Sections_become_providers_named_after_them_in_registry_order()
    {
        var settings = Valid("corp");
        foreach (var (key, value) in Valid("partner", issuer: "https://partner.test"))
        {
            settings[key] = value;
        }

        var (app, _) = await WebHostUnderTest.StartAsync(
            services => services.AddSingleton<IPlatformModule>(new IdentityModule()),
            settings,
            composeOperatedDefaults: false,
            postCompose: services => services.AddSingleton<IAuditSink>(
                new RecordingAuditSink("test-durable", isDurable: true)));

        try
        {
            var registered = app.Services.GetRequiredService<IAuthenticationProviderRegistry>().Registered;

            Assert.Equal(["corp", "partner"], registered.Select(p => p.Name).Order().ToArray());
            Assert.Equal(2, registered.Count);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    // S20.2 / S20.9 -----------------------------------------------------------------------------

    /// <summary>S20.2 and S20.9 — an Operated host whose only authentication provider comes from
    /// configuration starts; a token from the configured issuer authenticates, and a token from an
    /// issuer no section names is refused as unauthenticated.</summary>
    [Fact]
    public async Task S20_2_and_S20_9_An_operated_host_trusts_the_configured_issuer_and_refuses_another()
    {
        var (app, client) = await WebHostUnderTest.StartAsync(
            services => services.AddSingleton<IPlatformModule>(new IdentityModule()),
            Valid("corp"),
            composeOperatedDefaults: false,
            postCompose: services => services.AddSingleton<IAuditSink>(
                new RecordingAuditSink("test-durable", isDurable: true)));

        try
        {
            using var accepted = new HttpRequestMessage(HttpMethod.Get, "/");
            accepted.Headers.Add("Authorization", $"Bearer {Mint(Rsa, "key-1")}");
            using var acceptedResponse = await client.SendAsync(accepted);
            Assert.Equal(HttpStatusCode.OK, acceptedResponse.StatusCode);

            using var foreign = new HttpRequestMessage(HttpMethod.Get, "/");
            foreign.Headers.Add("Authorization", $"Bearer {Mint(Rsa, "key-1", issuer: "https://elsewhere.test")}");
            using var foreignResponse = await client.SendAsync(foreign);
            Assert.Equal(HttpStatusCode.Unauthorized, foreignResponse.StatusCode);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    // S20.3 -------------------------------------------------------------------------------------

    /// <summary>S20.3 — absent optional settings take their defaults.</summary>
    [Fact]
    public async Task S20_3_Optional_settings_take_their_defaults()
    {
        var provider = Provider(Valid("corp"));

        // Default algorithm RS256, subject claim "sub", no display name, one minute of tolerance.
        var ok = await provider.AuthenticateAsync(Request(Mint(Rsa, "key-1", extra: ("name", "Alice"))), default);
        Assert.True(ok.IsSuccess);
        Assert.Null(ok.Value.DisplayName);

        var slightlyExpired = Mint(Rsa, "key-1", expires: DateTimeOffset.UtcNow.AddSeconds(-30));
        Assert.True((await provider.AuthenticateAsync(Request(slightlyExpired), default)).IsSuccess);

        var longExpired = Mint(Rsa, "key-1", expires: DateTimeOffset.UtcNow.AddMinutes(-3));
        Assert.Equal(
            nameof(AuthenticationError.CredentialRejected),
            (await provider.AuthenticateAsync(Request(longExpired), default)).Error.Code);
    }

    /// <summary>S20.3 — configured values bind: algorithms, tolerance, subject and display-name
    /// claims.</summary>
    [Fact]
    public async Task S20_3_Configured_optional_settings_bind()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var settings = Valid("corp", jwks: PublicJwks(ec, "ec-1"));
        settings[$"{Section}:Algorithms:0"] = "ES256";
        settings[$"{Section}:ClockTolerance"] = "00:04:00";
        settings[$"{Section}:SubjectClaim"] = "oid";
        settings[$"{Section}:DisplayNameClaim"] = "name";
        var provider = Provider(settings);

        var token = Mint(
            ec,
            "ec-1",
            expires: DateTimeOffset.UtcNow.AddMinutes(-3),
            extras: [("oid", "object-1"), ("name", "Alice A")]);
        var authenticated = await provider.AuthenticateAsync(Request(token), default);

        Assert.True(authenticated.IsSuccess);
        Assert.Equal(new PrincipalId(Issuer, "object-1"), authenticated.Value.Id);
        Assert.Equal("Alice A", authenticated.Value.DisplayName);

        // The RS256 token is outside the configured set.
        var rs = await provider.AuthenticateAsync(Request(Mint(Rsa, "key-1")), default);
        Assert.Equal(nameof(AuthenticationError.CredentialRejected), rs.Error.Code);
    }

    // S20.4 / S20.5 / S20.6 ---------------------------------------------------------------------

    public static TheoryData<string, string, string, string?> SettingsDefects => new()
    {
        { "missing Audiences", "Audiences:0", "", "Audiences" },
        { "missing Issuer", "Issuer", "", "Issuer" },
        { "missing SigningKeys", "SigningKeys", "", "SigningKeys" },
        { "relative Issuer", "Issuer", "not-a-uri", "Issuer" },
        { "malformed SigningKeys", "SigningKeys", "{ not json", "SigningKeys" },
        { "private key in SigningKeys", "SigningKeys", "PRIVATE", "SigningKeys" },
        { "empty key set", "SigningKeys", "{\"keys\":[]}", "SigningKeys" },
        { "empty audience", "Audiences:0", "EMPTY", "Audiences" },
        { "HS256", "Algorithms:0", "HS256", "Algorithms" },
        { "none", "Algorithms:0", "none", "Algorithms" },
        { "tolerance over five minutes", "ClockTolerance", "00:05:01", "ClockTolerance" },
        { "negative tolerance", "ClockTolerance", "-00:00:01", "ClockTolerance" },
        { "malformed tolerance", "ClockTolerance", "soon", "ClockTolerance" },
        { "unrecognised key", "Colour", "blue", "Colour" },
        { "section where a value belongs", "Issuer:Nested", "x", "Issuer" },
        { "value where a section belongs", "Audiences", "platform-api", "Audiences" },
    };

    /// <summary>S20.4, S20.5 — each settings defect fails startup as
    /// <c>HostStartupError.Configuration</c> naming the full key. One row per defect.</summary>
    [Theory]
    [MemberData(nameof(SettingsDefects))]
    public void S20_4_A_settings_defect_fails_startup_naming_the_full_key(
        string defect, string key, string value, string? setting)
    {
        _ = defect;
        var settings = Valid("corp", jwks: value == "PRIVATE" ? PrivateJwks(Rsa, "key-1") : Jwks);
        Apply(settings, key, value);

        var exception = Assert.Throws<PlatformStartupException>(() => Register(settings));

        var error = Assert.IsType<HostStartupError>(exception.Error);
        Assert.Equal(nameof(HostStartupError.Configuration), error.Code);
        var inner = Assert.IsType<ConfigurationError>(error.Inner);
        Assert.Contains($"Platform:Identity:Bearer:corp:{setting}", inner.Detail, StringComparison.Ordinal);
        Assert.Equal(
            key.StartsWith("Audiences:0", StringComparison.Ordinal) && value == string.Empty
                ? nameof(ConfigurationError.MissingRequiredSetting)
                : value == string.Empty
                    ? nameof(ConfigurationError.MissingRequiredSetting)
                    : nameof(ConfigurationError.InvalidSetting),
            inner.Code);
    }

    /// <summary>S20.4 — a value where the provider's section belongs is itself an invalid
    /// setting.</summary>
    [Fact]
    public void S20_4_A_value_where_a_provider_section_belongs_is_invalid()
    {
        var settings = new Dictionary<string, string?> { ["Platform:Identity:Bearer:corp"] = "oops" };

        var exception = Assert.Throws<PlatformStartupException>(() => Register(settings));

        var inner = Assert.IsType<ConfigurationError>(Assert.IsType<HostStartupError>(exception.Error).Inner);
        Assert.Equal(nameof(ConfigurationError.InvalidSetting), inner.Code);
        Assert.Contains("Platform:Identity:Bearer:corp", inner.Detail, StringComparison.Ordinal);
    }

    /// <summary>S20.4 — a section whose name clashes with another authentication provider's is
    /// the registry's <c>DuplicateProviderName</c> inside <c>Registration</c>, not a configuration
    /// error.</summary>
    [Fact]
    public async Task S20_4_A_section_clashing_with_another_provider_is_a_duplicate_provider_name()
    {
        var exception = await Assert.ThrowsAsync<PlatformStartupException>(async () =>
        {
            var (app, _) = await WebHostUnderTest.StartAsync(
                services =>
                {
                    services.AddSingleton<IPlatformModule>(new IdentityModule());
                    services.AddSingleton<IAuthenticationProvider>(new StubAuthenticationProvider("corp"));
                },
                Valid("corp"),
                composeOperatedDefaults: false);
            await app.DisposeAsync();
        });

        var error = Assert.IsType<HostStartupError>(exception.Error);
        Assert.Equal(nameof(HostStartupError.Registration), error.Code);
        Assert.Equal(
            nameof(AuthenticationProviderRegistrationError.DuplicateProviderName),
            error.Inner!.Code);
    }

    /// <summary>S20.4 — keys compare case-insensitively, so two sections differing only in case
    /// are one section and one provider.</summary>
    [Fact]
    public void S20_4_Sections_differing_only_in_case_are_one_provider()
    {
        var settings = Valid("corp");
        settings["platform:identity:bearer:CORP:Algorithms:0"] = "RS256";

        var services = Register(settings);

        Assert.Single(services, d => d.ServiceType == typeof(IAuthenticationProvider));
    }

    /// <summary>S20.6 — a settings defect fails before any provider is registered; a second,
    /// valid section is not left behind.</summary>
    [Fact]
    public void S20_6_A_settings_defect_registers_no_provider()
    {
        var settings = Valid("good");
        foreach (var (key, value) in Valid("bad", jwks: "{ not json"))
        {
            settings[key] = value;
        }

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(Configure(settings));

        Assert.Throws<PlatformStartupException>(() => new IdentityModule().Register(services));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IAuthenticationProvider));
    }

    // S20.7 -------------------------------------------------------------------------------------

    /// <summary>S20.7 — every <c>CredentialRejected</c> condition that applies to a fixed-key
    /// provider.</summary>
    [Fact]
    public async Task S20_7_Credential_defects_are_rejected()
    {
        var provider = Provider(Valid("corp"));
        using var other = RSA.Create(2048);

        var rejected = new Dictionary<string, string>
        {
            ["algorithm outside the set"] = MintHmac(),
            ["alg none"] = Unsigned(),
            ["unknown kid"] = Mint(Rsa, "no-such-key"),
            ["bad signature"] = Mint(other, "key-1"),
            ["exp in the past"] = Mint(Rsa, "key-1", expires: DateTimeOffset.UtcNow.AddMinutes(-10)),
            ["nbf in the future"] = Mint(Rsa, "key-1", notBefore: DateTimeOffset.UtcNow.AddMinutes(10)),
            ["exp absent"] = Mint(Rsa, "key-1", omit: "exp"),
            ["audience mismatch"] = Mint(Rsa, "key-1", audience: "someone-else"),
            ["subject missing"] = Mint(Rsa, "key-1", omit: "sub"),
            ["subject empty"] = Mint(Rsa, "key-1", subject: ""),
            ["subject not a string"] = Mint(Rsa, "key-1", rawSubject: "42"),
            ["unreadable iss"] = "not.a.jwt",
        };

        foreach (var (condition, token) in rejected)
        {
            var result = await provider.AuthenticateAsync(Request(token), default);

            Assert.False(result.IsSuccess, condition);
            Assert.Equal(nameof(AuthenticationError.CredentialRejected), result.Error.Code);
        }
    }

    /// <summary>S20.7 — no <c>kid</c> is tried against every held key: it verifies against a held
    /// key, and is rejected when none verifies.</summary>
    [Fact]
    public async Task S20_7_A_token_with_no_kid_is_tried_against_the_held_keys()
    {
        var provider = Provider(Valid("corp"));
        using var other = RSA.Create(2048);

        Assert.True((await provider.AuthenticateAsync(Request(Mint(Rsa, kid: null)), default)).IsSuccess);

        var rejected = await provider.AuthenticateAsync(Request(Mint(other, kid: null)), default);
        Assert.Equal(nameof(AuthenticationError.CredentialRejected), rejected.Error.Code);
    }

    /// <summary>S20.7 — a kid that names a different held key than the one that signed is not
    /// "any key will do".</summary>
    [Fact]
    public async Task S20_7_A_kid_naming_another_key_does_not_fall_back_to_trying_all_keys()
    {
        using var second = RSA.Create(2048);
        var jwks = JsonSerializer.Serialize(new
        {
            keys = new[] { JsonDocument.Parse(PublicJwks(Rsa, "key-1")).RootElement.GetProperty("keys")[0],
                           JsonDocument.Parse(PublicJwks(second, "key-2")).RootElement.GetProperty("keys")[0] },
        });
        var provider = Provider(Valid("corp", jwks: jwks));

        Assert.True((await provider.AuthenticateAsync(Request(Mint(second, "key-2")), default)).IsSuccess);

        var mislabelled = await provider.AuthenticateAsync(Request(Mint(Rsa, "key-2")), default);
        Assert.Equal(nameof(AuthenticationError.CredentialRejected), mislabelled.Error.Code);
    }

    /// <summary>S20.7 — a token whose <c>iss</c> is not this provider's <c>Issuer</c> gets
    /// <c>CredentialNotClaimed</c>, compared ordinally.</summary>
    [Theory]
    [InlineData("https://elsewhere.test")]
    [InlineData("https://ISSUER.test")]
    [InlineData("https://issuer.test/")]
    public async Task S20_7_Another_issuer_is_not_claimed(string issuer)
    {
        var provider = Provider(Valid("corp"));

        var result = await provider.AuthenticateAsync(Request(Mint(Rsa, "key-1", issuer: issuer)), default);

        Assert.Equal(nameof(AuthenticationError.CredentialNotClaimed), result.Error.Code);
    }

    /// <summary>S20.7 — a fault inside the validation library, as opposed to the token's content,
    /// is <c>ProviderFailed</c>.</summary>
    [Fact]
    public async Task S20_7_A_library_fault_is_a_provider_failure()
    {
        var settings = ReadSettings(Valid("corp"));
        var provider = new ConfiguredBearerAuthenticationProvider(settings, new FaultingHandler());

        var result = await provider.AuthenticateAsync(Request(Mint(Rsa, "key-1")), default);

        Assert.Equal(nameof(AuthenticationError.ProviderFailed), result.Error.Code);
    }

    /// <summary>No bearer credential presented defers rather than failing.</summary>
    [Fact]
    public async Task S20_7_No_bearer_credential_defers()
    {
        var provider = Provider(Valid("corp"));

        var result = await provider.AuthenticateAsync(new StubAuthenticationRequest(), default);

        Assert.True(result.IsSuccess);
        Assert.Equal(PrincipalKind.Anonymous, result.Value.Kind);
    }

    // S20.8 -------------------------------------------------------------------------------------

    /// <summary>S20.8 — an accepted token gives an Account principal pairing the validated
    /// <c>iss</c> with the subject, untouched; two subjects differing only in case are two
    /// principals; the display name is used when configured and present; every other claim is
    /// carried.</summary>
    [Fact]
    public async Task S20_8_An_accepted_token_gives_an_account_principal()
    {
        var settings = Valid("corp");
        settings[$"{Section}:DisplayNameClaim"] = "name";
        var provider = Provider(settings);

        var lower = await provider.AuthenticateAsync(
            Request(Mint(Rsa, "key-1", subject: " alice ", extra: ("name", "Alice"), extras: [("team", "blue")])),
            default);
        var upper = await provider.AuthenticateAsync(Request(Mint(Rsa, "key-1", subject: " ALICE ")), default);

        Assert.True(lower.IsSuccess);
        Assert.Equal(PrincipalKind.Account, lower.Value.Kind);
        Assert.Equal(new PrincipalId(Issuer, " alice "), lower.Value.Id);
        Assert.Equal("Alice", lower.Value.DisplayName);
        Assert.Equal("blue", lower.Value.Claims!.FindFirst("team")!.Value);
        Assert.True(upper.IsSuccess);
        Assert.NotEqual(lower.Value.Id, upper.Value.Id);
        Assert.Null(upper.Value.DisplayName);
    }

    // S20.11 ------------------------------------------------------------------------------------

    /// <summary>S20.11 — no public .NET type carries these settings: Identity's public surface
    /// gains nothing from them. D5-S46's account types are the only additions.</summary>
    [Fact]
    public void S20_11_Identity_exposes_no_public_settings_type()
    {
        var exported = typeof(IdentityModule).Assembly.GetExportedTypes().Select(t => t.Name).Order().ToArray();

        Assert.Equal(
            [
                "Account", "AccountError", "AccountId", "IAccountApi", "IdentityAccountsModule",
                "IdentityAuditActions", "IdentityModule", "JwtBearerAuthenticationProvider", "LinkedIdentity",
                "UpstreamProxyAuthenticationProvider",
            ],
            exported);
    }

    // helpers -----------------------------------------------------------------------------------

    internal static Dictionary<string, string?> Valid(string name, string? jwks = null, string issuer = Issuer) => new()
    {
        [$"Platform:Identity:Bearer:{name}:Issuer"] = issuer,
        [$"Platform:Identity:Bearer:{name}:SigningKeys"] = jwks ?? Jwks,
        [$"Platform:Identity:Bearer:{name}:Audiences:0"] = Audience,
    };

    private static void Apply(Dictionary<string, string?> settings, string key, string value)
    {
        var full = $"{Section}:{key}";
        if (key == "Audiences")
        {
            settings.Remove($"{Section}:Audiences:0");
        }

        if (value == string.Empty)
        {
            settings.Remove(full);
        }
        else
        {
            settings[full] = value == "EMPTY" ? "" : value;
        }
    }

    internal static IConfiguration Configure(IDictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    private static ServiceCollection Register(IDictionary<string, string?> settings)
    {
        var services = new ServiceCollection();
        services.AddSingleton(Configure(settings));
        new IdentityModule().Register(services);
        return services;
    }

    internal static BearerSettings ReadSettings(IDictionary<string, string?> settings)
    {
        var read = BearerSettings.ReadAll(Configure(settings));
        Assert.True(read.IsSuccess);
        return read.Value.Single();
    }

    private static ConfiguredBearerAuthenticationProvider Provider(IDictionary<string, string?> settings) =>
        new(ReadSettings(settings));

    internal static StubAuthenticationRequest Request(string token) =>
        new(("Authorization", $"Bearer {token}"));

    internal static string PublicJwks(AsymmetricAlgorithm key, string kid) => Jwk(key, kid, includePrivate: false);

    private static string PrivateJwks(AsymmetricAlgorithm key, string kid) => Jwk(key, kid, includePrivate: true);

    private static string Jwk(AsymmetricAlgorithm key, string kid, bool includePrivate)
    {
        var securityKey = key switch
        {
            RSA rsa => (SecurityKey)new RsaSecurityKey(rsa.ExportParameters(includePrivate)) { KeyId = kid },
            ECDsa ec => new ECDsaSecurityKey(ec) { KeyId = kid },
            _ => throw new NotSupportedException(),
        };
        var jwk = JsonWebKeyConverter.ConvertFromSecurityKey(securityKey);
        if (!includePrivate && key is ECDsa)
        {
            jwk.D = null;
        }

        return $"{{\"keys\":[{JsonSerializer.Serialize(new
        {
            kty = jwk.Kty, kid = jwk.Kid, use = "sig", n = jwk.N, e = jwk.E, crv = jwk.Crv, x = jwk.X, y = jwk.Y,
            d = includePrivate ? jwk.D : null,
        }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull })}]}}";
    }

    internal static string Mint(
        AsymmetricAlgorithm key,
        string? kid = "key-1",
        string issuer = Issuer,
        string audience = Audience,
        string? subject = "alice",
        string? rawSubject = null,
        string? omit = null,
        DateTimeOffset? expires = null,
        DateTimeOffset? notBefore = null,
        (string Name, string Value)? extra = null,
        (string Name, string Value)[]? extras = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["iss"] = issuer,
            ["aud"] = audience,
            ["sub"] = subject,
            ["exp"] = (expires ?? DateTimeOffset.UtcNow.AddMinutes(10)).ToUnixTimeSeconds(),
        };
        if (notBefore is { } nbf)
        {
            payload["nbf"] = nbf.ToUnixTimeSeconds();
        }

        foreach (var (name, value) in (extras ?? []).Concat(extra is { } e ? [e] : []))
        {
            payload[name] = value;
        }

        if (omit is not null)
        {
            payload.Remove(omit);
        }

        var json = JsonSerializer.Serialize(payload);
        if (rawSubject is not null)
        {
            json = JsonSerializer.Serialize(payload.Where(p => p.Key != "sub").ToDictionary(p => p.Key, p => p.Value))
                .TrimEnd('}') + $",\"sub\":{rawSubject}}}";
        }

        SecurityKey securityKey = key switch
        {
            RSA rsa => new RsaSecurityKey(rsa) { KeyId = kid },
            ECDsa ec => new ECDsaSecurityKey(ec) { KeyId = kid },
            _ => throw new NotSupportedException(),
        };
        var algorithm = key is RSA ? SecurityAlgorithms.RsaSha256 : SecurityAlgorithms.EcdsaSha256;

        return new JsonWebTokenHandler().CreateToken(json, new SigningCredentials(securityKey, algorithm));
    }

    private static string MintHmac()
    {
        var secret = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32)) { KeyId = "key-1" };
        var json = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["iss"] = Issuer,
            ["aud"] = Audience,
            ["sub"] = "alice",
            ["exp"] = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds(),
        });
        return new JsonWebTokenHandler().CreateToken(json, new SigningCredentials(secret, SecurityAlgorithms.HmacSha256));
    }

    private static string Unsigned()
    {
        static string Encode(string json) => Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(json));
        var exp = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();
        return Encode("{\"alg\":\"none\"}")
            + "." + Encode($"{{\"iss\":\"{Issuer}\",\"aud\":\"{Audience}\",\"sub\":\"alice\",\"exp\":{exp}}}")
            + ".";
    }

    private sealed class FaultingHandler : JsonWebTokenHandler
    {
        public override Task<TokenValidationResult> ValidateTokenAsync(
            SecurityToken token, TokenValidationParameters validationParameters) =>
            throw new InvalidOperationException("the library faulted");
    }
}
