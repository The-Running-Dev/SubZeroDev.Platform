using Microsoft.Extensions.DependencyInjection;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Persistence;
using SubZeroDev.Platform.Testing;

namespace SubZeroDev.Platform.Tests;

/// <summary>S7: the entitlement seam and the Community baseline.</summary>
public sealed class EntitlementTests
{
    private static readonly TenantId TestTenant = new(Guid.Parse("33333333-3333-3333-3333-333333333333"));
    private static readonly FeatureName TestFeature = new("Test.Widget.Advanced");

    [Fact]
    public void S7_1_EvaluateAsync_declares_no_tenant_parameter()
    {
        var parameters = typeof(IEntitlementEvaluator)
            .GetMethod(nameof(IEntitlementEvaluator.EvaluateAsync))!
            .GetParameters();

        Assert.DoesNotContain(parameters, p => p.ParameterType == typeof(TenantId));
    }

    [Fact]
    public async Task S7_1_The_tenant_comes_from_the_ambient_scope_and_appears_on_the_decision()
    {
        await using var host = await BuildHostAsync();
        var evaluator = host.Services.GetRequiredService<IEntitlementEvaluator>();
        var scopeFactory = host.Services.GetRequiredService<IOperationScopeFactory>();

        using (scopeFactory.Begin(TestTenant, FakePrincipals.System))
        {
            var decision = await evaluator.EvaluateAsync(TestFeature, CancellationToken.None);
            Assert.Equal(TestTenant, decision.Tenant);
        }
    }

    [Fact]
    public async Task S7_2_Two_contributors_granting_produce_one_decision_naming_both_sources()
    {
        await using var host = await BuildHostAsync(services =>
        {
            services.AddKeyedSingleton<IEntitlementContributor>(EntitlementContributorRegistration.ServiceKey, GrantingContributor("first"));
            services.AddKeyedSingleton<IEntitlementContributor>(EntitlementContributorRegistration.ServiceKey, GrantingContributor("second"));
        });

        var decision = await EvaluateAsync(host);

        Assert.True(decision.Granted);
        Assert.Equal(
            new[] { "first", "second" },
            decision.Sources.Select(source => source.Value).Order());
    }

    [Fact]
    public async Task S7_2_A_decision_that_is_not_granted_carries_an_empty_source_set()
    {
        await using var host = await BuildHostAsync();

        var decision = await EvaluateAsync(host);

        Assert.False(decision.Granted);
        Assert.Empty(decision.Sources);
    }

    [Fact]
    public async Task S7_3_One_granting_and_one_declining_produce_a_granted_decision()
    {
        await using var host = await BuildHostAsync(services =>
        {
            services.AddKeyedSingleton<IEntitlementContributor>(EntitlementContributorRegistration.ServiceKey, GrantingContributor("granter"));
            services.AddKeyedSingleton<IEntitlementContributor>(EntitlementContributorRegistration.ServiceKey, DecliningContributor("decliner"));
        });

        var decision = await EvaluateAsync(host);

        Assert.True(decision.Granted);
        Assert.Equal("granter", Assert.Single(decision.Sources).Value);
    }

    [Fact]
    public async Task S7_4_An_unavailable_contributor_contributes_nothing_but_does_not_fail_the_evaluation()
    {
        await using var host = await BuildHostAsync(services =>
        {
            services.AddKeyedSingleton<IEntitlementContributor>(
                EntitlementContributorRegistration.ServiceKey,
                new StubEntitlementContributor(
                    "unavailable",
                    (_, _) => Result<bool, EntitlementError>.Failure(
                        EntitlementError.ContributorUnavailable(new EntitlementContributorName("unavailable")))));
            services.AddKeyedSingleton<IEntitlementContributor>(EntitlementContributorRegistration.ServiceKey, GrantingContributor("granter"));
        });

        var decision = await EvaluateAsync(host);

        Assert.True(decision.Granted);
        Assert.Equal("granter", Assert.Single(decision.Sources).Value);
    }

    [Fact]
    public void S7_4_ContributorUnavailable_is_retryable()
    {
        var error = EntitlementError.ContributorUnavailable(new EntitlementContributorName("store"));

        Assert.True(error.IsRetryable);
    }

    [Fact]
    public async Task S7_5_A_decision_round_trips_through_storage_beside_a_work_item()
    {
        const string table = "t_work_item_with_decision";

        await using var host = await PlatformTestHost.CreateBuilder()
            .WithProvider(PersistenceProvider.Sqlite)
            .WithServices(services =>
            {
                services.AddSingleton<IModuleMigrationSource>(new TestMigrationSource(
                    "EntitlementRoundTrip",
                    TestMigration.Sql(
                        "0001_create",
                        $"CREATE TABLE {table} (work_item_id TEXT PRIMARY KEY, feature TEXT NOT NULL, "
                        + "tenant TEXT NOT NULL, granted INTEGER NOT NULL, decided_at TEXT NOT NULL, "
                        + "sources TEXT NOT NULL);")));
                services.AddKeyedSingleton<IEntitlementContributor>(
                    EntitlementContributorRegistration.ServiceKey, GrantingContributor("granter"));
            })
            .StartAsync(CancellationToken.None);

        var applied = await host.Services.GetRequiredService<IMigrationRunner>().ApplyAsync(CancellationToken.None);
        Assert.True(applied.IsSuccess);

        var evaluator = host.Services.GetRequiredService<IEntitlementEvaluator>();
        var scopeFactory = host.Services.GetRequiredService<IOperationScopeFactory>();
        var unitOfWork = host.Services.GetRequiredService<IUnitOfWork>();
        var ambient = host.Services.GetRequiredService<IAmbientTransactionAccessor>();
        var capability = host.Services.GetRequiredService<IProviderCapability>();

        EntitlementDecision original;
        using (scopeFactory.Begin(TestTenant, FakePrincipals.System))
        {
            original = await evaluator.EvaluateAsync(TestFeature, CancellationToken.None);

            var written = await unitOfWork.ExecuteAsync(
                TransactionIntent.Write,
                async token =>
                {
                    var current = ambient.Current!;
                    await using var insert = current.Connection.CreateCommand();
                    insert.Transaction = current.Transaction;
                    insert.CommandText = $"INSERT INTO {table} "
                        + "(work_item_id, feature, tenant, granted, decided_at, sources) "
                        + "VALUES (@workItemId, @feature, @tenant, @granted, @decidedAt, @sources);";
                    AddParameter(insert, "@workItemId", "work-item-1");
                    AddParameter(insert, "@feature", original.Feature.Value);
                    AddParameter(insert, "@tenant", original.Tenant.ToString());
                    AddParameter(insert, "@granted", original.Granted ? 1 : 0);
                    AddParameter(insert, "@decidedAt", capability.FormatInstant(original.DecidedAt));
                    AddParameter(insert, "@sources", string.Join(';', original.Sources.Select(s => s.Value)));
                    await insert.ExecuteNonQueryAsync(token);
                },
                CancellationToken.None);
            Assert.True(written.IsSuccess);
        }

        EntitlementDecision readBack = default!;
        var read = await unitOfWork.ExecuteAsync(
            TransactionIntent.ReadOnly,
            async token =>
            {
                var current = ambient.Current!;
                await using var select = current.Connection.CreateCommand();
                select.Transaction = current.Transaction;
                select.CommandText = $"SELECT feature, tenant, granted, decided_at, sources FROM {table} "
                    + "WHERE work_item_id = @workItemId;";
                AddParameter(select, "@workItemId", "work-item-1");

                await using var reader = await select.ExecuteReaderAsync(token);
                Assert.True(await reader.ReadAsync(token));

                Assert.True(TenantId.TryParse(reader.GetString(1), out var tenant));
                Assert.True(capability.TryParseInstant(reader.GetString(3), out var decidedAt));

                readBack = new EntitlementDecision(
                    new FeatureName(reader.GetString(0)),
                    tenant,
                    reader.GetInt64(2) != 0,
                    decidedAt,
                    reader.GetString(4)
                        .Split(';', StringSplitOptions.RemoveEmptyEntries)
                        .Select(value => new EntitlementContributorName(value))
                        .ToList());
            },
            CancellationToken.None);
        Assert.True(read.IsSuccess);

        Assert.Equal(original.Feature, readBack.Feature);
        Assert.Equal(original.Tenant, readBack.Tenant);
        Assert.Equal(original.Granted, readBack.Granted);
        Assert.Equal(original.DecidedAt, readBack.DecidedAt);
        Assert.Equal(
            original.Sources.Select(s => s.Value).Order(),
            readBack.Sources.Select(s => s.Value).Order());
    }

    [Fact]
    public async Task S7_6_With_only_the_Community_baseline_a_named_feature_is_granted_and_an_unnamed_one_is_not()
    {
        var named = new FeatureName("Test.Baseline.Named");
        var unnamed = new FeatureName("Test.Baseline.Unnamed");

        await using var host = await BuildHostAsync(services =>
            services.AddSingleton(new CommunityBaselineOptions(new HashSet<FeatureName> { named })));

        var evaluator = host.Services.GetRequiredService<IEntitlementEvaluator>();
        var scopeFactory = host.Services.GetRequiredService<IOperationScopeFactory>();

        using (scopeFactory.Begin(TestTenant, FakePrincipals.System))
        {
            var grantedDecision = await evaluator.EvaluateAsync(named, CancellationToken.None);
            var deniedDecision = await evaluator.EvaluateAsync(unnamed, CancellationToken.None);

            Assert.True(grantedDecision.Granted);
            Assert.Contains(
                grantedDecision.Sources, source => source.Value == "Platform.Entitlement.CommunityBaseline");

            Assert.False(deniedDecision.Granted);
            Assert.Empty(deniedDecision.Sources);
        }
    }

    [Fact]
    public async Task S7_7_No_caller_can_resolve_a_contributor_from_the_container()
    {
        await using var host = await BuildHostAsync();

        Assert.Null(host.Services.GetService(typeof(IEntitlementContributor)));
        Assert.Empty(host.Services.GetServices<IEntitlementContributor>());
        Assert.NotNull(host.Services.GetService(typeof(IEntitlementEvaluator)));
    }

    [Fact]
    public void S7_8_Refusing_an_operation_names_no_contributor_to_the_caller()
    {
        var error = EntitlementError.FeatureNotEntitled(TestFeature);

        Assert.False(error.IsRetryable);
        Assert.DoesNotContain("granter", error.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Community", error.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(TestFeature.Value, error.Detail, StringComparison.Ordinal);
    }

    private static async Task<IPlatformTestHost> BuildHostAsync(Action<IServiceCollection>? configure = null) =>
        await PlatformTestHost.CreateBuilder().WithServices(configure ?? (_ => { })).StartAsync(CancellationToken.None);

    private static async Task<EntitlementDecision> EvaluateAsync(IPlatformTestHost host, FeatureName? feature = null)
    {
        var evaluator = host.Services.GetRequiredService<IEntitlementEvaluator>();
        var scopeFactory = host.Services.GetRequiredService<IOperationScopeFactory>();

        using (scopeFactory.Begin(TestTenant, FakePrincipals.System))
        {
            return await evaluator.EvaluateAsync(feature ?? TestFeature, CancellationToken.None);
        }
    }

    private static StubEntitlementContributor GrantingContributor(string name) =>
        new(name, (_, _) => Result<bool, EntitlementError>.Success(true));

    private static StubEntitlementContributor DecliningContributor(string name) =>
        new(name, (_, _) => Result<bool, EntitlementError>.Success(false));

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
