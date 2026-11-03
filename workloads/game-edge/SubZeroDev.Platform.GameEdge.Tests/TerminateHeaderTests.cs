using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Primitives;
using SubZeroDev.Platform.GameEdge.Tests.Support;

namespace SubZeroDev.Platform.GameEdge.Tests;

/// <summary>G3 S6 in terminate mode (game-service): the edge authenticated the bearer and vouches for
/// the caller with its own assertion, so no cookie or origin crosses in and nothing session-shaped —
/// <c>Set-Cookie</c>, <c>Access-Control-*</c> — comes back out.</summary>
[Collection(RealListener)]
public sealed class TerminateHeaderTests : HeaderPolicyContract, IDisposable
{
    private const string HandoffHeader = "SubZeroDev-Principal-Assertion";

    private readonly ECDsa handoffKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string directory = Directory.CreateTempSubdirectory("game-edge-headers-").FullName;
    private readonly string signingKeyPath;

    // Pinned before the first host starts, whichever test runs first.
    static TerminateHeaderTests() => TestIssuer.Pin();

    public TerminateHeaderTests()
    {
        signingKeyPath = Path.Combine(directory, "handoff.pem");
        File.WriteAllText(signingKeyPath, handoffKey.ExportPkcs8PrivateKeyPem());
    }

    public void Dispose()
    {
        handoffKey.Dispose();
        Directory.Delete(directory, recursive: true);
    }

    protected override WebApplicationFactory<Program> StartEdge(string workloadBaseAddress, Action<IWebHostBuilder>? settings)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("GameEdge:WorkloadBaseAddress", workloadBaseAddress);
            builder.UseSetting("GameEdge:ForwardTimeout", "00:00:10");
            builder.UseSetting("GameEdge:ReadinessTimeout", "00:00:01");
            builder.UseSetting("GameEdge:CredentialMode", "Terminate");
            builder.UseSetting("Platform:CompositionProfile", "AuthenticatedTransport");
            builder.UseSetting("GameEdge:Handoff:Issuer", "https://edge.test/handoff");
            builder.UseSetting("GameEdge:Handoff:Audience", "game-service.test");
            builder.UseSetting("GameEdge:Handoff:KeyId", "edge-key-1");
            builder.UseSetting("GameEdge:Handoff:SigningKeyPath", signingKeyPath);
            builder.UseSetting("GameEdge:Handoff:Lifetime", "00:00:30");
            builder.UseSetting("GameEdge:StreamingRoutes:0:PathPrefix", StreamedPrefix);
            builder.UseSetting("GameEdge:StreamingRoutes:0:FirstByteTimeout", "00:00:02");
            settings?.Invoke(builder);
        });

        factory.UseKestrel(0);
        factory.StartServer();
        return factory;
    }

    protected override void Authenticate(HttpRequestMessage request) =>
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + TestIssuer.Token("player-1"));

    // Exactly one assertion reaches the workload, and it is the edge's: the caller's is gone.
    protected override void AssertHandoff(RecordedRequest forwarded)
    {
        var assertion = Assert.Single(forwarded.Headers[HandoffHeader]);
        Assert.NotEqual("caller.forged.value", assertion);
        Assert.Equal(3, assertion.Split('.').Length);
    }

    /// <summary>S6.2 — neither <c>Cookie</c> nor <c>Origin</c> crosses, with a valid token alongside.</summary>
    [Theory]
    [InlineData(Buffered)]
    [InlineData(Streamed)]
    public async Task S6_2_Cookie_and_Origin_never_cross(string path)
    {
        var seen = await ExchangeAsync(path, request =>
        {
            Authenticate(request);
            request.Headers.TryAddWithoutValidation("Cookie", "sid=abc");
            request.Headers.TryAddWithoutValidation("Origin", "https://game.example");
        });

        Assert.DoesNotContain("Cookie", seen.Request.Headers.Keys);
        Assert.DoesNotContain("Origin", seen.Request.Headers.Keys);
    }

    /// <summary>S6.4 — three <c>Set-Cookie</c> values from the workload, none reach the caller.</summary>
    [Theory]
    [InlineData(Buffered)]
    [InlineData(Streamed)]
    public async Task S6_4_no_Set_Cookie_reaches_the_caller(string path)
    {
        var seen = await ExchangeAsync(path, Authenticate, script: context =>
        {
            context.Response.Headers.Append(
                "Set-Cookie",
                new StringValues(["a=1; Path=/", "b=2; HttpOnly", "c=3; Secure; SameSite=Strict"]));
            return Task.CompletedTask;
        });

        Assert.Equal(HttpStatusCode.OK, seen.Status);
        Assert.DoesNotContain("Set-Cookie", seen.Response.Keys);
    }

    /// <summary>The workload's own <c>Access-Control-*</c> is session-shaped too: terminate mode
    /// produces none, so a browser cannot be told to trust an origin the edge never vetted.</summary>
    [Theory]
    [InlineData(Buffered)]
    [InlineData(Streamed)]
    public async Task The_workloads_Access_Control_headers_do_not_reach_the_caller(string path)
    {
        var seen = await ExchangeAsync(path, Authenticate, script: context =>
        {
            context.Response.Headers["Access-Control-Allow-Origin"] = "*";
            context.Response.Headers["Access-Control-Allow-Credentials"] = "true";
            return Task.CompletedTask;
        });

        Assert.Equal(HttpStatusCode.OK, seen.Status);
        Assert.DoesNotContain(seen.Response.Keys, name => name.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>An anonymous preflight is an anonymous request: refused by the edge, with nothing
    /// forwarded and no CORS header produced.</summary>
    [Theory]
    [InlineData(Buffered)]
    [InlineData(Streamed)]
    public async Task An_anonymous_preflight_is_refused_and_not_forwarded(string path)
    {
        var seen = await ExchangeAsync(
            path,
            request =>
            {
                request.Headers.TryAddWithoutValidation("Origin", "https://game.example");
                request.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "POST");
            },
            method: HttpMethod.Options);

        Assert.Equal(HttpStatusCode.Unauthorized, seen.Status);
        Assert.Empty(seen.Workload.Requests);
        Assert.DoesNotContain(seen.Response.Keys, name => name.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase));
    }
}
