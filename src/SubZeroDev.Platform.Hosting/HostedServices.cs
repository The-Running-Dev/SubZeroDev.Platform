using Microsoft.Extensions.DependencyInjection;
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
    IEnumerable<IAuditSink> sinks,
    IEnumerable<IPermissionCatalog> permissionCatalogs,
    IEnumerable<IPermissionProvider> permissionProviders,
    IEnumerable<ITenantResolver> tenantResolvers,
    [FromKeyedServices(EntitlementContributorRegistration.ServiceKey)] IEnumerable<IEntitlementContributor> entitlementContributors,
    IEnumerable<IAuthenticationProvider> authenticationProviders,
    IHealthCheckRegistry healthChecks,
    IBackgroundWorkRegistry backgroundWork,
    IAuditSinkRegistry auditSinks,
    IPermissionCatalogRegistry permissionCatalogRegistry,
    IPermissionProviderRegistry permissionProviderRegistry,
    ITenantResolverRegistry tenantResolverRegistry,
    IEntitlementContributorRegistry entitlementContributorRegistry,
    IAuthenticationProviderRegistry authenticationProviderRegistry,
    PlatformOptions options) : IHostedLifecycleService
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

        foreach (var sink in sinks)
        {
            var registered = auditSinks.Register(sink);
            if (!registered.IsSuccess)
            {
                throw new PlatformStartupException(HostStartupError.Registration(
                    registered.Error,
                    registered.Error.Detail));
            }
        }

        // Permission names are collected from every contributing module and checked for duplicates
        // across modules — two modules claiming the same name is a composition defect, not a
        // last-writer-wins.
        foreach (var catalog in permissionCatalogs)
        {
            var registered = permissionCatalogRegistry.Register(catalog);
            if (!registered.IsSuccess)
            {
                throw new PlatformStartupException(HostStartupError.Registration(
                    registered.Error,
                    registered.Error.Detail));
            }
        }

        foreach (var provider in permissionProviders)
        {
            var registered = permissionProviderRegistry.Register(provider);
            if (!registered.IsSuccess)
            {
                throw new PlatformStartupException(HostStartupError.Registration(
                    registered.Error,
                    registered.Error.Detail));
            }
        }

        // Resolvers run in registration order, so registration failure aborts startup exactly like
        // every other registry rather than silently reordering by discovery order.
        foreach (var resolver in tenantResolvers)
        {
            var registered = tenantResolverRegistry.Register(resolver);
            if (!registered.IsSuccess)
            {
                throw new PlatformStartupException(HostStartupError.Registration(
                    registered.Error,
                    registered.Error.Detail));
            }
        }

        // Collected from the keyed slot, never from the plain IEntitlementContributor service type —
        // nothing in the container resolves a contributor by ordinary means, so IEntitlementEvaluator
        // stays the only public entry (S7.7).
        foreach (var contributor in entitlementContributors)
        {
            var registered = entitlementContributorRegistry.Register(contributor);
            if (!registered.IsSuccess)
            {
                throw new PlatformStartupException(HostStartupError.Registration(
                    registered.Error,
                    registered.Error.Detail));
            }
        }

        // The fifth registry. Absent in Local by rule rather than by accident, which is what the
        // profile validation below turns from a convention into a checked fact.
        foreach (var provider in authenticationProviders)
        {
            var registered = authenticationProviderRegistry.Register(provider);
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
        auditSinks.Freeze();
        permissionProviderRegistry.Freeze();
        tenantResolverRegistry.Freeze();
        entitlementContributorRegistry.Freeze();
        authenticationProviderRegistry.Freeze();

        // Validated only once every registry is closed: a rule about what is registered cannot be
        // checked while registration is still open. Every finding aborts startup — none degrades the
        // host into serving a composition nobody declared (I-C9).
        var violation = CompositionValidator.Validate(
            options.CompositionProfile,
            authenticationProviderRegistry.Registered,
            auditSinks.Registered,
            tenantResolverRegistry.Registered,
            entitlementContributorRegistry.Registered);

        if (violation is not null)
        {
            throw new PlatformStartupException(violation.Finding switch
            {
                CompositionFinding.AuthenticationProviderRequired =>
                    HostStartupError.AuthenticationProviderRequired(violation.Detail),
                CompositionFinding.DurableAuditSinkRequired =>
                    HostStartupError.DurableAuditSinkRequired(violation.Detail),
                _ => HostStartupError.RegistrationForbiddenByProfile(violation.Detail),
            });
        }

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
