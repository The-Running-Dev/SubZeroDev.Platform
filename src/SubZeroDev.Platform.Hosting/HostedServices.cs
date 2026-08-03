using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;

namespace SubZeroDev.Platform.Hosting;

/// <summary>Collects everything registered into the container and freezes the registries.</summary>
/// <remarks>The work happens in <see cref="StartingAsync"/>, which the host runs before any
/// service's <c>StartAsync</c> — so the registries are populated and frozen before Kestrel binds,
/// and a rejected registration aborts startup rather than surfacing at the first probe.</remarks>
internal sealed class PlatformRegistryStartup(
    IEnumerable<IHealthCheck> checks,
    IEnumerable<IBackgroundWork> work,
    IHealthCheckRegistry healthChecks,
    IBackgroundWorkRegistry backgroundWork) : IHostedLifecycleService
{
    public Task StartingAsync(CancellationToken cancellationToken)
    {
        foreach (var check in checks)
        {
            var registered = healthChecks.Register(check);
            if (!registered.IsSuccess)
            {
                throw new PlatformStartupException(HostStartupError.Registration(
                    registered.Error,
                    registered.Error.Detail));
            }
        }

        foreach (var unit in work)
        {
            var registered = backgroundWork.Register(unit);
            if (!registered.IsSuccess)
            {
                throw new PlatformStartupException(HostStartupError.Registration(
                    registered.Error,
                    registered.Error.Detail));
            }
        }

        // One-way. Registration after this returns a failure rather than mutating a structure
        // concurrent probe readers are walking, which is what makes lock-free probing correct.
        healthChecks.Freeze();
        backgroundWork.Freeze();

        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Owns the timer for every background-work registration this host's role runs.</summary>
/// <remarks>Hosting owns the schedule and a registration owns one tick. That separation is what
/// makes background work testable: no fake clock drives a real timer, so a test replaces the
/// schedule and controls the clock.</remarks>
internal sealed class BackgroundWorkService(
    IBackgroundWorkRegistry registry,
    PlatformOptions options,
    ILogger<BackgroundWorkService> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var scheduled = registry.ForRole(options.Role);
        return scheduled.Count == 0
            ? Task.CompletedTask
            : Task.WhenAll(scheduled.Select(work => RunAsync(work, stoppingToken)));
    }

    private async Task RunAsync(IBackgroundWork work, CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(work.Interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
                await work.TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // A failing tick must not stop the loop: the next one is the retry, and the
                // condition that caused this is reported on readiness rather than by dying here.
                logger.LogError(exception, "Background work {Work} failed a tick.", work.Name.Value);
            }
        }
    }
}
