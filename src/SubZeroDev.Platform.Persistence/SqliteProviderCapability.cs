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
        // Fails fast rather than waiting indefinitely: Microsoft.Data.Sqlite treats a zero busy
        // timeout as "no timeout" (retries forever) rather than SQLite's own "fail immediately", so
        // this uses the shortest timeout the connection string actually honours. An immediate
        // transaction acquires SQLite's single write lock up front — the same mutual exclusion an
        // exclusive transaction would give for this purpose, and the one the ADO surface exposes a
        // real DbTransaction for, which the runner needs to hand to a module's migration.
        var connection = new SqliteConnection(ConnectionString(busyTimeout: TimeSpan.FromSeconds(1)));
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
        _ => TransactionError.Faulted(exception.Message),
    };

    private string ConnectionString(TimeSpan? busyTimeout = null)
    {
        var builder = new SqliteConnectionStringBuilder(options.ConnectionString)
        {
            DefaultTimeout = (int)(busyTimeout ?? options.SqliteBusyWaitBound).TotalSeconds,

            // Pooling reuses a connection whose schema snapshot can predate a DDL change another
            // pooled connection just committed — reproduced directly: a fresh connection
            // immediately after a migration's commit intermittently read zero foreign keys off a
            // table that plainly has one, and the failure vanished at 50/50 with pooling off.
            Pooling = false,
        };

        return builder.ConnectionString;
    }

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
    internal static string SnakeCase(string value)
    {
        Span<char> buffer = stackalloc char[value.Length * 2];
        var length = 0;

        foreach (var character in value)
        {
            if (char.IsUpper(character))
            {
                if (length > 0)
                {
                    buffer[length++] = '_';
                }

                buffer[length++] = char.ToLowerInvariant(character);
            }
            else
            {
                buffer[length++] = character;
            }
        }

        return new string(buffer[..length]);
    }
}
