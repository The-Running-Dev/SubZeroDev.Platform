using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SubZeroDev.Platform.Hosting;
using SubZeroDev.Platform.SignIn;
using Vendor.ConfigurationFixture;

namespace SubZeroDev.Platform.Tests;

/// <summary>#94: the optional sign-in module. A fixture issuer answers discovery and the token
/// endpoint through the module's transport seam, so every outbound request is observed and nothing
/// leaves the process.</summary>
public sealed class SignInTests
{
    private const string Issuer = ConfiguredBearerTests.Issuer;
    private const string ClientId = "web-client";
    private const string Callback = "/signin-callback";
    private const string AccessToken = "access-token-value";
    private const string Template = "https://issuer.test/logout?client={client_id}&back={return_to}";

    // begin -------------------------------------------------------------------------------------

    /// <summary>Begin redirects to the issuer's authorize address with a proof key, a state, a nonce and
    /// the configured extra parameter, and stores nothing but an encrypted cookie.</summary>
    [Fact]
    public async Task Begin_redirects_to_the_authorize_address_with_the_proof_key_and_extras()
    {
        await using var host = await SignInHost.StartAsync();

        var response = await host.Client.GetAsync("/signin/corp/begin");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        var query = Query(response);
        Assert.StartsWith("https://issuer.test/authorize?", response.Headers.Location!.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal(ClientId, query["client_id"]);
        Assert.Equal(host.Base + Callback, query["redirect_uri"]);
        Assert.Equal("openid", query["scope"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.False(string.IsNullOrEmpty(query["code_challenge"]));
        Assert.False(string.IsNullOrEmpty(query["state"]));
        Assert.False(string.IsNullOrEmpty(query["nonce"]));
        Assert.Equal("platform-api", query["audience"]);
        Assert.DoesNotContain("client_secret", response.Headers.Location.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), c => c.Contains("httponly", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A hook adds a parameter, and cannot change one the module owns (I-S3).</summary>
    [Fact]
    public async Task The_authorize_hook_adds_parameters_and_cannot_change_the_modules_own()
    {
        await using var host = await SignInHost.StartAsync(hooks: hooks => hooks.AdjustAuthorizeRequest = context =>
        {
            var adjusted = new Dictionary<string, string>(context.Parameters)
            {
                ["prompt"] = "login",
                ["client_id"] = "someone-else",
                ["state"] = "forged",
                ["code_challenge_method"] = "plain",
                ["redirect_uri"] = "https://evil.test/",
            };
            return ValueTask.FromResult<IReadOnlyDictionary<string, string>>(adjusted);
        });

        var response = await host.Client.GetAsync("/signin/corp/begin");

        var query = Query(response);
        Assert.Equal("login", query["prompt"]);
        Assert.Equal(ClientId, query["client_id"]);
        Assert.NotEqual("forged", query["state"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal(host.Base + Callback, query["redirect_uri"]);
    }

    // callback ----------------------------------------------------------------------------------

    /// <summary>I-S1: a callback with no begin cookie creates no session and never reaches the issuer.</summary>
    [Fact]
    public async Task A_callback_without_a_begin_cookie_creates_no_session()
    {
        await using var host = await SignInHost.StartAsync();

        var response = await host.Client.GetAsync($"{Callback}?code=abc&state=anything");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Sign-in failed.", await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain(host.Issuer.Requests, r => r.Method == HttpMethod.Post);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.PostTokenAsync()).StatusCode);
    }

    /// <summary>I-S1: a callback whose state differs from the begin's creates no session and does not
    /// exchange the code.</summary>
    [Fact]
    public async Task A_callback_with_a_different_state_creates_no_session()
    {
        await using var host = await SignInHost.StartAsync();
        _ = await host.BeginAsync();

        var response = await host.Client.GetAsync($"{Callback}?code=abc&state=not-the-state");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(host.Issuer.Requests, r => r.Method == HttpMethod.Post);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.PostTokenAsync()).StatusCode);
    }

    /// <summary>An error returned by the issuer ends the attempt with the one generic sentence.</summary>
    [Fact]
    public async Task A_callback_carrying_an_error_creates_no_session()
    {
        await using var host = await SignInHost.StartAsync();
        var begin = await host.BeginAsync();

        var response = await host.Client.GetAsync($"{Callback}?error=access_denied&state={begin.State}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Sign-in failed.", await response.Content.ReadAsStringAsync());
    }

    /// <summary>A matching callback exchanges the code with the verifier the begin chose (no client
    /// secret), writes a session, and the token endpoint returns the access token.</summary>
    [Fact]
    public async Task A_matching_callback_signs_in_and_the_token_endpoint_returns_the_access_token()
    {
        await using var host = await SignInHost.StartAsync();

        await host.SignInAsync();

        var exchange = Assert.Single(host.Issuer.Requests, r => r.Method == HttpMethod.Post);
        var form = HttpUtility.ParseQueryString(exchange.Body);
        Assert.Equal("authorization_code", form["grant_type"]);
        Assert.Equal("abc", form["code"]);
        Assert.Equal(ClientId, form["client_id"]);
        Assert.Null(form["client_secret"]);
        var expected = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"]!)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(host.LastChallenge, expected);

        var token = await host.PostTokenAsync();
        Assert.Equal(HttpStatusCode.OK, token.StatusCode);
        Assert.Contains("no-store", token.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
        var body = await token.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(AccessToken, body.GetProperty("accessToken").GetString());
        Assert.True(body.GetProperty("expiresAt").GetInt64() > DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    /// <summary>I-S1: the cookie holds the token encrypted, never in the clear.</summary>
    [Fact]
    public async Task The_session_cookie_does_not_carry_the_token_in_the_clear()
    {
        await using var host = await SignInHost.StartAsync();

        await host.SignInAsync();

        var cookies = host.Cookies.GetCookies(new Uri(host.Base)).Select(c => c.Value).ToList();
        Assert.NotEmpty(cookies);
        Assert.All(cookies, value => Assert.DoesNotContain(AccessToken, WebUtility.UrlDecode(value), StringComparison.Ordinal));
    }

    /// <summary>The begin cookie is single use: replaying the same callback fails.</summary>
    [Fact]
    public async Task A_callback_cannot_be_replayed()
    {
        await using var host = await SignInHost.StartAsync();
        var begin = await host.SignInAsync();

        var replay = await host.Client.GetAsync($"{Callback}?code=abc&state={begin.State}");

        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
    }

    /// <summary>A failed exchange is the generic error and writes no session.</summary>
    [Fact]
    public async Task A_refused_code_exchange_creates_no_session()
    {
        await using var host = await SignInHost.StartAsync();
        host.Issuer.RefuseExchange = true;
        var begin = await host.BeginAsync();

        var response = await host.Client.GetAsync($"{Callback}?code=abc&state={begin.State}");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("Sign-in failed.", await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.PostTokenAsync()).StatusCode);
    }

    /// <summary>An id token for another nonce is refused.</summary>
    [Fact]
    public async Task An_id_token_with_a_different_nonce_creates_no_session()
    {
        await using var host = await SignInHost.StartAsync();
        host.Issuer.NonceOverride = "replayed-from-another-flow";
        var begin = await host.BeginAsync();

        var response = await host.Client.GetAsync($"{Callback}?code=abc&state={begin.State}");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.PostTokenAsync()).StatusCode);
    }

    // token -------------------------------------------------------------------------------------

    /// <summary>The token endpoint refuses a call that lacks the anti-forgery header.</summary>
    [Fact]
    public async Task The_token_endpoint_refuses_a_call_without_the_header()
    {
        await using var host = await SignInHost.StartAsync();
        await host.SignInAsync();

        var response = await host.Client.PostAsync("/signin/corp/token", content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>With the header but no session the answer is 401.</summary>
    [Fact]
    public async Task The_token_endpoint_without_a_session_is_unauthorized()
    {
        await using var host = await SignInHost.StartAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.PostTokenAsync()).StatusCode);
    }

    // sign-out ----------------------------------------------------------------------------------

    /// <summary>The template is the end-session address for an issuer whose discovery lists none; the
    /// session is cleared; nothing is sent to the issuer to revoke a token (I-S2).</summary>
    [Fact]
    public async Task Sign_out_uses_the_template_clears_the_session_and_revokes_nothing()
    {
        await using var host = await SignInHost.StartAsync();
        await host.SignInAsync();
        var before = host.Issuer.Requests.Count;

        var response = await host.Client.PostAsync("/signin/corp/signout", content: null);

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        var expected = $"https://issuer.test/logout?client={ClientId}&back={Uri.EscapeDataString(host.Base + "/")}";
        Assert.Equal(expected, response.Headers.Location!.AbsoluteUri);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.PostTokenAsync()).StatusCode);
        Assert.Equal(before, host.Issuer.Requests.Count);
    }

    /// <summary>With no template, an end-session endpoint in discovery is used with the client and the
    /// return address appended.</summary>
    [Fact]
    public async Task Sign_out_without_a_template_uses_the_discovered_end_session_endpoint()
    {
        var settings = SignInHost.Settings();
        settings.Remove("SubZeroDev:SignIn:corp:EndSessionTemplate");
        await using var host = await SignInHost.StartAsync(settings: settings, endSessionInDiscovery: true);

        var response = await host.Client.PostAsync("/signin/corp/signout", content: null);

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.StartsWith("https://issuer.test/endsession?", location.AbsoluteUri, StringComparison.Ordinal);
        var query = HttpUtility.ParseQueryString(location.Query);
        Assert.Equal(ClientId, query["client_id"]);
        Assert.Equal(host.Base + "/", query["post_logout_redirect_uri"]);
    }

    /// <summary>With neither a template nor a discovered endpoint, sign-out ends at the host's own page.</summary>
    [Fact]
    public async Task Sign_out_without_any_end_session_address_returns_to_the_host()
    {
        var settings = SignInHost.Settings();
        settings.Remove("SubZeroDev:SignIn:corp:EndSessionTemplate");
        await using var host = await SignInHost.StartAsync(settings: settings);

        var response = await host.Client.PostAsync("/signin/corp/signout", content: null);

        Assert.Equal(host.Base + "/", response.Headers.Location!.AbsoluteUri);
    }

    /// <summary>A hook replaces the end-session address and is given the one the module built.</summary>
    [Fact]
    public async Task The_end_session_hook_replaces_the_address()
    {
        Uri? seen = null;
        await using var host = await SignInHost.StartAsync(hooks: hooks => hooks.ReplaceEndSessionAddress = context =>
        {
            seen = context.Address;
            return ValueTask.FromResult(new Uri("https://issuer.test/custom-logout?x=1"));
        });

        var response = await host.Client.PostAsync("/signin/corp/signout", content: null);

        Assert.Equal("https://issuer.test/custom-logout?x=1", response.Headers.Location!.AbsoluteUri);
        Assert.StartsWith("https://issuer.test/logout?", seen!.AbsoluteUri, StringComparison.Ordinal);
    }

    // startup validation ------------------------------------------------------------------------

    public static TheoryData<string, string, string?> SettingsDefects => new()
    {
        { "unrecognised key", "ClientSecret", "shh" },
        { "no client id", "ClientId", null },
        { "no issuer", "Issuer", null },
        { "issuer matches no bearer provider", "Issuer", "https://other.test" },
        { "issuer not https", "Issuer", "http://issuer.test" },
        { "no redirect path", "RedirectPath", null },
        { "redirect path not rooted", "RedirectPath", "callback" },
        { "redirect path protocol relative", "RedirectPath", "//evil.test/cb" },
        { "redirect path with a query", "RedirectPath", "/cb?x=1" },
        { "post sign-out path absolute", "PostSignOutPath", "https://evil.test/" },
        { "scopes without openid", "Scopes", "profile email" },
        { "unknown template placeholder", "EndSessionTemplate", "https://issuer.test/logout?x={nope}" },
        { "template not http", "EndSessionTemplate", "javascript:alert(1)" },
        { "placeholder in the template host", "EndSessionTemplate", "https://{client_id}.test/logout" },
        { "extra parameter names a protected one", "ExtraAuthorizeParameters:state", "fixed" },
        { "extra parameter names the nonce", "ExtraAuthorizeParameters:nonce", "fixed" },
    };

    /// <summary>I-I10: each settings defect fails startup as <c>HostStartupError.Configuration</c> naming
    /// the full key.</summary>
    [Theory]
    [MemberData(nameof(SettingsDefects))]
    public void A_settings_defect_fails_startup_naming_the_full_key(string defect, string key, string? value)
    {
        _ = defect;
        var settings = SignInHost.Settings();
        if (value is null)
        {
            settings.Remove($"SubZeroDev:SignIn:corp:{key}");
        }
        else
        {
            settings[$"SubZeroDev:SignIn:corp:{key}"] = value;
        }

        var exception = Assert.Throws<PlatformStartupException>(() => Register(settings));

        var error = Assert.IsType<HostStartupError>(exception.Error);
        Assert.Equal(nameof(HostStartupError.Configuration), error.Code);
        var inner = Assert.IsType<Core.ConfigurationError>(error.Inner);
        Assert.Contains($"SubZeroDev:SignIn:corp:{key}", inner.Detail, StringComparison.Ordinal);
    }

    /// <summary>Two methods may not share a callback path: the callback could not tell whose it was.</summary>
    [Fact]
    public void Two_methods_may_not_share_a_redirect_path()
    {
        var settings = SignInHost.Settings();
        foreach (var pair in SignInHost.Settings().ToList())
        {
            settings[pair.Key.Replace(":corp:", ":second:", StringComparison.Ordinal)] = pair.Value;
        }

        var exception = Assert.Throws<PlatformStartupException>(() => Register(settings));

        var inner = Assert.IsType<Core.ConfigurationError>(Assert.IsType<HostStartupError>(exception.Error).Inner);
        Assert.Contains("RedirectPath", inner.Detail, StringComparison.Ordinal);
    }

    /// <summary>A hook for a method nobody configured would never run; that fails startup.</summary>
    [Fact]
    public void A_hook_for_an_unconfigured_method_fails_startup()
    {
        var exception = Assert.Throws<PlatformStartupException>(() =>
            Register(SignInHost.Settings(), services => services.ConfigurePlatformSignIn("ghost", _ => { })));

        var inner = Assert.IsType<Core.ConfigurationError>(Assert.IsType<HostStartupError>(exception.Error).Inner);
        Assert.Contains("SubZeroDev:SignIn:ghost", inner.Detail, StringComparison.Ordinal);
    }

    /// <summary>A valid method registers without a startup error, and without reaching the issuer.</summary>
    [Fact]
    public void A_valid_method_registers_and_the_issuer_is_not_contacted_at_startup()
    {
        var services = Register(SignInHost.Settings());

        Assert.NotNull(services.BuildServiceProvider().GetService<SignInRuntime>());
    }

    // optional ----------------------------------------------------------------------------------

    /// <summary>The module is optional: a host that does not take it has no sign-in endpoints even when
    /// the settings are present.</summary>
    [Fact]
    public async Task A_host_without_the_module_has_no_sign_in_endpoints()
    {
        var (app, client) = await WebHostUnderTest.StartAsync(settings: SignInHost.Settings());
        await using var _ = app;
        using var __ = client;

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/signin/corp/begin")).StatusCode);
        Assert.Null(app.Services.GetService<SignInRuntime>());
    }

    // vendor configuration ----------------------------------------------------------------------

    /// <summary>The same keys from a vendor's configuration source and from a settings file give equal
    /// validated settings (I-I13, I-I14).</summary>
    [Fact]
    public void Equal_settings_from_a_vendor_source_and_a_settings_file()
    {
        var vendor = new ConfigurationBuilder()
            .AddFixtureFixedKeyIssuer("corp", Issuer, ConfiguredBearerTests.Jwks, ConfiguredBearerTests.Audience)
            .AddFixtureSignInMethod("corp", Issuer, ClientId, Callback, Template, "audience", "platform-api")
            .Build();
        var file = ConfiguredBearerTests.Configure(SignInHost.Settings());

        var fromVendor = SignInSettings.ReadAll(vendor);
        var fromFile = SignInSettings.ReadAll(file);

        Assert.True(fromVendor.IsSuccess);
        Assert.True(fromFile.IsSuccess);
        Assert.Equal(Describe(fromFile.Value.Single()), Describe(fromVendor.Value.Single()));
    }

    // package graph -----------------------------------------------------------------------------

    /// <summary>I-C7, I-I12: the module references the framework packages and the shared framework,
    /// no other module, and no token-validation library.</summary>
    [Fact]
    public void The_sign_in_package_references_no_module_and_no_identity_model()
    {
        var references = typeof(SignInModule).Assembly.GetReferencedAssemblies()
            .Select(name => name.Name!)
            .ToList();

        Assert.DoesNotContain(references, name => name.StartsWith("Microsoft.IdentityModel", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name.StartsWith("System.IdentityModel", StringComparison.Ordinal));
        var platform = references.Where(name => name.StartsWith("SubZeroDev.Platform.", StringComparison.Ordinal)).Order().ToList();
        Assert.Equal(
            ["SubZeroDev.Platform.Abstractions", "SubZeroDev.Platform.Core", "SubZeroDev.Platform.Hosting"],
            platform);
    }

    // helpers -----------------------------------------------------------------------------------

    private static string Describe(SignInSettings s) =>
        string.Join(
            "|",
            s.Name, s.Issuer, s.ClientId, s.RedirectPath, s.Scopes, s.EndSessionTemplate, s.PostSignOutPath,
            string.Join(",", s.ExtraAuthorizeParameters.OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value}")));

    private static ServiceCollection Register(IDictionary<string, string?> settings, Action<IServiceCollection>? more = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(ConfiguredBearerTests.Configure(settings));
        services.AddPlatformSignIn();
        more?.Invoke(services);
        new SignInModule().Register(services);
        return services;
    }

    private static NameValueCollectionView Query(HttpResponseMessage response) =>
        new(HttpUtility.ParseQueryString(response.Headers.Location!.Query));

    /// <summary>A query string read by name, failing loudly on a parameter that is absent.</summary>
    private sealed class NameValueCollectionView(System.Collections.Specialized.NameValueCollection values)
    {
        internal string this[string name] => values[name] ?? throw new KeyNotFoundException(name);
    }

    /// <summary>The fixture issuer: discovery and a token endpoint, with every request recorded.</summary>
    private sealed class FakeIssuer : HttpMessageHandler
    {
        private readonly List<Seen> _requests = [];

        internal bool EndSessionInDiscovery { get; init; }

        internal bool RefuseExchange { get; set; }

        internal string? NonceOverride { get; set; }

        internal string Nonce { get; set; } = string.Empty;

        internal IReadOnlyList<Seen> Requests
        {
            get { lock (_requests) { return _requests.ToList(); } }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (_requests)
            {
                _requests.Add(new Seen(request.Method, request.RequestUri!, body));
            }

            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/.well-known/openid-configuration")
            {
                var document = new Dictionary<string, string>
                {
                    ["issuer"] = Issuer,
                    ["authorization_endpoint"] = Issuer + "/authorize",
                    ["token_endpoint"] = Issuer + "/token",
                };
                if (EndSessionInDiscovery)
                {
                    document["end_session_endpoint"] = Issuer + "/endsession";
                }

                return Json(document);
            }

            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/token" && !RefuseExchange)
            {
                var idToken = Part(new { alg = "none" }) + "."
                    + Part(new
                    {
                        iss = Issuer,
                        aud = ClientId,
                        sub = "alice",
                        nonce = NonceOverride ?? Nonce,
                        exp = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds(),
                    })
                    + ".sig";
                return Json(new { access_token = AccessToken, id_token = idToken, expires_in = 600, token_type = "Bearer" });
            }

            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        }

        private static string Part(object value) =>
            Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static HttpResponseMessage Json(object value) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
            };

        internal sealed record Seen(HttpMethod Method, Uri Address, string Body);
    }

    private sealed record BeginResult(string State, string Nonce, string Challenge);

    /// <summary>A host with the module, a cookie-keeping client that follows no redirect, and the fixture issuer.</summary>
    private sealed class SignInHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private SignInHost(WebApplication app, HttpClient client, CookieContainer cookies, FakeIssuer issuer)
        {
            _app = app;
            Client = client;
            Cookies = cookies;
            Issuer = issuer;
            Base = app.Urls.First().TrimEnd('/');
        }

        internal HttpClient Client { get; }

        internal CookieContainer Cookies { get; }

        internal FakeIssuer Issuer { get; }

        internal string Base { get; }

        internal string? LastChallenge { get; private set; }

        internal static Dictionary<string, string?> Settings()
        {
            var settings = ConfiguredBearerTests.Valid("corp");
            settings["SubZeroDev:SignIn:corp:Issuer"] = SignInTests.Issuer;
            settings["SubZeroDev:SignIn:corp:ClientId"] = ClientId;
            settings["SubZeroDev:SignIn:corp:RedirectPath"] = Callback;
            settings["SubZeroDev:SignIn:corp:EndSessionTemplate"] = Template;
            settings["SubZeroDev:SignIn:corp:ExtraAuthorizeParameters:audience"] = "platform-api";
            return settings;
        }

        internal static async Task<SignInHost> StartAsync(
            Action<SignInHooks>? hooks = null,
            Dictionary<string, string?>? settings = null,
            bool endSessionInDiscovery = false)
        {
            var issuer = new FakeIssuer { EndSessionInDiscovery = endSessionInDiscovery };
            var (app, plain) = await WebHostUnderTest.StartAsync(
                services: services =>
                {
                    services.AddSingleton(new SignInTransport(() => issuer));
                    services.AddPlatformSignIn();
                    if (hooks is not null)
                    {
                        services.ConfigurePlatformSignIn("corp", hooks);
                    }
                },
                settings: settings ?? Settings(),
                mapEndpoints: web => web.MapPlatformSignIn());
            plain.Dispose();

            var cookies = new CookieContainer();
            var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = cookies, UseCookies = true })
            {
                BaseAddress = new Uri(app.Urls.First()),
            };
            return new SignInHost(app, client, cookies, issuer);
        }

        internal async Task<BeginResult> BeginAsync()
        {
            var response = await Client.GetAsync("/signin/corp/begin");
            Assert.Equal(HttpStatusCode.Found, response.StatusCode);
            var query = Query(response);
            Issuer.Nonce = query["nonce"];
            LastChallenge = query["code_challenge"];
            return new BeginResult(query["state"], query["nonce"], query["code_challenge"]);
        }

        internal async Task<BeginResult> SignInAsync()
        {
            var begin = await BeginAsync();
            var response = await Client.GetAsync($"{Callback}?code=abc&state={begin.State}");
            Assert.Equal(HttpStatusCode.Found, response.StatusCode);
            return begin;
        }

        internal Task<HttpResponseMessage> PostTokenAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/signin/corp/token");
            request.Headers.Add("X-Platform-SignIn", "1");
            return Client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
        }
    }
}
