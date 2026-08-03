using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Hosting;
using SubZeroDev.Platform.Testing;

namespace SubZeroDev.Platform.Tests;

public sealed class BackgroundWorkTests
{
    [Fact]
    public async Task One_tick_is_one_tick()
    {
        var work = new CountingBackgroundWork("counter", HostRoles.Both);
        await using var host = await StartAsync(HostRole.Web, work);

        await host.RunBackgroundWorkOnceAsync(work.Name, CancellationToken.None);

        Assert.Equal(1, work.Ticks);
    }

    [Fact]
    public async Task Worker_work_does_not_run_in_the_web_host()
    {
        var workerOnly = new CountingBackgroundWork("worker-only", HostRoles.Worker);
        var both = new CountingBackgroundWork("both", HostRoles.Both);

        await using var host = await StartAsync(HostRole.Web, workerOnly, both);
        var registry = host.Services.GetRequiredService<IBackgroundWorkRegistry>();

        Assert.Equal(["both"], registry.ForRole(HostRole.Web).Select(registered => registered.Name.Value));
        Assert.Contains(registry.ForRole(HostRole.Worker), registered => registered.Name == workerOnly.Name);
    }

    [Fact]
    public async Task Hosting_drives_the_tick_on_the_declared_interval()
    {
        // The timer belongs to Hosting, so this is the one background-work assertion that needs a
        // real host rather than the test host, which suppresses the schedule on purpose.
        var work = new CountingBackgroundWork("ticker", HostRoles.Web) { Interval = TimeSpan.FromMilliseconds(20) };

        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = "Production",
        });

        builder.Configuration.AddInMemoryCollection(Settings.Required());
        builder.Services.AddSingleton<IBackgroundWork>(work);
        builder.AddPlatformWebHost();

        using var host = builder.Build();
        await host.StartAsync(CancellationToken.None);

        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (work.Ticks < 3 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20, CancellationToken.None);
            }
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }

        Assert.True(work.Ticks >= 3, $"Expected at least three ticks, saw {work.Ticks}.");
    }

    [Fact]
    public async Task The_test_host_runs_no_timer_so_a_single_tick_assertion_cannot_flake()
    {
        var work = new CountingBackgroundWork("quiet", HostRoles.Both) { Interval = TimeSpan.FromMilliseconds(10) };
        await using var host = await StartAsync(HostRole.Worker, work);

        await Task.Delay(100, CancellationToken.None);

        Assert.Equal(0, work.Ticks);
    }

    private static Task<IPlatformTestHost> StartAsync(HostRole role, params IBackgroundWork[] work) =>
        PlatformTestHost.CreateBuilder()
            .WithRole(role)
            .WithServices(services =>
            {
                foreach (var unit in work)
                {
                    services.AddSingleton(unit);
                }
            })
            .StartAsync(CancellationToken.None);
}
