using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Hosting;
using SubZeroDev.Platform.Persistence;
using SubZeroDev.Platform.Testing;

namespace SubZeroDev.Platform.Tests;

/// <summary>S37: migrate mode runs every registered seeder after its migrations, in module dependency
/// order, and a host never does.</summary>
[Collection(StandardErrorTestCollection.Name)]
public sealed class SeedingTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"seeding-{Guid.NewGuid():N}.db");
    private readonly SeedProbe _probe = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            try
            {
                File.Delete(_path + suffix);
            }
            catch (IOException)
            {
            }
        }
    }

    private string ConnectionString => $"Data Source={_path}";

    [Fact]
    public async Task S37_1_seeders_run_after_migrations_and_a_dependent_module_sees_its_dependency_seeded()
    {
        var (exitCode, stderr) = await MigrateAsync(SeedModules.X(), SeedModules.Y());

        Assert.True(exitCode == 0, stderr);
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM seed_x WHERE k = 1;"));
        Assert.Equal([1L], _probe.RecordedXCounts);
        Assert.Equal(["XSeeder", "YSeeder"], _probe.Invocations);
    }

    [Fact]
    public async Task S37_2_a_second_run_invokes_the_seeders_again_and_converges()
    {
        var first = await MigrateAsync(SeedModules.X(), SeedModules.Y());
        var second = await MigrateAsync(SeedModules.X(), SeedModules.Y());

        Assert.True(first.ExitCode == 0, first.Stderr);
        Assert.True(second.ExitCode == 0, second.Stderr);
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM seed_x;"));
        Assert.Equal(["XSeeder", "YSeeder", "XSeeder", "YSeeder"], _probe.Invocations);
    }

    [Fact]
    public async Task S37_4_a_dependency_seeds_first_even_when_registered_last()
    {
        var (exitCode, stderr) = await MigrateAsync(SeedModules.Y(), SeedModules.X());

        Assert.True(exitCode == 0, stderr);
        Assert.Equal(["XSeeder", "YSeeder"], _probe.Invocations);
        Assert.Equal([1L], _probe.RecordedXCounts);
    }

    [Fact]
    public async Task S37_4_seeders_registered_outside_their_modules_still_run_in_dependency_order()
    {
        var (exitCode, stderr) = await MigrateAsync(
            [SeedModules.Y(seeders: false), SeedModules.X(seeders: false)],
            settings: null,
            services =>
            {
                services.TryAddEnumerable(ServiceDescriptor.Singleton<ISeeder, YSeeder>());
                services.TryAddEnumerable(ServiceDescriptor.Singleton<ISeeder, XSeeder>());
            });

        Assert.True(exitCode == 0, stderr);
        Assert.Equal(["XSeeder", "YSeeder"], _probe.Invocations);
        Assert.Equal([1L], _probe.RecordedXCounts);
    }

    [Fact]
    public async Task S37_4_a_seeder_for_an_uncomposed_module_runs_after_every_composed_one_by_name()
    {
        var (exitCode, stderr) = await MigrateAsync(SeedModules.Orphans(), SeedModules.Y(), SeedModules.X());

        Assert.True(exitCode == 0, stderr);
        Assert.Equal(["XSeeder", "YSeeder", "AlphaOrphanSeeder", "ZuluOrphanSeeder"], _probe.Invocations);
    }

    [Fact]
    public async Task S37_5_a_throwing_seeder_fails_the_run_and_stops_the_seeders_after_it()
    {
        var (exitCode, stderr) = await MigrateAsync(SeedModules.Throwing());

        Assert.Equal(1, exitCode);
        Assert.StartsWith("SeedFailed: Module 'Throwing' seeder 'ThrowingSeeder' failed:", stderr);
        Assert.Contains(typeof(InvalidOperationException).FullName!, stderr);
        Assert.Contains("seed broke", stderr);
        Assert.Equal(["ThrowingSeeder"], _probe.Invocations);
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM platform_migrations_throwing;"));
    }

    [Fact]
    public async Task S37_6_a_seeder_enqueueing_without_a_scope_fails_the_run()
    {
        var (exitCode, stderr) = await MigrateAsync(SeedModules.NoScope());

        Assert.Equal(1, exitCode);
        Assert.StartsWith("SeedFailed:", stderr);
        Assert.Contains("NoAmbientOperationScope", stderr);
    }

    [Fact]
    public async Task S37_7_a_seeder_that_opens_a_local_system_scope_writes_under_it()
    {
        var (exitCode, stderr) = await MigrateAsync(SeedModules.Tenant());

        Assert.True(exitCode == 0, stderr);
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT tenant, created_by FROM seed_tenant;";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(TenantSeeder.Tenant.Value.ToString(), reader.GetString(0));
        Assert.Equal("system:local", reader.GetString(1));
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task S37_8_a_held_migration_lock_returns_two_and_runs_no_seeder()
    {
        await using var holder = await PlatformTestHost.CreateBuilder()
            .WithProvider(PersistenceProvider.Sqlite)
            .WithSetting("Persistence:ConnectionString", ConnectionString)
            .StartAsync(CancellationToken.None);
        var held = await holder.Services.GetRequiredService<IProviderCapability>()
            .AcquireMigrationLockAsync(CancellationToken.None);
        Assert.True(held.IsSuccess);

        try
        {
            var (exitCode, stderr) = await MigrateAsync(
                [SeedModules.X()],
                new Dictionary<string, string?> { ["Platform:Persistence:SqliteBusyWaitBound"] = "00:00:00.200" });

            Assert.True(exitCode == 2, stderr);
            Assert.Empty(_probe.Invocations);
        }
        finally
        {
            await held.Value.DisposeAsync();
        }
    }

    [Fact]
    public async Task S37_9_a_module_cycle_fails_the_run_with_the_graph_error()
    {
        var (exitCode, stderr) = await MigrateAsync(new StubModule("A", "B"), new StubModule("B", "A"));

        Assert.Equal(1, exitCode);
        Assert.StartsWith($"{nameof(ModuleGraphError.CyclicDependency)}:", stderr);
    }

    [Fact]
    public async Task S37_10_a_web_host_composes_seeders_and_never_invokes_them()
    {
        var composed = 0;
        var (app, client) = await WebHostUnderTest.StartAsync(
            services =>
            {
                services.AddSingleton(_probe);
                services.AddSingleton<IPlatformModule>(SeedModules.X());
                services.AddSingleton<IPlatformModule>(SeedModules.Y());
            },
            postCompose: services => composed = services.Count(descriptor => descriptor.ServiceType == typeof(ISeeder)));

        using (client)
        {
            Assert.Equal(2, composed);
            await app.StopAsync();
            await app.DisposeAsync();
        }

        Assert.Empty(_probe.Invocations);
    }

    private Task<(int ExitCode, string Stderr)> MigrateAsync(params IPlatformModule[] modules) =>
        MigrateAsync(modules, settings: null);

    private Task<(int ExitCode, string Stderr)> MigrateAsync(
        IPlatformModule[] modules,
        IDictionary<string, string?>? settings,
        Action<IServiceCollection>? services = null) =>
        SeedRun.MigrateAsync(ConnectionString, PersistenceProvider.Sqlite, _probe, modules, settings, services);

    private async Task<long> CountAsync(string sql)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}

/// <summary>S37.3 against PostgreSQL, where two migrate-mode processes can genuinely overlap.</summary>
[Collection(StandardErrorTestCollection.Name)]
public sealed class PostgresSeedingTests(PostgresContainerFixture fixture) : IClassFixture<PostgresContainerFixture>
{
    [Fact]
    public async Task S37_3_two_concurrent_runs_converge_on_one_row()
    {
        var database = $"test_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(fixture.AdminConnectionString))
        {
            await admin.OpenAsync();
            await using var create = admin.CreateCommand();
            create.CommandText = $"CREATE DATABASE \"{database}\";";
            await create.ExecuteNonQueryAsync();
        }

        var connectionString = new NpgsqlConnectionStringBuilder(fixture.AdminConnectionString) { Database = database }
            .ConnectionString;

        try
        {
            var probe = new SeedProbe();
            var results = await SeedRun.RunConcurrentlyAsync(
                () => SeedRun.MigrateCoreAsync(connectionString, PersistenceProvider.PostgreSql, probe, [SeedModules.X(), SeedModules.Y()], null),
                () => SeedRun.MigrateCoreAsync(connectionString, PersistenceProvider.PostgreSql, probe, [SeedModules.X(), SeedModules.Y()], null));

            var exitCodes = results.Order().ToArray();
            Assert.True(exitCodes is [0, 0] or [0, 2], string.Join(",", exitCodes));

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM seed_x;";
            Assert.Equal(1L, Convert.ToInt64(await command.ExecuteScalarAsync()));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(fixture.AdminConnectionString);
            await admin.OpenAsync();
            await using var drop = admin.CreateCommand();
            drop.CommandText = $"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE);";
            await drop.ExecuteNonQueryAsync();
        }
    }
}

/// <summary>Serialises every test that redirects the process-wide standard error, which is the
/// channel migrate mode reports through.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class StandardErrorTestCollection
{
    /// <summary>The collection name.</summary>
    public const string Name = "ProcessStandardError";
}

internal static class SeedRun
{
    /// <summary>Runs migrate mode with standard error captured.</summary>
    internal static async Task<(int ExitCode, string Stderr)> MigrateAsync(
        string connectionString,
        PersistenceProvider provider,
        SeedProbe probe,
        IPlatformModule[] modules,
        IDictionary<string, string?>? settings,
        Action<IServiceCollection>? services = null)
    {
        var original = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        try
        {
            var exitCode = await MigrateCoreAsync(connectionString, provider, probe, modules, settings, services);
            return (exitCode, captured.ToString());
        }
        finally
        {
            Console.SetError(original);
        }
    }

    internal static Task<int> MigrateCoreAsync(
        string connectionString,
        PersistenceProvider provider,
        SeedProbe probe,
        IPlatformModule[] modules,
        IDictionary<string, string?>? settings,
        Action<IServiceCollection>? services = null)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = "Production",
        });
        builder.Configuration.AddInMemoryCollection(Settings.Required());
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Platform:Persistence:Provider"] = provider.ToString(),
            ["Platform:Persistence:ConnectionString"] = connectionString,
        });
        if (settings is not null)
        {
            builder.Configuration.AddInMemoryCollection(settings);
        }

        builder.Services.AddSingleton(probe);
        services?.Invoke(builder.Services);
        foreach (var module in modules)
        {
            builder.Services.AddSingleton(module);
        }

        return builder.RunPlatformMigrateModeAsync(CancellationToken.None);
    }

    internal static async Task<int[]> RunConcurrentlyAsync(params Func<Task<int>>[] runs)
    {
        var original = Console.Error;
        Console.SetError(TextWriter.Null);
        try
        {
            return await Task.WhenAll(runs.Select(run => Task.Run(run)));
        }
        finally
        {
            Console.SetError(original);
        }
    }
}

/// <summary>What the seeders under test did, shared across runs.</summary>
internal sealed class SeedProbe
{
    private readonly ConcurrentQueue<string> _invocations = new();
    private readonly ConcurrentQueue<long> _recordedXCounts = new();

    internal IReadOnlyList<string> Invocations => [.. _invocations];

    internal IReadOnlyList<long> RecordedXCounts => [.. _recordedXCounts];

    internal void Invoked(string seeder) => _invocations.Enqueue(seeder);

    internal void RecordXCount(long count) => _recordedXCounts.Enqueue(count);
}

/// <summary>A module that owns one table and some seeders.</summary>
internal sealed class SeedModule(string name, string[] dependsOn, string? createTable, Action<IServiceCollection> seeders)
    : IPlatformModule
{
    public ModuleName Name { get; } = new(name);

    public IReadOnlyCollection<ModuleName> DependsOn { get; } = dependsOn.Select(d => new ModuleName(d)).ToArray();

    public void Register(IServiceCollection services)
    {
        if (createTable is not null)
        {
            services.AddSingleton<IModuleMigrationSource>(
                new TestMigrationSource(Name.Value, TestMigration.Sql("0001_create", createTable)));
        }

        seeders(services);
    }
}

internal static class SeedModules
{
    internal static SeedModule X(bool seeders = true) => new(
        "X",
        [],
        "CREATE TABLE seed_x (k INTEGER NOT NULL PRIMARY KEY);",
        services =>
        {
            if (seeders)
            {
                services.TryAddEnumerable(ServiceDescriptor.Singleton<ISeeder, XSeeder>());
            }
        });

    internal static SeedModule Y(bool seeders = true) => new(
        "Y",
        ["X"],
        null,
        services =>
        {
            if (seeders)
            {
                services.TryAddEnumerable(ServiceDescriptor.Singleton<ISeeder, YSeeder>());
            }
        });

    internal static SeedModule Orphans() => new(
        "Orphans",
        [],
        null,
        services =>
        {
            services.TryAddEnumerable(ServiceDescriptor.Singleton<ISeeder, ZuluOrphanSeeder>());
            services.TryAddEnumerable(ServiceDescriptor.Singleton<ISeeder, AlphaOrphanSeeder>());
        });

    internal static SeedModule Throwing() => new(
        "Throwing",
        [],
        "CREATE TABLE seed_throwing (k INTEGER NOT NULL PRIMARY KEY);",
        services =>
        {
            services.TryAddEnumerable(ServiceDescriptor.Singleton<ISeeder, ThrowingSeeder>());
            services.TryAddEnumerable(ServiceDescriptor.Singleton<ISeeder, AfterThrowingSeeder>());
        });

    internal static SeedModule NoScope() => new(
        "NoScope",
        [],
        null,
        services => services.TryAddEnumerable(ServiceDescriptor.Singleton<ISeeder, NoScopeEnqueueSeeder>()));

    internal static SeedModule Tenant() => new(
        "Tenant",
        [],
        "CREATE TABLE seed_tenant (tenant TEXT NOT NULL PRIMARY KEY, created_by TEXT NOT NULL);",
        services => services.TryAddEnumerable(ServiceDescriptor.Singleton<ISeeder, TenantSeeder>()));
}

/// <summary>Records its invocation, then seeds.</summary>
internal abstract class ProbeSeeder(string module, SeedProbe probe) : ISeeder
{
    public ModuleName Module { get; } = new(module);

    public string Name => GetType().Name;

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        probe.Invoked(Name);
        await SeedCoreAsync(cancellationToken);
    }

    protected abstract Task SeedCoreAsync(CancellationToken cancellationToken);

    protected static async Task<T> WriteAsync<T>(
        IUnitOfWork unitOfWork,
        Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken)
    {
        var result = await unitOfWork.ExecuteAsync(TransactionIntent.Write, work, cancellationToken);
        return result.IsSuccess
            ? result.Value
            : throw new InvalidOperationException($"{result.Error.Code}: {result.Error.Detail}");
    }

    protected static async Task<object?> ExecuteAsync(
        IAmbientTransactionAccessor ambient,
        string sql,
        bool scalar,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        var current = ambient.Current!;
        await using DbCommand command = current.Connection.CreateCommand();
        command.Transaction = current.Transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        return scalar
            ? await command.ExecuteScalarAsync(cancellationToken)
            : await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary>Inserts k=1 if absent, against the primary key.</summary>
internal sealed class XSeeder(SeedProbe probe, IUnitOfWork unitOfWork, IAmbientTransactionAccessor ambient)
    : ProbeSeeder("X", probe)
{
    protected override Task SeedCoreAsync(CancellationToken cancellationToken) =>
        WriteAsync(
            unitOfWork,
            token => ExecuteAsync(ambient, "INSERT INTO seed_x (k) VALUES (1) ON CONFLICT (k) DO NOTHING;", false, token),
            cancellationToken);
}

/// <summary>Records how many rows X's seeder left.</summary>
internal sealed class YSeeder(SeedProbe probe, IUnitOfWork unitOfWork, IAmbientTransactionAccessor ambient)
    : ProbeSeeder("Y", probe)
{
    private readonly SeedProbe _probe = probe;

    protected override async Task SeedCoreAsync(CancellationToken cancellationToken)
    {
        var count = await WriteAsync(
            unitOfWork,
            token => ExecuteAsync(ambient, "SELECT COUNT(*) FROM seed_x;", true, token),
            cancellationToken);
        _probe.RecordXCount(Convert.ToInt64(count));
    }
}

internal sealed class ZuluOrphanSeeder(SeedProbe probe) : ProbeSeeder("Zulu", probe)
{
    protected override Task SeedCoreAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class AlphaOrphanSeeder(SeedProbe probe) : ProbeSeeder("Alpha", probe)
{
    protected override Task SeedCoreAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class ThrowingSeeder(SeedProbe probe) : ProbeSeeder("Throwing", probe)
{
    protected override Task SeedCoreAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException("seed broke");
}

internal sealed class AfterThrowingSeeder(SeedProbe probe) : ProbeSeeder("Throwing", probe)
{
    protected override Task SeedCoreAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Enqueues inside a transaction but without a scope, which migrate mode does not open.</summary>
internal sealed class NoScopeEnqueueSeeder(SeedProbe probe, IUnitOfWork unitOfWork, IOutboxWriter outbox)
    : ProbeSeeder("NoScope", probe)
{
    protected override Task SeedCoreAsync(CancellationToken cancellationToken) =>
        WriteAsync(
            unitOfWork,
            _ =>
            {
                outbox.Enqueue(new TestEvent());
                return Task.FromResult(true);
            },
            cancellationToken);
}

/// <summary>Opens a local-system scope for one tenant and writes the ambient tenant and principal.</summary>
internal sealed class TenantSeeder(
    SeedProbe probe,
    IUnitOfWork unitOfWork,
    IAmbientTransactionAccessor ambient,
    IOperationScopeFactory scopes,
    ICurrentTenant tenant,
    ICurrentPrincipal principal) : ProbeSeeder("Tenant", probe)
{
    internal static readonly TenantId Tenant = new(Guid.Parse("5eed5eed-0000-4000-8000-000000000037"));

    protected override async Task SeedCoreAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.Begin(Tenant, Principal.LocalSystem);
        await WriteAsync(
            unitOfWork,
            token => ExecuteAsync(
                ambient,
                "INSERT INTO seed_tenant (tenant, created_by) VALUES (@tenant, @createdBy) ON CONFLICT (tenant) DO NOTHING;",
                false,
                token,
                ("@tenant", tenant.Current.Value.ToString()),
                ("@createdBy", principal.Current.Id.ToString())),
            cancellationToken);
    }
}
