using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.GameEdge.Tests.Support;
using SubZeroDev.Platform.Hosting;
using SubZeroDev.Platform.Testing;

namespace SubZeroDev.Platform.GameEdge.Tests;

/// <summary>S7.3, S7.4, S7.7 — the readiness check's own shape, its reaction to the probe, and what
/// it does and does not touch on the workload.</summary>
public sealed class ReadinessTests
{
    private static GameEdgeOptions Options(Uri workloadBaseAddress) => new()
    {
        WorkloadBaseAddress = workloadBaseAddress,
        ForwardTimeout = TimeSpan.FromSeconds(5),
        LivenessTimeout = TimeSpan.FromSeconds(2),
    };

    [Fact]
    public void S7_4_declares_readiness_required_and_touches_an_external_dependency()
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
    public async Task S7_7_readiness_probes_only_liveness_and_never_reaches_a_game_operation()
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

        Assert.Empty(workload.Requests);
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
