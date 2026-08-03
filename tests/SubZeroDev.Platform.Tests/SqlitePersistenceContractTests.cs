using Microsoft.Data.Sqlite;
using SubZeroDev.Platform.Core;

namespace SubZeroDev.Platform.Tests;

public sealed class SqlitePersistenceContractTests : PersistenceContractTests
{
    protected override PersistenceProvider Provider => PersistenceProvider.Sqlite;

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
