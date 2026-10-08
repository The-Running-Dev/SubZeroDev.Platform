using Microsoft.Data.Sqlite;
using Npgsql;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;

namespace SubZeroDev.Platform.Persistence;

/// <summary>Opens the one modelled cross-tenant read. Read-only, one declared type, audited once per
/// scope.</summary>
public interface ISharedReadScopeFactory
{
    /// <summary>Widens the query filter to "mine, or shared" for <typeparamref name="TEntity"/>
    /// only, for the scope's lifetime. Emits one <see cref="AuditClass.Required"/> audit record when
    /// the scope opens, and does not open at all when that record could not be written.</summary>
    /// <typeparam name="TEntity">The one shareable type this scope widens the filter for.</typeparam>
    /// <returns>A handle that narrows the filter back when disposed, or the audit write's failure
    /// — the retryable failure the class rule makes of it (Error semantics § 4), with the filter
    /// untouched.</returns>
    Result<IDisposable, AuditError> Open<TEntity>() where TEntity : class, IShareable;

    /// <summary>Whether a shared-read scope is currently open for <typeparamref name="TEntity"/>.
    /// Persistence imposes no repository or ORM, so this is the seam a consumer's own query code —
    /// EF's <c>HasQueryFilter</c>, Dapper, raw ADO — consults at model build to decide whether to
    /// widen its own filter.</summary>
    /// <typeparam name="TEntity">The shareable type to check.</typeparam>
    /// <returns><see langword="true"/> if a scope opened for this exact type is currently
    /// open.</returns>
    bool IsOpenFor<TEntity>() where TEntity : class, IShareable;
}

/// <summary>Holds which entity type's shared-read scope is open for the current asynchronous flow.
/// Not static, on the same terms as <see cref="AmbientTransactionState"/>: the ambient value flows
/// with the operation, so overlapping operations on one host never race on shared mutable
/// state, and a scope left undisposed does not survive into a request that did not open it — the
/// next request begins its own asynchronous flow rather than inheriting this one's.</summary>
internal sealed class SharedReadScopeState
{
    private readonly AsyncLocal<Type?> _openFor = new();

    internal Type? OpenFor
    {
        get => _openFor.Value;
        set => _openFor.Value = value;
    }
}

/// <inheritdoc cref="ISharedReadScopeFactory"/>
internal sealed class SharedReadScopeFactory(
    SharedReadScopeState state, IAuditWriter auditWriter, AmbientTransactionState ambient) : ISharedReadScopeFactory
{
    public Result<IDisposable, AuditError> Open<TEntity>() where TEntity : class, IShareable
    {
        // I-T1: a write transaction already open would carry on writing with the widened rows in
        // reach. Refused before the audit record, so an escape that never opens is never recorded.
        if (ambient.Current is { Intent: TransactionIntent.Write })
        {
            throw new PlatformContractViolationException(ContractViolation.WriteInsideSharedReadScope());
        }

        // One audit record per scope, not per row (I-T4) — written before the filter widens, so a
        // caller never observes a widened read that was not recorded. Open() stays synchronous
        // because the widened state is an AsyncLocal, and one set inside an async method does not
        // flow back to its caller. An escape that could not be recorded does not open at all, and
        // the filter is left untouched.
        var written = auditWriter.WriteAsync(
            PlatformAuditActions.SharedReadScopeOpened,
            resource: null,
            AuditOutcome.Allowed,
            AuditClass.Required,
            CancellationToken.None).GetAwaiter().GetResult();

        if (!written.IsSuccess)
        {
            return Result<IDisposable, AuditError>.Failure(written.Error);
        }

        // A read-only transaction already open refuses its writes from here on — the same refusal a
        // unit of work opened inside the scope gets (S27.1).
        if (ambient.Current is AmbientTransaction { Intent: TransactionIntent.ReadOnly } transaction)
        {
            SharedReadWriteRefusal.Apply(transaction);
        }

        var previous = state.OpenFor;
        state.OpenFor = typeof(TEntity);
        return Result<IDisposable, AuditError>.Success(new Scope(state, previous));
    }

    public bool IsOpenFor<TEntity>() where TEntity : class, IShareable => state.OpenFor == typeof(TEntity);

    private sealed class Scope(SharedReadScopeState state, Type? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            state.OpenFor = previous;
        }
    }
}

/// <summary>Makes a read-only transaction refuse every write at the database while a shared-read
/// scope is open (I-T1). The scope makes another tenant's published rows visible to the caller's
/// own query code; a declared <see cref="TransactionIntent.ReadOnly"/> is only a declaration, so
/// without this a write issued under it would reach those rows.</summary>
/// <remarks>Internal and keyed on <see cref="AmbientTransaction.Provider"/>, not a new
/// <see cref="IProviderCapability"/> member: the capability is a public contract a third-party
/// provider implements, and the two statements below are the whole of the difference.</remarks>
internal static class SharedReadWriteRefusal
{
    // SQLITE_READONLY: what a write against a connection with query_only set fails with.
    private const int SqliteReadOnly = 8;

    // read_only_sql_transaction: what a write inside a READ ONLY transaction fails with.
    private const string PostgreSqlReadOnlyTransaction = "25006";

    internal static void Apply(AmbientTransaction transaction)
    {
        if (transaction.WritesRefused)
        {
            return;
        }

        using var command = transaction.Connection.CreateCommand();
        command.Transaction = transaction.Transaction;
        command.CommandText = transaction.Provider switch
        {
            PersistenceProvider.Sqlite => "PRAGMA query_only = ON;",
            PersistenceProvider.PostgreSql => "SET TRANSACTION READ ONLY;",
            _ => throw new NotSupportedException($"No write refusal for '{transaction.Provider}'."),
        };
        command.ExecuteNonQuery();
        transaction.WritesRefused = true;
    }

    /// <summary>Clears SQLite's flag before the connection returns to the pool. It belongs to the
    /// connection, not the transaction, so left set it would refuse the next unit of work's writes;
    /// PostgreSQL's ends with the transaction and needs nothing.</summary>
    internal static async Task ReleaseAsync(AmbientTransaction transaction)
    {
        if (!transaction.WritesRefused || transaction.Connection is not SqliteConnection connection)
        {
            return;
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA query_only = OFF;";
            await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // The flag could not be cleared, so this connection must never be handed out again.
            SqliteConnection.ClearPool(connection);
        }
    }

    internal static bool IsRefusal(Exception exception) => exception switch
    {
        SqliteException { SqliteErrorCode: SqliteReadOnly } => true,
        PostgresException { SqlState: PostgreSqlReadOnlyTransaction } => true,
        _ => false,
    };
}
