using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Audit;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Hosting;
using SubZeroDev.Platform.Persistence;
using SubZeroDev.Platform.Testing;

namespace SubZeroDev.Platform.Tests;

/// <summary>D5-S32: an operator can cap how long audit rows are kept, and a host with no value keeps
/// them forever as before (I-U7, I-U11 to I-U13).</summary>
public sealed class AuditRetentionTests
{
    private static readonly BackgroundWorkName Prune = new("platform.audit.prune");

    private static readonly TenantId Tenant = new(Guid.Parse("33333333-3333-3333-3333-333333333333"));

    // S32.1 -----------------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task S32_1_With_no_retention_value_no_prune_is_registered_and_old_rows_survive_every_work(string? value)
    {
        var database = TemporaryPath();
        try
        {
            await using var host = await StartHostAsync(database, HostRole.Worker, value);
            var tenYearsAgo = host.Clock.UtcNow.AddYears(-10);
            await SeedAsync(host, tenYearsAgo, tenYearsAgo.AddDays(1));

            var registry = host.Services.GetRequiredService<IBackgroundWorkRegistry>();
            Assert.DoesNotContain(registry.Registered, work => work.Name == Prune);

            foreach (var work in registry.Registered)
            {
                await host.RunBackgroundWorkOnceAsync(work.Name, CancellationToken.None);
            }

            Assert.Equal(2, (await ReadOccurredAtAsync(host)).Count);
        }
        finally
        {
            DeleteSqliteFiles(database);
        }
    }

    // S32.2 -----------------------------------------------------------------------------------

    [Fact]
    public async Task S32_2_One_tick_deletes_exactly_the_rows_strictly_older_than_the_cutoff()
    {
        var database = TemporaryPath();
        try
        {
            await using var host = await StartHostAsync(database, HostRole.Worker, "30");
            var now = host.Clock.UtcNow;
            var cutoff = now - TimeSpan.FromDays(30);
            await SeedAsync(
                host,
                now - TimeSpan.FromDays(31),
                cutoff - TimeSpan.FromSeconds(1),
                cutoff,
                now - TimeSpan.FromDays(29));

            await host.RunBackgroundWorkOnceAsync(Prune, CancellationToken.None);

            Assert.Equal(
                Formatted(host, cutoff, now - TimeSpan.FromDays(29)),
                await ReadOccurredAtAsync(host));
        }
        finally
        {
            DeleteSqliteFiles(database);
        }
    }

    // S32.3 -----------------------------------------------------------------------------------

    [Fact]
    public async Task S32_3_A_backlog_within_one_tick_is_deleted_in_one_tick()
    {
        var database = TemporaryPath();
        try
        {
            await using var host = await StartHostAsync(database, HostRole.Worker, "30");
            var old = host.Clock.UtcNow - TimeSpan.FromDays(400);
            await SeedAsync(host, Enumerable.Range(0, 1_201).Select(i => old.AddSeconds(i)).ToArray());

            await host.RunBackgroundWorkOnceAsync(Prune, CancellationToken.None);

            Assert.Empty(await ReadOccurredAtAsync(host));
        }
        finally
        {
            DeleteSqliteFiles(database);
        }
    }

    [Fact]
    public async Task S32_3_A_tick_stops_after_one_hundred_statements_and_the_next_takes_the_rest()
    {
        var database = TemporaryPath();
        try
        {
            await using var host = await StartHostAsync(database, HostRole.Worker, "30");
            var old = host.Clock.UtcNow - TimeSpan.FromDays(400);
            var seeded = Enumerable.Range(0, 50_001).Select(i => old.AddSeconds(i)).ToArray();
            await SeedAsync(host, seeded);

            await host.RunBackgroundWorkOnceAsync(Prune, CancellationToken.None);

            // Oldest first: the one row left is the newest one seeded.
            Assert.Equal(Formatted(host, seeded[^1]), await ReadOccurredAtAsync(host));

            await host.RunBackgroundWorkOnceAsync(Prune, CancellationToken.None);

            Assert.Empty(await ReadOccurredAtAsync(host));
        }
        finally
        {
            DeleteSqliteFiles(database);
        }
    }

    // S32.4 -----------------------------------------------------------------------------------

    [Fact]
    public async Task S32_4_The_prune_runs_in_the_Worker_role_only()
    {
        var database = TemporaryPath();
        try
        {
            await using var host = await StartHostAsync(database, HostRole.Web, "30");
            var registry = host.Services.GetRequiredService<IBackgroundWorkRegistry>();

            // Registered, so the role filter is what keeps it out of the Web host.
            Assert.Contains(registry.Registered, work => work.Name == Prune);
            Assert.DoesNotContain(registry.ForRole(HostRole.Web), work => work.Name == Prune);
            Assert.Contains(registry.ForRole(HostRole.Worker), work => work.Name == Prune);
        }
        finally
        {
            DeleteSqliteFiles(database);
        }
    }

    // S32.5 -----------------------------------------------------------------------------------

    [Fact]
    public async Task S32_5_A_lease_held_by_another_instance_ends_the_tick_silently()
    {
        var database = TemporaryPath();
        try
        {
            await using var host = await StartHostAsync(database, HostRole.Worker, "30");
            var old = host.Clock.UtcNow - TimeSpan.FromDays(31);
            await SeedAsync(host, old);

            var other = new LeaseManager(
                host.Services.GetRequiredService<ILeaseStore>(),
                host.Services.GetRequiredService<PlatformOptions>(),
                new InstanceId("another-host/1"),
                host.Clock);
            var held = await other.AcquireAsync(Prune, CancellationToken.None);
            Assert.True(held.IsSuccess);
            await using var lease = held.Value;

            var logger = new CapturingLogger<AuditPruneWork>();
            await CreateWork(host, logger).TickAsync(CancellationToken.None);

            Assert.Single(await ReadOccurredAtAsync(host));
            Assert.Empty(logger.Entries);
        }
        finally
        {
            DeleteSqliteFiles(database);
        }
    }

    [Fact]
    public async Task S32_5_Two_ticks_back_to_back_leave_the_same_result_as_one()
    {
        var database = TemporaryPath();
        try
        {
            await using var host = await StartHostAsync(database, HostRole.Worker, "30");
            var now = host.Clock.UtcNow;
            await SeedAsync(host, now - TimeSpan.FromDays(60), now - TimeSpan.FromDays(31), now - TimeSpan.FromDays(1));

            await host.RunBackgroundWorkOnceAsync(Prune, CancellationToken.None);
            var afterOne = await ReadOccurredAtAsync(host);
            await host.RunBackgroundWorkOnceAsync(Prune, CancellationToken.None);

            Assert.Equal(Formatted(host, now - TimeSpan.FromDays(1)), afterOne);
            Assert.Equal(afterOne, await ReadOccurredAtAsync(host));
        }
        finally
        {
            DeleteSqliteFiles(database);
        }
    }

    // S32.6 -----------------------------------------------------------------------------------

    [Theory]
    [InlineData("0", "must be between 1 and 36500")]
    [InlineData("-1", "must be between 1 and 36500")]
    [InlineData("36501", "must be between 1 and 36500")]
    [InlineData("1.5", "must be a whole number")]
    [InlineData("abc", "must be a whole number")]
    public void S32_6_An_invalid_retention_value_fails_host_construction_in_both_roles(string value, string constraint)
    {
        foreach (var role in new[] { HostRole.Web, HostRole.Worker })
        {
            var builder = BuilderWithRetention(value);
            var thrown = Assert.Throws<PlatformStartupException>(() => AddHost(builder, role));
            var error = Assert.IsType<HostStartupError>(thrown.Error);

            Assert.Equal("Configuration", error.Code);
            var inner = Assert.IsType<ConfigurationError>(error.Inner);
            Assert.Equal("InvalidSetting", inner.Code);
            Assert.Contains("Platform:Audit:RetentionDays", inner.Detail, StringComparison.Ordinal);
            Assert.Contains(constraint, inner.Detail, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("36500", 36_500)]
    public void S32_6_The_bounds_themselves_bind_in_both_roles(string value, int expected)
    {
        foreach (var role in new[] { HostRole.Web, HostRole.Worker })
        {
            var builder = BuilderWithRetention(value);
            AddHost(builder, role);

            var options = builder.Services
                .Last(descriptor => descriptor.ServiceType == typeof(PlatformOptions))
                .ImplementationInstance as PlatformOptions;
            Assert.Equal(expected, options?.Audit.RetentionDays);
        }
    }

    // S32.7 -----------------------------------------------------------------------------------

    [Fact]
    public async Task S32_7_A_retention_value_without_the_Audit_module_starts_and_prunes_nothing()
    {
        await using var host = await PlatformTestHost.CreateBuilder()
            .WithRole(HostRole.Worker)
            .WithProvider(PersistenceProvider.Sqlite)
            .WithSetting("Audit:RetentionDays", "30")
            .StartAsync(CancellationToken.None);

        var registry = host.Services.GetRequiredService<IBackgroundWorkRegistry>();
        Assert.DoesNotContain(registry.Registered, work => work.Name == Prune);
        Assert.Equal(30, host.Services.GetRequiredService<PlatformOptions>().Audit.RetentionDays);
    }

    // S32.8 -----------------------------------------------------------------------------------

    [Fact]
    public async Task S32_8_A_tick_logs_one_Information_line_with_the_count_and_cutoff_and_writes_no_audit_row()
    {
        var database = TemporaryPath();
        try
        {
            await using var host = await StartHostAsync(database, HostRole.Worker, "30");
            var now = host.Clock.UtcNow;
            var cutoff = now - TimeSpan.FromDays(30);
            await SeedAsync(host, now - TimeSpan.FromDays(40), now - TimeSpan.FromDays(35), now - TimeSpan.FromDays(1));

            var logger = new CapturingLogger<AuditPruneWork>();
            await CreateWork(host, logger).TickAsync(CancellationToken.None);

            var entry = Assert.Single(logger.Entries);
            Assert.Equal(LogLevel.Information, entry.Level);
            Assert.Contains("deleted 2 rows", entry.Message, StringComparison.Ordinal);
            Assert.Contains(cutoff.ToString("O"), entry.Message, StringComparison.Ordinal);

            // No audit row about the prune: the one row left is the one seeded inside the window.
            Assert.Equal(Formatted(host, now - TimeSpan.FromDays(1)), await ReadOccurredAtAsync(host));
        }
        finally
        {
            DeleteSqliteFiles(database);
        }
    }

    [Fact]
    public async Task S32_8_A_failing_statement_logs_a_Warning_and_leaves_readiness_unchanged()
    {
        var database = TemporaryPath();
        try
        {
            await using var host = await StartHostAsync(database, HostRole.Worker, "30");
            await ExecuteAsync(host, "DROP TABLE audit_event;");
            var before = await host.ProbeAsync(HealthCheckKind.Readiness, CancellationToken.None);

            var logger = new CapturingLogger<AuditPruneWork>();
            await CreateWork(host, logger).TickAsync(CancellationToken.None);

            var entry = Assert.Single(logger.Entries);
            Assert.Equal(LogLevel.Warning, entry.Level);
            Assert.Contains("Audit prune statement failed", entry.Message, StringComparison.Ordinal);

            var after = await host.ProbeAsync(HealthCheckKind.Readiness, CancellationToken.None);
            Assert.Equal(before.Aggregate, after.Aggregate);
            Assert.Equal(
                before.Entries.Select(e => (e.Name, e.Status)),
                after.Entries.Select(e => (e.Name, e.Status)));
        }
        finally
        {
            DeleteSqliteFiles(database);
        }
    }

    // S32.9 -----------------------------------------------------------------------------------

    [Fact]
    public async Task S32_9_The_occurred_at_index_exists_and_a_database_at_0001_migrates_to_0002_without_data_loss()
    {
        var database = TemporaryPath();
        try
        {
            await using var host = await StartHostAsync(database, HostRole.Worker, null);
            Assert.Contains("ix_audit_event_occurred_at", await ListIndexesAsync(host));

            // Put the database back at 0001, with rows in it, and migrate again.
            var now = host.Clock.UtcNow;
            await SeedAsync(host, now - TimeSpan.FromDays(3), now - TimeSpan.FromDays(2));
            await ExecuteAsync(
                host,
                "DROP INDEX ix_audit_event_occurred_at; "
                + "DELETE FROM platform_migrations_audit WHERE name = '0002_create_audit_event_occurred_at_index';");
            Assert.DoesNotContain("ix_audit_event_occurred_at", await ListIndexesAsync(host));

            var migrated = await host.Services.GetRequiredService<IMigrationRunner>().ApplyAsync(CancellationToken.None);

            Assert.True(migrated.IsSuccess);
            Assert.Contains("ix_audit_event_occurred_at", await ListIndexesAsync(host));
            Assert.Equal(
                Formatted(host, now - TimeSpan.FromDays(3), now - TimeSpan.FromDays(2)),
                await ReadOccurredAtAsync(host));
        }
        finally
        {
            DeleteSqliteFiles(database);
        }
    }

    // Helpers -----------------------------------------------------------------------------------

    private static async Task<IPlatformTestHost> StartHostAsync(string databasePath, HostRole role, string? retentionDays)
    {
        var builder = PlatformTestHost.CreateBuilder()
            .WithRole(role)
            .WithProvider(PersistenceProvider.Sqlite)
            .WithSetting("Persistence:ConnectionString", $"Data Source={databasePath}")
            .WithServices(services => services.AddSingleton<IPlatformModule, AuditModule>());
        if (retentionDays is not null)
        {
            builder = builder.WithSetting("Audit:RetentionDays", retentionDays);
        }

        var host = await builder.StartAsync(CancellationToken.None);
        var migrated = await host.Services.GetRequiredService<IMigrationRunner>().ApplyAsync(CancellationToken.None);
        Assert.True(migrated.IsSuccess);
        return host;
    }

    private static AuditPruneWork CreateWork(IPlatformTestHost host, ILogger<AuditPruneWork> logger) => new(
        host.Services.GetRequiredService<AuditStore>(),
        host.Services.GetRequiredService<IProviderCapability>(),
        host.Services.GetRequiredService<ILeaseManager>(),
        host.Services.GetRequiredService<PlatformOptions>(),
        host.Services.GetRequiredService<IClock>(),
        logger);

    private static HostApplicationBuilder BuilderWithRetention(string value)
    {
        var settings = Settings.Required();
        settings["Platform:Audit:RetentionDays"] = value;

        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = "Production",
        });
        builder.Configuration.AddInMemoryCollection(settings);
        return builder;
    }

    private static void AddHost(HostApplicationBuilder builder, HostRole role)
    {
        if (role == HostRole.Web)
        {
            builder.AddPlatformWebHost();
        }
        else
        {
            builder.AddPlatformWorkerHost();
        }
    }

    /// <summary>Writes the rows straight through the store, so each one carries the instant the
    /// test chose rather than the clock's.</summary>
    private static async Task SeedAsync(IPlatformTestHost host, params DateTimeOffset[] instants)
    {
        var store = host.Services.GetRequiredService<AuditStore>();
        var capability = host.Services.GetRequiredService<IProviderCapability>();

        var opened = await capability.BeginAsync(TransactionIntent.Write, CancellationToken.None);
        Assert.True(opened.IsSuccess);
        var transaction = opened.Value;
        try
        {
            foreach (var instant in instants)
            {
                await store.InsertAsync(
                    transaction.Connection,
                    transaction.Transaction,
                    new AuditEvent(
                        new AuditEventId(Guid.NewGuid()),
                        instant,
                        new PrincipalId("issuer", "subject"),
                        PrincipalKind.Account,
                        Tenant,
                        new AuditAction("test.seeded"),
                        null,
                        AuditOutcome.Allowed,
                        new CorrelationId("5eed0000000000000000000000000001"),
                        AuditClass.Recorded),
                    CancellationToken.None);
            }

            await transaction.Transaction.CommitAsync(CancellationToken.None);
        }
        finally
        {
            await transaction.Connection.DisposeAsync();
        }
    }

    private static List<string> Formatted(IPlatformTestHost host, params DateTimeOffset[] instants)
    {
        var capability = host.Services.GetRequiredService<IProviderCapability>();
        return [.. instants.Select(capability.FormatInstant)];
    }

    private static Task<List<string>> ReadOccurredAtAsync(IPlatformTestHost host) =>
        ReadStringsAsync(host, "SELECT occurred_at FROM audit_event ORDER BY occurred_at;");

    private static Task<List<string>> ListIndexesAsync(IPlatformTestHost host) =>
        ReadStringsAsync(
            host,
            "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'audit_event' "
            + "AND name NOT LIKE 'sqlite_%';");

    private static async Task<List<string>> ReadStringsAsync(IPlatformTestHost host, string sql)
    {
        var capability = host.Services.GetRequiredService<IProviderCapability>();
        var opened = await capability.BeginAsync(TransactionIntent.ReadOnly, CancellationToken.None);
        Assert.True(opened.IsSuccess);
        var transaction = opened.Value;
        try
        {
            await using var command = transaction.Connection.CreateCommand();
            command.Transaction = transaction.Transaction;
            command.CommandText = sql;

            var values = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
            while (await reader.ReadAsync(CancellationToken.None))
            {
                values.Add(reader.GetString(0));
            }

            return values;
        }
        finally
        {
            await transaction.Connection.DisposeAsync();
        }
    }

    private static async Task ExecuteAsync(IPlatformTestHost host, string sql)
    {
        var capability = host.Services.GetRequiredService<IProviderCapability>();
        var opened = await capability.BeginAsync(TransactionIntent.Write, CancellationToken.None);
        Assert.True(opened.IsSuccess);
        var transaction = opened.Value;
        try
        {
            await using var command = transaction.Connection.CreateCommand();
            command.Transaction = transaction.Transaction;
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(CancellationToken.None);
            await transaction.Transaction.CommitAsync(CancellationToken.None);
        }
        finally
        {
            await transaction.Connection.DisposeAsync();
        }
    }

    private static string TemporaryPath() =>
        Path.Combine(Path.GetTempPath(), $"audit-retention-test-{Guid.NewGuid():N}.db");

    private static void DeleteSqliteFiles(string path)
    {
        foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
        {
            try
            {
                File.Delete(candidate);
            }
            catch (IOException)
            {
                // Best-effort cleanup; a lingering handle is an orphaned temp file, not a failure.
            }
        }
    }
}
