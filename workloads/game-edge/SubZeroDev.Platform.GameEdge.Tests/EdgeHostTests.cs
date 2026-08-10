using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using SubZeroDev.Platform.GameEdge.Tests.Support;

namespace SubZeroDev.Platform.GameEdge.Tests;

/// <summary>S7.1, S7.2, S7.3 — the whole composed host, end to end.</summary>
public sealed class EdgeHostTests
{
    [Fact]
    public void S7_1_programs_only_platform_shaped_registration_call_is_AddPlatformWebHost()
    {
        var source = File.ReadAllText(ProgramCsPath());

        var occurrences = System.Text.RegularExpressions.Regex.Matches(source, @"AddPlatformWebHost\(\)").Count;
        Assert.Equal(1, occurrences);
        Assert.DoesNotContain("AddGameEdge(", source);
    }

    [Fact]
    public async Task S7_2_a_request_returns_the_workloads_status_and_body_and_forwards_an_unknown_path()
    {
        await using var workload = await FakeWorkload.StartAsync();
        workload.ResponseStatus = 200;
        workload.ResponseBody = "{\"scene\":\"opening\"}"u8.ToArray();
        workload.ResponseContentType = "application/json";

        await using var factory = CreateFactory(new Uri(workload.BaseAddress));
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            "/v1/an-operation-the-edge-has-never-heard-of?x=1",
            new StringContent("{\"choice\":\"north\"}"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(workload.ResponseBody, body);

        var recorded = Assert.Single(workload.Requests);
        Assert.Equal("/v1/an-operation-the-edge-has-never-heard-of?x=1", recorded.PathAndQuery);
    }

    [Fact]
    public async Task S7_3_liveness_is_healthy_and_readiness_is_unhealthy_while_the_workload_is_down()
    {
        var closedPort = FindClosedPort();

        await using var factory = CreateFactory(new Uri($"http://127.0.0.1:{closedPort}"));
        using var client = factory.CreateClient();

        using var liveness = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, liveness.StatusCode);

        using var readiness = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, readiness.StatusCode);

        var body = JsonDocument.Parse(await readiness.Content.ReadAsStringAsync());
        var checks = body.RootElement.GetProperty("checks").EnumerateArray().ToList();
        Assert.Contains(checks, entry => entry.GetProperty("status").GetString() == "Unhealthy");
    }

    [Fact]
    public async Task S7_5_an_unreachable_workload_returns_503_carrying_the_correlation()
    {
        var closedPort = FindClosedPort();

        await using var factory = CreateFactory(new Uri($"http://127.0.0.1:{closedPort}"));
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/v1/create-session", new StringContent("{}"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("workload_unreachable", body.GetProperty("code").GetString());
        Assert.Matches("^[0-9a-f]{32}$", body.GetProperty("correlation").GetString());
    }

    [Fact]
    public async Task S7_6_a_workload_that_never_answers_returns_504_carrying_the_correlation()
    {
        await using var workload = await FakeWorkload.StartAsync();
        workload.Hang = true;

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("GameEdge:WorkloadBaseAddress", workload.BaseAddress);
            builder.UseSetting("GameEdge:ForwardTimeout", "00:00:00.300");
            builder.UseSetting("GameEdge:LivenessTimeout", "00:00:01");
        });
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/v1/create-session", new StringContent("{}"));

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("workload_timeout", body.GetProperty("code").GetString());
        Assert.Matches("^[0-9a-f]{32}$", body.GetProperty("correlation").GetString());
    }

    [Fact]
    public async Task An_unconfigured_workload_address_fails_startup_rather_than_every_request()
    {
        // appsettings.json carries the GameEdge section with both timeouts and no WorkloadBaseAddress,
        // and `required` is not something the configuration binder enforces — so without the check in
        // Program.cs this host starts, answers liveness 200, and 500s every forward instead.
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("GameEdge:ForwardTimeout", "00:00:05");
            builder.UseSetting("GameEdge:LivenessTimeout", "00:00:01");
        });

        var thrown = Assert.ThrowsAny<Exception>(factory.CreateClient);
        Assert.Contains("WorkloadBaseAddress", Flatten(thrown), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_non_positive_forward_timeout_fails_startup()
    {
        // An absent ForwardTimeout binds to TimeSpan.Zero, which makes CancelAfter fire immediately
        // and turns every forward into a 504. Zero is stated here rather than omitted, because
        // appsettings.json supplies one and UseSetting cannot remove it.
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("GameEdge:WorkloadBaseAddress", "http://127.0.0.1:1");
            builder.UseSetting("GameEdge:ForwardTimeout", "00:00:00");
        });

        var thrown = Assert.ThrowsAny<Exception>(factory.CreateClient);
        Assert.Contains("ForwardTimeout", Flatten(thrown), StringComparison.Ordinal);
    }

    /// <summary>The entry point's failure reaches the caller wrapped, so the assertion reads the
    /// whole chain rather than guessing which layer carries the message.</summary>
    private static string Flatten(Exception exception)
    {
        var text = new System.Text.StringBuilder();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            text.AppendLine(current.Message);
        }

        return text.ToString();
    }

    private static WebApplicationFactory<Program> CreateFactory(Uri workloadBaseAddress) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("GameEdge:WorkloadBaseAddress", workloadBaseAddress.ToString());
            builder.UseSetting("GameEdge:ForwardTimeout", "00:00:05");
            builder.UseSetting("GameEdge:LivenessTimeout", "00:00:01");
        });

    private static int FindClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string ProgramCsPath([CallerFilePath] string here = "") =>
        Path.Combine(Path.GetDirectoryName(here)!, "..", "SubZeroDev.Platform.GameEdge", "Program.cs");
}
