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
            "SELECT type, tenant, trace_parent, trace_state, correlation, culture, attempts, payload, "
            + "claimed_by, claimed_at, processed_at, poisoned_at FROM platform_outbox WHERE id = @id;";
        command.Parameters.AddWithValue("@id", capability.EncodeIdentifier(id.Value));

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new RawOutboxRow(
            Type: reader.GetString(0),
            Tenant: reader.GetString(1),
            TraceParent: reader.GetString(2),
            TraceState: reader.IsDBNull(3) ? null : reader.GetString(3),
            Correlation: reader.GetString(4),
            Culture: reader.GetString(5),
            Attempts: reader.GetInt32(6),
            Payload: reader.GetString(7),
            ClaimedByIsNull: reader.IsDBNull(8),
            ClaimedAtIsNull: reader.IsDBNull(9),
            ProcessedAtIsNull: reader.IsDBNull(10),
            PoisonedAtIsNull: reader.IsDBNull(11));
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
