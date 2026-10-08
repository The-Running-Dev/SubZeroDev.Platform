using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Hosting;
using SubZeroDev.Platform.Persistence;
using SubZeroDev.Platform.Testing;

namespace SubZeroDev.Platform.Tests;

/// <summary>S38: a host in the derived <c>Development</c> environment applies pending migrations at
/// start, through the runner and its lock, and no other host does.</summary>
public sealed class DevelopmentMigrationTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"devmigrate-{Guid.NewGuid():N}.db");

    /// <summary>An empty database: a file with no tables, already in the WAL mode the persistence
    /// startup check asserts before development application runs (contract § 16), as the test
    /// host's default database is.</summary>
    public DevelopmentMigrationTests()
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL;";
        pragma.ExecuteNonQuery();
    }

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

    [Theory]
    [InlineData("Development")]
    [InlineData("development")]
    public async Task S38_1_a_development_worker_applies_pending_migrations_before_any_start(string environment)
    {
        var recorder = new PendingAtStart();
        var builder = DevHost.Builder(environment, ConnectionString, PersistenceProvider.Sqlite);
        recorder.RegisterOn(builder.Services);
        builder.Services.AddSingleton<IPlatformModule>(DevHost.X());
        builder.Services.AddPlatformPersistence();
        builder.AddPlatformWorkerHost();

        using var host = builder.Build();
        await host.StartAsync(CancellationToken.None);
        await host.StopAsync(CancellationToken.None);

        Assert.Empty(recorder.Pending);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("development-ci")]
    public async Task S38_2_any_other_environment_starts_with_the_migration_still_pending(string environment)
    {
        var recorder = new PendingAtStart();
        var builder = DevHost.Builder(environment, ConnectionString, PersistenceProvider.Sqlite);
        recorder.RegisterOn(builder.Services);
        builder.Services.AddSingleton<IPlatformModule>(DevHost.X());
        builder.Services.AddPlatformPersistence();
        builder.AddPlatformWorkerHost();

        using var host = builder.Build();
        await host.StartAsync(CancellationToken.None);
        await host.StopAsync(CancellationToken.None);

        // Nothing applied at all: what was pending before the start is still pending after it.
        Assert.Contains("X:0001_create", recorder.Pending);
        Assert.Equal(await DevHost.PendingAsync(ConnectionString, PersistenceProvider.Sqlite, DevHost.X()), recorder.Pending);
        Assert.Contains("Platform:0001_create_host_registration", recorder.Pending);
    }

    [Fact]
    public async Task S38_3_a_configured_environment_setting_cannot_turn_it_on()
    {
        var recorder = new PendingAtStart();
        var builder = DevHost.Builder(Environments.Production, ConnectionString, PersistenceProvider.Sqlite);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Platform:Environment"] = Environments.Development,
        });
        recorder.RegisterOn(builder.Services);
        builder.Services.AddSingleton<IPlatformModule>(DevHost.X());
        builder.Services.AddPlatformPersistence();
        builder.AddPlatformWorkerHost();

        using var host = builder.Build();
        await host.StartAsync(CancellationToken.None);
        await host.StopAsync(CancellationToken.None);

        // Nothing applied at all: what was pending before the start is still pending after it.
        Assert.Contains("X:0001_create", recorder.Pending);
        Assert.Equal(await DevHost.PendingAsync(ConnectionString, PersistenceProvider.Sqlite, DevHost.X()), recorder.Pending);
        Assert.Contains("Platform:0001_create_host_registration", recorder.Pending);
    }

    [Fact]
    public async Task S38_5_a_held_lock_holds_the_start_until_it_is_released()
    {
        await using var holder = await PlatformTestHost.CreateBuilder()
            .WithProvider(PersistenceProvider.Sqlite)
            .WithSetting("Persistence:ConnectionString", ConnectionString)
            .StartAsync(CancellationToken.None);
        var held = await holder.Services.GetRequiredService<IProviderCapability>()
            .AcquireMigrationLockAsync(CancellationToken.None);
        Assert.True(held.IsSuccess);

        var recorder = new PendingAtStart();
        var builder = DevHost.Builder(Environments.Development, ConnectionString, PersistenceProvider.Sqlite);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Platform:Persistence:SqliteBusyWaitBound"] = "00:00:00.200",
        });
        recorder.RegisterOn(builder.Services);
        builder.Services.AddSingleton<IPlatformModule>(DevHost.X());
        builder.Services.AddPlatformPersistence();
        builder.AddPlatformWorkerHost();
        using var host = builder.Build();

        Task starting;
        try
        {
            starting = host.StartAsync(CancellationToken.None);

            // Long enough for several one-second retries: a start that gave up on the first Locked
            // would have completed by now.
            await Task.Delay(TimeSpan.FromSeconds(2.5), CancellationToken.None);
            Assert.False(starting.IsCompleted);
        }
        finally
        {
            await held.Value.DisposeAsync();
        }

        await starting.WaitAsync(TimeSpan.FromSeconds(10));
        await host.StopAsync(CancellationToken.None);

        Assert.Empty(recorder.Pending);
    }

    [Fact]
    public async Task S38_6_a_throwing_migration_fails_the_start_naming_it_and_leaves_history_unchanged()
    {
        var builder = DevHost.Builder(Environments.Development, ConnectionString, PersistenceProvider.Sqlite);
        builder.Services.AddSingleton<IPlatformModule>(
            new SeedModule("X", [], "CREATE TABLE seed_x (k INTEGER NOT NULL PRIMARY KEY); THIS IS NOT SQL;", _ => { }));
        builder.Services.AddPlatformPersistence();
        builder.AddPlatformWorkerHost();
        using var host = builder.Build();

        var thrown = await Assert.ThrowsAsync<PersistenceStartupException>(() => host.StartAsync(CancellationToken.None));

        Assert.Contains(nameof(MigrationError.Failed), thrown.Message, StringComparison.Ordinal);
        Assert.Contains("'X'", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("0001_create", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("X:0001_create", await DevHost.PendingAsync(ConnectionString, PersistenceProvider.Sqlite, DevHost.X()));
    }

    [Fact]
    public async Task S38_7_an_unreachable_database_starts_not_ready_with_a_warning()
    {
        var logs = new CapturingLoggerProvider();
        var port = DevHost.FreePort();
        var unreachable = $"Data Source={Path.Combine(_path + "-missing-dir", "x.db")}";

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
            ApplicationName = typeof(DevelopmentMigrationTests).Assembly.GetName().Name,
        });
        builder.Configuration.AddInMemoryCollection(DevHost.SettingsFor(unreachable, PersistenceProvider.Sqlite, port));
        Settings.ComposeOperated(builder.Services);
        builder.Services.AddSingleton<IPlatformModule>(DevHost.X());
        builder.Services.AddPlatformPersistence();
        builder.AddPlatformWorkerHost();

        // After the standard call, so it replaces the Serilog factory that call registers, which
        // writes to its own sinks and not to providers.
        using var loggerFactory = LoggerFactory.Create(logging => logging.AddProvider(logs));
        builder.Services.AddSingleton(loggerFactory);

        await using var app = builder.Build();
        await app.StartAsync(CancellationToken.None);

        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        var response = await client.GetAsync("/health/ready", CancellationToken.None);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None);
        await app.StopAsync(CancellationToken.None);

        // Not ready is degraded, not unhealthy: an unreachable database degrades readiness rather
        // than draining the host, so the probe answers 200 with a non-healthy report.
        Assert.NotEqual("Healthy", body.GetProperty("status").GetString());
        Assert.Contains(
            body.GetProperty("checks").EnumerateArray(),
            check => check.GetProperty("name").GetString() == PlatformHealthChecks.Database.Value
                && check.GetProperty("status").GetString() != "Healthy");
        Assert.Contains(
            logs.Entries,
            entry => entry.Level == LogLevel.Warning
                && entry.Message.Contains("Development migration", StringComparison.Ordinal)
                && entry.Message.Contains(nameof(MigrationError.Unavailable), StringComparison.Ordinal));
    }

    [Fact]
    public async Task S38_8_a_development_host_does_not_seed()
    {
        var probe = new SeedProbe();
        var recorder = new PendingAtStart();
        var builder = DevHost.Builder(Environments.Development, ConnectionString, PersistenceProvider.Sqlite);
        recorder.RegisterOn(builder.Services);
        builder.Services.AddSingleton(probe);
        builder.Services.AddSingleton<IPlatformModule>(SeedModules.X());
        builder.Services.AddPlatformPersistence();
        builder.AddPlatformWorkerHost();

        using var host = builder.Build();
        await host.StartAsync(CancellationToken.None);
        await host.StopAsync(CancellationToken.None);

        // Migrated, so the host did run its development application; and still no seeder ran.
        Assert.Empty(recorder.Pending);
        Assert.Empty(probe.Invocations);
    }
}

/// <summary>S38.4 on PostgreSQL: the two roles starting together against one database.</summary>
public sealed class PostgresDevelopmentMigrationTests(PostgresContainerFixture fixture) : IClassFixture<PostgresContainerFixture>
{
    [Fact]
    public async Task S38_4_a_web_and_a_worker_host_starting_together_both_start_and_apply_once()
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
            var web = DevHost.Builder(Environments.Development, connectionString, PersistenceProvider.PostgreSql);
            web.Services.AddSingleton<IPlatformModule>(DevHost.X());
            web.Services.AddPlatformPersistence();
            web.AddPlatformWebHost();

            var worker = DevHost.Builder(Environments.Development, connectionString, PersistenceProvider.PostgreSql);
            worker.Services.AddSingleton<IPlatformModule>(DevHost.X());
            worker.Services.AddPlatformPersistence();
            worker.AddPlatformWorkerHost();

            using var webHost = web.Build();
            using var workerHost = worker.Build();

            await Task.WhenAll(webHost.StartAsync(CancellationToken.None), workerHost.StartAsync(CancellationToken.None));
            await Task.WhenAll(webHost.StopAsync(CancellationToken.None), workerHost.StopAsync(CancellationToken.None));

            Assert.Empty(await DevHost.PendingAsync(connectionString, PersistenceProvider.PostgreSql, DevHost.X()));

            var table = webHost.Services.GetRequiredService<IProviderCapability>().MigrationHistoryTable(new ModuleName("X"));
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {table};";
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

internal static class DevHost
{
    /// <summary>Module X: one pending migration creating <c>seed_x</c>, and no seeder.</summary>
    internal static SeedModule X() => SeedModules.X(seeders: false);

    internal static HostApplicationBuilder Builder(string environment, string connectionString, PersistenceProvider provider)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = environment,
        });
        builder.Configuration.AddInMemoryCollection(SettingsFor(connectionString, provider, FreePort()));
        Settings.ComposeOperated(builder.Services);
        return builder;
    }

    internal static Dictionary<string, string?> SettingsFor(string connectionString, PersistenceProvider provider, int probePort)
    {
        var settings = Settings.Required();
        settings["Platform:Persistence:Provider"] = provider.ToString();
        settings["Platform:Persistence:ConnectionString"] = connectionString;
        settings["Platform:Hosting:WorkerProbePort"] = probePort.ToString();
        return settings;
    }

    /// <summary>What is pending, read by a host that is built and never started, so reading it
    /// applies nothing.</summary>
    internal static async Task<string[]> PendingAsync(string connectionString, PersistenceProvider provider, params IPlatformModule[] modules)
    {
        var builder = Builder(Environments.Production, connectionString, provider);
        foreach (var module in modules)
        {
            builder.Services.AddSingleton(module);
        }

        builder.Services.AddPlatformPersistence();
        builder.AddPlatformWorkerHost();
        using var host = builder.Build();
        var status = await host.Services.GetRequiredService<IMigrationRunner>().GetStatusAsync(CancellationToken.None);
        Assert.True(status.IsSuccess, status.IsSuccess ? null : status.Error.Detail);
        return [.. status.Value.SelectMany(module => module.Pending.Select(name => $"{module.Module}:{name}"))];
    }

    internal static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

/// <summary>A hosted service registered before the standard call, so its <c>StartAsync</c> is the
/// first to run; it records what is pending at that moment, which is after every
/// <c>StartingAsync</c> and before any other <c>StartAsync</c>.</summary>
internal sealed class PendingAtStart
{
    private string[]? _pending;

    internal string[] Pending => _pending ?? throw new InvalidOperationException("The host never reached StartAsync.");

    internal void RegisterOn(IServiceCollection services) =>
        services.AddHostedService(provider => new Recorder(this, provider.GetRequiredService<IMigrationRunner>()));

    private sealed class Recorder(PendingAtStart owner, IMigrationRunner runner) : IHostedService
    {
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var status = await runner.GetStatusAsync(cancellationToken);
            Assert.True(status.IsSuccess, status.IsSuccess ? null : status.Error.Detail);
            owner._pending = [.. status.Value.SelectMany(module => module.Pending.Select(name => $"{module.Module}:{name}"))];
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly List<(LogLevel Level, string Message)> _entries = [];

    public IReadOnlyList<(LogLevel Level, string Message)> Entries
    {
        get
        {
            lock (_entries)
            {
                return [.. _entries];
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new Logger(_entries);

    public void Dispose()
    {
    }

    private sealed class Logger(List<(LogLevel Level, string Message)> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (entries)
            {
                entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }
}
