using Microsoft.Extensions.DependencyInjection;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Testing;

namespace SubZeroDev.Platform.Tests;

public sealed class RegistryTests
{
    [Fact]
    public void A_check_touching_an_external_dependency_cannot_be_a_liveness_check()
    {
        var registry = new HealthCheckRegistry();

        var registered = registry.Register(new StubHealthCheck(
            "external",
            HealthCheckKind.Liveness,
            HealthStatus.Healthy,
            touchesExternalDependency: true));

        Assert.False(registered.IsSuccess);
        Assert.Equal("ExternalDependencyInLivenessCheck", registered.Error.Code);
        Assert.Contains("external", registered.Error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_check_can_be_a_readiness_check()
    {
        var registry = new HealthCheckRegistry();

        var registered = registry.Register(new StubHealthCheck(
            "external",
            HealthCheckKind.Readiness,
            HealthStatus.Healthy,
            touchesExternalDependency: true));

        Assert.True(registered.IsSuccess);
    }

    [Fact]
    public void A_duplicate_check_name_is_rejected_rather_than_silently_overwriting()
    {
        var registry = new HealthCheckRegistry();
        registry.Register(new StubHealthCheck("db", HealthCheckKind.Readiness, HealthStatus.Healthy));

        var second = registry.Register(new StubHealthCheck("db", HealthCheckKind.Readiness, HealthStatus.Degraded));

        Assert.False(second.IsSuccess);
        Assert.Equal("DuplicateName", second.Error.Code);
        Assert.Single(registry.Registered);
    }

    [Fact]
    public void Background_work_declaring_no_role_is_rejected()
    {
        var registry = new BackgroundWorkRegistry();

        var registered = registry.Register(new CountingBackgroundWork("orphan", 0));

        Assert.False(registered.IsSuccess);
        Assert.Equal("NoRoleDeclared", registered.Error.Code);
    }

    [Fact]
    public void For_role_returns_only_what_that_role_runs()
    {
        var registry = new BackgroundWorkRegistry();
        registry.Register(new CountingBackgroundWork("dispatch", HostRoles.Worker));
        registry.Register(new CountingBackgroundWork("heartbeat", HostRoles.Both));

        Assert.Equal(["heartbeat"], registry.ForRole(HostRole.Web).Select(work => work.Name.Value));
        Assert.Equal(
            ["dispatch", "heartbeat"],
            registry.ForRole(HostRole.Worker).Select(work => work.Name.Value).Order());
    }

    [Fact]
    public void A_frozen_health_registry_rejects_registration_rather_than_mutating()
    {
        var registry = new HealthCheckRegistry();
        registry.Freeze();

        var registered = registry.Register(new StubHealthCheck("late", HealthCheckKind.Readiness, HealthStatus.Healthy));

        Assert.False(registered.IsSuccess);
        Assert.Equal("RegistryFrozen", registered.Error.Code);
        Assert.Empty(registry.Registered);
    }

    [Fact]
    public void A_frozen_background_work_registry_rejects_registration()
    {
        var registry = new BackgroundWorkRegistry();
        registry.Freeze();

        var registered = registry.Register(new CountingBackgroundWork("late", HostRoles.Worker));

        Assert.False(registered.IsSuccess);
        Assert.Equal("RegistryFrozen", registered.Error.Code);
        Assert.Empty(registry.Registered);
    }

    [Fact]
    public async Task The_module_registry_is_frozen_by_the_time_the_host_has_started()
    {
        await using var host = await PlatformTestHost.CreateBuilder().StartAsync(CancellationToken.None);

        var checks = host.Services.GetRequiredService<IHealthCheckRegistry>();
        var work = host.Services.GetRequiredService<IBackgroundWorkRegistry>();

        Assert.Equal(
            "RegistryFrozen",
            checks.Register(new StubHealthCheck("late", HealthCheckKind.Readiness, HealthStatus.Healthy)).Error.Code);
        Assert.Equal(
            "RegistryFrozen",
            work.Register(new CountingBackgroundWork("late", HostRoles.Worker)).Error.Code);
    }

    [Fact]
    public async Task A_rejected_registration_aborts_startup()
    {
        var builder = PlatformTestHost.CreateBuilder()
            .WithServices(services => services.AddSingleton<IHealthCheck>(new StubHealthCheck(
                "external",
                HealthCheckKind.Liveness,
                HealthStatus.Healthy,
                touchesExternalDependency: true)));

        var thrown = await Assert.ThrowsAsync<Hosting.PlatformStartupException>(
            () => builder.StartAsync(CancellationToken.None));

        var error = Assert.IsType<Hosting.HostStartupError>(thrown.Error);
        Assert.Equal("Registration", error.Code);
        Assert.Equal("ExternalDependencyInLivenessCheck", error.Inner?.Code);
    }
}
