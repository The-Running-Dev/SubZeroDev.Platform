using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Persistence;
using SubZeroDev.Platform.Testing;

namespace SubZeroDev.Platform.Tests;

public sealed class SqlitePersistenceContractTests : PersistenceContractTests
{
    protected override PersistenceProvider Provider => PersistenceProvider.Sqlite;

    [Fact]
    public async Task A_sub_second_busy_wait_bound_still_fails_fast_rather_than_hanging_forever()
    {
        // DefaultTimeout is whole seconds. Casting a sub-second bound to int truncates it to zero,
        // and Microsoft.Data.Sqlite treats zero as "retry forever" rather than SQLite's own "fail
        // immediately" — silently turning a bounded wait into an unbounded one.
        var connectionString = await AcquireConnectionStringAsync();

        try
        {
            await using var host = await PlatformTestHost.CreateBuilder()
                .WithProvider(PersistenceProvider.Sqlite)
                .WithSetting("Persistence:ConnectionString", connectionString)
                .WithSetting("Persistence:SqliteBusyWaitBound", "00:00:00.500")
                .StartAsync(CancellationToken.None);

            var capability = host.Services.GetRequiredService<IProviderCapability>();
            var firstLock = await capability.AcquireMigrationLockAsync(CancellationToken.None);
            Assert.True(firstLock.IsSuccess);

            try
            {
                var stopwatch = Stopwatch.StartNew();
                var secondLock = await capability.AcquireMigrationLockAsync(CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(10));

                Assert.False(secondLock.IsSuccess);
                Assert.Equal(nameof(MigrationError.Locked), secondLock.Error.Code);
                Assert.True(
                    stopwatch.Elapsed < TimeSpan.FromSeconds(5),
                    $"Expected the bound (rounded up to one second) to fail the second acquisition quickly; took {stopwatch.Elapsed}.");
            }
            finally
            {
                await firstLock.Value.DisposeAsync();
            }
        }
        finally
        {
            await ReleaseConnectionStringAsync(connectionString);
        }
    }

    protected override Task<string> AcquireConnectionStringAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"platform-contract-{Guid.NewGuid():N}.db");
        return Task.FromResult($"Data Source={path}");
    }

    protected override Task ReleaseConnectionStringAsync(string connectionString)
    {
        var path = new SqliteConnectionStringBuilder(connectionString).DataSource;

        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            try
            {
                File.Delete(path + suffix);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>Pooling reuses a connection whose schema snapshot can predate a DDL change another
    /// pooled connection just committed — see <c>SqliteProviderCapability.ConnectionString</c> for
    /// the reproduction. Verification connections need the same guarantee the store gives itself.</summary>
    private static SqliteConnection OpenNonPooled(string connectionString) =>
        new(new SqliteConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);

    protected override async Task<int> CountRowsAsync(string connectionString, string table, string id)
    {
        await using var connection = OpenNonPooled(connectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE id = @id;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@id";
        parameter.Value = id;
        command.Parameters.Add(parameter);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    // Captured from the three migrations as they stood at d6daabf; their bodies are unchanged since.
    protected override IReadOnlyList<string> BaselineSchema { get; } =
    [
        "platform_background_work_lease.name TEXT NOT NULL PK1",
        "platform_background_work_lease.holder TEXT NOT NULL",
        "platform_background_work_lease.acquired_at TEXT NOT NULL",
        "platform_background_work_lease.expires_at TEXT NOT NULL",
        "platform_host_registration.role TEXT NOT NULL PK1",
        "platform_host_registration.instance TEXT NOT NULL PK2",
        "platform_host_registration.started_at TEXT NOT NULL",
        "platform_host_registration.heartbeat_at TEXT NOT NULL",
        "platform_host_registration.settings_fingerprint TEXT NOT NULL",
        "platform_host_registration ix_platform_host_registration_role_heartbeat(role, heartbeat_at)",
        "platform_outbox.id BLOB NOT NULL PK1",
        "platform_outbox.sequence INTEGER NOT NULL",
        "platform_outbox.occurred_at TEXT NOT NULL",
        "platform_outbox.type TEXT NOT NULL",
        "platform_outbox.payload TEXT NOT NULL",
        "platform_outbox.tenant TEXT NOT NULL",
        "platform_outbox.trace_parent TEXT NOT NULL",
        "platform_outbox.trace_state TEXT",
        "platform_outbox.correlation TEXT NOT NULL",
        "platform_outbox.culture TEXT NOT NULL",
        "platform_outbox.attempts INTEGER NOT NULL",
        "platform_outbox.next_attempt_at TEXT",
        "platform_outbox.first_deferred_at TEXT",
        "platform_outbox.claimed_by TEXT",
        "platform_outbox.claimed_at TEXT",
        "platform_outbox.processed_at TEXT",
        "platform_outbox.poisoned_at TEXT",
        "platform_outbox.last_error TEXT",
        "platform_outbox ix_platform_outbox_eligibility(processed_at, poisoned_at, next_attempt_at, claimed_at, sequence)",
        "platform_outbox ix_platform_outbox_poisoned_at(poisoned_at)",
        "platform_outbox ix_platform_outbox_processed_at(processed_at)",
    ];

    protected override async Task<IReadOnlyList<string>> DescribeSchemaAsync(
        string connectionString, IReadOnlyList<string> tables)
    {
        await using var connection = OpenNonPooled(connectionString);
        await connection.OpenAsync();

        var lines = new List<string>();
        foreach (var table in tables)
        {
            var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info({table});";
            await using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    var notNull = reader.GetInt64(3) != 0 ? " NOT NULL" : string.Empty;
                    var primaryKey = reader.GetInt64(5) != 0 ? $" PK{reader.GetInt64(5)}" : string.Empty;
                    lines.Add($"{table}.{reader.GetString(1)} {reader.GetString(2)}{notNull}{primaryKey}");
                }
            }

            var indexes = connection.CreateCommand();
            indexes.CommandText =
                "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = @table "
                + "AND name NOT LIKE 'sqlite_%' ORDER BY name;";
            indexes.Parameters.AddWithValue("@table", table);
            var names = new List<string>();
            await using (var reader = await indexes.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    names.Add(reader.GetString(0));
                }
            }

            foreach (var name in names)
            {
                var columns = connection.CreateCommand();
                columns.CommandText = $"PRAGMA index_info({name});";
                var indexed = new List<string>();
                await using (var reader = await columns.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        indexed.Add(reader.GetString(2));
                    }
                }

                lines.Add($"{table} {name}({string.Join(", ", indexed)})");
            }
        }

        return lines;
    }

    protected override async Task<int> CountTablesAsync(string connectionString, string table)
    {
        await using var connection = OpenNonPooled(connectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @name;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@name";
        parameter.Value = table;
        command.Parameters.Add(parameter);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    protected override async Task<int> CountCrossModuleForeignKeysAsync(
        string connectionString, string ownerTable, string referencingTable)
    {
        await using var connection = OpenNonPooled(connectionString);
        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA foreign_key_list({referencingTable});";
        await using var reader = await command.ExecuteReaderAsync();

        var count = 0;
        while (await reader.ReadAsync())
        {
            // Column "table" names the referenced table.
            if (string.Equals(reader.GetString(reader.GetOrdinal("table")), ownerTable, StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
    }

    protected override async Task<RawOutboxRow?> ReadOutboxRowAsync(
        string connectionString, IProviderCapability capability, OutboxMessageId id)
    {
        await using var connection = OpenNonPooled(connectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText =
            "SELECT sequence, type, tenant, trace_parent, trace_state, correlation, culture, attempts, payload, "
            + "claimed_by, claimed_at, processed_at, poisoned_at FROM platform_outbox WHERE id = @id;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@id";
        parameter.Value = capability.EncodeIdentifier(id.Value);
        command.Parameters.Add(parameter);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new RawOutboxRow(
            Sequence: reader.GetInt64(0),
            Type: reader.GetString(1),
            Tenant: reader.GetString(2),
            TraceParent: reader.GetString(3),
            TraceState: reader.IsDBNull(4) ? null : reader.GetString(4),
            Correlation: reader.GetString(5),
            Culture: reader.GetString(6),
            Attempts: reader.GetInt32(7),
            Payload: reader.GetString(8),
            ClaimedByIsNull: reader.IsDBNull(9),
            ClaimedAtIsNull: reader.IsDBNull(10),
            ProcessedAtIsNull: reader.IsDBNull(11),
            PoisonedAtIsNull: reader.IsDBNull(12));
    }

    protected override async Task<(string Tenant, string CreatedAt, string? CreatedBy)> ReadAuditRowAsync(
        string connectionString, string id)
    {
        await using var connection = OpenNonPooled(connectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT tenant, created_at, created_by FROM t_audited WHERE id = @id;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@id";
        parameter.Value = id;
        command.Parameters.Add(parameter);

        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2));
    }
}
