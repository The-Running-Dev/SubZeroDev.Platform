using Npgsql;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Persistence;
using Testcontainers.PostgreSql;

namespace SubZeroDev.Platform.Tests;

/// <summary>One PostgreSQL container shared across the class; each test gets its own database
/// within it, so tests stay isolated without paying for a container per test.</summary>
public sealed class PostgresContainerFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine").Build();

    internal string AdminConnectionString => _container.GetConnectionString();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

public sealed class PostgresPersistenceContractTests(PostgresContainerFixture fixture)
    : PersistenceContractTests, IClassFixture<PostgresContainerFixture>
{
    protected override PersistenceProvider Provider => PersistenceProvider.PostgreSql;

    protected override async Task<string> AcquireConnectionStringAsync()
    {
        var database = $"test_{Guid.NewGuid():N}";

        await using var admin = new NpgsqlConnection(fixture.AdminConnectionString);
        await admin.OpenAsync();
        await using (var create = admin.CreateCommand())
        {
            create.CommandText = $"CREATE DATABASE \"{database}\";";
            await create.ExecuteNonQueryAsync();
        }

        var builder = new NpgsqlConnectionStringBuilder(fixture.AdminConnectionString) { Database = database };
        return builder.ConnectionString;
    }

    protected override async Task ReleaseConnectionStringAsync(string connectionString)
    {
        var database = new NpgsqlConnectionStringBuilder(connectionString).Database;
        if (string.IsNullOrEmpty(database))
        {
            return;
        }

        await using var admin = new NpgsqlConnection(fixture.AdminConnectionString);
        await admin.OpenAsync();
        await using var drop = admin.CreateCommand();
        drop.CommandText = $"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE);";
        await drop.ExecuteNonQueryAsync();
    }

    protected override async Task<int> CountRowsAsync(string connectionString, string table, string id)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE id = @id;";
        command.Parameters.AddWithValue("@id", id);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    // Captured from the three migrations as they stood at d6daabf; their bodies are unchanged since.
    protected override IReadOnlyList<string> BaselineSchema { get; } =
    [
        "platform_background_work_lease.name text NOT NULL PK1",
        "platform_background_work_lease.holder text NOT NULL",
        "platform_background_work_lease.acquired_at text NOT NULL",
        "platform_background_work_lease.expires_at text NOT NULL",
        "platform_host_registration.role text NOT NULL PK1",
        "platform_host_registration.instance text NOT NULL PK2",
        "platform_host_registration.started_at text NOT NULL",
        "platform_host_registration.heartbeat_at text NOT NULL",
        "platform_host_registration.settings_fingerprint text NOT NULL",
        "platform_host_registration ix_platform_host_registration_role_heartbeat(role, heartbeat_at)",
        "platform_outbox.id bytea NOT NULL PK1",
        "platform_outbox.sequence bigint NOT NULL",
        "platform_outbox.occurred_at text NOT NULL",
        "platform_outbox.type text NOT NULL",
        "platform_outbox.payload text NOT NULL",
        "platform_outbox.tenant text NOT NULL",
        "platform_outbox.trace_parent text NOT NULL",
        "platform_outbox.trace_state text",
        "platform_outbox.correlation text NOT NULL",
        "platform_outbox.culture text NOT NULL",
        "platform_outbox.attempts integer NOT NULL",
        "platform_outbox.next_attempt_at text",
        "platform_outbox.first_deferred_at text",
        "platform_outbox.claimed_by text",
        "platform_outbox.claimed_at text",
        "platform_outbox.processed_at text",
        "platform_outbox.poisoned_at text",
        "platform_outbox.last_error text",
        "platform_outbox ix_platform_outbox_eligibility(processed_at, poisoned_at, next_attempt_at, claimed_at, sequence)",
        "platform_outbox ix_platform_outbox_poisoned_at(poisoned_at)",
        "platform_outbox ix_platform_outbox_processed_at(processed_at)",
    ];

    protected override async Task<IReadOnlyList<string>> DescribeSchemaAsync(
        string connectionString, IReadOnlyList<string> tables)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        var lines = new List<string>();
        foreach (var table in tables)
        {
            var command = connection.CreateCommand();
            command.CommandText = """
                SELECT c.column_name, c.data_type, c.is_nullable,
                       (SELECT array_position(i.indkey::int2[], a.attnum) + 1
                        FROM pg_index i
                        JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attname = c.column_name
                        WHERE i.indrelid = format('public.%I', c.table_name)::regclass AND i.indisprimary)
                FROM information_schema.columns c
                WHERE c.table_schema = 'public' AND c.table_name = @table
                ORDER BY c.ordinal_position;
                """;
            command.Parameters.AddWithValue("@table", table);
            await using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    var notNull = reader.GetString(2) == "NO" ? " NOT NULL" : string.Empty;
                    var primaryKey = reader.IsDBNull(3) ? string.Empty : $" PK{reader.GetInt32(3)}";
                    lines.Add($"{table}.{reader.GetString(0)} {reader.GetString(1)}{notNull}{primaryKey}");
                }
            }

            var indexes = connection.CreateCommand();
            indexes.CommandText = """
                SELECT ic.relname,
                       (SELECT string_agg(a.attname, ', ' ORDER BY k.ordinality)
                        FROM unnest(i.indkey::int2[]) WITH ORDINALITY AS k(attnum, ordinality)
                        JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = k.attnum)
                FROM pg_index i
                JOIN pg_class ic ON ic.oid = i.indexrelid
                WHERE i.indrelid = format('public.%I', @table::text)::regclass
                  AND NOT i.indisprimary AND NOT i.indisunique
                ORDER BY ic.relname;
                """;
            indexes.Parameters.AddWithValue("@table", table);
            await using (var reader = await indexes.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    lines.Add($"{table} {reader.GetString(0)}({reader.GetString(1)})");
                }
            }
        }

        return lines;
    }

    protected override async Task<int> CountTablesAsync(string connectionString, string table)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pg_tables WHERE schemaname = 'public' AND tablename = @name;";
        command.Parameters.AddWithValue("@name", table);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    protected override async Task<int> CountCrossModuleForeignKeysAsync(
        string connectionString, string ownerTable, string referencingTable)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT count(*)
            FROM pg_constraint c
            JOIN pg_class t ON t.oid = c.conrelid
            JOIN pg_class ref ON ref.oid = c.confrelid
            WHERE c.contype = 'f' AND t.relname = @referencing AND ref.relname = @owner;
            """;
        command.Parameters.AddWithValue("@referencing", referencingTable);
        command.Parameters.AddWithValue("@owner", ownerTable);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    protected override async Task<RawOutboxRow?> ReadOutboxRowAsync(
        string connectionString, IProviderCapability capability, OutboxMessageId id)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText =
            "SELECT sequence, type, tenant, trace_parent, trace_state, correlation, culture, attempts, payload, "
            + "claimed_by, claimed_at, processed_at, poisoned_at FROM platform_outbox WHERE id = @id;";
        command.Parameters.AddWithValue("@id", capability.EncodeIdentifier(id.Value));

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
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT tenant, created_at, created_by FROM t_audited WHERE id = @id;";
        command.Parameters.AddWithValue("@id", id);

        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2));
    }
}
