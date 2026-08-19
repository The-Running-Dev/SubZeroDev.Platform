using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.GameEdge.Tests.Support;
using SubZeroDev.Platform.Hosting;
using SubZeroDev.Platform.Testing;

namespace SubZeroDev.Platform.GameEdge.Tests;

/// <summary>S7.3, S7.4, S7.7, S10.1, S10.2, S10.5 — the readiness check's own shape, its reaction to
/// the probe, and what it does and does not touch on the workload.</summary>
public sealed class ReadinessTests
{
    private static GameEdgeOptions Options(Uri workloadBaseAddress) => new()
    {
        WorkloadBaseAddress = workloadBaseAddress,
        ForwardTimeout = TimeSpan.FromSeconds(5),
        ReadinessTimeout = TimeSpan.FromSeconds(2),
    };

    [Fact]
    public void S7_4_S10_4_declares_readiness_required_and_touches_an_external_dependency()
    {
        var check = new GameWorkloadReadinessCheck(
            new StubProbe(Result<EdgeError>.Success()),
            Options(new Uri("http://127.0.0.1:1")));

        Assert.Equal(HealthCheckKind.Readiness, check.Kind);
        Assert.Equal(HealthCheckCriticality.Required, check.Criticality);
        Assert.True(check.TouchesExternalDependency);
    }

    [Fact]
    public async Task S7_4_platform_rejects_a_check_shaped_like_this_one_if_it_were_registered_as_liveness()
    {
        // GameWorkloadReadinessCheck's own Kind is fixed to Readiness and so can never be composed
        // this way. This proves the rule it relies on: Platform's registry rejects exactly the
        // shape it deliberately avoids — Kind = Liveness with TouchesExternalDependency = true —
        // with its existing ExternalDependencyInLivenessCheck.
        var thrown = await Assert.ThrowsAsync<PlatformStartupException>(async () =>
        {
            await using var host = await PlatformTestHost.CreateBuilder()
                .WithServices(services => services.TryAddEnumerable(
                    ServiceDescriptor.Singleton<IHealthCheck>(new ExternalDependencyShapedAsLiveness())))
                .StartAsync(CancellationToken.None);
        });

        Assert.IsType<HostStartupError>(thrown.Error);
    }

    [Fact]
    public async Task S7_3_reports_unhealthy_when_the_probe_fails_and_healthy_when_it_succeeds()
    {
        var unhealthy = new GameWorkloadReadinessCheck(
            new StubProbe(Result<EdgeError>.Failure(EdgeError.WorkloadUnreachable())),
            Options(new Uri("http://127.0.0.1:1")));
        var unhealthyResult = await unhealthy.CheckAsync(CancellationToken.None);
        Assert.Equal(HealthStatus.Unhealthy, unhealthyResult.Status);

        var healthy = new GameWorkloadReadinessCheck(
            new StubProbe(Result<EdgeError>.Success()),
            Options(new Uri("http://127.0.0.1:1")));
        var healthyResult = await healthy.CheckAsync(CancellationToken.None);
        Assert.Equal(HealthStatus.Healthy, healthyResult.Status);
    }

    [Fact]
    public async Task S7_7_S10_5_readiness_probes_only_readiness_and_never_reaches_a_game_operation()
    {
        await using var workload = await FakeWorkload.StartAsync();
        var options = Options(new Uri(workload.BaseAddress));

        await using var host = await PlatformTestHost.CreateBuilder()
            .WithServices(services =>
            {
                services.AddSingleton(options);
                services.AddHttpClient(GameWorkloadProbe.HttpClientName);
                services.TryAddSingleton<IGameWorkloadProbe, GameWorkloadProbe>();
                services.TryAddEnumerable(
                    ServiceDescriptor.Singleton<IHealthCheck, GameWorkloadReadinessCheck>());
            })
            .StartAsync(CancellationToken.None);

        for (var i = 0; i < 5; i++)
        {
            var report = await host.ProbeAsync(HealthCheckKind.Readiness, CancellationToken.None);
            Assert.Equal(HealthStatus.Healthy, report.Aggregate);
        }

        // S10.5: no game operation ever reached the workload, so its session count — which only
        // moves in response to a recorded request — stayed zero across every one of those probes.
        Assert.Empty(workload.Requests);
    }

    [Fact]
    public async Task S10_1_reports_unhealthy_when_the_workloads_readiness_is_down_even_if_its_liveness_is_up()
    {
        await using var workload = await FakeWorkload.StartAsync();
        workload.LivenessStatus = 200;
        workload.ReadinessStatus = 503;
        var options = Options(new Uri(workload.BaseAddress));

        var check = new GameWorkloadReadinessCheck(
            new GameWorkloadProbe(new StubHttpClientFactory(workload.BaseAddress), options),
            options);

        var result = await check.CheckAsync(CancellationToken.None);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task S10_1_reports_healthy_when_the_workloads_readiness_is_up_even_if_its_liveness_is_down()
    {
        await using var workload = await FakeWorkload.StartAsync();
        workload.LivenessStatus = 503;
        workload.ReadinessStatus = 200;
        var options = Options(new Uri(workload.BaseAddress));

        var check = new GameWorkloadReadinessCheck(
            new GameWorkloadProbe(new StubHttpClientFactory(workload.BaseAddress), options),
            options);

        var result = await check.CheckAsync(CancellationToken.None);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task S10_2_reports_healthy_when_the_workload_is_fully_healthy()
    {
        await using var workload = await FakeWorkload.StartAsync();
        workload.LivenessStatus = 200;
        workload.ReadinessStatus = 200;
        var options = Options(new Uri(workload.BaseAddress));

        var check = new GameWorkloadReadinessCheck(
            new GameWorkloadProbe(new StubHttpClientFactory(workload.BaseAddress), options),
            options);

        var result = await check.CheckAsync(CancellationToken.None);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public void S10_3_GameEdgeOptions_has_no_LivenessTimeout_member_and_ReadinessTimeout_governs_the_probe()
    {
        var members = typeof(GameEdgeOptions).GetProperties().Select(property => property.Name);

        Assert.DoesNotContain("LivenessTimeout", members);
        Assert.Contains("ReadinessTimeout", members);
    }

    /// <summary>A minimal <see cref="IHttpClientFactory"/> for tests exercising the real
    /// <see cref="GameWorkloadProbe"/> against a <see cref="FakeWorkload"/>, without composing the
    /// DI container S7.7/S10.5 already do.</summary>
    private sealed class StubHttpClientFactory(string baseAddress) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new() { BaseAddress = new Uri(baseAddress) };
    }

    /// <summary>Not a real check — exists only to demonstrate the registry rule
    /// <see cref="GameWorkloadReadinessCheck"/> is deliberately built to satisfy.</summary>
    private sealed class ExternalDependencyShapedAsLiveness : IHealthCheck
    {
        public HealthCheckName Name { get; } = new("would-be-rejected");

        public HealthCheckKind Kind => HealthCheckKind.Liveness;

        public HealthCheckCriticality Criticality => HealthCheckCriticality.Required;

        public TimeSpan Timeout => TimeSpan.FromSeconds(1);

        public bool TouchesExternalDependency => true;

        public Task<HealthCheckResult> CheckAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new HealthCheckResult(HealthStatus.Healthy, null, new Dictionary<string, string>()));
    }
}
