using System.Data.Common;
using SubZeroDev.Platform.Abstractions;

namespace SubZeroDev.Platform.Persistence;

/// <summary>Executes a consumer's compare-and-swap write on the ambient transaction and judges it.
/// Platform builds no SQL: the command is the consumer's own.</summary>
public interface IVersionGuard
{
    /// <summary>Runs <paramref name="guardedWrite"/> on the ambient Write transaction.</summary>
    /// <param name="guardedWrite">An UPDATE or DELETE whose predicate includes the row's key, the
    /// tenant, and <c>version = @expected</c>, and whose UPDATE sets <c>version = @expected + 1</c>.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Success when exactly one row matched; <see cref="TransactionError.StaleVersion"/> when
    /// none did, after which the unit of work is doomed.</returns>
    /// <exception cref="PlatformContractViolationException">No ambient transaction, or one opened
    /// <see cref="TransactionIntent.ReadOnly"/> (<c>GuardedWriteOutsideWriteTransaction</c>); or more
    /// than one row matched (<c>GuardedWriteNotSingleRow</c>).</exception>
    Task<Result<TransactionError>> ExecuteAsync(DbCommand guardedWrite, CancellationToken cancellationToken);
}

/// <summary>Judges the matched-row count and nothing else, so it needs no provider branch: both
/// providers report the rows an UPDATE or DELETE matched. Zero rows cannot be told apart — the row
/// changed, is gone, or belongs to another tenant — and the one error says so without saying which,
/// so nothing about a row the caller cannot see crosses back to it.</summary>
internal sealed class VersionGuard(IAmbientTransactionAccessor ambient) : IVersionGuard
{
    public async Task<Result<TransactionError>> ExecuteAsync(DbCommand guardedWrite, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(guardedWrite);

        if (ambient.Current is not AmbientTransaction { Intent: TransactionIntent.Write } transaction)
        {
            throw new PlatformContractViolationException(ContractViolation.GuardedWriteOutsideWriteTransaction());
        }

        guardedWrite.Connection = transaction.Connection;
        guardedWrite.Transaction = transaction.Transaction;
        var matched = await guardedWrite.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        switch (matched)
        {
            case 1:
                return Result<TransactionError>.Success();
            case 0:
                // Doomed rather than merely reported: a caller that ignores this result must still
                // not commit the rest of a unit built on a read that no longer holds.
                transaction.Doomed = true;
                return Result<TransactionError>.Failure(TransactionError.StaleVersion());
            default:
                // A predicate that is not keyed to one row is a defect in the caller. Unit of work
                // rolls back on a contract violation, so neither row's change survives.
                throw new PlatformContractViolationException(ContractViolation.GuardedWriteNotSingleRow());
        }
    }
}
