using System.Data.Common;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;

namespace SubZeroDev.Platform.Persistence;

/// <summary>One named, up-only migration within a module's history. Ordered within that history by
/// <see cref="Name"/>, ordinally — either-order application is a property across modules, never
/// within one.</summary>
public interface IModuleMigration
{
    /// <summary>The migration's name, unique and ordering within its module's history.</summary>
    string Name { get; }

    /// <summary>Applies the migration. Runs inside the runner's transaction; the migration commits
    /// and rolls back nothing itself.</summary>
    /// <param name="connection">The connection the runner opened.</param>
    /// <param name="transaction">The transaction the runner opened.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task ApplyAsync(DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken);
}

/// <summary>A module's contribution to migrate mode. Collected as
/// <see cref="IEnumerable{T}"/>&lt;<see cref="IModuleMigrationSource"/>&gt; from dependency
/// injection, the same route <c>IHealthCheck</c> and <c>IBackgroundWork</c> already use — a module
/// with no migrations registers nothing.</summary>
public interface IModuleMigrationSource
{
    /// <summary>The module these migrations belong to.</summary>
    ModuleName Module { get; }

    /// <summary>Every migration this module owns, in the order supplied. The runner applies them in
    /// <see cref="IModuleMigration.Name"/> order regardless of this list's order.</summary>
    IReadOnlyList<IModuleMigration> Migrations { get; }
}

/// <summary>One module's migration state, compared symmetrically: what it registers but has not
/// applied, and what is applied but no longer registered.</summary>
/// <param name="Module">The module.</param>
/// <param name="Pending">Registered migrations not yet applied.</param>
/// <param name="Surplus">Applied migrations this host no longer registers — the normal state of a
/// not-yet-restarted process once migrate mode has run elsewhere.</param>
public sealed record ModuleMigrationStatus(
    ModuleName Module,
    IReadOnlyList<string> Pending,
    IReadOnlyList<string> Surplus);

/// <summary>Compares and applies every registered module's migrations.</summary>
public interface IMigrationRunner
{
    /// <summary>Compares applied migrations against registered ones, per module.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>One status per registered module, or why the comparison failed.</returns>
    Task<Result<IReadOnlyList<ModuleMigrationStatus>, MigrationError>> GetStatusAsync(
        CancellationToken cancellationToken);

    /// <summary>Applies every pending migration across every registered module under the
    /// provider-native migration lock. A second concurrent invocation fails fast.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Success once every migration is applied, or why it stopped.</returns>
    Task<Result<MigrationError>> ApplyAsync(CancellationToken cancellationToken);
}

/// <inheritdoc cref="IMigrationRunner"/>
internal sealed class MigrationRunner(
    IEnumerable<IModuleMigrationSource> sources,
    IProviderCapability capability,
    IClock clock) : IMigrationRunner
{
    public async Task<Result<IReadOnlyList<ModuleMigrationStatus>, MigrationError>> GetStatusAsync(
        CancellationToken cancellationToken)
    {
        var begun = await capability.BeginAsync(TransactionIntent.ReadOnly, cancellationToken).ConfigureAwait(false);
        if (!begun.IsSuccess)
        {
            return Result<IReadOnlyList<ModuleMigrationStatus>, MigrationError>.Failure(MigrationError.Unavailable());
        }

        var support = begun.Value;

        try
        {
            var statuses = new List<ModuleMigrationStatus>();
            foreach (var source in sources)
            {
                var table = capability.MigrationHistoryTable(source.Module);
                var applied = await TryReadAppliedAsync(support.Connection, support.Transaction, table, cancellationToken)
                    .ConfigureAwait(false);

                var registeredNames = source.Migrations.Select(migration => migration.Name).ToHashSet(StringComparer.Ordinal);
                var pending = source.Migrations
                    .Select(migration => migration.Name)
                    .Where(name => !applied.Contains(name))
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToList();
                var surplus = applied
                    .Where(name => !registeredNames.Contains(name))
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToList();

                statuses.Add(new ModuleMigrationStatus(source.Module, pending, surplus));
            }

            // A module whose history table this host does not register at all — not just a
            // registered module missing some migrations, but the whole module — is surplus too:
            // the binaries are behind, and the comparison is symmetric.
            var accountedFor = sources
                .Select(source => capability.MigrationHistoryTable(source.Module))
                .ToHashSet(StringComparer.Ordinal);

            var historyTables = await TryListMigrationHistoryTablesAsync(
                support.Connection, support.Transaction, capability.Provider, cancellationToken).ConfigureAwait(false);

            foreach (var table in historyTables.Where(table => !accountedFor.Contains(table)))
            {
                var applied = await TryReadAppliedAsync(support.Connection, support.Transaction, table, cancellationToken)
                    .ConfigureAwait(false);

                var inferredModule = table.StartsWith("platform_migrations_", StringComparison.Ordinal)
                    ? table["platform_migrations_".Length..]
                    : table;

                statuses.Add(new ModuleMigrationStatus(
                    new ModuleName(inferredModule),
                    Pending: [],
                    Surplus: applied.OrderBy(name => name, StringComparer.Ordinal).ToList()));
            }

            return Result<IReadOnlyList<ModuleMigrationStatus>, MigrationError>.Success(statuses);
        }
        finally
        {
            try
            {
                await support.Transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Read-only: nothing was written, so a failed rollback changes nothing.
            }

            await support.Connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task<Result<MigrationError>> ApplyAsync(CancellationToken cancellationToken)
    {
        if (FindHistoryTableCollision() is { } collision)
        {
            return Result<MigrationError>.Failure(collision);
        }

        var acquired = await capability.AcquireMigrationLockAsync(cancellationToken).ConfigureAwait(false);
        if (!acquired.IsSuccess)
        {
            return Result<MigrationError>.Failure(acquired.Error);
        }

        // One transaction spans the whole run, so a failure at any migration rolls back every
        // migration this run applied. Not a choice so much as a consequence: on SQLite the lock
        // *is* this transaction, so committing per migration would release the exclusion mid-run
        // and let a second invocation interleave.
        await using var migrationLock = acquired.Value;
        var support = migrationLock;
        var savepointCounter = 0;

        foreach (var source in sources)
        {
            var table = capability.MigrationHistoryTable(source.Module);
            await EnsureHistoryTableAsync(support.Connection, support.Transaction, table, cancellationToken)
                .ConfigureAwait(false);

            var applied = await ReadAppliedAsync(support.Connection, support.Transaction, table, cancellationToken)
                .ConfigureAwait(false);

            foreach (var migration in source.Migrations.OrderBy(m => m.Name, StringComparer.Ordinal))
            {
                if (applied.Contains(migration.Name))
                {
                    continue;
                }

                var savepoint = $"sp_{savepointCounter++}";

                try
                {
                    await ExecuteAsync(support.Connection, support.Transaction, $"SAVEPOINT {savepoint};", cancellationToken)
                        .ConfigureAwait(false);

                    await migration.ApplyAsync(support.Connection, support.Transaction, cancellationToken)
                        .ConfigureAwait(false);

                    await InsertHistoryRowAsync(support.Connection, support.Transaction, table, migration.Name, cancellationToken)
                        .ConfigureAwait(false);

                    await ExecuteAsync(support.Connection, support.Transaction, $"RELEASE SAVEPOINT {savepoint};", cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    try
                    {
                        await ExecuteAsync(
                            support.Connection, support.Transaction, $"ROLLBACK TO SAVEPOINT {savepoint};", CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                        // Best-effort: the outer rollback below covers it regardless.
                    }

                    try
                    {
                        await support.Transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Best-effort.
                    }

                    return Result<MigrationError>.Failure(
                        MigrationError.Failed(source.Module, migration.Name, exception.Message));
                }
            }
        }

        await support.Transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result<MigrationError>.Success();
    }

    private async Task EnsureHistoryTableAsync(
        DbConnection connection, DbTransaction transaction, string table, CancellationToken cancellationToken)
    {
        await ExecuteAsync(
            connection,
            transaction,
            $"CREATE TABLE IF NOT EXISTS {table} (name TEXT PRIMARY KEY, applied_at TEXT NOT NULL);",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<HashSet<string>> ReadAppliedAsync(
        DbConnection connection, DbTransaction transaction, string table, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT name FROM {table};";

        var applied = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            applied.Add(reader.GetString(0));
        }

        return applied;
    }

    /// <summary>Rejects two modules whose history tables resolve to one name before anything is
    /// applied.</summary>
    /// <remarks>Module names are unique case-sensitively, so <c>Orders</c> and <c>orders</c> are two
    /// legal modules — and both resolve to one history table. Sharing a history is silent corruption
    /// of the exact mechanism per-module histories exist to provide: each module would read the
    /// other's applied list and skip its own migrations as already applied.</remarks>
    private MigrationError? FindHistoryTableCollision()
    {
        var byTable = new Dictionary<string, ModuleName>(StringComparer.Ordinal);

        foreach (var source in sources)
        {
            var table = capability.MigrationHistoryTable(source.Module);
            if (byTable.TryGetValue(table, out var existing) && existing != source.Module)
            {
                return MigrationError.HistoryTableCollision(existing, source.Module, table);
            }

            byTable[table] = source.Module;
        }

        return null;
    }

    /// <summary>Lists every migration history table actually present, tolerating a schema that does
    /// not exist yet — the ordinary state before the first successful <see cref="ApplyAsync"/>.</summary>
    private static async Task<IReadOnlyList<string>> TryListMigrationHistoryTablesAsync(
        DbConnection connection, DbTransaction transaction, PersistenceProvider provider, CancellationToken cancellationToken)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = provider == PersistenceProvider.Sqlite
                ? "SELECT name FROM sqlite_master WHERE type = 'table' AND name LIKE 'platform\\_migrations\\_%' ESCAPE '\\';"
                : "SELECT tablename FROM pg_tables WHERE schemaname = 'public' AND tablename LIKE 'platform\\_migrations\\_%' ESCAPE '\\';";

            var tables = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                tables.Add(reader.GetString(0));
            }

            return tables;
        }
        catch (DbException)
        {
            return [];
        }
    }

    /// <summary>Reads applied migrations for a status check, tolerating a history table that does
    /// not exist yet — that is the ordinary state before the first successful <see cref="ApplyAsync"/>.</summary>
    private async Task<HashSet<string>> TryReadAppliedAsync(
        DbConnection connection, DbTransaction transaction, string table, CancellationToken cancellationToken)
    {
        try
        {
            return await ReadAppliedAsync(connection, transaction, table, cancellationToken).ConfigureAwait(false);
        }
        catch (DbException)
        {
            return [];
        }
    }

    private async Task InsertHistoryRowAsync(
        DbConnection connection, DbTransaction transaction, string table, string name, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"INSERT INTO {table} (name, applied_at) VALUES (@name, @appliedAt);";

        var nameParameter = command.CreateParameter();
        nameParameter.ParameterName = "@name";
        nameParameter.Value = name;
        command.Parameters.Add(nameParameter);

        var appliedAtParameter = command.CreateParameter();
        appliedAtParameter.ParameterName = "@appliedAt";
        appliedAtParameter.Value = clock.UtcNow.UtcDateTime.ToString("O");
        command.Parameters.Add(appliedAtParameter);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(
        DbConnection connection, DbTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
