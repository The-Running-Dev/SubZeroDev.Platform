using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Persistence;
using SubZeroDev.Platform.Testing;

namespace SubZeroDev.Platform.Tests;

/// <summary>S43.8 on PostgreSQL: the same tenant isolation, single migration and table CHECKs
/// <see cref="RuntimeSettingsTests"/> proves on SQLite.</summary>
public sealed class RuntimeSettingsPostgresTests(PostgresContainerFixture fixture) : IClassFixture<PostgresContainerFixture>
{
    [Fact]
    public Task S43_8_A_tenant_row_for_T1_is_not_returned_to_a_read_in_T2() =>
        RuntimeSettingsContract.TenantRowStaysInItsTenantAsync(StartAsync);

    [Fact]
    public Task S43_8_The_migration_applies_once() =>
        RuntimeSettingsContract.MigrationAppliesOnceAsync(StartAsync);

    [Fact]
    public Task S43_8_The_table_rejects_rows_that_break_its_layer_rules() =>
        RuntimeSettingsContract.TableRejectsMalformedRowsAsync(StartAsync);

    private async Task<IPlatformTestHost> StartAsync(Action<IServiceCollection> configure) =>
        await PlatformTestHost.CreateBuilder()
            .WithProvider(PersistenceProvider.PostgreSql)
            .WithSetting("Persistence:ConnectionString", await AcquireConnectionStringAsync())
            .WithServices(configure)
            .StartAsync(CancellationToken.None);

    private async Task<string> AcquireConnectionStringAsync()
    {
        var database = $"test_{Guid.NewGuid():N}";

        await using var admin = new NpgsqlConnection(fixture.AdminConnectionString);
        await admin.OpenAsync();
        await using var create = admin.CreateCommand();
        create.CommandText = $"CREATE DATABASE \"{database}\";";
        await create.ExecuteNonQueryAsync();

        return new NpgsqlConnectionStringBuilder(fixture.AdminConnectionString) { Database = database }.ConnectionString;
    }
}
