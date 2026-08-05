using System.Data.Common;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;

namespace SubZeroDev.Platform.Persistence;

/// <summary>Whether a transaction will only read, or will write. A parameter because no
/// implementation can infer it — "a transaction that will write begins immediate" is only
/// actionable if the caller says which kind it is opening.</summary>
public enum TransactionIntent
{
    /// <summary>The transaction will only read.</summary>
    ReadOnly,

    /// <summary>The transaction will write. Begins immediate, never deferred.</summary>
    Write,
}

/// <summary>Runs work inside one transaction over one connection. Commit and rollback happen
/// exactly once, here — never in a participant.</summary>
public interface IUnitOfWork
{
    /// <summary>Runs work with no return value inside one transaction.</summary>
    /// <param name="intent">Whether the transaction will only read, or will write.</param>
    /// <param name="work">The work. Enlists against <see cref="IAmbientTransactionAccessor"/>.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Success once committed, or why the transaction did not complete.</returns>
    Task<Result<TransactionError>> ExecuteAsync(
        TransactionIntent intent,
        Func<CancellationToken, Task> work,
        CancellationToken cancellationToken);

    /// <summary>Runs work that produces a value inside one transaction.</summary>
    /// <typeparam name="T">The value produced on success.</typeparam>
    /// <param name="intent">Whether the transaction will only read, or will write.</param>
    /// <param name="work">The work. Enlists against <see cref="IAmbientTransactionAccessor"/>.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The value once committed, or why the transaction did not complete.</returns>
    Task<Result<T, TransactionError>> ExecuteAsync<T>(
        TransactionIntent intent,
        Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken);
}

/// <summary>The one connection and transaction a unit of work opened. Every participant — the
/// product module's own data-access code and Platform's stores alike — enlists against this rather
/// than opening a connection of its own.</summary>
public interface IAmbientTransaction
{
    /// <summary>Whether this transaction was opened read-only or for writing.</summary>
    TransactionIntent Intent { get; }

    /// <summary>The connection every participant enlists against. A participant enlists and does
    /// nothing else with the lifetime — it does not commit, roll back or dispose it.</summary>
    DbConnection Connection { get; }

    /// <summary>The transaction every participant enlists against, on the same terms as
    /// <see cref="Connection"/>.</summary>
    DbTransaction Transaction { get; }
}

/// <summary>Reads the ambient transaction, when one is open. The outbox store, and a product's own
/// data-access code, enlist through this rather than opening a connection of their own.</summary>
public interface IAmbientTransactionAccessor
{
    /// <summary>The open transaction, or null outside <see cref="IUnitOfWork.ExecuteAsync(TransactionIntent, Func{CancellationToken, Task}, CancellationToken)"/>.</summary>
    IAmbientTransaction? Current { get; }
}

/// <summary>Holds the ambient transaction for the current asynchronous flow. Not static: the
/// ambient value flows with the operation, so overlapping operations on one host never race on
/// shared mutable state.</summary>
internal sealed class AmbientTransactionState
{
    private readonly AsyncLocal<IAmbientTransaction?> _current = new();

    internal IAmbientTransaction? Current
    {
        get => _current.Value;
        set => _current.Value = value;
    }
}

internal sealed class AmbientTransactionAccessor(AmbientTransactionState state) : IAmbientTransactionAccessor
{
    public IAmbientTransaction? Current => state.Current;
}

internal sealed record AmbientTransaction(
    TransactionIntent Intent,
    DbConnection Connection,
    DbTransaction Transaction) : IAmbientTransaction
{
    /// <summary>Rows <see cref="IOutboxWriter.Enqueue{TEvent}"/> staged against this transaction.
    /// <see cref="UnitOfWork"/> inserts them right before commit — which is what "the write happens
    /// on commit" means, and what gives a failed insert the same <c>TransactionError</c> handling as
    /// any other participant's write rather than a bare exception escaping a synchronous call.</summary>
    internal List<OutboxMessage> PendingOutboxMessages { get; } = [];
}

/// <summary>Chooses the capability the configured provider calls for. A capability holds no
/// per-transaction state — <see cref="IProviderCapability.BeginAsync"/> hands the connection and
/// transaction back to its caller — so one instance serves every overlapping unit of work.</summary>
internal static class ProviderCapabilityFactory
{
    internal static IProviderCapability Create(PlatformOptions options) => options.Persistence.Provider switch
    {
        PersistenceProvider.PostgreSql => new PostgreSqlProviderCapability(options.Persistence),
        PersistenceProvider.Sqlite => new SqliteProviderCapability(options.Persistence),
        _ => throw new NotSupportedException($"No provider capability for '{options.Persistence.Provider}'."),
    };
}

/// <inheritdoc cref="IUnitOfWork"/>
internal sealed class UnitOfWork(IProviderCapability capability, AmbientTransactionState ambient, IOutboxStore outboxStore)
    : IUnitOfWork
{
    public async Task<Result<TransactionError>> ExecuteAsync(
        TransactionIntent intent,
        Func<CancellationToken, Task> work,
        CancellationToken cancellationToken)
    {
        var result = await ExecuteAsync<object?>(
            intent,
            async token =>
            {
                await work(token).ConfigureAwait(false);
                return null;
            },
            cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? Result<TransactionError>.Success()
            : Result<TransactionError>.Failure(result.Error);
    }

    public async Task<Result<T, TransactionError>> ExecuteAsync<T>(
        TransactionIntent intent,
        Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);

        var opened = await capability.BeginAsync(intent, cancellationToken).ConfigureAwait(false);
        if (!opened.IsSuccess)
        {
            return Result<T, TransactionError>.Failure(opened.Error);
        }

        // The capability contract is public so a third-party provider can return its own
        // IAmbientTransaction implementation. Keep Platform's outbox staging state on an internal
        // wrapper over the returned handles rather than casting back to Platform's implementation.
        var providerTransaction = opened.Value;
        var transaction = new AmbientTransaction(
            providerTransaction.Intent,
            providerTransaction.Connection,
            providerTransaction.Transaction);
        var previous = ambient.Current;
        ambient.Current = transaction;

        try
        {
            var value = await work(cancellationToken).ConfigureAwait(false);

            // Enqueue stages rows rather than writing them, so a failed insert here is reported the
            // same way any other participant's write failure is — rolled back and classified —
            // rather than as a bare exception escaping Enqueue's synchronous call.
            foreach (var message in transaction.PendingOutboxMessages)
            {
                var inserted = await outboxStore.InsertAsync(message, cancellationToken).ConfigureAwait(false);
                if (!inserted.IsSuccess)
                {
                    try
                    {
                        // CancellationToken.None, not the caller's token — a cancelled token here
                        // must not prevent rollback from completing, the same reason every other
                        // rollback in this method uses it rather than the ambient token.
                        await transaction.Transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Best-effort: the connection may already be gone.
                    }

                    return Result<T, TransactionError>.Failure(inserted.Error);
                }
            }

            await transaction.Transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Result<T, TransactionError>.Success(value);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller asked to stop — a request aborted, a shutdown — not a database outage.
            // Classify exists to turn a provider's own connect-or-command timeout into Unavailable;
            // applying it here would report the caller's own cancellation as a retryable outage.
            // Rolling back and propagating the cancellation is the ordinary .NET convention instead.
            try
            {
                await transaction.Transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // The transaction is already gone — the rollback is moot.
            }

            throw;
        }
        catch (Exception exception)
        {
            try
            {
                await transaction.Transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // The transaction is already gone (connection dropped mid-write) — the rollback is
                // moot, and the original exception is the one worth reporting.
            }

            return Result<T, TransactionError>.Failure(capability.Classify(exception));
        }
        finally
        {
            ambient.Current = previous;
            await transaction.Connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}
