using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Hosting;
using SubZeroDev.Platform.Identity;

namespace SubZeroDev.Platform.Tests;

/// <summary>D5-S21: keys are found through discovery and kept fresh. Every outbound request is
/// observed through <see cref="FakeDiscovery"/>, the only connection a discovery fetch can use.</summary>
public sealed class KeyDiscoveryTests
{
    private const string Issuer = ConfiguredBearerTests.Issuer;
    private const string DiscoveryUrl = "https://issuer.test/.well-known/openid-configuration";
    private const string JwksUrl = "https://issuer.test/jwks";
    private const string Section = "Platform:Identity:Bearer:corp";

    private static readonly RSA Rsa = ConfiguredBearerTests.Rsa;

    // S21.1 -------------------------------------------------------------------------------------

    /// <summary>S21.1 — Discovery and KeyRefreshInterval bind; the interval defaults to five minutes.</summary>
    [Fact]
    public void S21_1_Discovery_and_KeyRefreshInterval_bind_with_a_five_minute_default()
    {
        var defaulted = ConfiguredBearerTests.ReadSettings(Discovered());
        Assert.Equal(new Uri(DiscoveryUrl), defaulted.Discovery);
        Assert.Null(defaulted.SigningKeys);
        Assert.Equal(TimeSpan.FromMinutes(5), defaulted.KeyRefreshInterval);

        var configured = Discovered();
        configured[$"{Section}:KeyRefreshInterval"] = "00:01:00";
        Assert.Equal(TimeSpan.FromMinutes(1), ConfiguredBearerTests.ReadSettings(configured).KeyRefreshInterval);
    }

    /// <summary>S21.1 — the interval's bounds are inclusive: thirty seconds to one day.</summary>
    [Theory]
    [InlineData("00:00:30")]
    [InlineData("1.00:00:00")]
    public void S21_1_The_interval_bounds_are_inclusive(string interval)
    {
        var settings = Discovered();
        settings[$"{Section}:KeyRefreshInterval"] = interval;

        Assert.Equal(TimeSpan.Parse(interval, System.Globalization.CultureInfo.InvariantCulture),
            ConfiguredBearerTests.ReadSettings(settings).KeyRefreshInterval);
    }

    // S21.2 -------------------------------------------------------------------------------------

    public static TheoryData<string, string, string, string> SettingsDefects => new()
    {
        { "Discovery with SigningKeys", "SigningKeys", "KEYS", nameof(ConfigurationError.InconsistentSettings) },
        { "KeyRefreshInterval with SigningKeys", "KeyRefreshInterval", "00:01:00", nameof(ConfigurationError.InconsistentSettings) },
        { "neither Discovery nor SigningKeys", "Discovery", "", nameof(ConfigurationError.MissingRequiredSetting) },
        { "malformed Discovery", "Discovery", "not-a-uri", nameof(ConfigurationError.InvalidSetting) },
        { "plain-http Discovery", "Discovery", "http://issuer.test/.well-known/openid-configuration", nameof(ConfigurationError.InvalidSetting) },
        { "interval below thirty seconds", "KeyRefreshInterval", "00:00:29", nameof(ConfigurationError.InvalidSetting) },
        { "interval above one day", "KeyRefreshInterval", "1.00:00:01", nameof(ConfigurationError.InvalidSetting) },
        { "malformed interval", "KeyRefreshInterval", "soon", nameof(ConfigurationError.InvalidSetting) },
    };

    /// <summary>S21.2 — each settings defect fails startup as <c>HostStartupError.Configuration</c>
    /// naming the full key. One row per defect.</summary>
    [Theory]
    [MemberData(nameof(SettingsDefects))]
    public void S21_2_A_settings_defect_fails_startup_naming_the_full_key(
        string defect, string key, string value, string code)
    {
        _ = defect;
        var settings = Discovered();
        if (key == "KeyRefreshInterval" && code == nameof(ConfigurationError.InconsistentSettings))
        {
            settings.Remove($"{Section}:Discovery");
            settings[$"{Section}:SigningKeys"] = ConfiguredBearerTests.Jwks;
        }

        if (value == "KEYS")
        {
            value = ConfiguredBearerTests.Jwks;
        }

        if (value == string.Empty)
        {
            settings.Remove($"{Section}:{key}");
        }
        else
        {
            settings[$"{Section}:{key}"] = value;
        }

        var exception = Assert.Throws<PlatformStartupException>(() => Register(settings));

        var error = Assert.IsType<HostStartupError>(exception.Error);
        Assert.Equal(nameof(HostStartupError.Configuration), error.Code);
        var inner = Assert.IsType<ConfigurationError>(error.Inner);
        Assert.Equal(code, inner.Code);
        Assert.Contains($"{Section}:{key}", inner.Detail, StringComparison.Ordinal);
    }

    // S21.3 / S21.4 -----------------------------------------------------------------------------

    /// <summary>S21.3 — the first fetch happens at startup, so a token verifies against the fetched
    /// key without any request having triggered a fetch.</summary>
    [Fact]
    public async Task S21_3_The_first_fetch_happens_at_startup()
    {
        var fake = FakeDiscovery.Serving(Rsa, "key-1");

        var (app, client) = await StartAsync(fake);
        try
        {
            Assert.Equal([DiscoveryUrl, JwksUrl], fake.Requested);

            using var response = await client.SendAsync(Authorized(ConfiguredBearerTests.Mint(Rsa, "key-1")));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    /// <summary>S21.3 — a failed first fetch (an error status, an exception) never fails startup: the
    /// provider holds no keys and answers a credential it claims with <c>KeyMaterialUnavailable</c>.</summary>
    [Theory]
    [InlineData("status")]
    [InlineData("exception")]
    [InlineData("garbage")]
    public async Task S21_3_A_failed_first_fetch_starts_the_host_with_no_keys(string failure)
    {
        var fake = new FakeDiscovery((_, _) => failure switch
        {
            "status" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)),
            "exception" => throw new HttpRequestException("unreachable"),
            _ => Task.FromResult(Json("<html>not json</html>")),
        });

        var (app, client) = await StartAsync(fake);
        try
        {
            using var response = await client.SendAsync(Authorized(ConfiguredBearerTests.Mint(Rsa, "key-1")));

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Contains(
                nameof(AuthenticationError.KeyMaterialUnavailable),
                await response.Content.ReadAsStringAsync(),
                StringComparison.Ordinal);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    /// <summary>S21.3 — a fetch that never answers is abandoned at the fixed bound, which is thirty
    /// seconds and not a setting. The default bound is asserted as a value; the abandonment is
    /// exercised with a shorter one passed to the fetcher alone.</summary>
    [Fact]
    public async Task S21_3_A_fetch_that_never_answers_is_abandoned_at_the_fixed_bound()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), KeyFetcher.Timeout);

        var fake = new FakeDiscovery(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var cache = new KeySetCache();

        await KeyFetcher.RefreshAsync(
            Settings(), cache, fake.Transport, CancellationToken.None, TimeSpan.FromMilliseconds(100));

        Assert.Null(cache.Current);
        Assert.True(cache.LastFetchFailed);
    }

    /// <summary>S21.4 — a discovery document whose <c>issuer</c> is not the provider's
    /// <c>Issuer</c> is a failed fetch, and no key from it is cached.</summary>
    [Theory]
    [InlineData("https://elsewhere.test")]
    [InlineData("https://ISSUER.test")]
    [InlineData("https://issuer.test/")]
    public async Task S21_4_A_document_naming_another_issuer_caches_nothing(string documentIssuer)
    {
        var fake = FakeDiscovery.Serving(Rsa, "key-1", documentIssuer);
        var cache = new KeySetCache();

        await KeyFetcher.RefreshAsync(Settings(), cache, fake.Transport, CancellationToken.None);

        Assert.Null(cache.Current);
        Assert.True(cache.LastFetchFailed);
        Assert.DoesNotContain(JwksUrl, fake.Requested);
    }

    // S22.5 -------------------------------------------------------------------------------------

    /// <summary>S22.5 — for a pattern provider the discovery document's <c>issuer</c> must equal the
    /// pattern string itself, and then its keys are cached.</summary>
    [Fact]
    public async Task S22_5_A_document_naming_the_pattern_is_accepted()
    {
        var fake = FakeDiscovery.Serving(Rsa, "key-1", IssuerPatternTests.Pattern);
        var cache = new KeySetCache();

        await KeyFetcher.RefreshAsync(PatternedSettings(), cache, fake.Transport, CancellationToken.None);

        Assert.Equal(["key-1"], Kids(cache));
        Assert.False(cache.LastFetchFailed);
    }

    /// <summary>S22.5 — any other <c>issuer</c> is a failed fetch (S21.4), including a concrete
    /// issuer the pattern would match.</summary>
    [Theory]
    [InlineData("https://acme.issuer.test")]
    [InlineData("https://{TENANTID}.issuer.test")]
    [InlineData("https://{tenantid}.issuer.test/")]
    [InlineData("https://elsewhere.test")]
    public async Task S22_5_A_document_naming_anything_else_caches_nothing(string documentIssuer)
    {
        var fake = FakeDiscovery.Serving(Rsa, "key-1", documentIssuer);
        var cache = new KeySetCache();

        await KeyFetcher.RefreshAsync(PatternedSettings(), cache, fake.Transport, CancellationToken.None);

        Assert.Null(cache.Current);
        Assert.True(cache.LastFetchFailed);
        Assert.DoesNotContain(JwksUrl, fake.Requested);
    }

    // S21.5 -------------------------------------------------------------------------------------

    /// <summary>S21.5 — a discovery provider registers one refresh work item, named for it, in both
    /// roles, at its interval, taking no lease; a fixed-key provider registers none.</summary>
    [Fact]
    public async Task S21_5_A_discovery_provider_registers_one_leaseless_refresh_work_item()
    {
        var settings = Discovered();
        settings[$"{Section}:KeyRefreshInterval"] = "00:02:00";
        foreach (var (key, value) in ConfiguredBearerTests.Valid("fixed", issuer: "https://fixed.test"))
        {
            settings[key] = value;
        }

        var (app, _) = await StartAsync(FakeDiscovery.Serving(Rsa, "key-1"), settings);
        try
        {
            var work = app.Services.GetServices<IBackgroundWork>()
                .Where(w => w.Name.Value.StartsWith("platform.identity.key-refresh:", StringComparison.Ordinal))
                .Single();

            Assert.Equal("platform.identity.key-refresh:corp", work.Name.Value);
            Assert.Equal(HostRoles.Both, work.Roles);
            Assert.Equal(TimeSpan.FromMinutes(2), work.Interval);
            Assert.False(work.RequiresLease);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    // S21.6 -------------------------------------------------------------------------------------

    /// <summary>S21.6 — a successful refresh replaces the whole set; a failed one keeps the previous
    /// set, so the cache is never emptied.</summary>
    [Fact]
    public async Task S21_6_A_refresh_replaces_the_whole_set_and_a_failure_keeps_it()
    {
        using var second = RSA.Create(2048);
        var fake = FakeDiscovery.Serving(Rsa, "key-1");
        var cache = new KeySetCache();
        var work = new KeyRefreshWork(Settings(), cache, fake.Transport);

        await work.TickAsync(CancellationToken.None);
        Assert.Equal(["key-1"], Kids(cache));

        fake.ServeKeys(second, "key-2");
        await work.TickAsync(CancellationToken.None);
        Assert.Equal(["key-2"], Kids(cache));
        Assert.False(cache.LastFetchFailed);

        var held = cache.Current;
        fake.Fail();
        await work.TickAsync(CancellationToken.None);

        Assert.Same(held, cache.Current);
        Assert.Equal(["key-2"], Kids(cache));
        Assert.True(cache.LastFetchFailed);
    }

    /// <summary>S21.6 — a rotated-out key stops verifying once the refresh lands, and the new one
    /// starts to, with no restart.</summary>
    [Fact]
    public async Task S21_6_A_rotated_key_takes_effect_on_the_next_refresh()
    {
        using var second = RSA.Create(2048);
        var fake = FakeDiscovery.Serving(Rsa, "key-1");
        var cache = new KeySetCache();
        var work = new KeyRefreshWork(Settings(), cache, fake.Transport);
        var provider = new ConfiguredBearerAuthenticationProvider(Settings(), cache: cache);
        await work.TickAsync(CancellationToken.None);

        Assert.True((await provider.AuthenticateAsync(
            ConfiguredBearerTests.Request(ConfiguredBearerTests.Mint(Rsa, "key-1")), default)).IsSuccess);

        fake.ServeKeys(second, "key-2");
        await work.TickAsync(CancellationToken.None);

        Assert.False((await provider.AuthenticateAsync(
            ConfiguredBearerTests.Request(ConfiguredBearerTests.Mint(Rsa, "key-1")), default)).IsSuccess);
        Assert.True((await provider.AuthenticateAsync(
            ConfiguredBearerTests.Request(ConfiguredBearerTests.Mint(second, "key-2")), default)).IsSuccess);
    }

    // S21.7 -------------------------------------------------------------------------------------

    /// <summary>S21.7 — the readiness check is Unhealthy with no cached set, Degraded with one whose
    /// last fetch failed, and Healthy with one whose last fetch succeeded.</summary>
    [Fact]
    public async Task S21_7_Key_set_readiness_follows_the_cache()
    {
        var fake = FakeDiscovery.Serving(Rsa, "key-1");
        var cache = new KeySetCache();
        var check = new KeySetHealthCheck("corp", cache);
        var work = new KeyRefreshWork(Settings(), cache, fake.Transport);

        Assert.Equal("platform.identity.key-set:corp", check.Name.Value);
        Assert.Equal(HealthCheckKind.Readiness, check.Kind);
        Assert.Equal(HealthCheckCriticality.Required, check.Criticality);

        Assert.Equal(HealthStatus.Unhealthy, (await check.CheckAsync(default)).Status);

        fake.Fail();
        await work.TickAsync(CancellationToken.None);
        Assert.Equal(HealthStatus.Unhealthy, (await check.CheckAsync(default)).Status);

        fake.ServeKeys(Rsa, "key-1");
        await work.TickAsync(CancellationToken.None);
        Assert.Equal(HealthStatus.Healthy, (await check.CheckAsync(default)).Status);

        fake.Fail();
        await work.TickAsync(CancellationToken.None);
        Assert.Equal(HealthStatus.Degraded, (await check.CheckAsync(default)).Status);
    }

    /// <summary>S21.7 — a fixed-key provider is always healthy; registered checks are the ones the
    /// host reports.</summary>
    [Fact]
    public async Task S21_7_A_fixed_key_provider_is_always_healthy_and_checks_are_registered()
    {
        var settings = Discovered();
        foreach (var (key, value) in ConfiguredBearerTests.Valid("fixed", issuer: "https://fixed.test"))
        {
            settings[key] = value;
        }

        var (app, _) = await StartAsync(FakeDiscovery.Serving(Rsa, "key-1"), settings);
        try
        {
            var checks = app.Services.GetServices<IHealthCheck>()
                .Where(c => c.Name.Value.StartsWith("platform.identity.key-set:", StringComparison.Ordinal))
                .ToDictionary(c => c.Name.Value);

            Assert.Equal(["platform.identity.key-set:corp", "platform.identity.key-set:fixed"], checks.Keys.Order());
            Assert.Equal(HealthStatus.Healthy, (await checks["platform.identity.key-set:fixed"].CheckAsync(default)).Status);
            Assert.Equal(HealthStatus.Healthy, (await checks["platform.identity.key-set:corp"].CheckAsync(default)).Status);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    // S21.8 -------------------------------------------------------------------------------------

    /// <summary>S21.8 — a token naming a kid the cached set lacks is <c>CredentialRejected</c> and
    /// issues no fetch; a verifying token issues none either. Asserted on the outbound requests
    /// observed across the whole request.</summary>
    [Fact]
    public async Task S21_8_A_request_never_fetches_key_material()
    {
        var fake = FakeDiscovery.Serving(Rsa, "key-1");

        var (app, client) = await StartAsync(fake);
        try
        {
            var afterStartup = fake.Requested.Count;

            using var unknown = await client.SendAsync(Authorized(ConfiguredBearerTests.Mint(Rsa, "rotated-in")));
            Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
            Assert.Contains(
                nameof(AuthenticationError.CredentialRejected),
                await unknown.Content.ReadAsStringAsync(),
                StringComparison.Ordinal);

            using var known = await client.SendAsync(Authorized(ConfiguredBearerTests.Mint(Rsa, "key-1")));
            Assert.Equal(HttpStatusCode.OK, known.StatusCode);

            Assert.Equal(afterStartup, fake.Requested.Count);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    /// <summary>S21.8, S21.9 — with no cached key the request fails end to end as unauthenticated
    /// with <c>KeyMaterialUnavailable</c>, never as a server error, and still issues no fetch of its
    /// own.</summary>
    [Fact]
    public async Task S21_9_No_cached_key_fails_end_to_end_without_fetching_on_the_request()
    {
        var fake = new FakeDiscovery((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));

        var (app, client) = await StartAsync(fake);
        try
        {
            var afterStartup = fake.Requested.Count;

            using var response = await client.SendAsync(Authorized(ConfiguredBearerTests.Mint(Rsa, "key-1")));

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Contains(
                nameof(AuthenticationError.KeyMaterialUnavailable),
                await response.Content.ReadAsStringAsync(),
                StringComparison.Ordinal);
            Assert.Equal(afterStartup, fake.Requested.Count);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    // helpers -----------------------------------------------------------------------------------

    private static Dictionary<string, string?> Discovered() => new()
    {
        [$"{Section}:Issuer"] = Issuer,
        [$"{Section}:Discovery"] = DiscoveryUrl,
        [$"{Section}:Audiences:0"] = ConfiguredBearerTests.Audience,
    };

    private static BearerSettings Settings() => ConfiguredBearerTests.ReadSettings(Discovered());

    private static BearerSettings PatternedSettings()
    {
        var settings = Discovered();
        settings.Remove($"{Section}:Issuer");
        settings[$"{Section}:IssuerPattern"] = IssuerPatternTests.Pattern;
        return ConfiguredBearerTests.ReadSettings(settings);
    }

    private static string[] Kids(KeySetCache cache) =>
        cache.Current!.Keys.Select(key => key.Kid).Order().ToArray();

    private static HttpRequestMessage Authorized(string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("Authorization", $"Bearer {token}");
        return request;
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static ServiceCollection Register(IDictionary<string, string?> settings)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(ConfiguredBearerTests.Configure(settings));
        new IdentityModule().Register(services);
        return services;
    }

    private static Task<(WebApplication App, HttpClient Client)> StartAsync(
        FakeDiscovery fake, Dictionary<string, string?>? settings = null) =>
        WebHostUnderTest.StartAsync(
            services => services.AddSingleton<IPlatformModule>(new IdentityModule()),
            settings ?? Discovered(),
            composeOperatedDefaults: false,
            postCompose: services =>
            {
                services.AddSingleton<IAuditSink>(new RecordingAuditSink("test-durable", isDurable: true));
                services.AddSingleton(fake.Transport);
            });

    /// <summary>The only connection a discovery fetch can use in these tests. Records every request
    /// and is never disposed by a client that uses it.</summary>
    private sealed class FakeDiscovery
    {
        private readonly List<string> _requested = [];
        private Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;

        internal FakeDiscovery(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        {
            _responder = responder;
            Transport = new DiscoveryTransport(() => new Handler(this));
        }

        internal DiscoveryTransport Transport { get; }

        internal IReadOnlyList<string> Requested
        {
            get
            {
                lock (_requested)
                {
                    return [.. _requested];
                }
            }
        }

        internal static FakeDiscovery Serving(AsymmetricAlgorithm key, string kid, string? documentIssuer = null)
        {
            var fake = new FakeDiscovery((_, _) => throw new InvalidOperationException());
            fake.ServeKeys(key, kid, documentIssuer);
            return fake;
        }

        internal void ServeKeys(AsymmetricAlgorithm key, string kid, string? documentIssuer = null)
        {
            var document = JsonSerializer.Serialize(new { issuer = documentIssuer ?? Issuer, jwks_uri = JwksUrl });
            var keys = ConfiguredBearerTests.PublicJwks(key, kid);
            _responder = (request, _) => Task.FromResult(request.RequestUri!.ToString() switch
            {
                DiscoveryUrl => Json(document),
                JwksUrl => Json(keys),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });
        }

        internal void Fail() =>
            _responder = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));

        private sealed class Handler(FakeDiscovery owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                lock (owner._requested)
                {
                    owner._requested.Add(request.RequestUri!.ToString());
                }

                return await owner._responder(request, cancellationToken);
            }

            protected override void Dispose(bool disposing)
            {
            }
        }
    }
}
