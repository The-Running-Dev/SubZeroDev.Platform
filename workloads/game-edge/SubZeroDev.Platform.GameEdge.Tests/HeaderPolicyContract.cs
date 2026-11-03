using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SubZeroDev.Platform.GameEdge.Tests.Support;

namespace SubZeroDev.Platform.GameEdge.Tests;

/// <summary>G3 S6.1 — the credential policy, header by header (<c>10-design.md</c>, <i>Control flow</i>
/// §6). The rows that read the same in both modes are asserted here once and run in each mode's class,
/// on a buffered route and on a configured streaming one; the rows that differ are in the mode classes.
/// The edge runs on real Kestrel throughout: the connection's address is what is being observed, and the
/// in-memory test server has none.</summary>
public abstract class HeaderPolicyContract
{
    /// <summary>The xUnit collection of every test class that starts an edge on a real listener. Hosts
    /// started concurrently on <c>UseKestrel(0)</c> intermittently fall back to the default port 5000 and
    /// fail to bind, so these classes run one after another.</summary>
    public const string RealListener = "Edge on a real listener";

    /// <summary>A buffered route and a streamed one: S6.7 and invariant 104 say the rules are the same
    /// on both paths.</summary>
    public const string Buffered = "/plain/x";

    public const string Streamed = "/stream/x";

    protected const string StreamedPrefix = "/stream";

    /// <summary>Starts an edge in this class's mode, with <see cref="StreamedPrefix"/> a streaming route.
    /// <paramref name="settings"/> adds to its configuration.</summary>
    /// <param name="workloadBaseAddress">Where the fake workload listens.</param>
    /// <param name="settings">Extra configuration, or <see langword="null"/>.</param>
    /// <returns>An edge already listening on loopback.</returns>
    protected abstract WebApplicationFactory<Program> StartEdge(string workloadBaseAddress, Action<IWebHostBuilder>? settings);

    /// <summary>Gives <paramref name="request"/> the credential this mode's edge expects of a caller:
    /// a token the issuer's pinned keys accept in terminate mode, an opaque one in pass-through.</summary>
    /// <param name="request">The request about to be sent.</param>
    protected abstract void Authenticate(HttpRequestMessage request);

    /// <summary>Asserts what the workload saw of the handoff header after a caller sent its own.</summary>
    /// <param name="forwarded">What the workload received.</param>
    protected abstract void AssertHandoff(RecordedRequest forwarded);

    [Theory]
    [InlineData(Buffered)]
    [InlineData(Streamed)]
    public async Task Authorization_is_never_forwarded(string path)
    {
        var seen = await ExchangeAsync(path, request =>
        {
            Authenticate(request);
            request.Headers.TryAddWithoutValidation("Proxy-Authorization", "Basic cHJveHk6c2VjcmV0");
        });

        Assert.DoesNotContain("Authorization", seen.Request.Headers.Keys);
        Assert.DoesNotContain("Proxy-Authorization", seen.Request.Headers.Keys);
    }

    [Theory]
    [InlineData(Buffered)]
    [InlineData(Streamed)]
    public async Task A_handoff_header_the_caller_sends_is_stripped(string path)
    {
        var seen = await ExchangeAsync(path, request =>
        {
            Authenticate(request);
            request.Headers.TryAddWithoutValidation("SubZeroDev-Principal-Assertion", "caller.forged.value");
        });

        AssertHandoff(seen.Request);
    }

    [Theory]
    [InlineData(Buffered)]
    [InlineData(Streamed)]
    public async Task Hop_by_hop_headers_and_the_headers_a_Connection_names_are_stripped(string path)
    {
        var seen = await ExchangeAsync(path, request =>
        {
            Authenticate(request);
            request.Headers.TryAddWithoutValidation("Connection", "X-Private-Hop");
            request.Headers.TryAddWithoutValidation("X-Private-Hop", "secret");
            request.Headers.TryAddWithoutValidation("Keep-Alive", "timeout=5");
            request.Headers.TryAddWithoutValidation("TE", "trailers");
            request.Headers.TryAddWithoutValidation("Upgrade", "websocket");
        });

        foreach (var name in new[] { "X-Private-Hop", "Keep-Alive", "TE", "Upgrade" })
        {
            Assert.DoesNotContain(name, seen.Request.Headers.Keys);
        }
    }

    /// <summary>S6.5 — a caller's own forwarding headers never reach the workload. It sees the edge's
    /// connection values.</summary>
    [Theory]
    [InlineData(Buffered)]
    [InlineData(Streamed)]
    public async Task Forwarding_headers_a_caller_sends_are_replaced_by_the_connections_own(string path)
    {
        var seen = await ExchangeAsync(path, ForgedForwarding);

        var forwardedFor = Assert.Single(seen.Request.Headers["X-Forwarded-For"]);
        Assert.True(IPAddress.TryParse(forwardedFor, out var address) && IPAddress.IsLoopback(address!), forwardedFor);
        Assert.Equal(["http"], seen.Request.Headers["X-Forwarded-Proto"]);
        Assert.Equal([seen.EdgeAuthority], seen.Request.Headers["X-Forwarded-Host"]);
        Assert.DoesNotContain("Forwarded", seen.Request.Headers.Keys);
    }

    /// <summary>S6.5 — a proxy the operator lists has already seen the real client, so its values are
    /// the ones the workload sees. <c>Forwarded</c> still never passes.</summary>
    [Theory]
    [InlineData(Buffered)]
    [InlineData(Streamed)]
    public async Task A_listed_proxys_forwarding_headers_replace_the_connections(string path)
    {
        var seen = await ExchangeAsync(path, ForgedForwarding, settings: builder =>
        {
            builder.UseSetting("GameEdge:TrustedProxies:0", "127.0.0.1");
            builder.UseSetting("GameEdge:TrustedProxies:1", "::1");
        });

        Assert.Equal(["203.0.113.7"], seen.Request.Headers["X-Forwarded-For"]);
        Assert.Equal(["https"], seen.Request.Headers["X-Forwarded-Proto"]);
        Assert.Equal(["game.example"], seen.Request.Headers["X-Forwarded-Host"]);
        Assert.DoesNotContain("Forwarded", seen.Request.Headers.Keys);
    }

    [Theory]
    [InlineData(Buffered)]
    [InlineData(Streamed)]
    public async Task A_listed_proxy_that_is_not_the_connection_is_not_trusted(string path)
    {
        var seen = await ExchangeAsync(path, ForgedForwarding, settings: builder =>
            builder.UseSetting("GameEdge:TrustedProxies:0", "10.9.9.9"));

        var forwardedFor = Assert.Single(seen.Request.Headers["X-Forwarded-For"]);
        Assert.NotEqual("203.0.113.7", forwardedFor);
        Assert.Equal(["http"], seen.Request.Headers["X-Forwarded-Proto"]);
    }

    [Theory]
    [InlineData(Buffered)]
    [InlineData(Streamed)]
    public async Task Location_on_a_redirect_reaches_the_caller_verbatim_and_is_not_followed(string path)
    {
        const string Destination = "https://elsewhere.example/a%20b?c=d&e=%2F#f";
        var seen = await ExchangeAsync(path, Authenticate, script: context =>
        {
            context.Response.StatusCode = StatusCodes.Status302Found;
            context.Response.Headers.Location = Destination;
            return Task.CompletedTask;
        });

        Assert.Equal(HttpStatusCode.Found, seen.Status);
        Assert.Equal([Destination], seen.Response["Location"]);
        Assert.Single(seen.Workload.Requests);
    }

    [Theory]
    [InlineData(Buffered)]
    [InlineData(Streamed)]
    public async Task Cache_Control_and_Vary_reach_the_caller_unchanged(string path)
    {
        var seen = await ExchangeAsync(path, Authenticate, script: context =>
        {
            context.Response.Headers.CacheControl = "private, max-age=0, must-revalidate";
            context.Response.Headers.Append("Vary", new Microsoft.Extensions.Primitives.StringValues(["Accept-Language", "Origin"]));
            return Task.CompletedTask;
        });

        Assert.Equal(["private, max-age=0, must-revalidate"], seen.Response["Cache-Control"]);
        Assert.Equal(["Accept-Language", "Origin"], seen.Response["Vary"]);
    }

    /// <summary>A credentialed request carrying forwarding headers a caller has no business sending.</summary>
    protected void ForgedForwarding(HttpRequestMessage request)
    {
        Authenticate(request);
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.7");
        request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
        request.Headers.TryAddWithoutValidation("X-Forwarded-Host", "game.example");
        request.Headers.TryAddWithoutValidation("Forwarded", "for=203.0.113.7;proto=https;host=game.example");
    }

    /// <summary>Sends one request through a fresh edge to a fresh workload and returns what each side
    /// saw. The body is read to its end, so a streamed route has finished by the time this returns.</summary>
    /// <param name="path">The request path.</param>
    /// <param name="shape">Adds the headers under test to the request.</param>
    /// <param name="script">What the workload answers; <see langword="null"/> is an empty <c>200</c>.</param>
    /// <param name="settings">Extra edge configuration.</param>
    /// <param name="method">The request method; <c>GET</c> by default.</param>
    protected async Task<Seen> ExchangeAsync(
        string path,
        Action<HttpRequestMessage> shape,
        Func<HttpContext, Task>? script = null,
        Action<IWebHostBuilder>? settings = null,
        HttpMethod? method = null)
    {
        await using var workload = await FakeWorkload.StartAsync();
        workload.Script = script ?? (_ => Task.CompletedTask);

        await using var edge = StartEdge(workload.BaseAddress, settings);
        var address = new Uri(edge.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());

        // No redirect following and no cookie jar: the caller under test is the edge's, not a browser.
        using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            BaseAddress = address,
            Timeout = TimeSpan.FromSeconds(30),
        };

        using var request = new HttpRequestMessage(method ?? HttpMethod.Get, path);
        shape(request);

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        await response.Content.ReadAsByteArrayAsync();

        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in response.Headers.NonValidated.Concat(response.Content.Headers.NonValidated))
        {
            headers[header.Key] = header.Value.ToArray();
        }

        return new Seen(response.StatusCode, headers, workload.Requests.LastOrDefault()!, address.Authority, workload);
    }

    /// <summary>What an exchange showed on each side.</summary>
    /// <param name="Status">The status the caller received.</param>
    /// <param name="Response">The headers the caller received, each with every value.</param>
    /// <param name="Request">The last request the workload received.</param>
    /// <param name="EdgeAuthority">The host and port the caller addressed.</param>
    /// <param name="Workload">The workload, for the count of requests it received.</param>
    protected sealed record Seen(
        HttpStatusCode Status,
        IReadOnlyDictionary<string, string[]> Response,
        RecordedRequest Request,
        string EdgeAuthority,
        FakeWorkload Workload);
}
