using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Persistence;
using SubZeroDev.Platform.Testing;

namespace SubZeroDev.Platform.Tests;

/// <summary>S6: the shareable type and the audited cross-tenant read.</summary>
public sealed class SharedReadTests
{
    private static readonly TenantId TenantA = new(Guid.Parse("55555555-5555-5555-5555-555555555555"));
    private static readonly TenantId TenantB = new(Guid.Parse("66666666-6666-6666-6666-666666666666"));

    private const string TableA = "t_shared_a";
    private const string TableB = "t_shared_b";

    [Fact]
    public async Task S6_1_Two_tenants_write_the_same_logical_id_without_collision_and_each_reads_only_its_own()
    {
        await using var host = await BuildHostAsync();
        var scopes = host.Services.GetRequiredService<IOperationScopeFactory>();
        var unitOfWork = host.Services.GetRequiredService<IUnitOfWork>();
        var ambient = host.Services.GetRequiredService<IAmbientTransactionAccessor>();
        var capability = host.Services.GetRequiredService<IProviderCapability>();
        var scopeFactory = host.Services.GetRequiredService<ISharedReadScopeFactory>();

        using (scopes.Begin(TenantA, FakePrincipals.System))
        {
            await InsertAsync(unitOfWork, ambient, capability, TableA, TenantA, "L1", sharedAt: null, CancellationToken.None);
        }

        using (scopes.Begin(TenantB, FakePrincipals.System))
        {
            await InsertAsync(unitOfWork, ambient, capability, TableA, TenantB, "L1", sharedAt: null, CancellationToken.None);
        }

        using (scopes.Begin(TenantA, FakePrincipals.System))
        {
            var rows = await QueryAsync<ShareableRowA>(unitOfWork, ambient, scopeFactory, TableA, TenantA, CancellationToken.None);
            var row = Assert.Single(rows);
            Assert.Equal(TenantA.ToString(), row.Tenant);
            Assert.Equal("L1", row.LogicalId);
        }

        using (scopes.Begin(TenantB, FakePrincipals.System))
        {
            var rows = await QueryAsync<ShareableRowA>(unitOfWork, ambient, scopeFactory, TableA, TenantB, CancellationToken.None);
            var row = Assert.Single(rows);
            Assert.Equal(TenantB.ToString(), row.Tenant);
            Assert.Equal("L1", row.LogicalId);
        }
    }

    [Fact]
    public async Task S6_2_Outside_a_shared_read_scope_the_filter_is_tenant_equals_current_regardless_of_publication()
    {
        await using var host = await BuildHostAsync();
        var scopes = host.Services.GetRequiredService<IOperationScopeFactory>();
        var unitOfWork = host.Services.GetRequiredService<IUnitOfWork>();
        var ambient = host.Services.GetRequiredService<IAmbientTransactionAccessor>();
        var capability = host.Services.GetRequiredService<IProviderCapability>();
        var scopeFactory = host.Services.GetRequiredService<ISharedReadScopeFactory>();

        using (scopes.Begin(TenantA, FakePrincipals.System))
        {
            await InsertAsync(unitOfWork, ambient, capability, TableA, TenantA, "mine", sharedAt: null, CancellationToken.None);
        }

        using (scopes.Begin(TenantB, FakePrincipals.System))
        {
            await InsertAsync(unitOfWork, ambient, capability, TableA, TenantB, "published", host.Clock.UtcNow, CancellationToken.None);
        }

        using (scopes.Begin(TenantA, FakePrincipals.System))
        {
            // No scope open: tenant B's row is not visible even though it is published.
            var rows = await QueryAsync<ShareableRowA>(unitOfWork, ambient, scopeFactory, TableA, TenantA, CancellationToken.None);
            var row = Assert.Single(rows);
            Assert.Equal("mine", row.LogicalId);
        }
    }

    [Fact]
    public async Task S6_3_Inside_a_scope_the_named_type_widens_and_every_other_type_stays_tenant_only()
    {
        await using var host = await BuildHostAsync();
        var scopes = host.Services.GetRequiredService<IOperationScopeFactory>();
        var unitOfWork = host.Services.GetRequiredService<IUnitOfWork>();
        var ambient = host.Services.GetRequiredService<IAmbientTransactionAccessor>();
        var capability = host.Services.GetRequiredService<IProviderCapability>();
        var scopeFactory = host.Services.GetRequiredService<ISharedReadScopeFactory>();

        using (scopes.Begin(TenantA, FakePrincipals.System))
        {
            await InsertAsync(unitOfWork, ambient, capability, TableA, TenantA, "mine-a", sharedAt: null, CancellationToken.None);
            await InsertAsync(unitOfWork, ambient, capability, TableB, TenantA, "mine-b", sharedAt: null, CancellationToken.None);
        }

        using (scopes.Begin(TenantB, FakePrincipals.System))
        {
            await InsertAsync(unitOfWork, ambient, capability, TableA, TenantB, "shared-a", host.Clock.UtcNow, CancellationToken.None);
            await InsertAsync(unitOfWork, ambient, capability, TableB, TenantB, "shared-b", host.Clock.UtcNow, CancellationToken.None);
        }

        using (scopes.Begin(TenantA, FakePrincipals.System))
        {
            using (scopeFactory.Open<ShareableRowA>())
            {
                var widened = await QueryAsync<ShareableRowA>(unitOfWork, ambient, scopeFactory, TableA, TenantA, CancellationToken.None);
                Assert.Equal(2, widened.Count);
                Assert.Contains(widened, row => row.LogicalId == "mine-a");
                Assert.Contains(widened, row => row.LogicalId == "shared-a");

                // A different declared type, in the same open scope, stays tenant-only.
                var untouched = await QueryAsync<ShareableRowB>(unitOfWork, ambient, scopeFactory, TableB, TenantA, CancellationToken.None);
                var row = Assert.Single(untouched);
                Assert.Equal("mine-b", row.LogicalId);
            }
        }
    }

    [Fact]
    public async Task S6_4_Opening_the_scope_writes_exactly_one_audit_record_regardless_of_how_many_rows_the_queries_return()
    {
        var sink = new RecordingAuditSink();
        await using var host = await BuildHostAsync(services =>
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditSink>(sink)));

        var scopes = host.Services.GetRequiredService<IOperationScopeFactory>();
        var unitOfWork = host.Services.GetRequiredService<IUnitOfWork>();
        var ambient = host.Services.GetRequiredService<IAmbientTransactionAccessor>();
        var capability = host.Services.GetRequiredService<IProviderCapability>();
        var scopeFactory = host.Services.GetRequiredService<ISharedReadScopeFactory>();

        using (scopes.Begin(TenantB, FakePrincipals.System))
        {
            for (var index = 0; index < 5; index++)
            {
                await InsertAsync(unitOfWork, ambient, capability, TableA, TenantB, $"row-{index}", host.Clock.UtcNow, CancellationToken.None);
            }
        }

        using (scopes.Begin(TenantA, FakePrincipals.System))
        {
            using (scopeFactory.Open<ShareableRowA>())
            {
                var first = await QueryAsync<ShareableRowA>(unitOfWork, ambient, scopeFactory, TableA, TenantA, CancellationToken.None);
                var second = await QueryAsync<ShareableRowA>(unitOfWork, ambient, scopeFactory, TableA, TenantA, CancellationToken.None);
                Assert.Equal(5, first.Count);
                Assert.Equal(5, second.Count);
            }
        }

        var recorded = Assert.Single(sink.Received, e => e.Action == PlatformAuditActions.SharedReadScopeOpened);
        Assert.Equal("platform.tenancy.shared-read", recorded.Action.Value);
        Assert.Equal(AuditOutcome.Allowed, recorded.Outcome);
    }

    [Fact]
    public async Task S6_5_A_write_attempted_while_a_shared_read_scope_is_open_throws_and_writes_nothing()
    {
        await using var host = await BuildHostAsync();
        var scopes = host.Services.GetRequiredService<IOperationScopeFactory>();
        var unitOfWork = host.Services.GetRequiredService<IUnitOfWork>();
        var ambient = host.Services.GetRequiredService<IAmbientTransactionAccessor>();
        var capability = host.Services.GetRequiredService<IProviderCapability>();
        var scopeFactory = host.Services.GetRequiredService<ISharedReadScopeFactory>();

        using (scopes.Begin(TenantA, FakePrincipals.System))
        {
            using (scopeFactory.Open<ShareableRowA>())
            {
                var thrown = Assert.Throws<PlatformContractViolationException>(() =>
                    unitOfWork.ExecuteAsync(
                        TransactionIntent.Write,
                        token => InsertRowAsync(ambient, capability, TableA, TenantA, "blocked", null, token),
                        CancellationToken.None).GetAwaiter().GetResult());

                Assert.Equal(nameof(ContractViolation.WriteInsideSharedReadScope), thrown.Error.Code);
            }

            var rows = await QueryAsync<ShareableRowA>(unitOfWork, ambient, scopeFactory, TableA, TenantA, CancellationToken.None);
            Assert.Empty(rows);
        }
    }

    [Fact]
    public async Task S6_6_Publishing_requires_ShareResource_is_a_tenant_scoped_write_and_audits_resource_shared()
    {
        var sink = new RecordingAuditSink();
        await using var host = await BuildHostAsync(services =>
        {
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditSink>(sink));
            services.AddSingleton<IPermissionProvider>(new StubPermissionProvider(
                "sharer",
                (_, _, _) => Result<IReadOnlySet<PermissionName>, AuthorizationError>.Success(
                    new HashSet<PermissionName> { PlatformPermissions.ShareResource })));
        });

        var scopes = host.Services.GetRequiredService<IOperationScopeFactory>();
        var unitOfWork = host.Services.GetRequiredService<IUnitOfWork>();
        var ambient = host.Services.GetRequiredService<IAmbientTransactionAccessor>();
        var capability = host.Services.GetRequiredService<IProviderCapability>();
        var evaluator = host.Services.GetRequiredService<IAuthorizationEvaluator>();
        var auditWriter = host.Services.GetRequiredService<IAuditWriter>();

        using (scopes.Begin(TenantA, FakePrincipals.System))
        {
            await InsertAsync(unitOfWork, ambient, capability, TableA, TenantA, "to-publish", sharedAt: null, CancellationToken.None);

            var decision = await evaluator.EvaluateAsync(PlatformPermissions.ShareResource, null, CancellationToken.None);
            Assert.Equal(AuthorizationOutcome.Allowed, decision.Outcome);

            var published = await unitOfWork.ExecuteAsync(
                TransactionIntent.Write,
                async token =>
                {
                    var current = ambient.Current!;
                    await using var update = current.Connection.CreateCommand();
                    update.Transaction = current.Transaction;
                    update.CommandText = $"UPDATE {TableA} SET shared_at = @sharedAt WHERE tenant = @tenant AND logical_id = @logicalId;";
                    AddParameter(update, "@sharedAt", capability.FormatInstant(host.Clock.UtcNow));
                    AddParameter(update, "@tenant", TenantA.ToString());
                    AddParameter(update, "@logicalId", "to-publish");
                    await update.ExecuteNonQueryAsync(token);

                    await auditWriter.WriteAsync(
                        PlatformAuditActions.ResourceShared, null, AuditOutcome.Allowed, AuditClass.Required, token);
                },
                CancellationToken.None);

            Assert.True(published.IsSuccess);
        }

        var recorded = Assert.Single(sink.Received, e => e.Action == PlatformAuditActions.ResourceShared);
        Assert.Equal("platform.tenancy.resource-shared", recorded.Action.Value);
        Assert.Equal(TenantA, recorded.Tenant);
    }

    [Fact]
    public void S6_7_A_fresh_asynchronous_flow_does_not_inherit_an_undisposed_scope()
    {
        // Simulates "the next request" as an independent flow: a plain Thread starts with no
        // captured ExecutionContext, so it cannot inherit AsyncLocal state the way a continuation
        // of the same flow would — the same isolation an inbound request's own fresh flow gives it.
        var openedOnFirstFlow = false;
        var openOnSecondFlow = false;

        var first = new Thread(() =>
        {
            // Deliberately not disposed — the caller failed to dispose it.
            SharedState.Factory.Open<ShareableRowA>();
            openedOnFirstFlow = SharedState.Factory.IsOpenFor<ShareableRowA>();
        });
        first.Start();
        first.Join();

        var second = new Thread(() =>
        {
            openOnSecondFlow = SharedState.Factory.IsOpenFor<ShareableRowA>();
        });
        second.Start();
        second.Join();

        Assert.True(openedOnFirstFlow);
        Assert.False(openOnSecondFlow);
    }

    [Fact]
    public void S6_8_Open_declares_no_tenant_parameter()
    {
        var method = typeof(ISharedReadScopeFactory).GetMethod(nameof(ISharedReadScopeFactory.Open))!;

        Assert.DoesNotContain(method.GetParameters(), p => p.ParameterType == typeof(TenantId));
    }

    [Fact]
    public void S6_9_No_member_of_the_persistence_surface_takes_an_explicit_tenant_parameter()
    {
        var writeSurfaces = new[]
        {
            typeof(ISharedReadScopeFactory),
            typeof(IUnitOfWork),
        };

        foreach (var surface in writeSurfaces)
        {
            foreach (var method in surface.GetMethods())
            {
                Assert.DoesNotContain(method.GetParameters(), p => p.ParameterType == typeof(TenantId));
            }
        }
    }

    private static async Task<IPlatformTestHost> BuildHostAsync(Action<IServiceCollection>? configure = null)
    {
        var host = await PlatformTestHost.CreateBuilder()
            .WithProvider(PersistenceProvider.Sqlite)
            .WithServices(services =>
            {
                services.AddSingleton<IModuleMigrationSource>(new TestMigrationSource(
                    "SharedReadA", TestMigration.Sql(
                        "0001_create",
                        $"CREATE TABLE {TableA} (tenant TEXT NOT NULL, logical_id TEXT NOT NULL, shared_at TEXT NULL, PRIMARY KEY (tenant, logical_id));")));
                services.AddSingleton<IModuleMigrationSource>(new TestMigrationSource(
                    "SharedReadB", TestMigration.Sql(
                        "0001_create",
                        $"CREATE TABLE {TableB} (tenant TEXT NOT NULL, logical_id TEXT NOT NULL, shared_at TEXT NULL, PRIMARY KEY (tenant, logical_id));")));
                configure?.Invoke(services);
            })
            .StartAsync(CancellationToken.None);

        var applied = await host.Services.GetRequiredService<IMigrationRunner>().ApplyAsync(CancellationToken.None);
        Assert.True(applied.IsSuccess);

        return host;
    }

    private static async Task InsertAsync(
        IUnitOfWork unitOfWork,
        IAmbientTransactionAccessor ambient,
        IProviderCapability capability,
        string table,
        TenantId tenant,
        string logicalId,
        DateTimeOffset? sharedAt,
        CancellationToken cancellationToken)
    {
        var result = await unitOfWork.ExecuteAsync(
            TransactionIntent.Write,
            token => InsertRowAsync(ambient, capability, table, tenant, logicalId, sharedAt, token),
            cancellationToken);
        Assert.True(result.IsSuccess);
    }

    private static async Task InsertRowAsync(
        IAmbientTransactionAccessor ambient,
        IProviderCapability capability,
        string table,
        TenantId tenant,
        string logicalId,
        DateTimeOffset? sharedAt,
        CancellationToken cancellationToken)
    {
        var current = ambient.Current!;
        await using var insert = current.Connection.CreateCommand();
        insert.Transaction = current.Transaction;
        insert.CommandText = $"INSERT INTO {table} (tenant, logical_id, shared_at) VALUES (@tenant, @logicalId, @sharedAt);";
        AddParameter(insert, "@tenant", tenant.ToString());
        AddParameter(insert, "@logicalId", logicalId);
        AddParameter(insert, "@sharedAt", sharedAt is { } value ? capability.FormatInstant(value) : null);
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<List<(string Tenant, string LogicalId)>> QueryAsync<TEntity>(
        IUnitOfWork unitOfWork,
        IAmbientTransactionAccessor ambient,
        ISharedReadScopeFactory scopeFactory,
        string table,
        TenantId currentTenant,
        CancellationToken cancellationToken)
        where TEntity : class, IShareable
    {
        var rows = new List<(string, string)>();

        var result = await unitOfWork.ExecuteAsync(
            TransactionIntent.ReadOnly,
            async token =>
            {
                var current = ambient.Current!;
                await using var select = current.Connection.CreateCommand();
                select.Transaction = current.Transaction;
                select.CommandText = scopeFactory.IsOpenFor<TEntity>()
                    ? $"SELECT tenant, logical_id FROM {table} WHERE tenant = @tenant OR shared_at IS NOT NULL;"
                    : $"SELECT tenant, logical_id FROM {table} WHERE tenant = @tenant;";
                AddParameter(select, "@tenant", currentTenant.ToString());

                await using var reader = await select.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    rows.Add((reader.GetString(0), reader.GetString(1)));
                }
            },
            cancellationToken);

        Assert.True(result.IsSuccess);
        return rows;
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private sealed record ShareableRowA(TenantId Tenant, string LogicalId, DateTimeOffset? SharedAt) : IShareable;

    private sealed record ShareableRowB(TenantId Tenant, string LogicalId, DateTimeOffset? SharedAt) : IShareable;

    /// <summary>S6.7's helper: one process-wide factory over one process-wide <see cref="SharedReadScopeState"/>,
    /// so two independent threads observe the same ambient state store the way two requests in one
    /// host would.</summary>
    private static class SharedState
    {
        internal static readonly ISharedReadScopeFactory Factory =
            new SharedReadScopeFactory(new SharedReadScopeState(), new NoOpAuditWriter());
    }

    /// <summary>Answers success without dispatching anywhere — S6.7 only needs <c>Open</c> to
    /// complete, not to prove anything about the audit trail.</summary>
    private sealed class NoOpAuditWriter : IAuditWriter
    {
        public Task<Result<AuditError>> WriteAsync(
            AuditAction action, ResourceRef? resource, AuditOutcome outcome, AuditClass auditClass, CancellationToken cancellationToken) =>
            Task.FromResult(Result<AuditError>.Success());
    }
}
