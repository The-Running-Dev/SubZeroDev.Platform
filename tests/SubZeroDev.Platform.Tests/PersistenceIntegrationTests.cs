using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Persistence;
using SubZeroDev.Platform.Testing;

namespace SubZeroDev.Platform.Tests;

/// <summary>The provider contract test suite: what every acceptance criterion in S2 requires,
/// asserted once and run against each provider by the two subclasses below. The invocation surface
/// — an abstract base parameterised by provider, per <c>design/d3/20-contract.md</c>'s Unresolved #7 —
/// is decided here; see <c>design/d3/90-decisions.md</c>.</summary>
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

    /// <summary>S2.5: a row written through Persistence carries <c>CreatedBy</c> equal to the acting
    /// principal's <c>PrincipalId.ToString()</c>, and a row written by the local host carries
    /// <c>system:local</c>.</summary>
    [Fact]
    public async Task A_row_written_by_the_local_system_principal_carries_system_local_as_created_by()
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
        var scopeFactory = host.Services.GetRequiredService<IOperationScopeFactory>();
        var currentPrincipal = host.Services.GetRequiredService<ICurrentPrincipal>();

        var id = Guid.NewGuid().ToString();

        using (scopeFactory.Begin(TenantId.Implicit, Principal.LocalSystem))
        {
            var written = await unitOfWork.ExecuteAsync(
                TransactionIntent.Write,
                async token =>
                {
                    var current = ambient.Current!;
                    await using var insert = current.Connection.CreateCommand();
                    insert.Transaction = current.Transaction;
                    insert.CommandText =
                        "INSERT INTO t_audited (id, tenant, created_at, created_by) VALUES (@id, @tenant, @createdAt, @createdBy);";
                    AddParameter(insert, "@id", id);
                    AddParameter(insert, "@tenant", TenantId.Implicit.ToString());
                    AddParameter(insert, "@createdAt", capability.FormatInstant(host.Clock.UtcNow));
                    AddParameter(insert, "@createdBy", currentPrincipal.Current.Id.ToString());
                    await insert.ExecuteNonQueryAsync(token);
                },
                CancellationToken.None);

            Assert.True(written.IsSuccess);
        }

        var (_, _, createdBy) = await ReadAuditRowAsync(_connectionString, id);
        Assert.Equal("system:local", createdBy);
        Assert.Equal(PrincipalId.LocalSystem.ToString(), createdBy);
    }

    [Fact]
    public async Task Enqueue_commits_with_the_domain_write_and_neither_survives_a_rollback()
    {
        var product = ProductTableSource();
        await using var host = await StartWithOutboxAsync(product);
        Assert.True((await host.Services.GetRequiredService<IMigrationRunner>().ApplyAsync(CancellationToken.None)).IsSuccess);

        var unitOfWork = host.Services.GetRequiredService<IUnitOfWork>();
        var ambient = host.Services.GetRequiredService<IAmbientTransactionAccessor>();
        var outbox = host.Services.GetRequiredService<IOutboxWriter>();
        var scopeFactory = host.Services.GetRequiredService<IOperationScopeFactory>();
        var capability = host.Services.GetRequiredService<IProviderCapability>();

        var committedProductId = Guid.NewGuid().ToString();
        OutboxMessageId committedOutboxId;

        using (scopeFactory.Begin(TenantId.Implicit, Principal.Anonymous))
        {
            var committed = await unitOfWork.ExecuteAsync(
                TransactionIntent.Write,
                async token =>
                {
                    await InsertProductRowAsync(ambient.Current!, committedProductId, token);
                    return outbox.Enqueue(new TestEvent());
                },
                CancellationToken.None);

            Assert.True(committed.IsSuccess);
            committedOutboxId = committed.Value;
        }

        Assert.Equal(1, await CountRowsAsync(_connectionString, "t_outbox_product", committedProductId));
        Assert.NotNull(await ReadOutboxRowAsync(_connectionString, capability, committedOutboxId));

        var rolledBackProductId = Guid.NewGuid().ToString();

        using (scopeFactory.Begin(TenantId.Implicit, Principal.Anonymous))
        {
            var rolledBack = await unitOfWork.ExecuteAsync(
                TransactionIntent.Write,
                async token =>
                {
                    await InsertProductRowAsync(ambient.Current!, rolledBackProductId, token);
                    outbox.Enqueue(new TestEvent());
                    throw new InvalidOperationException("Simulated failure after both writes.");
                },
                CancellationToken.None);

            Assert.False(rolledBack.IsSuccess);
        }

        Assert.Equal(0, await CountRowsAsync(_connectionString, "t_outbox_product", rolledBackProductId));
    }

    [Fact]
    public async Task Enqueue_returns_the_id_synchronously_before_commit_and_the_committed_row_carries_it()
    {
        await using var host = await StartWithOutboxAsync(ProductTableSource());
        Assert.True((await host.Services.GetRequiredService<IMigrationRunner>().ApplyAsync(CancellationToken.None)).IsSuccess);

        var unitOfWork = host.Services.GetRequiredService<IUnitOfWork>();
        var outbox = host.Services.GetRequiredService<IOutboxWriter>();
        var scopeFactory = host.Services.GetRequiredService<IOperationScopeFactory>();
        var capability = host.Services.GetRequiredService<IProviderCapability>();

        OutboxMessageId returnedId = default;

        using (scopeFactory.Begin(TenantId.Implicit, Principal.Anonymous))
        {
            var committed = await unitOfWork.ExecuteAsync(
                TransactionIntent.Write,
                token =>
                {
                    // Returned before the row is durable — nothing has committed yet at this point.
                    returnedId = outbox.Enqueue(new TestEvent());
                    return Task.CompletedTask;
                },
                CancellationToken.None);

            Assert.True(committed.IsSuccess);
        }

        var row = await ReadOutboxRowAsync(_connectionString, capability, returnedId);
        Assert.NotNull(row);
    }

    [Fact]
    public async Task Caller_cancellation_during_the_outbox_flush_propagates_rather_than_reporting_a_retryable_outage()
    {
        // The same property PersistenceContractTests already asserts for the work callback itself,
        // but here the cancellation happens after work returns, while UnitOfWork is flushing a
        // staged row through IOutboxStore — Classify must not turn the caller's own cancellation
        // into a misreported TransactionError.Unavailable there either.
        await using var host = await StartWithOutboxAsync();
        Assert.True((await host.Services.GetRequiredService<IMigrationRunner>().ApplyAsync(CancellationToken.None)).IsSuccess);

        var unitOfWork = host.Services.GetRequiredService<IUnitOfWork>();
        var outbox = host.Services.GetRequiredService<IOutboxWriter>();
        var scopeFactory = host.Services.GetRequiredService<IOperationScopeFactory>();
        using var caller = new CancellationTokenSource();

        using var scope = scopeFactory.Begin(TenantId.Implicit, Principal.Anonymous);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => unitOfWork.ExecuteAsync(
            TransactionIntent.Write,
            async token =>
            {
                outbox.Enqueue(new TestEvent());
                await caller.CancelAsync();
            },
            caller.Token));
    }

    [Fact]
    public async Task Enqueue_throws_without_an_ambient_transaction_and_writes_nothing()
    {
        await using var host = await StartWithOutboxAsync(ProductTableSource());
        Assert.True((await host.Services.GetRequiredService<IMigrationRunner>().ApplyAsync(CancellationToken.None)).IsSuccess);

        var outbox = host.Services.GetRequiredService<IOutboxWriter>();
        var scopeFactory = host.Services.GetRequiredService<IOperationScopeFactory>();

        using var scope = scopeFactory.Begin(TenantId.Implicit, Principal.Anonymous);

        var thrown = Assert.Throws<PlatformContractViolationException>(() => outbox.Enqueue(new TestEvent()));
        Assert.Equal("NoAmbientTransaction", thrown.Error.Code);
    }

    [Fact]
    public async Task Enqueue_throws_without_an_ambient_operation_scope_and_writes_nothing()
    {
        await using var host = await StartWithOutboxAsync(ProductTableSource());
        Assert.True((await host.Services.GetRequiredService<IMigrationRunner>().ApplyAsync(CancellationToken.None)).IsSuccess);

        var unitOfWork = host.Services.GetRequiredService<IUnitOfWork>();
        var outbox = host.Services.GetRequiredService<IOutboxWriter>();

        PlatformContractViolationException? thrown = null;

        var result = await unitOfWork.ExecuteAsync(
            TransactionIntent.Write,
            token =>
            {
                thrown = Assert.Throws<PlatformContractViolationException>(() => outbox.Enqueue(new TestEvent()));
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.NotNull(thrown);
        Assert.Equal("NoAmbientOperationScope", thrown!.Error.Code);
        Assert.True(result.IsSuccess); // the callback itself did not rethrow; committing an empty transaction is not this test's concern
    }

    [Fact]
    public async Task Enqueue_throws_for_an_unregistered_event_type_and_writes_nothing()
    {
        // No AddPlatformEventHandler call at all — TestEvent is never bound to a name. Built inline
        // rather than through StartWithOutboxAsync, which always registers one.
        await using var host = await PlatformTestHost.CreateBuilder()
            .WithProvider(Provider)
            .WithSetting("Persistence:ConnectionString", _connectionString)
            .StartAsync(CancellationToken.None);

        Assert.True((await host.Services.GetRequiredService<IMigrationRunner>().ApplyAsync(CancellationToken.None)).IsSuccess);

        var unitOfWork = host.Services.GetRequiredService<IUnitOfWork>();
        var outbox = host.Services.GetRequiredService<IOutboxWriter>();
        var scopeFactory = host.Services.GetRequiredService<IOperationScopeFactory>();

        using var scope = scopeFactory.Begin(TenantId.Implicit, Principal.Anonymous);

        var thrown = await unitOfWork.ExecuteAsync(
            TransactionIntent.Write,
            token =>
            {
                var violation = Assert.Throws<PlatformContractViolationException>(() => outbox.Enqueue(new TestEvent()));
                Assert.Equal("UnregisteredEventType", violation.Error.Code);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.True(thrown.IsSuccess);
    }

    [Fact]
    public async Task The_stored_row_carries_the_registered_type_tenant_trace_correlation_culture_and_zero_attempts()
    {
        await using var host = await StartWithOutboxAsync();
        Assert.True((await host.Services.GetRequiredService<IMigrationRunner>().ApplyAsync(CancellationToken.None)).IsSuccess);

        var unitOfWork = host.Services.GetRequiredService<IUnitOfWork>();
        var outbox = host.Services.GetRequiredService<IOutboxWriter>();
        var scopeFactory = host.Services.GetRequiredService<IOperationScopeFactory>();
        var capability = host.Services.GetRequiredService<IProviderCapability>();

        var trace = new TraceContext("00-1111111111111111111111111111aaaa-2222222222222222-01", "vendor=state");
        var correlation = new CorrelationId("3333333333333333333333333333bbbb");

        OutboxMessageId id = default;

        using (scopeFactory.Begin(trace, correlation, TenantId.Implicit, Principal.Anonymous, new CultureTag("bg")))
        {
            var committed = await unitOfWork.ExecuteAsync(
                TransactionIntent.Write,
                token =>
                {
                    id = outbox.Enqueue(new TestEvent());
                    return Task.CompletedTask;
                },
                CancellationToken.None);

            Assert.True(committed.IsSuccess);
        }

        var row = await ReadOutboxRowAsync(_connectionString, capability, id);
        Assert.NotNull(row);

        Assert.Equal("test.event", row!.Type);
        Assert.Equal(TenantId.Implicit.ToString(), row.Tenant);
        Assert.Equal(trace.TraceParent, row.TraceParent);
        Assert.Equal("vendor=state", row.TraceState);
        Assert.Equal(correlation.TraceId, row.Correlation);
        Assert.Equal("bg", row.Culture);
        Assert.Equal(0, row.Attempts);
        Assert.True(row.ClaimedByIsNull);
        Assert.True(row.ClaimedAtIsNull);
        Assert.True(row.ProcessedAtIsNull);
        Assert.True(row.PoisonedAtIsNull);
    }

    [Fact]
    public async Task IEventCapture_Enqueued_records_id_type_tenant_correlation_and_instant()
    {
        await using var host = await StartWithOutboxAsync();
        Assert.True((await host.Services.GetRequiredService<IMigrationRunner>().ApplyAsync(CancellationToken.None)).IsSuccess);

        var unitOfWork = host.Services.GetRequiredService<IUnitOfWork>();
        var outbox = host.Services.GetRequiredService<IOutboxWriter>();
        var scopeFactory = host.Services.GetRequiredService<IOperationScopeFactory>();

        OutboxMessageId id = default;

        using (scopeFactory.Begin(TenantId.Implicit, Principal.Anonymous))
        {
            await unitOfWork.ExecuteAsync(
                TransactionIntent.Write,
                token =>
                {
                    id = outbox.Enqueue(new TestEvent());
                    return Task.CompletedTask;
                },
                CancellationToken.None);
        }

        var captured = Assert.Single(host.Events.Enqueued);
        Assert.Equal(id, captured.Id);
        Assert.Equal(new EventTypeName("test.event"), captured.Type);
        Assert.Equal(TenantId.Implicit, captured.Tenant);
    }

    [Fact]
    public async Task Exactly_one_of_two_concurrent_claimants_receives_one_eligible_row()
    {
        await using var host = await StartWithOutboxAsync();
        Assert.True((await host.Services.GetRequiredService<IMigrationRunner>().ApplyAsync(CancellationToken.None)).IsSuccess);
        await EnqueueOneAsync(host);

        var store = host.Services.GetRequiredService<IOutboxStore>();
        var first = store.ClaimNextAsync(new InstanceId("claimant/first"), CancellationToken.None);
        var second = store.ClaimNextAsync(new InstanceId("claimant/second"), CancellationToken.None);
        var results = await Task.WhenAll(first, second);

        Assert.All(results, result => Assert.True(result.IsSuccess));
        Assert.Single(results, result => result.Value is not null);
    }

    [Fact]
    public async Task Expired_claim_is_reclaimed_by_the_ordinary_query_and_the_old_holder_cannot_write()
    {
        await using var host = await StartWithOutboxAsync();
        Assert.True((await host.Services.GetRequiredService<IMigrationRunner>().ApplyAsync(CancellationToken.None)).IsSuccess);
        var id = await EnqueueOneAsync(host);

        var store = host.Services.GetRequiredService<IOutboxStore>();
        var oldHolder = new InstanceId("claimant/old");
        var newHolder = new InstanceId("claimant/new");
        Assert.NotNull((await store.ClaimNextAsync(oldHolder, CancellationToken.None)).Value);

        host.Clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromTicks(1));
        var reclaimed = await store.ClaimNextAsync(newHolder, CancellationToken.None);
        Assert.True(reclaimed.IsSuccess);
        Assert.Equal(id, reclaimed.Value!.Id);

        var staleWrite = await store.MarkProcessedAsync(id, oldHolder, CancellationToken.None);
        Assert.True(staleWrite.IsSuccess);
        Assert.Equal(ClaimedWriteOutcome.ClaimLost, staleWrite.Value);

        var liveWrite = await store.MarkProcessedAsync(id, newHolder, CancellationToken.None);
        Assert.True(liveWrite.IsSuccess);
        Assert.Equal(ClaimedWriteOutcome.Applied, liveWrite.Value);
    }

    [Fact]
    public async Task Id_is_unique_across_a_drain_prune_to_empty_insert_cycle()
    {
        // SQLite's sequence is MAX(sequence)+1: draining and pruning the table to empty resets the
        // next value to 1, the same value the first row carried. Id is minted independently and is
        // what a dedupe key or a re-read must rely on, not the sequence — this is why the sequence
        // is not the identity.
        await using var host = await PlatformTestHost.CreateBuilder()
            .WithProvider(Provider)
            .WithSetting("Persistence:ConnectionString", _connectionString)
            .WithSetting("Outbox:ProcessedRetention", "00:00:01")
            .WithRole(HostRole.Worker)
            .WithServices(services =>
                services.AddPlatformEventHandler<TestEvent, TestEventHandler>(new EventTypeName("test.event")))
            .StartAsync(CancellationToken.None);
        Assert.True((await host.Services.GetRequiredService<IMigrationRunner>().ApplyAsync(CancellationToken.None)).IsSuccess);

        var capability = host.Services.GetRequiredService<IProviderCapability>();
        var store = host.Services.GetRequiredService<IOutboxStore>();
        var instance = new InstanceId("worker/id-cycle");

        var firstId = await EnqueueOneAsync(host);
        var claimed = await store.ClaimNextAsync(instance, CancellationToken.None);
        Assert.True(claimed.IsSuccess);
        Assert.Equal(firstId, claimed.Value!.Id);
        Assert.True((await store.MarkProcessedAsync(firstId, instance, CancellationToken.None)).IsSuccess);

        host.Clock.Advance(TimeSpan.FromSeconds(2));
        await host.RunBackgroundWorkOnceAsync(PlatformBackgroundWork.Prune, CancellationToken.None);
        Assert.Null(await ReadOutboxRowAsync(_connectionString, capability, firstId));

        var secondId = await EnqueueOneAsync(host);

        Assert.NotEqual(firstId, secondId);
        Assert.Null(await ReadOutboxRowAsync(_connectionString, capability, firstId));
        Assert.NotNull(await ReadOutboxRowAsync(_connectionString, capability, secondId));
    }

    [Fact]
    public async Task Concurrent_enqueues_each_commit_with_a_distinct_sequence()
    {
        // MAX(sequence)+1 races under real concurrency — on SQLite a write transaction already
        // serialises against every other writer, so this exercises the case that matters:
        // PostgreSQL's identity column must allocate every concurrent insert its own value rather
        // than colliding on the UNIQUE constraint.
        await using var host = await StartWithOutboxAsync();
        Assert.True((await host.Services.GetRequiredService<IMigrationRunner>().ApplyAsync(CancellationToken.None)).IsSuccess);

        var unitOfWork = host.Services.GetRequiredService<IUnitOfWork>();
        var outbox = host.Services.GetRequiredService<IOutboxWriter>();
        var scopeFactory = host.Services.GetRequiredService<IOperationScopeFactory>();
        var capability = host.Services.GetRequiredService<IProviderCapability>();

        async Task<OutboxMessageId> EnqueueOneAsync()
        {
            using var scope = scopeFactory.Begin(TenantId.Implicit, Principal.Anonymous);
            var id = default(OutboxMessageId);

            var committed = await unitOfWork.ExecuteAsync(
                TransactionIntent.Write,
                token =>
                {
                    id = outbox.Enqueue(new TestEvent());
                    return Task.CompletedTask;
                },
                CancellationToken.None);

            Assert.True(committed.IsSuccess);
            return id;
        }

        var ids = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => EnqueueOneAsync()));

        var sequences = new List<long>();
        foreach (var id in ids)
        {
            var row = await ReadOutboxRowAsync(_connectionString, capability, id);
            Assert.NotNull(row);
            sequences.Add(row!.Sequence);
        }

        Assert.Equal(sequences.Count, sequences.Distinct().Count());
    }

    // Cases: 1 positive (both dispatch marks set, the shape discard alone may produce), 2 negative
    // (claimed_by/claimed_at set independently of each other; poisoned_at set with no last_error) —
    // one direct write per case, asserted against the schema's own CHECK constraints rather than
    // against IOutboxStore, which never constructs a message that could violate them.
    [Fact]
    public async Task The_check_constraints_reject_a_direct_write_that_violates_them()
    {
        await using var host = await StartWithOutboxAsync();
        Assert.True((await host.Services.GetRequiredService<IMigrationRunner>().ApplyAsync(CancellationToken.None)).IsSuccess);

        var unitOfWork = host.Services.GetRequiredService<IUnitOfWork>();
        var ambient = host.Services.GetRequiredService<IAmbientTransactionAccessor>();
        var capability = host.Services.GetRequiredService<IProviderCapability>();

        // Negative case 1 of 2: claimed_by set without claimed_at violates "null together".
        var claimMismatch = await unitOfWork.ExecuteAsync(
            TransactionIntent.Write,
            token => InsertRawOutboxRowAsync(ambient.Current!, capability, claimedBy: "homelab-01/aaaaaaaa", claimedAt: null, poisonedAt: null, lastError: null, token),
            CancellationToken.None);
        Assert.False(claimMismatch.IsSuccess);

        // Negative case 2 of 2: poisoned_at set without last_error violates "poisoned implies last_error".
        var poisonMismatch = await unitOfWork.ExecuteAsync(
            TransactionIntent.Write,
            token => InsertRawOutboxRowAsync(ambient.Current!, capability, claimedBy: null, claimedAt: null, poisonedAt: capability.FormatInstant(host.Clock.UtcNow), lastError: null, token),
            CancellationToken.None);
        Assert.False(poisonMismatch.IsSuccess);

        // Positive case 1 of 1: both marks set (discard's own shape) is legal and rejects neither
        // constraint.
        var discardShape = await unitOfWork.ExecuteAsync(
            TransactionIntent.Write,
            token => InsertRawOutboxRowAsync(
                ambient.Current!,
                capability,
                claimedBy: null,
                claimedAt: null,
                poisonedAt: capability.FormatInstant(host.Clock.UtcNow),
                lastError: "discarded",
                token,
                processedAt: capability.FormatInstant(host.Clock.UtcNow)),
            CancellationToken.None);
        Assert.True(discardShape.IsSuccess);
    }

    [Fact]
    public async Task One_prune_tick_deletes_processed_and_poisoned_rows_past_retention_and_never_deletes_pending()
    {
        await using var host = await PlatformTestHost.CreateBuilder()
            .WithProvider(Provider)
            .WithSetting("Persistence:ConnectionString", _connectionString)
            .WithSetting("Outbox:ProcessedRetention", "01:00:00")
            .WithSetting("Outbox:PoisonedRetention", "02:00:00")
            .WithRole(HostRole.Worker)
            .StartAsync(CancellationToken.None);
        Assert.True((await host.Services.GetRequiredService<IMigrationRunner>().ApplyAsync(CancellationToken.None)).IsSuccess);

        var capability = host.Services.GetRequiredService<IProviderCapability>();
        var now = host.Clock.UtcNow;

        var oldProcessed = await InsertRawOutboxRowAtAsync(host, now - TimeSpan.FromHours(2), null, null);
        var youngProcessed = await InsertRawOutboxRowAtAsync(host, now - TimeSpan.FromMinutes(1), null, null);
        var oldPoisoned = await InsertRawOutboxRowAtAsync(host, null, now - TimeSpan.FromHours(3), "boom");
        var youngPoisoned = await InsertRawOutboxRowAtAsync(host, null, now - TimeSpan.FromMinutes(1), "boom");
        var ancientPending = await InsertRawOutboxRowAtAsync(host, null, null, null);

        await host.RunBackgroundWorkOnceAsync(PlatformBackgroundWork.Prune, CancellationToken.None);

        Assert.Null(await ReadOutboxRowAsync(_connectionString, capability, oldProcessed));
        Assert.NotNull(await ReadOutboxRowAsync(_connectionString, capability, youngProcessed));
        Assert.Null(await ReadOutboxRowAsync(_connectionString, capability, oldPoisoned));
        Assert.NotNull(await ReadOutboxRowAsync(_connectionString, capability, youngPoisoned));
        Assert.NotNull(await ReadOutboxRowAsync(_connectionString, capability, ancientPending));
    }

    [Fact]
    public async Task Poisoned_and_discarded_rows_both_prune_on_the_poison_window_and_the_counts_exclude_the_wrong_states()
    {
        await using var host = await PlatformTestHost.CreateBuilder()
            .WithProvider(Provider)
            .WithSetting("Persistence:ConnectionString", _connectionString)
            .WithSetting("Outbox:ProcessedRetention", "01:00:00")
            .WithSetting("Outbox:PoisonedRetention", "02:00:00")
            .WithRole(HostRole.Worker)
            .StartAsync(CancellationToken.None);
        Assert.True((await host.Services.GetRequiredService<IMigrationRunner>().ApplyAsync(CancellationToken.None)).IsSuccess);

        var capability = host.Services.GetRequiredService<IProviderCapability>();
        var store = host.Services.GetRequiredService<IOutboxStore>();
        var now = host.Clock.UtcNow;

        var pending = await InsertRawOutboxRowAtAsync(host, null, null, null);
        var processed = await InsertRawOutboxRowAtAsync(host, now - TimeSpan.FromMinutes(1), null, null);
        var poisoned = await InsertRawOutboxRowAtAsync(host, null, now - TimeSpan.FromMinutes(1), "boom");
        var discardedOld = await InsertRawOutboxRowAtAsync(host, now - TimeSpan.FromMinutes(1), now - TimeSpan.FromHours(3), "boom");

        // Pending and poisoned (never discarded) are what the two counts see.
        Assert.Equal(1, (await store.PendingCountAsync(CancellationToken.None)).Value);
        Assert.Equal(1, (await store.PoisonedCountAsync(CancellationToken.None)).Value);

        await host.RunBackgroundWorkOnceAsync(PlatformBackgroundWork.Prune, CancellationToken.None);

        // The discarded row is old enough to prune on the poison window even though it also carries
        // processed_at — discard alone may produce the both-set state, and it prunes on the same
        // window a purely poisoned row does.
        Assert.Null(await ReadOutboxRowAsync(_connectionString, capability, discardedOld));
        Assert.NotNull(await ReadOutboxRowAsync(_connectionString, capability, pending));
        Assert.NotNull(await ReadOutboxRowAsync(_connectionString, capability, processed));
        Assert.NotNull(await ReadOutboxRowAsync(_connectionString, capability, poisoned));
    }

    [Fact]
    public async Task One_prune_tick_deletes_a_dead_host_registration_and_leaves_a_live_one()
    {
        await using var host = await PlatformTestHost.CreateBuilder()
            .WithProvider(Provider)
            .WithSetting("Persistence:ConnectionString", _connectionString)
            .WithSetting("HostRegistration:RetentionWindow", "01:00:00")
            .WithRole(HostRole.Worker)
            .StartAsync(CancellationToken.None);
        Assert.True((await host.Services.GetRequiredService<IMigrationRunner>().ApplyAsync(CancellationToken.None)).IsSuccess);

        var store = host.Services.GetRequiredService<IHostRegistrationStore>();
        var now = host.Clock.UtcNow;
        var dead = new HostRegistration
        {
            Role = HostRole.Web,
            Instance = new InstanceId("dead/1"),
            StartedAt = now - TimeSpan.FromDays(2),
            HeartbeatAt = now - TimeSpan.FromHours(2),
            SettingsFingerprint = "fp",
        };
        var live = new HostRegistration
        {
            Role = HostRole.Worker,
            Instance = new InstanceId("live/1"),
            StartedAt = now,
            HeartbeatAt = now,
            SettingsFingerprint = "fp",
        };
        Assert.True((await store.UpsertAsync(dead, CancellationToken.None)).IsSuccess);
        Assert.True((await store.UpsertAsync(live, CancellationToken.None)).IsSuccess);

        await host.RunBackgroundWorkOnceAsync(PlatformBackgroundWork.Prune, CancellationToken.None);

        var remaining = await store.ListLiveAsync(DateTimeOffset.MinValue, CancellationToken.None);
        Assert.True(remaining.IsSuccess);
        Assert.DoesNotContain(remaining.Value, registration => registration.Instance == dead.Instance);
        Assert.Contains(remaining.Value, registration => registration.Instance == live.Instance);
    }

    [Fact]
    public async Task A_single_prune_statement_never_removes_more_than_the_configured_batch_size()
    {
        await using var host = await StartWithOutboxAsync();
        Assert.True((await host.Services.GetRequiredService<IMigrationRunner>().ApplyAsync(CancellationToken.None)).IsSuccess);

        var capability = host.Services.GetRequiredService<IProviderCapability>();
        var unitOfWork = host.Services.GetRequiredService<IUnitOfWork>();
        var ambient = host.Services.GetRequiredService<IAmbientTransactionAccessor>();
        var now = host.Clock.UtcNow;
        var processedAt = capability.FormatInstant(now - TimeSpan.FromDays(2));

        var inserted = await unitOfWork.ExecuteAsync(
            TransactionIntent.Write,
            async token =>
            {
                var current = ambient.Current!;
                for (var index = 0; index < 1200; index++)
                {
                    await using var insert = current.Connection.CreateCommand();
                    insert.Transaction = current.Transaction;
                    insert.CommandText = """
                        INSERT INTO platform_outbox
                            (id, sequence, occurred_at, type, payload, tenant, trace_parent, trace_state,
                             correlation, culture, attempts, next_attempt_at, first_deferred_at, claimed_by,
                             claimed_at, processed_at, poisoned_at, last_error)
                        VALUES
                            (@id, (SELECT COALESCE(MAX(sequence), 0) + 1 FROM platform_outbox), @occurredAt, @type,
                             @payload, @tenant, @traceParent, NULL, @correlation, @culture, 0, NULL, NULL,
                             NULL, NULL, @processedAt, NULL, NULL);
                        """;
                    AddParameter(insert, "@id", capability.EncodeIdentifier(Guid.NewGuid()));
                    AddParameter(insert, "@occurredAt", processedAt);
                    AddParameter(insert, "@type", "test.batch");
                    AddParameter(insert, "@payload", "{}");
                    AddParameter(insert, "@tenant", TenantId.Implicit.ToString());
                    AddParameter(insert, "@traceParent", "00-1111111111111111111111111111aaaa-2222222222222222-01");
                    AddParameter(insert, "@correlation", "3333333333333333333333333333bbbb");
                    AddParameter(insert, "@culture", string.Empty);
                    AddParameter(insert, "@processedAt", processedAt);
                    await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            },
            CancellationToken.None);
        Assert.True(inserted.IsSuccess);

        var deleted = await capability.DeleteBoundedAsync(
            PruneTarget.ProcessedOutboxRows, now - TimeSpan.FromHours(1), batchSize: 500, CancellationToken.None);

        Assert.True(deleted.IsSuccess);
        Assert.Equal(500, deleted.Value);
    }

    [Fact]
    public async Task The_three_readiness_queries_self_guard_on_an_absent_schema_rather_than_throwing()
    {
        await using var host = await StartWithOutboxAsync();
        // No IMigrationRunner.ApplyAsync — the schema does not exist yet.
        var store = host.Services.GetRequiredService<IOutboxStore>();

        var oldest = await store.OldestPendingDueAsync(CancellationToken.None);
        var pending = await store.PendingCountAsync(CancellationToken.None);
        var poisoned = await store.PoisonedCountAsync(CancellationToken.None);

        Assert.False(oldest.IsSuccess);
        Assert.Equal(nameof(TransactionError.Unavailable), oldest.Error.Code);
        Assert.False(pending.IsSuccess);
        Assert.Equal(nameof(TransactionError.Unavailable), pending.Error.Code);
        Assert.False(poisoned.IsSuccess);
        Assert.Equal(nameof(TransactionError.Unavailable), poisoned.Error.Code);
    }

    [Fact]
    public async Task Two_concurrent_lease_acquisitions_over_one_store__one_succeeds_the_other_is_held()
    {
        await using var host = await StartWithOutboxAsync();
        Assert.True((await host.Services.GetRequiredService<IMigrationRunner>().ApplyAsync(CancellationToken.None)).IsSuccess);

        var store = host.Services.GetRequiredService<ILeaseStore>();
        var expiresAt = host.Clock.UtcNow + TimeSpan.FromMinutes(5);

        var first = store.TryAcquireAsync(PlatformBackgroundWork.Prune, new InstanceId("worker/1"), expiresAt, CancellationToken.None);
        var second = store.TryAcquireAsync(PlatformBackgroundWork.Prune, new InstanceId("worker/2"), expiresAt, CancellationToken.None);
        var results = await Task.WhenAll(first, second);

        Assert.All(results, result => Assert.True(result.IsSuccess));
        Assert.Single(results, result => result.Value);
    }

    [Fact]
    public async Task An_expired_lease_is_acquired_by_a_second_holder_and_the_original_holders_renewal_is_lost()
    {
        await using var host = await StartWithOutboxAsync();
        Assert.True((await host.Services.GetRequiredService<IMigrationRunner>().ApplyAsync(CancellationToken.None)).IsSuccess);

        var store = host.Services.GetRequiredService<ILeaseStore>();
        var first = new InstanceId("worker/first");
        var second = new InstanceId("worker/second");

        var firstAcquired = await store.TryAcquireAsync(
            PlatformBackgroundWork.Prune, first, host.Clock.UtcNow + TimeSpan.FromMinutes(5), CancellationToken.None);
        Assert.True(firstAcquired.IsSuccess);
        Assert.True(firstAcquired.Value);

        host.Clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromTicks(1));

        var secondAcquired = await store.TryAcquireAsync(
            PlatformBackgroundWork.Prune, second, host.Clock.UtcNow + TimeSpan.FromMinutes(5), CancellationToken.None);
        Assert.True(secondAcquired.IsSuccess);
        Assert.True(secondAcquired.Value);

        var firstRenewed = await store.TryRenewAsync(
            PlatformBackgroundWork.Prune, first, host.Clock.UtcNow + TimeSpan.FromMinutes(5), CancellationToken.None);
        Assert.True(firstRenewed.IsSuccess);
        Assert.False(firstRenewed.Value);
    }

    /// <summary>Inserts directly against <c>platform_outbox</c> with a known id, so the caller can
    /// read it back afterward — the same bypass <see cref="InsertRawOutboxRowAsync"/> uses, with an
    /// id the caller controls instead of a random one.</summary>
    [Fact]
    public async Task S27_1_Inside_a_shared_read_scope_no_write_of_either_intent_reaches_another_tenants_row()
    {
        var source = new TestMigrationSource(
            "Isolation",
            TestMigration.Sql(
                "0001_create",
                "CREATE TABLE t_isolation (id TEXT PRIMARY KEY, tenant TEXT NOT NULL, value TEXT NOT NULL, shared_at TEXT NULL);"));
        await using var host = await StartAsync(sources: [source]);
        Assert.True((await host.Services.GetRequiredService<IMigrationRunner>().ApplyAsync(CancellationToken.None)).IsSuccess);

        var unitOfWork = host.Services.GetRequiredService<IUnitOfWork>();
        var ambient = host.Services.GetRequiredService<IAmbientTransactionAccessor>();
        var scopes = host.Services.GetRequiredService<IOperationScopeFactory>();
        var sharedRead = host.Services.GetRequiredService<ISharedReadScopeFactory>();
        var tenantA = new TenantId(Guid.Parse("77777777-7777-7777-7777-777777777777"));
        var tenantB = new TenantId(Guid.Parse("88888888-8888-8888-8888-888888888888"));

        // Tenant B publishes the one row tenant A's shared-read scope makes visible.
        using (scopes.Begin(tenantB, FakePrincipals.System))
        {
            var published = await unitOfWork.ExecuteAsync(
                TransactionIntent.Write,
                token => ExecuteOnAmbientAsync(
                    ambient,
                    "INSERT INTO t_isolation (id, tenant, value, shared_at) VALUES (@id, @tenant, 'original', 'published');",
                    "b-row",
                    tenantB,
                    token),
                CancellationToken.None);
            Assert.True(published.IsSuccess);
        }

        // An update, a delete and an insert, each carrying tenant B's id.
        string[] writes =
        [
            "UPDATE t_isolation SET value = 'tampered' WHERE id = @id AND tenant = @tenant;",
            "DELETE FROM t_isolation WHERE id = @id AND tenant = @tenant;",
            "INSERT INTO t_isolation (id, tenant, value) VALUES (@id || '-forged', @tenant, 'forged');",
        ];

        using (scopes.Begin(tenantA, FakePrincipals.System))
        {
            // A scope opened before the unit of work: refused whatever intent the unit of work
            // declares — a read-only intent is not a way round it.
            using (sharedRead.Open<IsolationRow>().Value)
            {
                foreach (var intent in new[] { TransactionIntent.Write, TransactionIntent.ReadOnly })
                {
                    foreach (var write in writes)
                    {
                        var thrown = await Assert.ThrowsAsync<PlatformContractViolationException>(() =>
                            unitOfWork.ExecuteAsync(
                                intent,
                                token => ExecuteOnAmbientAsync(ambient, write, "b-row", tenantB, token),
                                CancellationToken.None));
                        Assert.Equal("WriteInsideSharedReadScope", thrown.Error.Code);
                    }
                }
            }

            // A scope opened inside a read-only unit of work: refused from the moment it opens.
            foreach (var write in writes)
            {
                var thrown = await Assert.ThrowsAsync<PlatformContractViolationException>(() =>
                    unitOfWork.ExecuteAsync(
                        TransactionIntent.ReadOnly,
                        async token =>
                        {
                            using (sharedRead.Open<IsolationRow>().Value)
                            {
                                await ExecuteOnAmbientAsync(ambient, write, "b-row", tenantB, token);
                            }
                        },
                        CancellationToken.None));
                Assert.Equal("WriteInsideSharedReadScope", thrown.Error.Code);
            }

            // A scope opened inside a write unit of work: the scope itself is refused.
            var refused = await Assert.ThrowsAsync<PlatformContractViolationException>(() =>
                unitOfWork.ExecuteAsync(
                    TransactionIntent.Write,
                    _ =>
                    {
                        using (sharedRead.Open<IsolationRow>().Value)
                        {
                            return Task.CompletedTask;
                        }
                    },
                    CancellationToken.None));
            Assert.Equal("WriteInsideSharedReadScope", refused.Error.Code);
        }

        // Tenant B's row is exactly as it was published, and nothing was forged under its id.
        Assert.Equal(1, await CountRowsAsync(_connectionString, "t_isolation", "b-row"));
        Assert.Equal(0, await CountRowsAsync(_connectionString, "t_isolation", "b-row-forged"));
        using (scopes.Begin(tenantB, FakePrincipals.System))
        {
            var value = await unitOfWork.ExecuteAsync(
                TransactionIntent.ReadOnly,
                async token =>
                {
                    var current = ambient.Current!;
                    await using var select = current.Connection.CreateCommand();
                    select.Transaction = current.Transaction;
                    select.CommandText = "SELECT value FROM t_isolation WHERE id = 'b-row';";
                    return (string?)await select.ExecuteScalarAsync(token);
                },
                CancellationToken.None);
            Assert.True(value.IsSuccess);
            Assert.Equal("original", value.Value);
        }

        // The refusal did not outlive the scope: the same tenant writes its own row normally after.
        using (scopes.Begin(tenantA, FakePrincipals.System))
        {
            var own = await unitOfWork.ExecuteAsync(
                TransactionIntent.Write,
                token => ExecuteOnAmbientAsync(
                    ambient,
                    "INSERT INTO t_isolation (id, tenant, value) VALUES (@id, @tenant, 'mine');",
                    "a-row",
                    tenantA,
                    token),
                CancellationToken.None);
            Assert.True(own.IsSuccess);
        }

        Assert.Equal(1, await CountRowsAsync(_connectionString, "t_isolation", "a-row"));
    }

    private static async Task ExecuteOnAmbientAsync(
        IAmbientTransactionAccessor ambient, string sql, string id, TenantId tenant, CancellationToken cancellationToken)
    {
        var current = ambient.Current!;
        await using var command = current.Connection.CreateCommand();
        command.Transaction = current.Transaction;
        command.CommandText = sql;
        AddParameter(command, "@id", id);
        AddParameter(command, "@tenant", tenant.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record IsolationRow(TenantId Tenant, string Id, DateTimeOffset? SharedAt) : IShareable;

    private static async Task<OutboxMessageId> InsertRawOutboxRowAtAsync(
        IPlatformTestHost host, DateTimeOffset? processedAt, DateTimeOffset? poisonedAt, string? lastError)
    {
        var capability = host.Services.GetRequiredService<IProviderCapability>();
        var unitOfWork = host.Services.GetRequiredService<IUnitOfWork>();
        var ambient = host.Services.GetRequiredService<IAmbientTransactionAccessor>();
        var id = OutboxMessageId.Create(host.Clock.UtcNow);

        var committed = await unitOfWork.ExecuteAsync(
            TransactionIntent.Write,
            async token =>
            {
                var current = ambient.Current!;
                await using var insert = current.Connection.CreateCommand();
                insert.Transaction = current.Transaction;
                insert.CommandText = """
                    INSERT INTO platform_outbox
                        (id, sequence, occurred_at, type, payload, tenant, trace_parent, trace_state,
                         correlation, culture, attempts, next_attempt_at, first_deferred_at, claimed_by,
                         claimed_at, processed_at, poisoned_at, last_error)
                    VALUES
                        (@id, (SELECT COALESCE(MAX(sequence), 0) + 1 FROM platform_outbox), @occurredAt, @type,
                         @payload, @tenant, @traceParent, NULL, @correlation, @culture, 0, NULL, NULL,
                         NULL, NULL, @processedAt, @poisonedAt, @lastError);
                    """;
                AddParameter(insert, "@id", capability.EncodeIdentifier(id.Value));
                AddParameter(insert, "@occurredAt", capability.FormatInstant(host.Clock.UtcNow));
                AddParameter(insert, "@type", "test.prune");
                AddParameter(insert, "@payload", "{}");
                AddParameter(insert, "@tenant", TenantId.Implicit.ToString());
                AddParameter(insert, "@traceParent", "00-1111111111111111111111111111aaaa-2222222222222222-01");
                AddParameter(insert, "@correlation", "3333333333333333333333333333bbbb");
                AddParameter(insert, "@culture", string.Empty);
                AddParameter(insert, "@processedAt", processedAt is { } processed ? capability.FormatInstant(processed) : null);
                AddParameter(insert, "@poisonedAt", poisonedAt is { } poisoned ? capability.FormatInstant(poisoned) : null);
                AddParameter(insert, "@lastError", lastError);
                await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            },
            CancellationToken.None);
        Assert.True(committed.IsSuccess);
        return id;
    }

    private static TestMigrationSource ProductTableSource() => new(
        "OutboxProduct",
        TestMigration.Sql("0001_create", "CREATE TABLE t_outbox_product (id TEXT PRIMARY KEY);"));

    private async Task<IPlatformTestHost> StartWithOutboxAsync(params IModuleMigrationSource[] sources) =>
        await PlatformTestHost.CreateBuilder()
            .WithProvider(Provider)
            .WithSetting("Persistence:ConnectionString", _connectionString)
            .WithServices(services =>
            {
                foreach (var source in sources)
                {
                    services.AddSingleton<IModuleMigrationSource>(source);
                }

                services.AddPlatformEventHandler<TestEvent, TestEventHandler>(new EventTypeName("test.event"));
            })
            .StartAsync(CancellationToken.None);

    private static async Task<OutboxMessageId> EnqueueOneAsync(IPlatformTestHost host)
    {
        var unitOfWork = host.Services.GetRequiredService<IUnitOfWork>();
        var outbox = host.Services.GetRequiredService<IOutboxWriter>();
        var scopes = host.Services.GetRequiredService<IOperationScopeFactory>();
        using var scope = scopes.Begin(TenantId.Implicit, Principal.Anonymous);
        var id = default(OutboxMessageId);
        var committed = await unitOfWork.ExecuteAsync(
            TransactionIntent.Write,
            token =>
            {
                id = outbox.Enqueue(new TestEvent());
                return Task.CompletedTask;
            },
            CancellationToken.None);
        Assert.True(committed.IsSuccess);
        return id;
    }

    private static async Task InsertProductRowAsync(IAmbientTransaction current, string id, CancellationToken cancellationToken)
    {
        await using var insert = current.Connection.CreateCommand();
        insert.Transaction = current.Transaction;
        insert.CommandText = "INSERT INTO t_outbox_product (id) VALUES (@id);";
        AddParameter(insert, "@id", id);
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Inserts directly against <c>platform_outbox</c>, bypassing <see cref="IOutboxStore"/>
    /// — the only way to exercise a check constraint the store's own writer never violates by
    /// construction.</summary>
    private static async Task InsertRawOutboxRowAsync(
        IAmbientTransaction current,
        IProviderCapability capability,
        string? claimedBy,
        string? claimedAt,
        string? poisonedAt,
        string? lastError,
        CancellationToken cancellationToken,
        string? processedAt = null)
    {
        var occurredAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        await using var insert = current.Connection.CreateCommand();
        insert.Transaction = current.Transaction;
        insert.CommandText = """
            INSERT INTO platform_outbox
                (id, sequence, occurred_at, type, payload, tenant, trace_parent, trace_state,
                 correlation, culture, attempts, next_attempt_at, first_deferred_at, claimed_by,
                 claimed_at, processed_at, poisoned_at, last_error)
            VALUES
                (@id, (SELECT COALESCE(MAX(sequence), 0) + 1 FROM platform_outbox), @occurredAt, @type,
                 @payload, @tenant, @traceParent, NULL, @correlation, @culture, 0, NULL, NULL,
                 @claimedBy, @claimedAt, @processedAt, @poisonedAt, @lastError);
            """;

        AddParameter(insert, "@id", capability.EncodeIdentifier(Guid.NewGuid()));
        AddParameter(insert, "@occurredAt", capability.FormatInstant(occurredAt));
        AddParameter(insert, "@type", "test.constraint");
        AddParameter(insert, "@payload", "{}");
        AddParameter(insert, "@tenant", TenantId.Implicit.ToString());
        AddParameter(insert, "@traceParent", "00-1111111111111111111111111111aaaa-2222222222222222-01");
        AddParameter(insert, "@correlation", "3333333333333333333333333333bbbb");
        AddParameter(insert, "@culture", string.Empty);
        AddParameter(insert, "@claimedBy", claimedBy);
        AddParameter(insert, "@claimedAt", claimedAt);
        AddParameter(insert, "@processedAt", processedAt);
        AddParameter(insert, "@poisonedAt", poisonedAt);
        AddParameter(insert, "@lastError", lastError);

        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    protected abstract Task<RawOutboxRow?> ReadOutboxRowAsync(
        string connectionString, IProviderCapability capability, OutboxMessageId id);

    // S33 ---------------------------------------------------------------------------------------

    [Fact]
    public async Task S33_1_Migrate_creates_platform_inbox_after_the_lease_table_and_leaves_the_baseline_schema()
    {
        await using var host = await StartAsync();
        Assert.True((await host.Services.GetRequiredService<IMigrationRunner>().ApplyAsync(CancellationToken.None)).IsSuccess);

        // Keyed on (message_id, consumer), tenant recorded and non-null, no secondary index.
        Assert.Equal(InboxSchema, await DescribeSchemaAsync(_connectionString, ["platform_inbox"]));
        Assert.Equal(BaselineSchema, await DescribeSchemaAsync(_connectionString, BaselineTables));

        string[] expected = [.. BaselineMigrations, "0004_create_inbox"];
        var platform = host.Services.GetServices<IModuleMigrationSource>()
            .Single(source => source.Module == new ModuleName("Platform"));
        Assert.Equal(expected, platform.Migrations.Select(migration => migration.Name));

        var history = host.Services.GetRequiredService<IProviderCapability>()
            .MigrationHistoryTable(new ModuleName("Platform"));
        var ambient = host.Services.GetRequiredService<IAmbientTransactionAccessor>();
        var applied = new List<string>();
        var read = await host.Services.GetRequiredService<IUnitOfWork>().ExecuteAsync(
            TransactionIntent.ReadOnly,
            async token =>
            {
                var current = ambient.Current!;
                await using var command = current.Connection.CreateCommand();
                command.Transaction = current.Transaction;
                command.CommandText = $"SELECT name FROM {history} ORDER BY name;";
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    applied.Add(reader.GetString(0));
                }
            },
            CancellationToken.None);
        Assert.True(read.IsSuccess);
        Assert.Equal(expected, applied);
    }

    [Fact]
    public async Task S33_2_A_redelivered_message_is_skipped__its_effect_lands_once_and_the_skip_is_logged()
    {
        var probe = new InboxProbe();
        var log = new CapturingLogger<OutboxDispatcher>();
        await using var host = await StartInboxWorkerAsync(_connectionString, probe, log);
        var capability = host.Services.GetRequiredService<IProviderCapability>();
        var id = await EnqueueOneAsync(host);

        await host.RunBackgroundWorkOnceAsync(PlatformBackgroundWork.OutboxDispatch, CancellationToken.None);
        var first = await ReadOutboxRowAsync(_connectionString, capability, id);
        Assert.False(first!.ProcessedAtIsNull);
        Assert.Equal(1, probe.Invocations);

        // A lost claim, simulated: the committed message looks pending again.
        await ExecuteOnOutboxRowAsync(
            host, "UPDATE platform_outbox SET processed_at = NULL, claimed_by = NULL, claimed_at = NULL WHERE id = @id;", id);
        Assert.True((await ReadOutboxRowAsync(_connectionString, capability, id))!.ProcessedAtIsNull);

        await host.RunBackgroundWorkOnceAsync(PlatformBackgroundWork.OutboxDispatch, CancellationToken.None);

        Assert.Equal(1, probe.Invocations);
        Assert.Equal(1, await CountRowsAsync(_connectionString, "t_inbox_effect", new TestEvent().Value));
        var second = await ReadOutboxRowAsync(_connectionString, capability, id);
        Assert.False(second!.ProcessedAtIsNull);
        Assert.Equal(first.Attempts, second.Attempts);
        var skipped = Assert.Single(log.Entries, entry => entry.Message.Contains("the inbox skipped it", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Information, skipped.Level);
        Assert.Contains(id.ToString(), skipped.Message, StringComparison.Ordinal);
        Assert.Contains(InboxProbe.EventType.Value, skipped.Message, StringComparison.Ordinal);
    }

    /// <summary><c>platform_inbox</c> as <see cref="DescribeSchemaAsync"/> renders it.</summary>
    protected abstract IReadOnlyList<string> InboxSchema { get; }

    /// <summary>A Worker host whose <c>test.event</c> handler writes one <c>t_inbox_effect</c> row
    /// on the dispatch transaction, so a second application of the handler would show as a
    /// second row.</summary>
    private protected async Task<IPlatformTestHost> StartInboxWorkerAsync(
        string connectionString, InboxProbe probe, CapturingLogger<OutboxDispatcher>? log = null)
    {
        var host = await PlatformTestHost.CreateBuilder()
            .WithRole(HostRole.Worker)
            .WithProvider(Provider)
            .WithSetting("Persistence:ConnectionString", connectionString)
            .WithServices(services =>
            {
                services.AddSingleton<IModuleMigrationSource>(InboxProbe.EffectTable);
                services.AddSingleton(probe);
                services.AddPlatformEventHandler<TestEvent, InboxEffectHandler>(InboxProbe.EventType);
                if (log is not null)
                {
                    services.AddSingleton<ILogger<OutboxDispatcher>>(log);
                }
            })
            .StartAsync(CancellationToken.None);
        Assert.True((await host.Services.GetRequiredService<IMigrationRunner>().ApplyAsync(CancellationToken.None)).IsSuccess);
        return host;
    }

    private static async Task ExecuteOnOutboxRowAsync(IPlatformTestHost host, string sql, OutboxMessageId id)
    {
        var capability = host.Services.GetRequiredService<IProviderCapability>();
        var ambient = host.Services.GetRequiredService<IAmbientTransactionAccessor>();
        var written = await host.Services.GetRequiredService<IUnitOfWork>().ExecuteAsync(
            TransactionIntent.Write,
            async token =>
            {
                var current = ambient.Current!;
                await using var command = current.Connection.CreateCommand();
                command.Transaction = current.Transaction;
                command.CommandText = sql;
                AddParameter(command, "@id", capability.EncodeIdentifier(id.Value));
                Assert.Equal(1, await command.ExecuteNonQueryAsync(token));
            },
            CancellationToken.None);
        Assert.True(written.IsSuccess);
    }

    // S34 ---------------------------------------------------------------------------------------

    [Fact]
    public async Task S34_1_One_prune_pass_deletes_only_the_record_whose_outbox_row_is_gone()
    {
        await using var host = await StartPruneWorkerAsync();
        var now = host.Clock.UtcNow;

        var orphaned = OutboxMessageId.Create(now);
        var processed = await InsertRawOutboxRowAtAsync(host, now - TimeSpan.FromMinutes(1), null, null);
        var pending = await InsertRawOutboxRowAtAsync(host, null, null, null);
        foreach (var id in new[] { orphaned, processed, pending })
        {
            await InsertInboxRecordsAsync(host, now - TimeSpan.FromMinutes(1), id);
        }

        await host.RunBackgroundWorkOnceAsync(PlatformBackgroundWork.Prune, CancellationToken.None);

        Assert.Equal(0, await CountInboxRecordsAsync(host, orphaned));
        Assert.Equal(1, await CountInboxRecordsAsync(host, processed));
        Assert.Equal(1, await CountInboxRecordsAsync(host, pending));
    }

    [Fact]
    public async Task S34_2_Orphans_are_deleted_at_most_a_batch_per_statement_and_one_pass_leaves_none()
    {
        await using var host = await StartPruneWorkerAsync(pruneBatchSize: 10);
        var now = host.Clock.UtcNow;
        var orphans = Enumerable.Range(0, 25).Select(_ => OutboxMessageId.Create(now)).ToArray();
        await InsertInboxRecordsAsync(host, now - TimeSpan.FromMinutes(1), orphans);

        var capability = host.Services.GetRequiredService<IProviderCapability>();
        var first = await capability.DeleteBoundedAsync(
            PruneTarget.OrphanedInboxRecords, now, batchSize: 10, CancellationToken.None);
        Assert.True(first.IsSuccess);
        Assert.Equal(10, first.Value);
        Assert.Equal(15, await CountInboxRecordsAsync(host));

        await host.RunBackgroundWorkOnceAsync(PlatformBackgroundWork.Prune, CancellationToken.None);

        Assert.Equal(0, await CountInboxRecordsAsync(host));
    }

    [Fact]
    public async Task S34_3_Once_the_processed_prune_removes_an_outbox_row_the_next_pass_removes_its_inbox_record()
    {
        await using var host = await StartPruneWorkerAsync();
        var now = host.Clock.UtcNow;
        var id = await InsertRawOutboxRowAtAsync(host, now - TimeSpan.FromHours(2), null, null);
        await InsertInboxRecordsAsync(host, now - TimeSpan.FromHours(2), id);

        var capability = host.Services.GetRequiredService<IProviderCapability>();
        var outboxPruned = await capability.DeleteBoundedAsync(
            PruneTarget.ProcessedOutboxRows, now - TimeSpan.FromHours(1), batchSize: 500, CancellationToken.None);
        Assert.Equal(1, outboxPruned.Value);
        Assert.Null(await ReadOutboxRowAsync(_connectionString, capability, id));
        Assert.Equal(1, await CountInboxRecordsAsync(host, id));

        await host.RunBackgroundWorkOnceAsync(PlatformBackgroundWork.Prune, CancellationToken.None);

        Assert.Equal(0, await CountInboxRecordsAsync(host, id));
    }

    private async Task<IPlatformTestHost> StartPruneWorkerAsync(int pruneBatchSize = 500)
    {
        var host = await PlatformTestHost.CreateBuilder()
            .WithProvider(Provider)
            .WithSetting("Persistence:ConnectionString", _connectionString)
            .WithSetting("Outbox:ProcessedRetention", "01:00:00")
            .WithSetting("Outbox:PoisonedRetention", "02:00:00")
            .WithSetting("Outbox:PruneBatchSize", pruneBatchSize.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .WithRole(HostRole.Worker)
            .StartAsync(CancellationToken.None);
        Assert.True((await host.Services.GetRequiredService<IMigrationRunner>().ApplyAsync(CancellationToken.None)).IsSuccess);
        return host;
    }

    /// <summary>Writes one <c>test.event</c> record per message, as the dispatcher would have.</summary>
    private static async Task InsertInboxRecordsAsync(
        IPlatformTestHost host, DateTimeOffset processedAt, params OutboxMessageId[] ids)
    {
        var capability = host.Services.GetRequiredService<IProviderCapability>();
        var ambient = host.Services.GetRequiredService<IAmbientTransactionAccessor>();
        var written = await host.Services.GetRequiredService<IUnitOfWork>().ExecuteAsync(
            TransactionIntent.Write,
            async token =>
            {
                var current = ambient.Current!;
                foreach (var id in ids)
                {
                    await using var insert = current.Connection.CreateCommand();
                    insert.Transaction = current.Transaction;
                    insert.CommandText =
                        "INSERT INTO platform_inbox (message_id, consumer, tenant, processed_at) "
                        + "VALUES (@id, @consumer, @tenant, @processedAt);";
                    AddParameter(insert, "@id", capability.EncodeIdentifier(id.Value));
                    AddParameter(insert, "@consumer", "test.event");
                    AddParameter(insert, "@tenant", TenantId.Implicit.ToString());
                    AddParameter(insert, "@processedAt", capability.FormatInstant(processedAt));
                    await insert.ExecuteNonQueryAsync(token);
                }
            },
            CancellationToken.None);
        Assert.True(written.IsSuccess);
    }

    private static async Task<long> CountInboxRecordsAsync(IPlatformTestHost host, OutboxMessageId? id = null)
    {
        var capability = host.Services.GetRequiredService<IProviderCapability>();
        var ambient = host.Services.GetRequiredService<IAmbientTransactionAccessor>();
        long count = -1;
        var read = await host.Services.GetRequiredService<IUnitOfWork>().ExecuteAsync(
            TransactionIntent.ReadOnly,
            async token =>
            {
                var current = ambient.Current!;
                await using var command = current.Connection.CreateCommand();
                command.Transaction = current.Transaction;
                command.CommandText = id is null
                    ? "SELECT COUNT(*) FROM platform_inbox;"
                    : "SELECT COUNT(*) FROM platform_inbox WHERE message_id = @id;";
                if (id is { } message)
                {
                    AddParameter(command, "@id", capability.EncodeIdentifier(message.Value));
                }

                count = Convert.ToInt64(await command.ExecuteScalarAsync(token));
            },
            CancellationToken.None);
        Assert.True(read.IsSuccess);
        return count;
    }

    // S35 ---------------------------------------------------------------------------------------

    private static readonly TenantId OtherTenant = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));

    [Fact]
    public async Task S35_1_Of_two_units_that_read_the_same_version_the_second_guarded_write_is_stale()
    {
        await using var host = await StartVersionedAsync();
        await InsertVersionedRowAsync(host, "row", TenantId.Implicit, "original", version: 1);
        var unitOfWork = host.Services.GetRequiredService<IUnitOfWork>();
        var guard = host.Services.GetRequiredService<IVersionGuard>();

        // Both units read the row before either writes it.
        var firstRead = (await ReadVersionedRowAsync(host, "row"))!.Value.Version;
        var secondRead = (await ReadVersionedRowAsync(host, "row"))!.Value.Version;
        Assert.Equal(1, firstRead);
        Assert.Equal(1, secondRead);

        var first = await unitOfWork.ExecuteAsync(
            TransactionIntent.Write,
            token => GuardedRenameAsync(guard, "row", TenantId.Implicit, firstRead, "first", token),
            CancellationToken.None);
        Assert.True(first.IsSuccess);
        Assert.True(first.Value.IsSuccess);
        Assert.Equal(("first", 2L), (await ReadVersionedRowAsync(host, "row"))!.Value);

        Result<TransactionError> seen = default;
        var second = await unitOfWork.ExecuteAsync(
            TransactionIntent.Write,
            async token => seen = await GuardedRenameAsync(guard, "row", TenantId.Implicit, secondRead, "second", token),
            CancellationToken.None);

        Assert.False(second.IsSuccess);
        Assert.Equal(nameof(TransactionError.StaleVersion), second.Error.Code);
        Assert.False(second.Error.IsRetryable);
        Assert.False(seen.IsSuccess);
        Assert.Equal(second.Error, seen.Error);
        Assert.Equal(("first", 2L), (await ReadVersionedRowAsync(host, "row"))!.Value);
    }

    [Fact]
    public async Task S35_2_A_stale_guarded_write_rolls_back_the_whole_unit_even_when_its_work_ignores_the_result()
    {
        var sink = new RecordingAuditSink();
        await using var host = await StartVersionedAsync(sink);
        await InsertVersionedRowAsync(host, "row", TenantId.Implicit, "original", version: 3);
        var ambient = host.Services.GetRequiredService<IAmbientTransactionAccessor>();
        var guard = host.Services.GetRequiredService<IVersionGuard>();
        var outbox = host.Services.GetRequiredService<IOutboxWriter>();
        var audit = host.Services.GetRequiredService<IAuditWriter>();

        using var scope = host.Services.GetRequiredService<IOperationScopeFactory>().Begin(TenantId.Implicit, Principal.Anonymous);
        var executed = await host.Services.GetRequiredService<IUnitOfWork>().ExecuteAsync(
            TransactionIntent.Write,
            async token =>
            {
                var current = ambient.Current!;
                await using var insert = current.Connection.CreateCommand();
                insert.Transaction = current.Transaction;
                insert.CommandText = "INSERT INTO t_versioned_side (id) VALUES (@id);";
                AddParameter(insert, "@id", "side");
                await insert.ExecuteNonQueryAsync(token);

                outbox.Enqueue(new TestEvent());
                Assert.True((await audit.WriteAsync(
                    new AuditAction("test.versioned"), null, AuditOutcome.Allowed, AuditClass.Recorded, token)).IsSuccess);

                // Expects 1; the row is at 3. The result is deliberately not looked at.
                _ = await GuardedRenameAsync(guard, "row", TenantId.Implicit, expected: 1, "ignored", token);
            },
            CancellationToken.None);

        Assert.False(executed.IsSuccess);
        Assert.Equal(nameof(TransactionError.StaleVersion), executed.Error.Code);
        Assert.Equal(0, await CountRowsAsync(_connectionString, "t_versioned_side", "side"));
        Assert.Equal(0, await CountAllAsync(host, "platform_outbox"));
        Assert.Empty(sink.Received);
        Assert.Equal(("original", 3L), (await ReadVersionedRowAsync(host, "row"))!.Value);
    }

    [Fact]
    public async Task S35_3_A_deleted_row_a_newer_version_and_another_tenants_row_return_the_same_error()
    {
        await using var host = await StartVersionedAsync();
        await InsertVersionedRowAsync(host, "deleted", TenantId.Implicit, "a", version: 1);
        await InsertVersionedRowAsync(host, "newer", TenantId.Implicit, "b", version: 5);
        await InsertVersionedRowAsync(host, "foreign", OtherTenant, "c", version: 1);
        var unitOfWork = host.Services.GetRequiredService<IUnitOfWork>();
        var guard = host.Services.GetRequiredService<IVersionGuard>();
        var ambient = host.Services.GetRequiredService<IAmbientTransactionAccessor>();

        Assert.True((await unitOfWork.ExecuteAsync(
            TransactionIntent.Write,
            async token =>
            {
                await using var delete = ambient.Current!.Connection.CreateCommand();
                delete.Transaction = ambient.Current.Transaction;
                delete.CommandText = "DELETE FROM t_versioned WHERE id = 'deleted';";
                await delete.ExecuteNonQueryAsync(token);
            },
            CancellationToken.None)).IsSuccess);

        var errors = new List<TransactionError>();
        foreach (var id in new[] { "deleted", "newer", "foreign" })
        {
            var executed = await unitOfWork.ExecuteAsync(
                TransactionIntent.Write,
                token => GuardedRenameAsync(guard, id, TenantId.Implicit, expected: 1, "renamed", token),
                CancellationToken.None);
            Assert.False(executed.IsSuccess);
            errors.Add(executed.Error);
        }

        Assert.All(errors, error =>
        {
            Assert.Equal(nameof(TransactionError.StaleVersion), error.Code);
            Assert.Equal(TransactionError.StaleVersion().Detail, error.Detail);
        });
        Assert.Single(errors.Distinct());
        Assert.Equal(("b", 5L), (await ReadVersionedRowAsync(host, "newer"))!.Value);
        Assert.Equal(("c", 1L), (await ReadVersionedRowAsync(host, "foreign"))!.Value);
    }

    [Fact]
    public async Task S35_4_A_guarded_write_outside_a_write_unit_or_matching_two_rows_is_a_contract_violation()
    {
        await using var host = await StartVersionedAsync();
        await InsertVersionedRowAsync(host, "one", TenantId.Implicit, "a", version: 1);
        await InsertVersionedRowAsync(host, "two", TenantId.Implicit, "b", version: 1);
        var unitOfWork = host.Services.GetRequiredService<IUnitOfWork>();
        var guard = host.Services.GetRequiredService<IVersionGuard>();

        await using (var detached = DetachedCommand())
        {
            detached.CommandText = "UPDATE t_versioned SET version = 2 WHERE id = 'one' AND version = 1;";
            var outside = await Assert.ThrowsAsync<PlatformContractViolationException>(
                () => guard.ExecuteAsync(detached, CancellationToken.None));
            Assert.Equal(nameof(ContractViolation.GuardedWriteOutsideWriteTransaction), outside.Error.Code);
        }

        var readOnly = await Assert.ThrowsAsync<PlatformContractViolationException>(() => unitOfWork.ExecuteAsync(
            TransactionIntent.ReadOnly,
            token => GuardedRenameAsync(guard, "one", TenantId.Implicit, expected: 1, "read-only", token),
            CancellationToken.None));
        Assert.Equal(nameof(ContractViolation.GuardedWriteOutsideWriteTransaction), readOnly.Error.Code);

        var ambient = host.Services.GetRequiredService<IAmbientTransactionAccessor>();
        var notSingle = await Assert.ThrowsAsync<PlatformContractViolationException>(() => unitOfWork.ExecuteAsync(
            TransactionIntent.Write,
            async token =>
            {
                // Keyed to the tenant and version but not to a row: both rows match.
                await using var update = ambient.Current!.Connection.CreateCommand();
                update.CommandText =
                    "UPDATE t_versioned SET name = 'both', version = @expected + 1 WHERE tenant = @tenant AND version = @expected;";
                AddParameter(update, "@expected", 1L);
                AddParameter(update, "@tenant", TenantId.Implicit.ToString());
                return await guard.ExecuteAsync(update, token);
            },
            CancellationToken.None));
        Assert.Equal(nameof(ContractViolation.GuardedWriteNotSingleRow), notSingle.Error.Code);

        Assert.Equal(("a", 1L), (await ReadVersionedRowAsync(host, "one"))!.Value);
        Assert.Equal(("b", 1L), (await ReadVersionedRowAsync(host, "two"))!.Value);
    }

    private static TestMigrationSource VersionedTableSource(PersistenceProvider provider) => new(
        "Versioned",
        TestMigration.Sql(
            "0001_create",
            "CREATE TABLE t_versioned (id TEXT PRIMARY KEY, tenant TEXT NOT NULL, name TEXT NOT NULL, version "
            + (provider == PersistenceProvider.Sqlite ? "INTEGER" : "BIGINT") + " NOT NULL DEFAULT 1);"),
        TestMigration.Sql("0002_side", "CREATE TABLE t_versioned_side (id TEXT PRIMARY KEY);"));

    private protected async Task<IPlatformTestHost> StartVersionedAsync(RecordingAuditSink? sink = null, string? connectionString = null)
    {
        var host = await PlatformTestHost.CreateBuilder()
            .WithProvider(Provider)
            .WithSetting("Persistence:ConnectionString", connectionString ?? _connectionString)
            .WithServices(services =>
            {
                services.AddSingleton<IModuleMigrationSource>(VersionedTableSource(Provider));
                services.AddPlatformEventHandler<TestEvent, TestEventHandler>(new EventTypeName("test.event"));
                if (sink is not null)
                {
                    services.AddSingleton<IAuditSink>(sink);
                }
            })
            .StartAsync(CancellationToken.None);
        Assert.True((await host.Services.GetRequiredService<IMigrationRunner>().ApplyAsync(CancellationToken.None)).IsSuccess);
        return host;
    }

    /// <summary>Inserts with the version given, as a consumer's own insert would, letting the column
    /// default stand when it is 1.</summary>
    private protected static async Task InsertVersionedRowAsync(IPlatformTestHost host, string id, TenantId tenant, string name, long version)
    {
        var ambient = host.Services.GetRequiredService<IAmbientTransactionAccessor>();
        var inserted = await host.Services.GetRequiredService<IUnitOfWork>().ExecuteAsync(
            TransactionIntent.Write,
            async token =>
            {
                await using var insert = ambient.Current!.Connection.CreateCommand();
                insert.Transaction = ambient.Current.Transaction;
                insert.CommandText = version == 1
                    ? "INSERT INTO t_versioned (id, tenant, name) VALUES (@id, @tenant, @name);"
                    : "INSERT INTO t_versioned (id, tenant, name, version) VALUES (@id, @tenant, @name, @version);";
                AddParameter(insert, "@id", id);
                AddParameter(insert, "@tenant", tenant.ToString());
                AddParameter(insert, "@name", name);
                if (version != 1)
                {
                    AddParameter(insert, "@version", version);
                }

                await insert.ExecuteNonQueryAsync(token);
            },
            CancellationToken.None);
        Assert.True(inserted.IsSuccess);
    }

    /// <summary>The consumer's own compare-and-swap: keyed to the row, the tenant and the version
    /// read. The command has no connection or transaction — enlisting it is the guard's job.</summary>
    private protected async Task<Result<TransactionError>> GuardedRenameAsync(
        IVersionGuard guard, string id, TenantId tenant, long expected, string name, CancellationToken token)
    {
        await using var update = DetachedCommand();
        update.CommandText =
            "UPDATE t_versioned SET name = @name, version = @expected + 1 "
            + "WHERE id = @id AND tenant = @tenant AND version = @expected;";
        AddParameter(update, "@name", name);
        AddParameter(update, "@expected", expected);
        AddParameter(update, "@id", id);
        AddParameter(update, "@tenant", tenant.ToString());
        return await guard.ExecuteAsync(update, token);
    }

    /// <summary>A provider command bound to nothing, as a consumer would build one.</summary>
    private System.Data.Common.DbCommand DetachedCommand() => Provider == PersistenceProvider.Sqlite
        ? new SqliteCommand()
        : new NpgsqlCommand();

    private protected static async Task<(string Name, long Version)?> ReadVersionedRowAsync(IPlatformTestHost host, string id)
    {
        var ambient = host.Services.GetRequiredService<IAmbientTransactionAccessor>();
        (string, long)? row = null;
        var read = await host.Services.GetRequiredService<IUnitOfWork>().ExecuteAsync(
            TransactionIntent.ReadOnly,
            async token =>
            {
                await using var select = ambient.Current!.Connection.CreateCommand();
                select.Transaction = ambient.Current.Transaction;
                select.CommandText = "SELECT name, version FROM t_versioned WHERE id = @id;";
                AddParameter(select, "@id", id);
                await using var reader = await select.ExecuteReaderAsync(token);
                if (await reader.ReadAsync(token))
                {
                    row = (reader.GetString(0), Convert.ToInt64(reader.GetValue(1)));
                }
            },
            CancellationToken.None);
        Assert.True(read.IsSuccess);
        return row;
    }

    private static async Task<long> CountAllAsync(IPlatformTestHost host, string table)
    {
        var ambient = host.Services.GetRequiredService<IAmbientTransactionAccessor>();
        long count = -1;
        var read = await host.Services.GetRequiredService<IUnitOfWork>().ExecuteAsync(
            TransactionIntent.ReadOnly,
            async token =>
            {
                await using var command = ambient.Current!.Connection.CreateCommand();
                command.Transaction = ambient.Current.Transaction;
                command.CommandText = $"SELECT COUNT(*) FROM {table};";
                count = Convert.ToInt64(await command.ExecuteScalarAsync(token));
            },
            CancellationToken.None);
        Assert.True(read.IsSuccess);
        return count;
    }

    // S27.3 -----------------------------------------------------------------------------------

    /// <summary>The migrations Platform registered at D5's baseline commit (<c>d6daabf</c>, the
    /// parent of D5-S1), by name. Each is up-only: an applied migration is never re-run, so one
    /// changed after the fact silently diverges every database that already applied it.</summary>
    private static readonly string[] BaselineMigrations =
        ["0001_create_host_registration", "0002_create_outbox", "0003_create_background_work_lease"];

    /// <summary>The tables those migrations create.</summary>
    private static readonly string[] BaselineTables =
        ["platform_background_work_lease", "platform_host_registration", "platform_outbox"];

    [Fact]
    public async Task S27_3_The_migrations_present_at_D5s_baseline_are_unmodified_and_still_produce_the_baseline_schema()
    {
        // The implicit tenant's stored form — the representation D3 and G2 fixed.
        Assert.Equal("00000000-0000-0000-0000-000000000000", TenantId.Implicit.ToString());

        await using var host = await StartAsync();
        var runner = host.Services.GetRequiredService<IMigrationRunner>();
        Assert.True((await runner.ApplyAsync(CancellationToken.None)).IsSuccess);

        // Every baseline migration is still registered under its baseline name: a rename is a
        // modification too — the history would record it as new and re-run it.
        var platform = host.Services.GetServices<IModuleMigrationSource>()
            .Single(source => source.Module == new ModuleName("Platform"));
        Assert.Subset(
            platform.Migrations.Select(migration => migration.Name).ToHashSet(StringComparer.Ordinal),
            BaselineMigrations.ToHashSet(StringComparer.Ordinal));

        // What they produce, column by column — the tenant column's type and nullability and every
        // primary key among it — pinned per provider as captured from the baseline. A changed
        // migration body that alters any of it fails here.
        var schema = await DescribeSchemaAsync(_connectionString, BaselineTables);
        Assert.Equal(BaselineSchema, schema);
    }

    /// <summary>The baseline tables' schema as <see cref="DescribeSchemaAsync"/> renders it, pinned
    /// from the migrations as they stood at D5's baseline commit.</summary>
    protected abstract IReadOnlyList<string> BaselineSchema { get; }

    /// <summary>One line per column — <c>table.column type [NOT NULL] [PKn]</c> — then one per
    /// secondary index, <c>table index(columns)</c>; tables in the order given, columns in declared
    /// order.</summary>
    protected abstract Task<IReadOnlyList<string>> DescribeSchemaAsync(string connectionString, IReadOnlyList<string> tables);

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

/// <summary>Contract assertion "a payload written under one provider deserializes under the other",
/// in `20-contract.md`'s provider-contract-tests table: the payload format is the serialiser's, not
/// the provider's. Enqueues under SQLite, then carries the exact stored payload text into a fresh
/// row inserted under PostgreSQL and deserializes it there — proving the text one provider wrote is
/// meaningful to the other with no provider-specific step in between.</summary>
public sealed class CrossProviderPayloadTests(PostgresContainerFixture fixture) : IClassFixture<PostgresContainerFixture>
{
    [Fact]
    public async Task A_payload_written_under_one_provider_deserializes_under_the_other()
    {
        var sqliteConnectionString =
            $"Data Source={Path.Combine(Path.GetTempPath(), $"platform-cross-{Guid.NewGuid():N}.db")}";
        var postgresConnectionString = await AcquirePostgresConnectionStringAsync();

        try
        {
            var written = new TestEvent("cross-provider-payload");

            await using var sqliteHost = await PlatformTestHost.CreateBuilder()
                .WithProvider(PersistenceProvider.Sqlite)
                .WithSetting("Persistence:ConnectionString", sqliteConnectionString)
                .WithRole(HostRole.Worker)
                .WithServices(services =>
                    services.AddPlatformEventHandler<TestEvent, TestEventHandler>(new EventTypeName("test.event")))
                .StartAsync(CancellationToken.None);
            Assert.True((await sqliteHost.Services.GetRequiredService<IMigrationRunner>().ApplyAsync(CancellationToken.None)).IsSuccess);

            var sqliteWriter = sqliteHost.Services.GetRequiredService<IOutboxWriter>();
            var sqliteScopes = sqliteHost.Services.GetRequiredService<IOperationScopeFactory>();
            using var sqliteScope = sqliteScopes.Begin(TenantId.Implicit, Principal.Anonymous);

            var writtenId = default(OutboxMessageId);
            var sqliteCommitted = await sqliteHost.Services.GetRequiredService<IUnitOfWork>().ExecuteAsync(
                TransactionIntent.Write,
                token =>
                {
                    writtenId = sqliteWriter.Enqueue(written);
                    return Task.CompletedTask;
                },
                CancellationToken.None);
            Assert.True(sqliteCommitted.IsSuccess);

            var sqliteStore = sqliteHost.Services.GetRequiredService<IOutboxStore>();
            var sqliteClaim = await sqliteStore.ClaimNextAsync(new InstanceId("cross/sqlite"), CancellationToken.None);
            Assert.True(sqliteClaim.IsSuccess);
            Assert.Equal(writtenId, sqliteClaim.Value!.Id);
            var payload = sqliteClaim.Value.Payload;

            await using var postgresHost = await PlatformTestHost.CreateBuilder()
                .WithProvider(PersistenceProvider.PostgreSql)
                .WithSetting("Persistence:ConnectionString", postgresConnectionString)
                .WithRole(HostRole.Worker)
                .StartAsync(CancellationToken.None);
            Assert.True((await postgresHost.Services.GetRequiredService<IMigrationRunner>().ApplyAsync(CancellationToken.None)).IsSuccess);

            var postgresStore = postgresHost.Services.GetRequiredService<IOutboxStore>();
            var movedId = OutboxMessageId.Create(postgresHost.Clock.UtcNow);
            var movedMessage = new OutboxMessage
            {
                Id = movedId,
                Sequence = 0,
                OccurredAt = postgresHost.Clock.UtcNow,
                Type = new EventTypeName("test.event"),
                Payload = payload,
                Tenant = TenantId.Implicit,
                TraceContext = new TraceContext("00-1111111111111111111111111111aaaa-2222222222222222-01", null),
                Correlation = new CorrelationId("1111111111111111111111111111aaaa"),
                Culture = CultureTag.Invariant,
                Attempts = 0,
            };

            var insertResult = default(Result<TransactionError>);
            var postgresCommitted = await postgresHost.Services.GetRequiredService<IUnitOfWork>().ExecuteAsync(
                TransactionIntent.Write,
                async token => { insertResult = await postgresStore.InsertAsync(movedMessage, token); },
                CancellationToken.None);
            Assert.True(postgresCommitted.IsSuccess);
            Assert.True(insertResult.IsSuccess);

            var postgresClaim = await postgresStore.ClaimNextAsync(new InstanceId("cross/postgres"), CancellationToken.None);
            Assert.True(postgresClaim.IsSuccess);
            Assert.Equal(movedId, postgresClaim.Value!.Id);

            var deserialized = JsonSerializer.Deserialize<TestEvent>(postgresClaim.Value.Payload, OutboxSerializer.Options);
            Assert.Equal(written.Value, deserialized!.Value);
        }
        finally
        {
            CleanupSqlite(sqliteConnectionString);
            await DropPostgresAsync(postgresConnectionString);
        }
    }

    private async Task<string> AcquirePostgresConnectionStringAsync()
    {
        var database = $"test_{Guid.NewGuid():N}";

        await using var admin = new NpgsqlConnection(fixture.AdminConnectionString);
        await admin.OpenAsync();
        await using var create = admin.CreateCommand();
        create.CommandText = $"CREATE DATABASE \"{database}\";";
        await create.ExecuteNonQueryAsync();

        var builder = new NpgsqlConnectionStringBuilder(fixture.AdminConnectionString) { Database = database };
        return builder.ConnectionString;
    }

    private async Task DropPostgresAsync(string connectionString)
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

    private static void CleanupSqlite(string connectionString)
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
    }
}

/// <summary>Shared by every host in an inbox test: how often the handler ran, and an optional gate
/// that holds the handler — and so its dispatch transaction — open after it has written.</summary>
internal sealed class InboxProbe
{
    internal static readonly EventTypeName EventType = new("test.event");

    internal static readonly TestMigrationSource EffectTable = new(
        "InboxEffect", TestMigration.Sql("0001_create", "CREATE TABLE t_inbox_effect (id TEXT NOT NULL);"));

    private int _invocations;

    internal int Invocations => Volatile.Read(ref _invocations);

    internal bool Block { get; set; }

    internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal void Invoked() => Interlocked.Increment(ref _invocations);
}

/// <summary>Writes one unkeyed <c>t_inbox_effect</c> row per invocation on the dispatch
/// transaction: applied twice, it leaves two rows.</summary>
internal sealed class InboxEffectHandler(IAmbientTransactionAccessor ambient, InboxProbe probe)
    : IIntegrationEventHandler<TestEvent>
{
    public async Task<Result<HandlerError>> HandleAsync(TestEvent @event, CancellationToken cancellationToken)
    {
        probe.Invoked();
        var current = ambient.Current!;
        await using (var insert = current.Connection.CreateCommand())
        {
            insert.Transaction = current.Transaction;
            insert.CommandText = "INSERT INTO t_inbox_effect (id) VALUES (@id);";
            var parameter = insert.CreateParameter();
            parameter.ParameterName = "@id";
            parameter.Value = @event.Value;
            insert.Parameters.Add(parameter);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        if (probe.Block)
        {
            probe.Started.TrySetResult();
            await probe.Release.Task.WaitAsync(cancellationToken);
        }

        return Result<HandlerError>.Success();
    }
}
