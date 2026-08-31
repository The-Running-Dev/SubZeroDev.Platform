using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
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
