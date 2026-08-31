namespace SubZeroDev.Platform.Abstractions;

/// <summary>The resource a check or an audit record names. Both halves are caller-controlled text and
/// pass through the redaction boundary before storage. Materialised here, ahead of the rest of
/// Authorization (S4), because <see cref="AuditEvent"/> declares it.</summary>
public readonly record struct ResourceRef(string Type, string Id)
{
    /// <summary>The resource's type, opaque to Platform.</summary>
    public string Type { get; } = Type ?? throw new ArgumentNullException(nameof(Type));

    /// <summary>The resource's id, opaque to Platform.</summary>
    public string Id { get; } = Id ?? throw new ArgumentNullException(nameof(Id));
}

/// <summary>An audit record's identity. Minted by the writer, not the sink, so a <c>Required</c>
/// write that fails can be reported by id.</summary>
/// <param name="Value">The identifier.</param>
public readonly record struct AuditEventId(Guid Value)
{
    /// <summary>Mints a new identifier.</summary>
    /// <returns>The identifier.</returns>
    public static AuditEventId CreateNew() => new(Guid.NewGuid());

    /// <inheritdoc/>
    public override string ToString() => Value.ToString();
}

/// <summary>The audited action's stable name. Caller-controlled; passes through the redaction
/// boundary.</summary>
public readonly record struct AuditAction(string Value)
{
    /// <summary>The action's stable name.</summary>
    public string Value { get; } = Value ?? throw new ArgumentNullException(nameof(Value));

    /// <inheritdoc/>
    public override string ToString() => Value;
}

/// <summary>What an audited action resolved to.</summary>
public enum AuditOutcome
{
    /// <summary>The action was permitted and, where it wrote state, succeeded.</summary>
    Allowed,

    /// <summary>The action was refused by authorization or entitlement.</summary>
    Denied,

    /// <summary>The action was attempted and failed.</summary>
    Failed,
}

/// <summary>What happens when the audit write itself fails.</summary>
public enum AuditClass
{
    /// <summary>The response becomes a retryable failure and readiness degrades.</summary>
    Required,

    /// <summary>Logged, readiness degrades, the response is unaffected.</summary>
    Recorded,
}

/// <summary>One audited fact. No payload, no changed-field list and no free-form detail member, and
/// none may be added — a consumer wanting field-level change history builds it in its own
/// tables.</summary>
public sealed record AuditEvent(
    AuditEventId Id,
    DateTimeOffset OccurredAt,
    PrincipalId Actor,
    PrincipalKind ActorKind,
    TenantId Tenant,
    AuditAction Action,
    ResourceRef? Resource,
    AuditOutcome Outcome,
    CorrelationId Correlation,
    AuditClass Class);

/// <summary>Platform's own audited action names. Public surface: they appear in the record an
/// auditor reads.</summary>
public static class PlatformAuditActions
{
    /// <summary>An authorization decision denied. Written by the evaluator only (S4).</summary>
    public static AuditAction AuthorizationDenied { get; } = new("platform.authorization.denied");

    /// <summary>A shared-read scope opened (S6).</summary>
    public static AuditAction SharedReadScopeOpened { get; } = new("platform.tenancy.shared-read");

    /// <summary>A tenant published a row for cross-tenant reading (S6).</summary>
    public static AuditAction ResourceShared { get; } = new("platform.tenancy.resource-shared");

    /// <summary>A licence's resolved state changed (S12).</summary>
    public static AuditAction LicenceStateChanged { get; } = new("platform.licensing.state-changed");

    /// <summary>A subscription's resolved entitlement changed (S11).</summary>
    public static AuditAction EntitlementChanged { get; } = new("platform.billing.entitlement-changed");
}

/// <summary>Writes an audit record. Framework packages, modules and product code all write through
/// this.</summary>
public interface IAuditWriter
{
    /// <summary>Mints the id, stamps the instant from <see cref="IClock"/> and the actor, tenant and
    /// correlation from the ambient scope, redacts the three caller-controlled strings, and
    /// dispatches to every sink.</summary>
    /// <param name="action">The audited action's stable name.</param>
    /// <param name="resource">The resource the action names, if any.</param>
    /// <param name="outcome">What the action resolved to.</param>
    /// <param name="auditClass">What happens if the write itself fails.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Success, or why the write did not complete.</returns>
    Task<Result<AuditError>> WriteAsync(
        AuditAction action,
        ResourceRef? resource,
        AuditOutcome outcome,
        AuditClass auditClass,
        CancellationToken cancellationToken);
}

/// <summary>A destination for audit records.</summary>
public interface IAuditSink
{
    /// <summary>The sink's name, unique within the registry.</summary>
    string Name { get; }

    /// <summary>Whether records survive process restart. Declared rather than inferred, so startup
    /// can reject an <c>Operated</c> composition that has no durable sink.</summary>
    bool IsDurable { get; }

    /// <summary>Writes one record.</summary>
    /// <param name="auditEvent">The record.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>Success, or why the write did not complete.</returns>
    Task<Result<AuditError>> WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken);
}

/// <summary>Why an audit write did not complete.</summary>
public sealed record AuditError : PlatformError
{
    private AuditError(string code, bool isRetryable, string detail)
        : base(code)
    {
        IsRetryable = isRetryable;
        Detail = detail;
    }

    /// <summary>Names the cause.</summary>
    public string Detail { get; }

    /// <inheritdoc/>
    public override bool IsRetryable { get; }

    /// <summary>A sink could not write.</summary>
    /// <param name="sink">The sink's name.</param>
    /// <returns>The error.</returns>
    public static AuditError SinkUnavailable(string sink) =>
        new(nameof(SinkUnavailable), isRetryable: true, $"Audit sink '{sink}' could not write the record.");

    /// <summary>A sink refused the record as malformed.</summary>
    /// <param name="sink">The sink's name.</param>
    /// <param name="reason">Why it was refused.</param>
    /// <returns>The error.</returns>
    public static AuditError SinkRejected(string sink, string reason) =>
        new(nameof(SinkRejected), isRetryable: false, $"Audit sink '{sink}' rejected the record: {reason}");
}
