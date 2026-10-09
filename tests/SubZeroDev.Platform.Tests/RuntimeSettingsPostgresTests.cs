using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Persistence;
using SubZeroDev.Platform.RuntimeSettings;
using SubZeroDev.Platform.Testing;

namespace SubZeroDev.Platform.Tests;

/// <summary>S43.8 on PostgreSQL: the same tenant isolation, single migration and table CHECKs
/// <see cref="RuntimeSettingsTests"/> proves on SQLite. S45: a web host and a worker over one database
/// see each other's writes on their next read, and concurrent writes to one row leave one row.</summary>
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

    // S45.1 -----------------------------------------------------------------------------------

    [Fact]
    public async Task S45_1_A_global_set_on_the_web_host_is_the_worker_s_next_read()
    {
        var connectionString = await AcquireConnectionStringAsync();
        var sink = new RecordingAuditSink("settings", isDurable: true);
        await using var web = await StartRoleAsync(connectionString, HostRole.Web, sink, migrate: true);
        await using var worker = await StartRoleAsync(connectionString, HostRole.Worker, sink, migrate: false);

        // The worker has read the setting before, so a value it kept would be the default.
        Assert.Equal(
            new ResolvedSetting<long>(4, null),
            (await RuntimeSettingsTests.ReadAsync(worker, RuntimeSettingsTests.MaxConcurrent, RuntimeSettingsTests.T1, RuntimeSettingsTests.AccountA)).Value);

        var set = await RuntimeSettingsTests.SetAsync(
            web, RuntimeSettingsTests.MaxConcurrent, SettingLayer.Global, 8L, TenantId.Implicit, RuntimeSettingsTests.AccountA);
        var read = await RuntimeSettingsTests.ReadAsync(
            worker, RuntimeSettingsTests.MaxConcurrent, RuntimeSettingsTests.T1, RuntimeSettingsTests.AccountA);

        Assert.True(set.IsSuccess);
        Assert.Equal(new ResolvedSetting<long>(8, SettingLayer.Global), read.Value);
    }

    // S45.2 -----------------------------------------------------------------------------------

    [Fact]
    public async Task S45_2_A_hundred_concurrent_global_sets_leave_one_row_and_a_hundred_records()
    {
        // The pool is bounded below the server's max_connections (100), as a deployed host's is: a
        // hundred unpooled connections at once are refused by the server ("too many clients"), which
        // is the database's limit and not the writer's. The sets still contend on the row together.
        var connectionString = new NpgsqlConnectionStringBuilder(await AcquireConnectionStringAsync())
        {
            MaxPoolSize = 20,
        }.ConnectionString;
        var sink = new RecordingAuditSink("settings", isDurable: true);
        await using var web = await StartRoleAsync(connectionString, HostRole.Web, sink, migrate: true);
        await using var worker = await StartRoleAsync(connectionString, HostRole.Worker, sink, migrate: false);

        // Fifty threes from one host and fifty fives from the other, all in flight at once.
        using var go = new ManualResetEventSlim();
        var sets = Enumerable.Range(0, 100)
            .Select(index => Task.Run(async () =>
            {
                go.Wait();
                return await RuntimeSettingsTests.SetAsync(
                    index % 2 == 0 ? web : worker,
                    RuntimeSettingsTests.MaxConcurrent,
                    SettingLayer.Global,
                    index % 2 == 0 ? 3L : 5L,
                    TenantId.Implicit,
                    RuntimeSettingsTests.AccountA);
            }))
            .ToArray();
        go.Set();
        var results = await Task.WhenAll(sets);

        Assert.All(results, result => Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.ToString()));
        Assert.Equal(1, await RuntimeSettingsTests.ScalarAsync(web, "SELECT COUNT(*) FROM runtime_setting;"));
        Assert.Contains(
            await RuntimeSettingsTests.ScalarAsync(web, "SELECT CAST(value AS bigint) FROM runtime_setting;"),
            new long[] { 3, 5 });
        Assert.Equal(100, sink.Received.Count(record => record.Action == RuntimeSettingsAuditActions.SettingSet));
    }

    private async Task<IPlatformTestHost> StartAsync(Action<IServiceCollection> configure) =>
        await PlatformTestHost.CreateBuilder()
            .WithProvider(PersistenceProvider.PostgreSql)
            .WithSetting("Persistence:ConnectionString", await AcquireConnectionStringAsync())
            .WithServices(configure)
            .StartAsync(CancellationToken.None);

    /// <summary>An Operated host of <paramref name="role"/> over <paramref name="connectionString"/>,
    /// composing the module with the sample catalogue, where account A holds WriteGlobal on the
    /// sample setting.</summary>
    private static async Task<IPlatformTestHost> StartRoleAsync(
        string connectionString, HostRole role, RecordingAuditSink sink, bool migrate)
    {
        var host = await PlatformTestHost.CreateBuilder()
            .WithRole(role)
            .WithProvider(PersistenceProvider.PostgreSql)
            .WithSetting("Persistence:ConnectionString", connectionString)
            .WithServices(services =>
            {
                services.AddSingleton<IPlatformModule, RuntimeSettingsModule>();
                services.AddSingleton<ISettingCatalog>(new RuntimeSettingsTests.SampleCatalog(RuntimeSettingsTests.MaxConcurrent));
                services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditSink>(sink));
                services.TryAddEnumerable(ServiceDescriptor.Singleton<IPermissionProvider>(
                    new RuntimeSettingsTests.SettingGrantProvider(
                        RuntimeSettingsPermissions.WriteGlobal, RuntimeSettingsTests.MaxConcurrentName)));
            })
            .StartAsync(CancellationToken.None);

        if (migrate)
        {
            Assert.True((await host.Services.GetRequiredService<IMigrationRunner>().ApplyAsync(CancellationToken.None)).IsSuccess);
        }

        return host;
    }

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
