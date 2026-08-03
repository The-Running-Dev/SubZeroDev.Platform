using System.Data.Common;
using System.Globalization;
using Npgsql;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;

namespace SubZeroDev.Platform.Persistence;

/// <summary>The PostgreSQL capability. Serves everything but the single-file installation.</summary>
internal sealed class PostgreSqlProviderCapability(PersistenceOptions options) : IProviderCapability
{
    /// <summary>Same fixed-width form as SQLite's, so a store that formats and compares instants
    /// the same way on both providers needs no provider-specific branch.</summary>
    private const string InstantFormat = "yyyy-MM-ddTHH:mm:ss.fffffffZ";

    /// <summary>A fixed, arbitrary key for the migrate-mode advisory lock. One key: migrate mode
    /// locks the whole store, not one module at a time, because migrations across modules must
    /// still not interleave with a concurrent invocation.</summary>
    private const long MigrationLockKey = 725_910_442_017_558_121;

    public PersistenceProvider Provider => PersistenceProvider.PostgreSql;

    public string FormatInstant(DateTimeOffset instant) =>
        instant.UtcDateTime.ToString(InstantFormat, CultureInfo.InvariantCulture);

    public bool TryParseInstant(string stored, out DateTimeOffset instant)
    {
        if (DateTime.TryParseExact(
                stored,
                InstantFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            instant = new DateTimeOffset(parsed, TimeSpan.Zero);
            return true;
        }

        instant = default;
        return false;
    }

    public byte[] EncodeIdentifier(Guid value)
    {
        var bytes = value.ToByteArray();
        Array.Reverse(bytes, 0, 4);
        Array.Reverse(bytes, 4, 2);
        Array.Reverse(bytes, 6, 2);
        return bytes;
    }

    public bool TryDecodeIdentifier(ReadOnlySpan<byte> encoded, out Guid value)
    {
        if (encoded.Length != 16)
        {
            value = default;
            return false;
        }

        Span<byte> reordered = stackalloc byte[16];
        encoded.CopyTo(reordered);
        reordered[..4].Reverse();
        reordered[4..6].Reverse();
        reordered[6..8].Reverse();
        value = new Guid(reordered);
        return true;
    }

    public string MigrationHistoryTable(ModuleName module) =>
        $"platform_migrations_{Naming.SnakeCase(module.Value)}";

    public async Task<Result<IAmbientTransaction, TransactionError>> BeginAsync(
        TransactionIntent intent,
        CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(options.ConnectionString);

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            return Result<IAmbientTransaction, TransactionError>.Success(
                new AmbientTransaction(intent, connection, transaction));
        }
        catch (Exception exception)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            return Result<IAmbientTransaction, TransactionError>.Failure(Classify(exception));
        }
    }

    public async Task<Result<IMigrationLock, MigrationError>> AcquireMigrationLockAsync(
        CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(options.ConnectionString);
        NpgsqlTransaction? transaction = null;

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var tryLock = connection.CreateCommand();
            tryLock.CommandText = "SELECT pg_try_advisory_lock($1);";
            tryLock.Parameters.AddWithValue(MigrationLockKey);
            var acquired = (bool)(await tryLock.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;

            if (!acquired)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                return Result<IMigrationLock, MigrationError>.Failure(MigrationError.Locked());
            }

            // The advisory lock is session-scoped and independent of any transaction; a transaction
            // is opened anyway so the runner has one connection and one transaction to apply
            // migrations through, on the same terms as SQLite.
            transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            return Result<IMigrationLock, MigrationError>.Failure(MigrationError.Unavailable());
        }

        return Result<IMigrationLock, MigrationError>.Success(new PostgresMigrationLock(connection, transaction));
    }

    public Task<Result<ConfigurationError>> AssertStartupPreconditionsAsync(CancellationToken cancellationToken) =>
        // PostgreSQL has no journal-mode analogue and no capability-level precondition of its own;
        // reachability is a readiness concern, not a startup abort.
        Task.FromResult(Result<ConfigurationError>.Success());

    public TransactionError Classify(Exception exception) => exception switch
    {
        PostgresException { SqlState: "40001" or "40P01" } => TransactionError.Conflict(),
        NpgsqlException => TransactionError.Unavailable(),

        // Npgsql surfaces a connect or command timeout as a cancellation rather than as an
        // NpgsqlException, so without this arm the single most retryable condition in the system —
        // a database that is merely down — reports itself as not retryable.
        OperationCanceledException or TimeoutException => TransactionError.Unavailable(),
        _ => TransactionError.Faulted(),
    };

    private sealed class PostgresMigrationLock(NpgsqlConnection connection, NpgsqlTransaction transaction)
        : IMigrationLock
    {
        public DbConnection Connection => connection;

        public DbTransaction Transaction => transaction;

        public async ValueTask DisposeAsync()
        {
            try
            {
                await transaction.RollbackAsync().ConfigureAwait(false);
            }
            catch
            {
                // Already committed or rolled back — nothing left to release there.
            }

            try
            {
                await using var unlock = connection.CreateCommand();
                unlock.CommandText = "SELECT pg_advisory_unlock($1);";
                unlock.Parameters.AddWithValue(MigrationLockKey);
                await unlock.ExecuteScalarAsync().ConfigureAwait(false);
            }
            catch
            {
                // Best-effort: closing the connection releases a session-level advisory lock
                // regardless.
            }
            finally
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
