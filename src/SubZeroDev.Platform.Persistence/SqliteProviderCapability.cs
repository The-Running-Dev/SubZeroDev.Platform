using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;

namespace SubZeroDev.Platform.Persistence;

/// <summary>The SQLite capability. Serves local developer execution and single-file homelab
/// installations — a production path, not a test double.</summary>
internal sealed class SqliteProviderCapability(PersistenceOptions options) : IProviderCapability
{
    /// <summary>Fixed-width, <c>Z</c>-suffixed, exactly seven fractional digits, never trimmed —
    /// the format every comparand bound as a SQL parameter must also use, so the comparison stays
    /// correct across a sub-second boundary.</summary>
    private const string InstantFormat = "yyyy-MM-ddTHH:mm:ss.fffffffZ";

    public PersistenceProvider Provider => PersistenceProvider.Sqlite;

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
        // Guid.ToByteArray() stores Data1/Data2/Data3 little-endian — the platform's native layout,
        // not RFC 4122's. Reversing those three fields yields network byte order, which is what
        // makes bytewise blob comparison equal mint order for a version-7 UUID.
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
        var connection = new SqliteConnection(ConnectionString());

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            // A transaction that will write begins immediate, never deferred: waiting for a
            // deferred-then-upgraded write to escalate can hit a busy condition no amount of
            // waiting resolves, because the read snapshot is no longer valid by then.
            var transaction = connection.BeginTransaction(deferred: intent == TransactionIntent.ReadOnly);
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
        // Bounded by the configured busy-wait, which is what that setting already means: how long a
        // write waits for the single write lock before failing, and acquiring this lock is exactly
        // that write. It must be bounded rather than zero — Microsoft.Data.Sqlite reads a zero
        // timeout as "retry forever" rather than SQLite's own "fail immediately", so zero would turn
        // a fail-fast lock into one that never fails. An immediate transaction takes the write lock
        // up front — the same mutual exclusion an exclusive transaction gives for this purpose, and
        // the one the ADO surface exposes a real DbTransaction for, which the runner hands to a
        // module's migration.
        var connection = new SqliteConnection(ConnectionString());
        DbTransaction? transaction = null;

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            // Migrate mode is what brings a fresh file into compliance — WAL must be set before any
            // transaction opens, since SQLite refuses to change journal mode inside one.
            await using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode=WAL;";
                await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            transaction = connection.BeginTransaction(deferred: false);
        }
        catch (SqliteException exception) when (IsBusy(exception))
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            return Result<IMigrationLock, MigrationError>.Failure(MigrationError.Locked());
        }
        catch (Exception)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            return Result<IMigrationLock, MigrationError>.Failure(MigrationError.Unavailable());
        }

        return Result<IMigrationLock, MigrationError>.Success(new SqliteMigrationLock(connection, transaction));
    }

    public async Task<Result<ConfigurationError>> AssertStartupPreconditionsAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(ConnectionString());

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Unreachable is a readiness condition, never a startup abort — the file may not exist
            // yet on a fresh installation, and migrate mode is what creates it.
            return Result<ConfigurationError>.Success();
        }

        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode;";
        var mode = (string?)await pragma.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? string.Empty;

        return string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase)
            ? Result<ConfigurationError>.Success()
            : Result<ConfigurationError>.Failure(
                ConfigurationError.UnsupportedJournalMode(connection.DataSource, mode));
    }

    public TransactionError Classify(Exception exception) => exception switch
    {
        SqliteException sqlite when IsBusy(sqlite) => TransactionError.Busy(),
        SqliteException => TransactionError.Unavailable(),
        OperationCanceledException or TimeoutException => TransactionError.Unavailable(),
        _ => TransactionError.Faulted(),
    };

    public async Task<Result<OutboxMessageId?, TransactionError>> StampClaimAsync(
        InstanceId holder,
        DateTimeOffset now,
        TimeSpan claimWindow,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqliteConnection(ConnectionString());
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE platform_outbox
                SET claimed_by = @holder, claimed_at = @now
                WHERE id = (
                    SELECT id FROM platform_outbox
                    WHERE processed_at IS NULL AND poisoned_at IS NULL
                      AND (claimed_at IS NULL OR claimed_at <= @expired)
                      AND COALESCE(next_attempt_at, occurred_at) <= @now
                    ORDER BY sequence
                    LIMIT 1
                )
                  AND processed_at IS NULL AND poisoned_at IS NULL
                  AND (claimed_at IS NULL OR claimed_at <= @expired)
                  AND COALESCE(next_attempt_at, occurred_at) <= @now
                RETURNING id;
                """;
            AddParameter(command, "@holder", holder.Value);
            AddParameter(command, "@now", FormatInstant(now));
            AddParameter(command, "@expired", FormatInstant(now - claimWindow));
            var claimed = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (claimed is null || claimed is DBNull)
            {
                return Result<OutboxMessageId?, TransactionError>.Success(null);
            }

            return TryDecodeIdentifier((byte[])claimed, out var id)
                ? Result<OutboxMessageId?, TransactionError>.Success(new OutboxMessageId(id))
                : Result<OutboxMessageId?, TransactionError>.Failure(TransactionError.Faulted());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Result<OutboxMessageId?, TransactionError>.Failure(Classify(exception));
        }
    }

    public async Task<Result<int, TransactionError>> DeleteBoundedAsync(
        PruneTarget target,
        DateTimeOffset olderThan,
        int batchSize,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqliteConnection(ConnectionString());
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = PruneSql.For(target);
            AddParameter(command, "@olderThan", FormatInstant(olderThan));
            AddParameter(command, "@batchSize", batchSize);
            var deleted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return Result<int, TransactionError>.Success(deleted);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Result<int, TransactionError>.Failure(Classify(exception));
        }
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private string ConnectionString(TimeSpan? busyTimeout = null)
    {
        var builder = new SqliteConnectionStringBuilder(options.ConnectionString)
        {
            DefaultTimeout = ToWholeSecondsAtLeastOne(busyTimeout ?? options.SqliteBusyWaitBound),

            // Pooling reuses a connection whose schema snapshot can predate a DDL change another
            // pooled connection just committed — reproduced directly: a fresh connection
            // immediately after a migration's commit intermittently read zero foreign keys off a
            // table that plainly has one, and the failure vanished at 50/50 with pooling off.
            Pooling = false,
        };

        return builder.ConnectionString;
    }

    /// <summary>Rounds up to at least one second. <c>DefaultTimeout</c> is whole seconds, so a
    /// configured bound under a second — valid, since the binder only requires it positive — would
    /// otherwise truncate to zero, which Microsoft.Data.Sqlite treats as "retry forever" rather than
    /// SQLite's own "fail immediately". Truncating a sub-second bound to zero would turn a bounded
    /// wait into an unbounded one, silently.</summary>
    private static int ToWholeSecondsAtLeastOne(TimeSpan value) =>
        Math.Max(1, (int)Math.Ceiling(value.TotalSeconds));

    private static bool IsBusy(SqliteException exception) =>
        exception.SqliteErrorCode is 5 /* SQLITE_BUSY */ or 6 /* SQLITE_LOCKED */;

    private sealed class SqliteMigrationLock(DbConnection connection, DbTransaction transaction) : IMigrationLock
    {
        public DbConnection Connection => connection;

        public DbTransaction Transaction => transaction;

        public async ValueTask DisposeAsync()
        {
            try
            {
                // A safety net only: the runner commits on success and rolls back on failure
                // itself. Rolling back an already-completed transaction throws, which the catch
                // below absorbs — there is nothing left to release beyond closing the connection.
                await transaction.RollbackAsync().ConfigureAwait(false);
            }
            catch
            {
                // Already committed or rolled back — nothing left to release.
            }
            finally
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}

internal static class Naming
{
    /// <summary>Lower snake case, so a migration history table name is legal and readable on both
    /// providers. Decided in S2, per the contract's unresolved list.</summary>
    /// <remarks><c>ModuleName</c> only requires non-empty and trims — it permits spaces, punctuation
    /// and anything else a developer types — and this result is interpolated unquoted into DDL and
    /// DML. Every character outside <c>[a-z0-9]</c> collapses to a single underscore rather than
    /// passing through, so no module name can produce a malformed or unintended SQL identifier; the
    /// migration runner's collision guard is what catches two names that collapse to the same
    /// table.</remarks>
    internal static string SnakeCase(string value)
    {
        Span<char> buffer = stackalloc char[value.Length * 2];
        var length = 0;

        foreach (var character in value)
        {
            if (char.IsAsciiLetterUpper(character))
            {
                if (length > 0 && buffer[length - 1] != '_')
                {
                    buffer[length++] = '_';
                }

                buffer[length++] = char.ToLowerInvariant(character);
            }
            else if (char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character))
            {
                buffer[length++] = character;
            }
            else if (length > 0 && buffer[length - 1] != '_')
            {
                // Anything not a plain ASCII letter or digit — space, hyphen, quote, semicolon, any
                // non-ASCII character — becomes one underscore rather than passing through.
                buffer[length++] = '_';
            }
        }

        while (length > 0 && buffer[length - 1] == '_')
        {
            length--;
        }

        return new string(buffer[..length]);
    }
}
