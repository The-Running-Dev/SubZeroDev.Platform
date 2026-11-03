using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SubZeroDev.Platform.GameEdge.Tests.Support;

namespace SubZeroDev.Platform.GameEdge.Tests;

/// <summary>G3 S10 — the abuse limiter answers <c>429</c> with <c>Retry-After</c> before anything is
/// forwarded, keyed by the principal when one is established and by the client address otherwise
/// (invariant 121). Every edge here runs on a real listener: the connection's address is what the
/// anonymous rows count, and the in-memory test server has none.</summary>
[Collection(HeaderPolicyContract.RealListener)]
public sealed class AbuseLimitTests : IDisposable
{
    private const string Plain = "/sessions/s-1";

    private readonly ECDsa handoffKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string directory = Directory.CreateTempSubdirectory("game-edge-limits-").FullName;
    private readonly string signingKeyPath;

    // Pinned before the first host starts, whichever test runs first.
    static AbuseLimitTests() => TestIssuer.Pin();

    public AbuseLimitTests()
    {
        signingKeyPath = Path.Combine(directory, "handoff.pem");
        File.WriteAllText(signingKeyPath, handoffKey.ExportPkcs8PrivateKeyPem());
    }

    public void Dispose()
    {
        handoffKey.Dispose();
        Directory.Delete(directory, recursive: true);
    }

    /// <summary>S10.1 — the fourth request in the window is <c>429 RateLimited</c> with a
    /// <c>Retry-After</c> inside the window, and the workload received three. S10.2 — another principal's
    /// first request is unaffected.</summary>
    [Fact]
    public async Task S10_1_and_S10_2_a_principal_over_its_budget_is_refused_and_another_is_not()
    {
        await using var workload = await FakeWorkload.StartAsync();
        await using var edge = StartTerminate(workload.BaseAddress, permits: 3, window: "00:00:10");
        using var client = ClientOf(edge);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            using var admitted = await SendAsync(client, request => Bearer(request, "player-a"));
            Assert.Equal(HttpStatusCode.OK, admitted.StatusCode);
        }

        using var refused = await SendAsync(client, request => Bearer(request, "player-a"));

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        using var body = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
        Assert.Equal("RateLimited", body.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrEmpty(body.RootElement.GetProperty("correlation").GetString()));
        var retryAfter = Assert.Single(refused.Headers.GetValues("Retry-After"));
        Assert.True(int.TryParse(retryAfter, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds), retryAfter);
        Assert.InRange(seconds, 1, 10);
        Assert.Equal(3, workload.Requests.Count);

        using var other = await SendAsync(client, request => Bearer(request, "player-b"));

        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        Assert.Equal(4, workload.Requests.Count);
    }

    /// <summary>S10.3 — an unauthenticated caller is counted under its client address with the same
    /// settings. Behind a listed proxy that is the address the proxy appended, and a second address's
    /// first request succeeds.</summary>
    [Fact]
    public async Task S10_3_an_anonymous_caller_is_counted_by_the_address_a_listed_proxy_names()
    {
        await using var workload = await FakeWorkload.StartAsync();
        await using var edge = StartPassThrough(workload.BaseAddress, permits: 3, window: "00:00:10", trustedProxies: true);
        using var client = ClientOf(edge);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            using var admitted = await SendAsync(client, request => Forwarded(request, "203.0.113.1"));
            Assert.Equal(HttpStatusCode.OK, admitted.StatusCode);
        }

        using var refused = await SendAsync(client, request => Forwarded(request, "203.0.113.1"));
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.NotEmpty(refused.Headers.GetValues("Retry-After"));
        Assert.Equal(3, workload.Requests.Count);

        using var second = await SendAsync(client, request => Forwarded(request, "203.0.113.2"));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(4, workload.Requests.Count);
    }

    /// <summary>S10.3 — the proxy's entry is the last one: what a caller put before it is its own claim,
    /// and a caller must not choose its own key.</summary>
    [Fact]
    public async Task S10_3_a_caller_cannot_choose_its_key_through_the_entries_before_a_proxys()
    {
        await using var workload = await FakeWorkload.StartAsync();
        await using var edge = StartPassThrough(workload.BaseAddress, permits: 2, window: "00:00:10", trustedProxies: true);
        using var client = ClientOf(edge);

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            using var admitted = await SendAsync(client, request => Forwarded(request, $"198.51.100.{attempt}, 203.0.113.9"));
            Assert.Equal(HttpStatusCode.OK, admitted.StatusCode);
        }

        using var refused = await SendAsync(client, request => Forwarded(request, "198.51.100.77, 203.0.113.9"));

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal(2, workload.Requests.Count);
    }

    /// <summary>S10.3 — from an unlisted connection a forwarding header is discarded, so a caller cannot
    /// escape the limit by naming a new address with each request.</summary>
    [Fact]
    public async Task S10_3_forwarding_headers_from_an_unlisted_connection_do_not_change_the_key()
    {
        await using var workload = await FakeWorkload.StartAsync();
        await using var edge = StartPassThrough(workload.BaseAddress, permits: 2, window: "00:00:10", trustedProxies: false);
        using var client = ClientOf(edge);

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            using var admitted = await SendAsync(client, request => Forwarded(request, $"203.0.113.{attempt}"));
            Assert.Equal(HttpStatusCode.OK, admitted.StatusCode);
        }

        using var refused = await SendAsync(client, request => Forwarded(request, "203.0.113.200"));

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal(2, workload.Requests.Count);
    }

    /// <summary>S10.4 — a request the limiter admits reaches the workload exactly as it does when the
    /// limit is too high ever to fire. Per-request values — the trace and the edge's own authority — are
    /// not part of the comparison.</summary>
    [Fact]
    public async Task S10_4_an_admitted_request_forwards_the_same_bytes_as_one_that_is_never_limited()
    {
        await using var workload = await FakeWorkload.StartAsync();

        async Task<RecordedRequest> ForwardedUnder(int permits)
        {
            workload.Requests.Clear();
            await using var edge = StartPassThrough(workload.BaseAddress, permits, window: "00:00:10", trustedProxies: false);
            using var client = ClientOf(edge);
            using var response = await SendAsync(client, request =>
            {
                request.Method = HttpMethod.Post;
                request.RequestUri = new Uri("/sessions/s-1/act?turn=2", UriKind.Relative);
                request.Content = new StringContent("{\"move\":\"north\"}", Encoding.UTF8, "application/json");
                request.Headers.TryAddWithoutValidation("Cookie", "sid=abc");
                request.Headers.TryAddWithoutValidation("Origin", "https://game.example");
            });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return Assert.Single(workload.Requests);
        }

        var limited = await ForwardedUnder(permits: 3);
        var unlimited = await ForwardedUnder(permits: int.MaxValue);

        Assert.Equal(unlimited.Method, limited.Method);
        Assert.Equal(unlimited.PathAndQuery, limited.PathAndQuery);
        Assert.Equal(unlimited.Body, limited.Body);
        Assert.Equal(Comparable(unlimited), Comparable(limited));

        // Each edge is on its own port, so its own authority and its trace differ; both are still present.
        foreach (var name in new[] { "traceparent", "X-Forwarded-Host" })
        {
            Assert.Contains(name, limited.Headers.Keys);
            Assert.Contains(name, unlimited.Headers.Keys);
        }
    }

    /// <summary>S10.5 — an edge started with no <c>AbuseLimits</c> setting limits at 60 requests in 60
    /// seconds.</summary>
    [Fact]
    public async Task S10_5_an_edge_with_no_setting_limits_at_sixty_in_sixty_seconds()
    {
        Assert.Equal(new AbuseLimitOptions(60, TimeSpan.FromSeconds(60)), new GameEdgeOptions
        {
            WorkloadBaseAddress = new Uri("http://127.0.0.1:1"),
            ForwardTimeout = TimeSpan.FromSeconds(1),
            ReadinessTimeout = TimeSpan.FromSeconds(1),
        }.AbuseLimits);

        await using var workload = await FakeWorkload.StartAsync();
        await using var edge = StartPassThrough(workload.BaseAddress, permits: null, window: null, trustedProxies: false);
        using var client = ClientOf(edge);

        for (var attempt = 1; attempt <= 60; attempt++)
        {
            using var admitted = await SendAsync(client, _ => { });
            Assert.Equal(HttpStatusCode.OK, admitted.StatusCode);
        }

        using var refused = await SendAsync(client, _ => { });

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        var seconds = int.Parse(Assert.Single(refused.Headers.GetValues("Retry-After")), CultureInfo.InvariantCulture);
        Assert.InRange(seconds, 1, 60);
        Assert.Equal(60, workload.Requests.Count);
    }

    /// <summary>The limiter runs after authentication: a caller the edge refuses is answered
    /// <c>401</c> however often it asks, and is never counted or told to wait.</summary>
    [Fact]
    public async Task A_refused_credential_is_answered_401_and_is_never_counted()
    {
        await using var workload = await FakeWorkload.StartAsync();
        await using var edge = StartTerminate(workload.BaseAddress, permits: 1, window: "00:00:10");
        using var client = ClientOf(edge);

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            using var anonymous = await SendAsync(client, _ => { });
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            Assert.False(anonymous.Headers.Contains("Retry-After"));
        }

        using var admitted = await SendAsync(client, request => Bearer(request, "player-a"));

        Assert.Equal(HttpStatusCode.OK, admitted.StatusCode);
        Assert.Single(workload.Requests);
    }

    /// <summary>The window closes: once it has, the caller is admitted again. Driven against the limiter
    /// itself, whose clock a test can hold.</summary>
    [Fact]
    public void A_caller_is_admitted_again_when_its_window_has_closed()
    {
        var clock = new HeldTime();
        var limiter = new AbuseLimiter(Options(2, TimeSpan.FromSeconds(10)), clock);

        Assert.Null(limiter.TryAdmit("a"));
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Null(limiter.TryAdmit("a"));

        // 4 s in: the window has 6 s left, and a refusal is not counted against it.
        Assert.Equal(TimeSpan.FromSeconds(6), limiter.TryAdmit("a"));
        clock.Advance(TimeSpan.FromMilliseconds(5_500));
        Assert.Equal(TimeSpan.FromSeconds(1), limiter.TryAdmit("a"));

        clock.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Null(limiter.TryAdmit("a"));
        Assert.Null(limiter.TryAdmit("a"));
        Assert.NotNull(limiter.TryAdmit("a"));
        Assert.Null(limiter.TryAdmit("b"));
    }

    /// <summary>A principal and an address never share a counter, however alike their text.</summary>
    [Fact]
    public void The_key_separates_principals_from_addresses_and_one_issuer_from_another()
    {
        var account = AbuseLimiter.Key(new Abstractions.PrincipalId("https://issuer.test", "203.0.113.1"), "198.51.100.1");
        var address = AbuseLimiter.Key(null, "203.0.113.1");
        var otherIssuer = AbuseLimiter.Key(new Abstractions.PrincipalId("https://other.test", "203.0.113.1"), "198.51.100.1");

        Assert.Equal(3, new[] { account, address, otherIssuer }.Distinct().Count());
        Assert.Equal(account, AbuseLimiter.Key(new Abstractions.PrincipalId("https://issuer.test", "203.0.113.1"), "192.0.2.5"));
    }

    [Theory]
    [InlineData(null, "00:01:00", "GameEdge:AbuseLimits:PermitCount")]
    [InlineData("0", "00:01:00", "GameEdge:AbuseLimits:PermitCount")]
    [InlineData("-3", "00:01:00", "GameEdge:AbuseLimits:PermitCount")]
    [InlineData("many", "00:01:00", "GameEdge:AbuseLimits:PermitCount")]
    [InlineData("3", null, "GameEdge:AbuseLimits:Window")]
    [InlineData("3", "00:00:00", "GameEdge:AbuseLimits:Window")]
    [InlineData("3", "00:00:00.500", "GameEdge:AbuseLimits:Window")]
    [InlineData("3", "soon", "GameEdge:AbuseLimits:Window")]
    public void A_present_setting_with_a_missing_or_invalid_field_names_that_field(string? permits, string? window, string key)
    {
        var values = new Dictionary<string, string?>();
        if (permits is not null) values["GameEdge:AbuseLimits:PermitCount"] = permits;
        if (window is not null) values["GameEdge:AbuseLimits:Window"] = window;
        values["GameEdge:AbuseLimits:Marker"] = "present";

        var errors = AbuseLimitSettings.Validate(new ConfigurationBuilder().AddInMemoryCollection(values).Build().GetSection("GameEdge"));

        var error = Assert.Single(errors);
        Assert.Contains($"'{key}'", error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_absent_setting_is_valid_and_leaves_the_default()
    {
        var empty = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();

        Assert.Empty(AbuseLimitSettings.Validate(empty.GetSection("GameEdge")));
    }

    /// <summary>A malformed setting stops the edge before it binds, naming the key.</summary>
    [Fact]
    public async Task An_invalid_setting_fails_startup_naming_its_key()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("GameEdge:CredentialMode", "PassThrough");
            builder.UseSetting("GameEdge:WorkloadBaseAddress", "http://127.0.0.1:1");
            builder.UseSetting("GameEdge:ForwardTimeout", "00:00:05");
            builder.UseSetting("GameEdge:ReadinessTimeout", "00:00:01");
            builder.UseSetting("GameEdge:AbuseLimits:PermitCount", "0");
            builder.UseSetting("GameEdge:AbuseLimits:Window", "00:01:00");
        });

        var thrown = Assert.ThrowsAny<Exception>(factory.CreateClient);

        var messages = new List<string>();
        for (Exception? current = thrown; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message);
        }

        Assert.Contains(messages, message => message.Contains("GameEdge:AbuseLimits:PermitCount", StringComparison.Ordinal));
    }

    private static GameEdgeOptions Options(int permits, TimeSpan window) => new()
    {
        WorkloadBaseAddress = new Uri("http://127.0.0.1:1"),
        ForwardTimeout = TimeSpan.FromSeconds(1),
        ReadinessTimeout = TimeSpan.FromSeconds(1),
        AbuseLimits = new AbuseLimitOptions(permits, window),
    };

    private static IEnumerable<string> Comparable(RecordedRequest request) =>
        request.Headers
            .Where(header => !header.Key.Equals("traceparent", StringComparison.OrdinalIgnoreCase)
                && !header.Key.Equals("X-Forwarded-Host", StringComparison.OrdinalIgnoreCase))
            .OrderBy(header => header.Key, StringComparer.OrdinalIgnoreCase)
            .Select(header => header.Key.ToUpperInvariant() + "=" + string.Join("|", header.Value));

    private static void Bearer(HttpRequestMessage request, string subject) =>
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestIssuer.Token(subject));

    private static void Forwarded(HttpRequestMessage request, string forwardedFor) =>
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, Action<HttpRequestMessage> shape)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Plain);
        shape(request);
        try
        {
            return await client.SendAsync(request);
        }
        finally
        {
            request.Dispose();
        }
    }

    private static HttpClient ClientOf(WebApplicationFactory<Program> edge)
    {
        var address = new Uri(edge.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
        return new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            BaseAddress = address,
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    private WebApplicationFactory<Program> StartTerminate(string workloadBaseAddress, int permits, string window) =>
        Start(workloadBaseAddress, builder =>
        {
            builder.UseSetting("GameEdge:CredentialMode", "Terminate");
            builder.UseSetting("Platform:CompositionProfile", "AuthenticatedTransport");
            builder.UseSetting("GameEdge:Handoff:Issuer", "https://edge.test/handoff");
            builder.UseSetting("GameEdge:Handoff:Audience", "game-service.test");
            builder.UseSetting("GameEdge:Handoff:KeyId", "edge-key-1");
            builder.UseSetting("GameEdge:Handoff:SigningKeyPath", signingKeyPath);
            builder.UseSetting("GameEdge:Handoff:Lifetime", "00:00:30");
            Limits(builder, permits, window);
        });

    private WebApplicationFactory<Program> StartPassThrough(
        string workloadBaseAddress, int? permits, string? window, bool trustedProxies) =>
        Start(workloadBaseAddress, builder =>
        {
            builder.UseSetting("GameEdge:CredentialMode", "PassThrough");
            if (trustedProxies)
            {
                builder.UseSetting("GameEdge:TrustedProxies:0", "127.0.0.1");
                builder.UseSetting("GameEdge:TrustedProxies:1", "::1");
            }

            if (permits is { } count && window is not null)
            {
                Limits(builder, count, window);
            }
        });

    private static WebApplicationFactory<Program> Start(string workloadBaseAddress, Action<IWebHostBuilder> settings)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("GameEdge:WorkloadBaseAddress", workloadBaseAddress);
            builder.UseSetting("GameEdge:ForwardTimeout", "00:00:10");
            builder.UseSetting("GameEdge:ReadinessTimeout", "00:00:01");
            settings(builder);
        });

        factory.UseKestrel(0);
        factory.StartServer();
        return factory;
    }

    private static void Limits(IWebHostBuilder builder, int permits, string window)
    {
        builder.UseSetting("GameEdge:AbuseLimits:PermitCount", permits.ToString(CultureInfo.InvariantCulture));
        builder.UseSetting("GameEdge:AbuseLimits:Window", window);
    }

    /// <summary>A clock that moves only when a test says so.</summary>
    private sealed class HeldTime : TimeProvider
    {
        private long ticks = 1_000_000;

        public override long TimestampFrequency => 1_000;

        public override long GetTimestamp() => Interlocked.Read(ref ticks);

        public void Advance(TimeSpan by) => Interlocked.Add(ref ticks, (long)(by.TotalSeconds * TimestampFrequency));
    }
}
