using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;

namespace SubZeroDev.Platform.Persistence;

/// <summary>The writer <c>AddPlatformPersistence</c> installs in place of Core's — the sink's
/// enlistment in the ambient transaction (<c>design/30-slices.md</c> § S3). A successful action that
/// wrote state (<see cref="AuditOutcome.Allowed"/>, with a write-intent transaction open) enlists its
/// audit event on that transaction rather than dispatching it: <see cref="UnitOfWork"/> flushes every
/// enlisted event right before commit, so rolling the action back leaves no dispatched event and
/// committing dispatches each exactly once. A denial, a read, or a failure that wrote nothing — every
/// other case — dispatches immediately, in its own right, after the outcome is already known, so it
/// survives the action's own rollback.</summary>
internal sealed class TransactionalAuditWriter(
    AuditEventFactory factory,
    AuditSinkDispatcher dispatcher,
    IAmbientTransactionAccessor ambient) : IAuditWriter
{
    public Task<Result<AuditError>> WriteAsync(
        AuditAction action,
        ResourceRef? resource,
        AuditOutcome outcome,
        AuditClass auditClass,
        CancellationToken cancellationToken)
    {
        var auditEvent = factory.Build(action, resource, outcome, auditClass);

        if (outcome == AuditOutcome.Allowed
            && ambient.Current is AmbientTransaction { Intent: TransactionIntent.Write } transaction)
        {
            transaction.PendingAuditEvents.Add((auditEvent, auditClass));
            return Task.FromResult(Result<AuditError>.Success());
        }

        return dispatcher.DispatchAsync(auditEvent, auditClass, cancellationToken);
    }
}
