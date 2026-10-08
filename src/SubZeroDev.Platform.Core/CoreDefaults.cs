using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SubZeroDev.Platform.Abstractions;

namespace SubZeroDev.Platform.Core;

/// <summary>The Core defaults every process that runs platform code registers, host or not: the
/// clock, the trace-context codec a scope originates from, the operation scope and its accessors,
/// and the audit writer those accessors feed. Hosting
/// registers these as the first part of its own defaults; migrate mode registers these alone, since
/// it runs seeders and nothing else Core composes.</summary>
internal static class CoreDefaults
{
    /// <summary>Registers the ambient defaults, idempotently.</summary>
    /// <param name="services">The service collection.</param>
    internal static void AddAmbientDefaults(IServiceCollection services)
    {
        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton<ITraceContextCodec, TraceContextCodec>();
        services.TryAddSingleton<AmbientOperationScope>();
        services.TryAddSingleton<IOperationScopeAccessor, OperationScopeAccessor>();
        services.TryAddSingleton<IOperationScopeFactory, OperationScopeFactory>();
        services.TryAddSingleton<ICurrentTenant, CurrentTenant>();
        services.TryAddSingleton<ICurrentPrincipal, CurrentPrincipal>();
        services.TryAddSingleton<ICurrentCorrelation, CurrentCorrelation>();
        services.TryAddSingleton<ICurrentCulture, CurrentCulture>();

        // A unit of work dispatches enlisted audit events before commit, so anything that opens one
        // needs the dispatcher, even where no sink is registered and dispatch reaches none.
        services.TryAddSingleton<AuditSinkHealthState>();
        services.TryAddSingleton<AuditEventFactory>();
        services.TryAddSingleton<AuditSinkDispatcher>();
        services.TryAddSingleton<IAuditSinkRegistry, AuditSinkRegistry>();
        services.TryAddSingleton<IAuditWriter>(provider => new AuditWriter(
            provider.GetRequiredService<AuditEventFactory>(),
            provider.GetRequiredService<AuditSinkDispatcher>()));
    }
}
