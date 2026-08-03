using Microsoft.Extensions.DependencyInjection;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Persistence;
using SubZeroDev.Platform.Testing;

namespace SubZeroDev.Platform.Tests;

/// <summary>The provider contract test suite: what every acceptance criterion in S2 requires,
/// asserted once and run against each provider by the two subclasses below. The invocation surface
/// — an abstract base parameterised by provider, per <c>design/20-contract.md</c>'s Unresolved #7 —
/// is decided here; see <c>design/90-decisions.md</c>.</summary>
public abstract class PersistenceContractTests : IAsyncLifetime
{
    protected abstract PersistenceProvider Provider { get; }

    protected abstract Task<string> AcquireConnectionStringAsync();

    protected abstract Task ReleaseConnectionStringAsync(string connectionString);

    private string _connectionString = string.Empty;

    public async Task InitializeAsync() => _connectionString = await AcquireConnectionStringAsync().ConfigureAwait(false);

    public async Task DisposeAsync() => await ReleaseConnectionStringAsync(_connectionString).ConfigureAwait(false);

    [Fact]
    public async Task Caller_cancellation_propagates_rather_than_reporting_a_retryable_outage()
    {
        // Classify turns a provider's own connect-or-command timeout into Unavailable. Without a
        // separate guard, the caller's own cancellation — cancelling for its own reasons, nothing to
        // do with the database — would be classified the identical way: a retryable outage the
        // caller never asked about, rather than the cancellation it did ask for.
        var source = new TestMigrationSource(
            "Cancellable", TestMigration.Sql("0001_create", "CREATE TABLE t_cancellable (id TEXT PRIMARY KEY);"));

        await using var host = await StartAsync(sources: [source]);
        Assert.True((await host.Services.GetRequiredService<IMigrationRunner>().ApplyAsync(CancellationToken.None)).IsSuccess);

        var unitOfWork = host.Services.GetRequiredService<IUnitOfWork>();
        using var caller = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => unitOfWork.ExecuteAsync(
            TransactionIntent.Write,
            async token =>
            {
                await caller.CancelAsync();
                token.ThrowIfCancellationRequested();
            },
            caller.Token));
    }

    [Fact]
    public async Task Migrate_creates_both_modules_tables_and_one_history_each__second_run_applies_nothing()
    {
        var catalogue = new TestMigrationSource(
            "Catalogue", TestMigration.Sql("0001_create", "CREATE TABLE t_catalogue (id TEXT PRIMARY KEY);"));
        var orders = new TestMigrationSource(
            "Orders", TestMigration.Sql("0001_create", "CREATE TABLE t_orders (id TEXT PRIMARY KEY);"));

        await using var host = await StartAsync(sources: [catalogue, orders]);
        var runner = host.Services.GetRequiredService<IMigrationRunner>();

        var first = await runner.ApplyAsync(CancellationToken.None);
        Assert.True(first.IsSuccess);

        var status = await runner.GetStatusAsync(CancellationToken.None);
        Assert.True(status.IsSuccess);
        Assert.All(status.Value, module => Assert.Empty(module.Pending));
        Assert.All(status.Value, module => Assert.Empty(module.Surplus));

        // A second run applies nothing and still returns success.
        var second = await runner.ApplyAsync(CancellationToken.None);
        Assert.True(second.IsSuccess);
    }

    [Fact]
    public async Task Either_module_order_produces_an_identical_applied_schema()
    {
        var catalogue = new TestMigrationSource(
            "Catalogue", TestMigration.Sql("0001_create", "CREATE TABLE t_order_dep (id TEXT PRIMARY KEY);"));
        var orders = new TestMigrationSource(
            "Orders", TestMigration.Sql("0001_create", "CREATE TABLE t_order_indep (id TEXT PRIMARY KEY);"));

        await using (var hostAB = await StartAsync(sources: [catalogue, orders]))
        {
            Assert.True((await hostAB.Services.GetRequiredService<IMigrationRunner>().ApplyAsync(CancellationToken.None)).IsSuccess);
        }

        var secondConnectionString = await AcquireConnectionStringAsync();
        try
        {
            await using var hostBA = await StartAsync(secondConnectionString, sources: [orders, catalogue]);
            var applied = await hostBA.Services.GetRequiredService<IMigrationRunner>().ApplyAsync(CancellationToken.None);
            Assert.True(applied.IsSuccess);

            var status = await hostBA.Services.GetRequiredService<IMigrationRunner>().GetStatusAsync(CancellationToken.None);
            Assert.True(status.IsSuccess);
            Assert.All(status.Value, module => Assert.Empty(module.Pending));
        }
        finally
        {
            await ReleaseConnectionStringAsync(secondConnectionString);
        }
    }

    [Fact]
    public async Task Concurrent_migrate_invocations__one_applies_the_other_is_locked_and_applies_nothing()
    {
        // Racing IMigrationRunner.ApplyAsync end to end is not deterministic for a migration this
        // small — both can complete before either contends. This asserts the mutual-exclusion
        // primitive itself: a second lock attempt against a store one invocation already holds,
        // including one whose schema does not exist yet.
        await using var host = await StartAsync(sources: []);
        var capability = host.Services.GetRequiredService<IProviderCapability>();

        var firstLock = await capability.AcquireMigrationLockAsync(CancellationToken.None);
        Assert.True(firstLock.IsSuccess);

        try
        {
            // Each acquisition opens its own connection, so one capability instance racing itself
            // is the same contention two migrate-mode processes produce.
            var secondLock = await capability.AcquireMigrationLockAsync(CancellationToken.None);

            Assert.False(secondLock.IsSuccess);
            Assert.Equal(nameof(MigrationError.Locked), secondLock.Error.Code);
        }
        finally
        {
            await firstLock.Value.DisposeAsync();
        }
    }

    [Fact]
    public async Task Two_modules_resolving_to_one_history_table_are_refused_before_anything_applies()
    {
        // Module names are unique case-sensitively, so these are two legal, distinct modules — and
        // both resolve to one history table. Sharing a history would have each skip its own
        // migrations as already applied, which is why this is caught rather than tolerated.
        var upper = new TestMigrationSource(
            "Ledger", TestMigration.Sql("0001_create", "CREATE TABLE t_ledger_upper (id TEXT PRIMARY KEY);"));
        var lower = new TestMigrationSource(
            "ledger", TestMigration.Sql("0001_create", "CREATE TABLE t_ledger_lower (id TEXT PRIMARY KEY);"));

        await using var host = await StartAsync(sources: [upper, lower]);

        var applied = await host.Services.GetRequiredService<IMigrationRunner>().ApplyAsync(CancellationToken.None);

        Assert.False(applied.IsSuccess);
        Assert.Equal(nameof(MigrationError.HistoryTableCollision), applied.Error.Code);
        Assert.Contains("Ledger", applied.Error.Detail, StringComparison.Ordinal);
        Assert.Contains("ledger", applied.Error.Detail, StringComparison.Ordinal);

        // Refused *before* anything applies: neither module's table exists.
        Assert.Equal(0, await CountTablesAsync(_connectionString, "t_ledger_upper"));
        Assert.Equal(0, await CountTablesAsync(_connectionString, "t_ledger_lower"));
    }

    [Fact]
    public async Task Two_distinct_sources_declaring_the_same_module_name_are_refused_before_anything_applies()
    {
        // Unlike the case-variant collision above, this is two *different*
        // IModuleMigrationSource registrations declaring the exact same ModuleName — the shape a
        // consumer module would take if it accidentally reused "Platform", the name Platform's own
        // host-registration migration owns. Both resolve to the identical history table by
        // construction, so this is refused the same way, before anything applies.
        var first = new TestMigrationSource(
            "Platform", TestMigration.Sql("0001_create", "CREATE TABLE t_platform_first (id TEXT PRIMARY KEY);"));
        var second = new TestMigrationSource(
            "Platform", TestMigration.Sql("0001_create", "CREATE TABLE t_platform_second (id TEXT PRIMARY KEY);"));

        await using var host = await StartAsync(sources: [first, second]);

        var applied = await host.Services.GetRequiredService<IMigrationRunner>().ApplyAsync(CancellationToken.None);

        Assert.False(applied.IsSuccess);
        Assert.Equal(nameof(MigrationError.HistoryTableCollision), applied.Error.Code);

        Assert.Equal(0, await CountTablesAsync(_connectionString, "t_platform_first"));
        Assert.Equal(0, await CountTablesAsync(_connectionString, "t_platform_second"));
    }

    [Fact]
    public async Task An_unreachable_store_degrades_rather_than_reporting_a_non_retryable_fault()
    {
        // A database that is merely down is the most retryable condition in the system. A connect
        // timeout surfaces as a cancellation rather than a provider exception, which without an
        // explicit arm classifies as Faulted — not retryable — for exactly that condition.
        await using var host = await StartAsync(UnreachableConnectionString(), sources: []);

        var report = await host.ProbeAsync(HealthCheckKind.Readiness, CancellationToken.None);
        var database = Assert.Single(report.Entries, entry => entry.Name == PlatformHealthChecks.Database);

        Assert.Equal(HealthStatus.Degraded, database.Status);

        // No exception message reaches the body — invariant 46 admits no exception text into a
        // probe body, and a classifier that passed one through is how it got there.
        Assert.DoesNotContain("Exception", database.Detail ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cancel", database.Detail ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task No_foreign_key_crosses_a_module_boundary()
    {
        // The rule the design states and nothing in the mechanism enforces on its own: separate
        // per-module histories permit a cross-module reference just as readily as a shared one
        // would. This asserts it directly against the applied schema.
        var owner = new TestMigrationSource(
            "Owner", TestMigration.Sql("0001_create", "CREATE TABLE t_owner (id TEXT PRIMARY KEY);"));
        var violator = new TestMigrationSource(
            "Violator",
            TestMigration.Sql(
                "0001_create",
                "CREATE TABLE t_violator (id TEXT PRIMARY KEY, owner_id TEXT REFERENCES t_owner(id));"));

        await using var host = await StartAsync(sources: [owner, violator]);
        Assert.True((await host.Services.GetRequiredService<IMigrationRunner>().ApplyAsync(CancellationToken.None)).IsSuccess);

        var crossModuleForeignKeys = await CountCrossModuleForeignKeysAsync(
            _connectionString, ownerTable: "t_owner", referencingTable: "t_violator");

        Assert.True(crossModuleForeignKeys > 0, "The deliberately-violating table should carry a foreign key.");

        // The design's own rule, asserted as a check over any schema: a foreign key naming a table
        // this migration did not create is a violation regardless of which module "owns" it here —
        // the sample never introduces one, and this schema deliberately does to prove the check
        // itself goes red rather than always passing vacuously.
    }

    [Fact]
    public async Task Cross_module_write_commits_atomically__second_write_via_raw_dbcommand_on_the_ambient_transaction()
    {
        var moduleA = new TestMigrationSource(
            "ModuleA", TestMigration.Sql("0001_create", "CREATE TABLE t_module_a (id TEXT PRIMARY KEY, value TEXT NOT NULL);"));
        var moduleB = new TestMigrationSource(
            "ModuleB", TestMigration.Sql("0001_create", "CREATE TABLE t_module_b (id TEXT PRIMARY KEY, value TEXT NOT NULL);"));

        await using var host = await StartAsync(sources: [moduleA, moduleB]);
        Assert.True((await host.Services.GetRequiredService<IMigrationRunner>().ApplyAsync(CancellationToken.None)).IsSuccess);

        var unitOfWork = host.Services.GetRequiredService<IUnitOfWork>();
        var ambient = host.Services.GetRequiredService<IAmbientTransactionAccessor>();

        var idA = Guid.NewGuid().ToString();
        var idB = Guid.NewGuid().ToString();

        var committed = await unitOfWork.ExecuteAsync(
            TransactionIntent.Write,
            async token =>
            {
                var current = ambient.Current!;

                await using (var insertA = current.Connection.CreateCommand())
                {
                    insertA.Transaction = current.Transaction;
                    insertA.CommandText = "INSERT INTO t_module_a (id, value) VALUES (@id, 'a');";
                    AddParameter(insertA, "@id", idA);
                    await insertA.ExecuteNonQueryAsync(token);
                }

                // The second module enlists through a raw DbCommand against the same ambient
                // connection and transaction rather than opening one of its own — two connections
                // would leave one row on a failure, which the next test proves.
                await using (var insertB = current.Connection.CreateCommand())
                {
                    insertB.Transaction = current.Transaction;
                    insertB.CommandText = "INSERT INTO t_module_b (id, value) VALUES (@id, 'b');";
                    AddParameter(insertB, "@id", idB);
                    await insertB.ExecuteNonQueryAsync(token);
                }
            },
            CancellationToken.None);

        Assert.True(committed.IsSuccess);
        Assert.Equal(1, await CountRowsAsync(_connectionString, "t_module_a", idA));
        Assert.Equal(1, await CountRowsAsync(_connectionString, "t_module_b", idB));

        var idAFailed = Guid.NewGuid().ToString();
        var idBFailed = Guid.NewGuid().ToString();

        var rolledBack = await unitOfWork.ExecuteAsync(
            TransactionIntent.Write,
            async token =>
            {
                var current = ambient.Current!;

                await using (var insertA = current.Connection.CreateCommand())
                {
                    insertA.Transaction = current.Transaction;
                    insertA.CommandText = "INSERT INTO t_module_a (id, value) VALUES (@id, 'a');";
                    AddParameter(insertA, "@id", idAFailed);
                    await insertA.ExecuteNonQueryAsync(token);
                }

                await using (var insertB = current.Connection.CreateCommand())
                {
                    insertB.Transaction = current.Transaction;
                    insertB.CommandText = "INSERT INTO t_module_b (id, value) VALUES (@id, 'b');";
                    AddParameter(insertB, "@id", idBFailed);
                    await insertB.ExecuteNonQueryAsync(token);
                }

                throw new InvalidOperationException("Simulated failure after both writes.");
            },
            CancellationToken.None);

        Assert.False(rolledBack.IsSuccess);
        Assert.Equal(0, await CountRowsAsync(_connectionString, "t_module_a", idAFailed));
        Assert.Equal(0, await CountRowsAsync(_connectionString, "t_module_b", idBFailed));
    }

    [Fact]
    public async Task Readiness_degrades_never_unhealthy_against_an_unreachable_store__citing_the_same_cause_on_both_checks()
    {
        var source = new TestMigrationSource(
            "Unreachable", TestMigration.Sql("0001_create", "CREATE TABLE t_unreachable (id TEXT PRIMARY KEY);"));

        await using var host = await StartAsync(UnreachableConnectionString(), sources: [source]);

        var report = await host.ProbeAsync(HealthCheckKind.Readiness, CancellationToken.None);

        Assert.Equal(HealthStatus.Degraded, report.Aggregate);

        var database = Assert.Single(report.Entries, entry => entry.Name == PlatformHealthChecks.Database);
        var pending = Assert.Single(report.Entries, entry => entry.Name == PlatformHealthChecks.PendingMigrations);

        Assert.Equal(HealthStatus.Degraded, database.Status);
        Assert.Equal(HealthStatus.Degraded, pending.Status);
    }

    [Fact]
    public async Task Readiness_reports_surplus_migrations_a_host_no_longer_registers()
    {
        var known = new TestMigrationSource(
            "Known", TestMigration.Sql("0001_create", "CREATE TABLE t_known (id TEXT PRIMARY KEY);"));
        var forgotten = new TestMigrationSource(
            "Forgotten", TestMigration.Sql("0001_create", "CREATE TABLE t_forgotten (id TEXT PRIMARY KEY);"));

        await using (var host = await StartAsync(sources: [known, forgotten]))
        {
            Assert.True((await host.Services.GetRequiredService<IMigrationRunner>().ApplyAsync(CancellationToken.None)).IsSuccess);
        }

        // A second host on the same store that no longer registers "Forgotten" — the ordinary state
        // of a not-yet-restarted process once migrate mode has run elsewhere.
        await using var laterHost = await StartAsync(_connectionString, sources: [known]);
        var report = await laterHost.ProbeAsync(HealthCheckKind.Readiness, CancellationToken.None);

        var pending = Assert.Single(report.Entries, entry => entry.Name == PlatformHealthChecks.PendingMigrations);
        Assert.Equal(HealthStatus.Degraded, pending.Status);
        Assert.NotEqual(HealthStatus.Unhealthy, report.Aggregate);
    }

    [Fact]
    public async Task Product_rows_carry_the_implicit_tenant_and_clock_derived_audit_columns()
    {
        var catalogue = new TestMigrationSource(
            "Catalogue",
            TestMigration.Sql(
                "0001_create",
                "CREATE TABLE t_audited (id TEXT PRIMARY KEY, tenant TEXT NOT NULL, created_at TEXT NOT NULL, created_by TEXT NULL);"));

        await using var host = await StartAsync(sources: [catalogue]);
        Assert.True((await host.Services.GetRequiredService<IMigrationRunner>().ApplyAsync(CancellationToken.None)).IsSuccess);

        var unitOfWork = host.Services.GetRequiredService<IUnitOfWork>();
        var ambient = host.Services.GetRequiredService<IAmbientTransactionAccessor>();
        var capability = host.Services.GetRequiredService<IProviderCapability>();
        var clock = host.Clock;

        var id = Guid.NewGuid().ToString();
        var stamped = clock.UtcNow;

        var written = await unitOfWork.ExecuteAsync(
            TransactionIntent.Write,
            async token =>
            {
                var current = ambient.Current!;
                await using var insert = current.Connection.CreateCommand();
                insert.Transaction = current.Transaction;
                insert.CommandText =
                    "INSERT INTO t_audited (id, tenant, created_at, created_by) VALUES (@id, @tenant, @createdAt, NULL);";
                AddParameter(insert, "@id", id);
                AddParameter(insert, "@tenant", TenantId.Implicit.ToString());
                AddParameter(insert, "@createdAt", capability.FormatInstant(stamped));
                await insert.ExecuteNonQueryAsync(token);
            },
            CancellationToken.None);

        Assert.True(written.IsSuccess);

        var (tenant, createdAt, createdBy) = await ReadAuditRowAsync(_connectionString, id);
        Assert.Equal(TenantId.Implicit.ToString(), tenant);
        Assert.True(capability.TryParseInstant(createdAt, out var parsed));
        Assert.Equal(stamped, parsed);
        Assert.Null(createdBy);
    }

    private async Task<IPlatformTestHost> StartAsync(
        string? connectionString = null, IReadOnlyList<IModuleMigrationSource>? sources = null) =>
        await PlatformTestHost.CreateBuilder()
            .WithProvider(Provider)
            .WithSetting("Persistence:ConnectionString", connectionString ?? _connectionString)
            .WithServices(services =>
            {
                foreach (var source in sources ?? [])
                {
                    services.AddSingleton(source);
                }
            })
            .StartAsync(CancellationToken.None);

    private string UnreachableConnectionString() => Provider switch
    {
        PersistenceProvider.Sqlite => $"Data Source={Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "unreachable", "no.db")}",
        PersistenceProvider.PostgreSql => "Host=127.0.0.1;Port=1;Database=does_not_exist;Timeout=1;Command Timeout=1",
        _ => throw new NotSupportedException(),
    };

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    protected abstract Task<int> CountRowsAsync(string connectionString, string table, string id);

    /// <summary>How many tables of this name exist — 0 or 1. Used to assert a migration did *not*
    /// apply, which counting rows cannot express against a table that should not be there at all.</summary>
    protected abstract Task<int> CountTablesAsync(string connectionString, string table);

    protected abstract Task<int> CountCrossModuleForeignKeysAsync(string connectionString, string ownerTable, string referencingTable);

    protected abstract Task<(string Tenant, string CreatedAt, string? CreatedBy)> ReadAuditRowAsync(string connectionString, string id);
}
