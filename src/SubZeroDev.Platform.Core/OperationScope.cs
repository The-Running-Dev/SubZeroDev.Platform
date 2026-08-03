using System.Security.Claims;
using SubZeroDev.Platform.Abstractions;

namespace SubZeroDev.Platform.Core;

/// <summary>The clock everything else reads. The one place a real instant enters the system.</summary>
internal sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>Holds the ambient scope for the current asynchronous flow. Not static: the ambient
/// context flows with the operation, and there is no shared mutable state for requests to race on.</summary>
internal sealed class AmbientOperationScope
{
    private readonly AsyncLocal<IOperationScope?> _current = new();

    internal IOperationScope? Current
    {
        get => _current.Value;
        set => _current.Value = value;
    }
}

internal sealed class OperationScopeAccessor(AmbientOperationScope ambient) : IOperationScopeAccessor
{
    public IOperationScope? Current => ambient.Current;
}

internal sealed class OperationScopeFactory(AmbientOperationScope ambient, ITraceContextCodec codec)
    : IOperationScopeFactory
{
    public IOperationScope Begin(TenantId tenant, ClaimsPrincipal? principal)
    {
        // Origination. The scope that calls this is the origin, so the root's trace-id is the
        // correlation — the same claim an inbound request with no traceparent makes.
        var handle = codec.StartRoot("platform.operation");
        var established = handle.Context;

        return new Scope(
            ambient,
            established,
            new CorrelationId(established.TraceId),
            tenant,
            principal,
            handle);
    }

    public IOperationScope Begin(
        TraceContext established,
        CorrelationId correlation,
        TenantId tenant,
        ClaimsPrincipal? principal) =>
        new Scope(ambient, established, correlation, tenant, principal, ownedTrace: null);

    private sealed class Scope : IOperationScope
    {
        private readonly AmbientOperationScope _ambient;
        private readonly IOperationScope? _previous;
        private readonly ITraceHandle? _ownedTrace;
        private bool _disposed;

        internal Scope(
            AmbientOperationScope ambient,
            TraceContext trace,
            CorrelationId correlation,
            TenantId tenant,
            ClaimsPrincipal? principal,
            ITraceHandle? ownedTrace)
        {
            _ambient = ambient;
            _previous = ambient.Current;
            _ownedTrace = ownedTrace;

            Trace = trace;
            Correlation = correlation;
            Tenant = tenant;
            Principal = principal;

            ambient.Current = this;
        }

        public CorrelationId Correlation { get; }

        public TenantId Tenant { get; }

        public ClaimsPrincipal? Principal { get; }

        public TraceContext Trace { get; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _ambient.Current = _previous;
            _ownedTrace?.Dispose();
        }
    }
}

internal sealed class CurrentTenant(IOperationScopeAccessor accessor) : ICurrentTenant
{
    public TenantId Current => AmbientScope.Require(accessor).Tenant;
}

internal sealed class CurrentPrincipal(IOperationScopeAccessor accessor) : ICurrentPrincipal
{
    public ClaimsPrincipal? Current => AmbientScope.Require(accessor).Principal;
}

internal sealed class CurrentCorrelation(IOperationScopeAccessor accessor) : ICurrentCorrelation
{
    public CorrelationId Current => AmbientScope.Require(accessor).Correlation;
}

internal static class AmbientScope
{
    /// <summary>Reading an ambient value outside a scope is a defect in the caller, not a runtime
    /// condition — so it throws rather than quietly returning a default, which is what keeps
    /// "correlation is always present" true as written.</summary>
    internal static IOperationScope Require(IOperationScopeAccessor accessor) =>
        accessor.Current
        ?? throw new PlatformContractViolationException(ContractViolation.NoAmbientOperationScope());
}
