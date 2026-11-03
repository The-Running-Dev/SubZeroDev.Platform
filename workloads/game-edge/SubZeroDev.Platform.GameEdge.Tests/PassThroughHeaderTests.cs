using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Primitives;
using SubZeroDev.Platform.GameEdge.Tests.Support;

namespace SubZeroDev.Platform.GameEdge.Tests;

/// <summary>G3 S6 in pass-through mode (Adventures): the workload owns the session, so the edge carries
/// the caller's <c>Cookie</c> and <c>Origin</c> in and the workload's <c>Set-Cookie</c> and
/// <c>Access-Control-*</c> out, and answers neither a preflight nor a CORS header itself.</summary>
[Collection(RealListener)]
public sealed class PassThroughHeaderTests : HeaderPolicyContract
{
    protected override WebApplicationFactory<Program> StartEdge(string workloadBaseAddress, Action<IWebHostBuilder>? settings)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("GameEdge:CredentialMode", "PassThrough");
            builder.UseSetting("GameEdge:WorkloadBaseAddress", workloadBaseAddress);
            builder.UseSetting("GameEdge:ForwardTimeout", "00:00:10");
            builder.UseSetting("GameEdge:ReadinessTimeout", "00:00:01");
            builder.UseSetting("GameEdge:StreamingRoutes:0:PathPrefix", StreamedPrefix);
            builder.UseSetting("GameEdge:StreamingRoutes:0:FirstByteTimeout", "00:00:02");
            settings?.Invoke(builder);
        });

        factory.UseKestrel(0);
        factory.StartServer();
        return factory;
    }

    // Pass-through has no bearer chain, so this is the header a mistaken client would send; the
    // contract's rows assert it never reaches the workload.
    protected override void Authenticate(HttpRequestMessage request) =>
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer opaque-caller-token");

    protected override void AssertHandoff(RecordedRequest forwarded) =>
        Assert.DoesNotContain("SubZeroDev-Principal-Assertion", forwarded.Headers.Keys);

    /// <summary>S6.2 — Cookie and Origin arrive exactly as sent, whichever of the two the caller sent.</summary>
    [Theory]
    [InlineData(Buffered, "sid=abc; theme=dark", "https://game.example")]
    [InlineData(Buffered, "sid=abc; theme=dark", null)]
    [InlineData(Buffered, null, "https://game.example")]
    [InlineData(Streamed, "sid=abc; theme=dark", "https://game.example")]
    [InlineData(Streamed, "sid=abc; theme=dark", null)]
    [InlineData(Streamed, null, "https://game.example")]
    public async Task S6_2_Cookie_and_Origin_cross_exactly_as_sent(string path, string? cookie, string? origin)
    {
        var seen = await ExchangeAsync(path, request =>
        {
            if (cookie is not null) request.Headers.TryAddWithoutValidation("Cookie", cookie);
            if (origin is not null) request.Headers.TryAddWithoutValidation("Origin", origin);
        });

        if (cookie is null) Assert.DoesNotContain("Cookie", seen.Request.Headers.Keys);
        else Assert.Equal([cookie], seen.Request.Headers["Cookie"]);

        if (origin is null) Assert.DoesNotContain("Origin", seen.Request.Headers.Keys);
        else Assert.Equal([origin], seen.Request.Headers["Origin"]);
    }

    /// <summary>S6.4 — every <c>Set-Cookie</c> the workload sends reaches the caller as its own header,
    /// byte for byte. A comma inside <c>Expires</c> must not split or merge a value.</summary>
    [Theory]
    [InlineData(Buffered)]
    [InlineData(Streamed)]
    public async Task S6_4_three_Set_Cookie_values_reach_the_caller_byte_identical(string path)
    {
        string[] cookies =
        [
            "sid=abc123; Path=/; HttpOnly; Secure; SameSite=Lax; Expires=Wed, 21 Oct 2026 07:28:00 GMT",
            "theme=dark; Path=/settings; Max-Age=3600",
            "csrf=\"a b\"; Domain=game.example; SameSite=Strict",
        ];

        var seen = await ExchangeAsync(path, Authenticate, script: context =>
        {
            context.Response.Headers.Append("Set-Cookie", new StringValues(cookies));
            return Task.CompletedTask;
        });

        Assert.Equal(cookies, seen.Response["Set-Cookie"]);
    }

    /// <summary>S6.6 — a preflight reaches the workload, the workload's <c>Access-Control-*</c> answer
    /// reaches the caller unchanged, and the edge answers none of it.</summary>
    [Theory]
    [InlineData(Buffered)]
    [InlineData(Streamed)]
    public async Task S6_6_a_preflight_is_answered_by_the_workload(string path)
    {
        var seen = await ExchangeAsync(
            path,
            request =>
            {
                request.Headers.TryAddWithoutValidation("Origin", "https://game.example");
                request.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "POST");
                request.Headers.TryAddWithoutValidation("Access-Control-Request-Headers", "content-type,x-custom");
            },
            script: context =>
            {
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                context.Response.Headers["Access-Control-Allow-Origin"] = "https://game.example";
                context.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
                context.Response.Headers["Access-Control-Allow-Headers"] = "content-type,x-custom";
                context.Response.Headers["Access-Control-Allow-Credentials"] = "true";
                context.Response.Headers["Access-Control-Max-Age"] = "600";
                return Task.CompletedTask;
            },
            method: HttpMethod.Options);

        Assert.Equal(HttpStatusCode.NoContent, seen.Status);
        Assert.Equal("OPTIONS", seen.Request.Method);
        Assert.Equal(["https://game.example"], seen.Request.Headers["Origin"]);
        Assert.Equal(["POST"], seen.Request.Headers["Access-Control-Request-Method"]);
        Assert.Equal(["content-type,x-custom"], seen.Request.Headers["Access-Control-Request-Headers"]);

        Assert.Equal(["https://game.example"], seen.Response["Access-Control-Allow-Origin"]);
        Assert.Equal(["GET, POST, OPTIONS"], seen.Response["Access-Control-Allow-Methods"]);
        Assert.Equal(["content-type,x-custom"], seen.Response["Access-Control-Allow-Headers"]);
        Assert.Equal(["true"], seen.Response["Access-Control-Allow-Credentials"]);
        Assert.Equal(["600"], seen.Response["Access-Control-Max-Age"]);
    }

    [Theory]
    [InlineData(Buffered)]
    [InlineData(Streamed)]
    public async Task S6_6_the_edge_adds_no_Access_Control_header_of_its_own(string path)
    {
        var seen = await ExchangeAsync(path, request =>
        {
            request.Headers.TryAddWithoutValidation("Origin", "https://game.example");
            request.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "POST");
        });

        Assert.DoesNotContain(seen.Response.Keys, name => name.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_trusted_proxy_that_is_not_an_IP_address_stops_the_edge_at_startup()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("GameEdge:CredentialMode", "PassThrough");
            builder.UseSetting("GameEdge:WorkloadBaseAddress", "http://127.0.0.1:1");
            builder.UseSetting("GameEdge:ForwardTimeout", "00:00:05");
            builder.UseSetting("GameEdge:ReadinessTimeout", "00:00:01");
            builder.UseSetting("GameEdge:TrustedProxies:0", "not-an-address");
        });

        var thrown = Assert.ThrowsAny<Exception>(factory.CreateClient);

        Assert.Contains("GameEdge:TrustedProxies:0", Flatten(thrown), StringComparison.Ordinal);
    }

    private static string Flatten(Exception thrown)
    {
        var text = new System.Text.StringBuilder();
        for (var current = thrown; current is not null; current = current.InnerException!)
        {
            text.AppendLine(current.Message);
            if (current.InnerException is null) break;
        }

        return text.ToString();
    }
}
