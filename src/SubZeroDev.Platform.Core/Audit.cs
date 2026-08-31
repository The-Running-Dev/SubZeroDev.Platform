using Microsoft.Extensions.Logging;
using SubZeroDev.Platform.Abstractions;

namespace SubZeroDev.Platform.Core;

/// <summary>Builds an <see cref="AuditEvent"/> from the ambient scope, redacting the three
/// caller-controlled strings. Internal and shared with Persistence via
/// <c>InternalsVisibleTo</c> — the transaction-aware writer Persistence installs reuses this rather
/// than re-implementing minting and redaction.</summary>
internal sealed class AuditEventFactory(
    IClock clock,
    ICurrentPrincipal principal,
    ICurrentTenant tenant,
    ICurrentCorrelation correlation)
{
    internal AuditEvent Build(
        AuditAction action, ResourceRef? resource, AuditOutcome outcome, AuditClass auditClass)
    {
        var current = principal.Current;
        var redactedAction = new AuditAction(Redaction.RedactValue(action.Value));
        var redactedResource = resource is { } value
            ? new ResourceRef(Redaction.RedactValue(value.Type), Redaction.RedactValue(value.Id))
            : (ResourceRef?)null;

        return new AuditEvent(
            AuditEventId.CreateNew(),
            clock.UtcNow,
            current.Id,
            current.Kind,
            tenant.Current,
            redactedAction,
            redactedResource,
            outcome,
            correlation.Current,
            auditClass);
    }
}

/// <summary>Whether every registered audit sink accepted its most recent write. Read by
/// <see cref="AuditSinkHealthCheck"/>; written only by <see cref="AuditSinkDispatcher"/> — I-U10.</summary>
internal sealed class AuditSinkHealthState
{
    private readonly Lock _gate = new();
    private string? _detail;

    internal string? Detail
    {
        get { lock (_gate) { return _detail; } }
    }

    internal void MarkDegraded(string detail)
    {
        lock (_gate)
        {
            _detail = detail;
        }
    }

    internal void MarkHealthy()
    {
        lock (_gate)
        {
            _detail = null;
        }
    }
}

/// <summary>Dispatches one built <see cref="AuditEvent"/> to every registered sink and applies the
/// class rule (<c>design/20-contract.md</c>, Error semantics § 4): a <see cref="AuditClass.Required"/>
/// write whose sink failed turns into a failure a caller may retry; a <see cref="AuditClass.Recorded"/>
/// write leaves the response unaffected. Either class degrades <c>platform.audit.sink</c> on
/// failure, and a fully successful dispatch clears the degradation. Internal and shared with
/// Persistence via <c>InternalsVisibleTo</c>, so the transaction-aware writer applies the identical
/// rule to a deferred, pre-commit flush.</summary>
internal sealed class AuditSinkDispatcher(
    IAuditSinkRegistry registry,
    AuditSinkHealthState health,
    ILogger<AuditSinkDispatcher> logger)
{
    internal async Task<Result<AuditError>> DispatchAsync(
        AuditEvent auditEvent, AuditClass auditClass, CancellationToken cancellationToken)
    {
        AuditError? failure = null;

        foreach (var sink in registry.Registered)
        {
            var written = await sink.WriteAsync(auditEvent, cancellationToken).ConfigureAwait(false);
            if (!written.IsSuccess)
            {
                failure ??= written.Error;
                logger.LogWarning(
                    "Audit sink '{Sink}' failed to write event {EventId}: {Code}.",
                    sink.Name, auditEvent.Id, written.Error.Code);
            }
        }

        if (failure is null)
        {
            health.MarkHealthy();
            return Result<AuditError>.Success();
        }

        health.MarkDegraded(failure.Detail);

        return auditClass == AuditClass.Required
            ? Result<AuditError>.Failure(failure)
            : Result<AuditError>.Success();
    }
}

/// <inheritdoc cref="IAuditWriter"/>
/// <remarks>Its dependencies (<see cref="AuditEventFactory"/>, <see cref="AuditSinkDispatcher"/>) are
/// internal, so this is registered by factory rather than by type — the same reason
/// <c>SubZeroDev.Platform.Persistence.OutboxWriter</c> is.</remarks>
public sealed class AuditWriter : IAuditWriter
{
    private readonly AuditEventFactory _factory;
    private readonly AuditSinkDispatcher _dispatcher;

    internal AuditWriter(AuditEventFactory factory, AuditSinkDispatcher dispatcher)
    {
        _factory = factory;
        _dispatcher = dispatcher;
    }

    /// <inheritdoc/>
    public Task<Result<AuditError>> WriteAsync(
        AuditAction action,
        ResourceRef? resource,
        AuditOutcome outcome,
        AuditClass auditClass,
        CancellationToken cancellationToken)
    {
        var auditEvent = _factory.Build(action, resource, outcome, auditClass);
        return _dispatcher.DispatchAsync(auditEvent, auditClass, cancellationToken);
    }
}

/// <summary>The default sink: writes to the log and never claims durability. What lets a deployment
/// with no audit package installed still get the brief's facts, in its log instead of
/// silence.</summary>
internal sealed class LogAuditSink(ILogger<LogAuditSink> logger) : IAuditSink
{
    public string Name => "platform.log";

    public bool IsDurable => false;

    public Task<Result<AuditError>> WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "audit {EventId} {Outcome} {Action} actor={ActorKind}:{Actor} tenant={Tenant} "
            + "resource={ResourceType}:{ResourceId} correlation={Correlation} class={Class} at={OccurredAt}",
            auditEvent.Id,
            auditEvent.Outcome,
            auditEvent.Action,
            auditEvent.ActorKind,
            auditEvent.Actor,
            auditEvent.Tenant,
            auditEvent.Resource?.Type,
            auditEvent.Resource?.Id,
            auditEvent.Correlation,
            auditEvent.Class,
            auditEvent.OccurredAt);

        return Task.FromResult(Result<AuditError>.Success());
    }
}

/// <summary>Reports <see cref="AuditSinkHealthState"/>. Readiness only: a degraded audit sink is not
/// a reason to fail liveness.</summary>
internal sealed class AuditSinkHealthCheck(AuditSinkHealthState state) : IHealthCheck
{
    public HealthCheckName Name => PlatformHealthChecks.AuditSink;

    public HealthCheckKind Kind => HealthCheckKind.Readiness;

    public HealthCheckCriticality Criticality => HealthCheckCriticality.Required;

    public TimeSpan Timeout { get; } = TimeSpan.FromSeconds(5);

    public bool TouchesExternalDependency => true;

    public Task<HealthCheckResult> CheckAsync(CancellationToken cancellationToken) =>
        Task.FromResult(state.Detail is { } detail
            ? new HealthCheckResult(HealthStatus.Degraded, detail, new Dictionary<string, string>())
            : new HealthCheckResult(HealthStatus.Healthy, null, new Dictionary<string, string>()));
}
