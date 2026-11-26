using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using SubZeroDev.Platform.GameEdge.Tests.Support;

namespace SubZeroDev.Platform.GameEdge.Tests;

/// <summary>/health/deployed — the revision a deploy waits for, answered by the edge itself.</summary>
public sealed class DeployedRevisionTests
{
    private const string Revision = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public async Task Reports_its_revision_with_200_when_the_workload_is_ready_and_never_forwards_the_path()
    {
        await using var workload = await FakeWorkload.StartAsync();
        await using var factory = CreateFactory(workload.BaseAddress, Revision);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(DeployedRevision.Path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(Revision, body.RootElement.GetProperty("revision").GetString());
        Assert.Equal("Healthy", body.RootElement.GetProperty("status").GetString());
        Assert.Empty(workload.Requests);
    }

    [Fact]
    public async Task Answers_503_with_its_revision_when_the_workload_is_not_ready()
    {
        await using var workload = await FakeWorkload.StartAsync();
        workload.ReadinessStatus = 503;
        await using var factory = CreateFactory(workload.BaseAddress, Revision);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(DeployedRevision.Path);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(Revision, body.RootElement.GetProperty("revision").GetString());
        Assert.Equal("Unhealthy", body.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Answers_503_with_a_null_revision_when_none_was_configured()
    {
        await using var workload = await FakeWorkload.StartAsync();
        await using var factory = CreateFactory(workload.BaseAddress, revision: null);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(DeployedRevision.Path);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("revision").ValueKind);
    }

    [Theory]
    [InlineData("abc123")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF01234567")]
    [InlineData("main")]
    public void A_revision_that_is_not_a_full_commit_hash_is_refused(string revision)
    {
        var options = new GameEdgeOptions
        {
            WorkloadBaseAddress = new Uri("http://127.0.0.1:1"),
            ForwardTimeout = TimeSpan.FromSeconds(5),
            ReadinessTimeout = TimeSpan.FromSeconds(1),
            Revision = revision,
        };

        Assert.NotNull(DeployedRevision.Validate(options));
    }

    [Fact]
    public async Task A_host_configured_with_a_bad_revision_does_not_start()
    {
        await using var factory = CreateFactory("http://127.0.0.1:1", "not-a-commit");

        var thrown = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("Revision", thrown.ToString(), StringComparison.Ordinal);
    }

    private static WebApplicationFactory<Program> CreateFactory(string workloadBaseAddress, string? revision) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("GameEdge:CredentialMode", "PassThrough");
            builder.UseSetting("GameEdge:WorkloadBaseAddress", workloadBaseAddress);
            builder.UseSetting("GameEdge:ForwardTimeout", "00:00:05");
            builder.UseSetting("GameEdge:ReadinessTimeout", "00:00:01");
            if (revision is not null)
            {
                builder.UseSetting("GameEdge:Revision", revision);
            }
        });
}
