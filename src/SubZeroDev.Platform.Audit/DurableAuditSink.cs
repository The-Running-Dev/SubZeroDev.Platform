using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Persistence;

namespace SubZeroDev.Platform.Audit;

/// <summary>The sink an <c>Operated</c> host installs to satisfy I-C2: it declares
/// <see cref="IsDurable"/>, so a host carrying this module starts where the same host without it
/// fails with <c>HostStartupError.DurableAuditSinkRequired</c> (S13.6, S8.6).</summary>
/// <remarks>Writes inside the ambient transaction when one is open — which is what makes a
/// successful action's audit row commit or roll back atomically with the state change it audits —
/// and against a transaction of its own, committed immediately, when none is: a denial, a read, or a
/// failure dispatches ahead of any transaction the pipeline has opened
/// (<c>design/20-contract.md</c>, Public surface §6).</remarks>
internal sealed class DurableAuditSink(
    AuditStore store, IAmbientTransactionAccessor ambient, IProviderCapability capability) : IAuditSink
{
    public string Name => "platform.audit.durable";

    public bool IsDurable => true;

    public async Task<Result<AuditError>> WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);

        if (ambient.Current is { } current)
        {
            try
            {
                await store.InsertAsync(current.Connection, current.Transaction, auditEvent, cancellationToken)
                    .ConfigureAwait(false);
                return Result<AuditError>.Success();
            }
            catch (Exception)
            {
                return Result<AuditError>.Failure(AuditError.SinkUnavailable(Name));
            }
        }

        var opened = await capability.BeginAsync(TransactionIntent.Write, cancellationToken).ConfigureAwait(false);
        if (!opened.IsSuccess)
        {
            return Result<AuditError>.Failure(AuditError.SinkUnavailable(Name));
        }

        var transaction = opened.Value;
        try
        {
            await store.InsertAsync(transaction.Connection, transaction.Transaction, auditEvent, cancellationToken)
                .ConfigureAwait(false);
            await transaction.Transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Result<AuditError>.Success();
        }
        catch (Exception)
        {
            try
            {
                await transaction.Transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort: the connection may already be gone.
            }

            return Result<AuditError>.Failure(AuditError.SinkUnavailable(Name));
        }
        finally
        {
            await transaction.Connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}
