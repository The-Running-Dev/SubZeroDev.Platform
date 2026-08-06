using Serilog.Core;
using Serilog.Events;
using SubZeroDev.Platform.Abstractions;

namespace SubZeroDev.Platform.Observability;

/// <summary>Stamps correlation, tenant, culture and actor on a log event when an operation scope is
/// open, and adds nothing when one is not — an ambient scope is optional, and a log line outside one
/// (during startup, for instance) must not throw or invent placeholder values.</summary>
internal sealed class AmbientScopeEnricher(IOperationScopeAccessor? accessor) : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var scope = accessor?.Current;
        if (scope is null)
        {
            return;
        }

        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("correlation", scope.Correlation.TraceId));
        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("tenant", scope.Tenant.Value));
        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("culture", scope.Culture.Value));

        var actor = scope.Principal?.Identity?.Name;
        if (!string.IsNullOrEmpty(actor))
        {
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("actor", actor));
        }
    }
}
