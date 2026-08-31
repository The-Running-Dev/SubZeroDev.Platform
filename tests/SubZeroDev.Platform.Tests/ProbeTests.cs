using Microsoft.Extensions.DependencyInjection;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Testing;

namespace SubZeroDev.Platform.Tests;

public sealed class ProbeTests
{
    [Fact]
    public async Task A_host_with_no_persistence_has_no_database_entry_rather_than_a_passing_one()
    {
        await using var host = await PlatformTestHost.CreateBuilder().StartAsync(CancellationToken.None);

        var report = await host.ProbeAsync(HealthCheckKind.Readiness, CancellationToken.None);

        Assert.DoesNotContain(report.Entries, entry => entry.Name == PlatformHealthChecks.Database);
    }

    [Fact]
    public async Task A_report_enumerates_every_registered_check_of_that_kind_and_no_other()
    {
        await using var host = await StartWithChecks(
            new StubHealthCheck("ready-one", HealthCheckKind.Readiness, HealthStatus.Healthy),
            new StubHealthCheck("ready-two", HealthCheckKind.Readiness, HealthStatus.Healthy),
            new StubHealthCheck("live-one", HealthCheckKind.Liveness, HealthStatus.Healthy));

        var readiness = await host.ProbeAsync(HealthCheckKind.Readiness, CancellationToken.None);
        var liveness = await host.ProbeAsync(HealthCheckKind.Liveness, CancellationToken.None);

        // platform.audit.sink is a Core default present on every host — see AuditSinkHealthCheck.
        Assert.Equal(
            ["ready-one", "ready-two", "platform.audit.sink"],
            readiness.Entries.Select(entry => entry.Name.Value));
        Assert.Equal(["live-one"], liveness.Entries.Select(entry => entry.Name.Value));
    }

    [Fact]
    public async Task A_degraded_check_degrades_the_aggregate_without_making_it_unhealthy()
    {
        await using var host = await StartWithChecks(
            new StubHealthCheck("healthy", HealthCheckKind.Readiness, HealthStatus.Healthy),
            new StubHealthCheck("degraded", HealthCheckKind.Readiness, HealthStatus.Degraded));

        var report = await host.ProbeAsync(HealthCheckKind.Readiness, CancellationToken.None);

        Assert.Equal(HealthStatus.Degraded, report.Aggregate);
    }

    [Fact]
    public async Task A_required_check_failing_makes_the_aggregate_unhealthy()
    {
        await using var host = await StartWithChecks(
            new StubHealthCheck("required", HealthCheckKind.Readiness, HealthStatus.Unhealthy));

        var report = await host.ProbeAsync(HealthCheckKind.Readiness, CancellationToken.None);

        Assert.Equal(HealthStatus.Unhealthy, report.Aggregate);
    }

    [Fact]
    public async Task An_optional_check_failing_degrades_rather_than_drains()
    {
        await using var host = await StartWithChecks(
            new StubHealthCheck(
                "optional",
                HealthCheckKind.Readiness,
                HealthStatus.Unhealthy,
                HealthCheckCriticality.Optional));

        var report = await host.ProbeAsync(HealthCheckKind.Readiness, CancellationToken.None);

        // The criticality flag exists to stop a host being drained over a non-essential provider.
        Assert.Equal(HealthStatus.Degraded, report.Aggregate);
    }

    [Fact]
    public async Task A_throwing_check_is_unhealthy_and_does_not_escape_the_probe()
    {
        await using var host = await StartWithChecks(new ThrowingCheck());

        var report = await host.ProbeAsync(HealthCheckKind.Readiness, CancellationToken.None);

        // platform.audit.sink is a Core default present on every host — see AuditSinkHealthCheck.
        var entry = Assert.Single(report.Entries, candidate => candidate.Name.Value == "throwing");
        Assert.Equal(HealthStatus.Unhealthy, entry.Status);
        Assert.DoesNotContain("secret", entry.Detail ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_hanging_check_is_unhealthy_at_its_timeout()
    {
        await using var host = await StartWithChecks(new HangingCheck());

        var report = await host.ProbeAsync(HealthCheckKind.Readiness, CancellationToken.None);

        // platform.audit.sink is a Core default present on every host — see AuditSinkHealthCheck.
        var entry = Assert.Single(report.Entries, candidate => candidate.Name.Value == "hanging");
        Assert.Equal(HealthStatus.Unhealthy, entry.Status);
    }

    private static Task<IPlatformTestHost> StartWithChecks(params IHealthCheck[] checks) =>
        PlatformTestHost.CreateBuilder()
            .WithServices(services =>
            {
                foreach (var check in checks)
                {
                    services.AddSingleton(check);
                }
            })
            .StartAsync(CancellationToken.None);

    private sealed class ThrowingCheck : IHealthCheck
    {
        public HealthCheckName Name { get; } = new("throwing");

        public HealthCheckKind Kind => HealthCheckKind.Readiness;

        public HealthCheckCriticality Criticality => HealthCheckCriticality.Required;

        public TimeSpan Timeout => TimeSpan.FromSeconds(5);

        public bool TouchesExternalDependency => false;

        public Task<HealthCheckResult> CheckAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("secret detail that must not reach the body");
    }

    [Fact]
    public async Task Exhausting_the_endpoint_budget_still_returns_a_report_rather_than_throwing()
    {
        // Each check's own timeout exceeds the endpoint budget, so the budget is what cancels them.
        // Before this was guarded on the caller's token rather than the budget, the cancellation
        // escaped both catch clauses and the probe threw — answering a readiness request with a 500
        // and no report, while draining the host it was asked about.
        await using var host = await StartWithChecks(
            new SlowCheck("slow-one"),
            new SlowCheck("slow-two"));

        var report = await host.ProbeAsync(HealthCheckKind.Readiness, CancellationToken.None);

        // platform.audit.sink is a Core default present on every host — see AuditSinkHealthCheck.
        var slow = report.Entries.Where(entry => entry.Name.Value != "platform.audit.sink").ToList();
        Assert.Equal(["slow-one", "slow-two"], slow.Select(entry => entry.Name.Value));
        Assert.All(slow, entry => Assert.Equal(HealthStatus.Unhealthy, entry.Status));
    }

    [Fact]
    public async Task A_disconnected_caller_stops_the_probe_rather_than_degrading_every_entry()
    {
        // The one cancellation that should still propagate: nobody is left to read the report.
        await using var host = await StartWithChecks(new SlowCheck("slow"));

        using var caller = new CancellationTokenSource();
        await caller.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => host.ProbeAsync(HealthCheckKind.Readiness, caller.Token));
    }

    /// <summary>Hangs past the endpoint budget, with a per-check timeout deliberately longer than
    /// it, so the budget is the deadline that fires.</summary>
    private sealed class SlowCheck(string name) : IHealthCheck
    {
        public HealthCheckName Name { get; } = new(name);

        public HealthCheckKind Kind => HealthCheckKind.Readiness;

        public HealthCheckCriticality Criticality => HealthCheckCriticality.Required;

        public TimeSpan Timeout => TimeSpan.FromMinutes(10);

        public bool TouchesExternalDependency => false;

        public async Task<HealthCheckResult> CheckAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken);
            return new HealthCheckResult(HealthStatus.Healthy, null, new Dictionary<string, string>());
        }
    }

    private sealed class HangingCheck : IHealthCheck
    {
        public HealthCheckName Name { get; } = new("hanging");

        public HealthCheckKind Kind => HealthCheckKind.Readiness;

        public HealthCheckCriticality Criticality => HealthCheckCriticality.Required;

        public TimeSpan Timeout => TimeSpan.FromMilliseconds(50);

        public bool TouchesExternalDependency => false;

        public async Task<HealthCheckResult> CheckAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken);
            return new HealthCheckResult(HealthStatus.Healthy, null, new Dictionary<string, string>());
        }
    }
}
