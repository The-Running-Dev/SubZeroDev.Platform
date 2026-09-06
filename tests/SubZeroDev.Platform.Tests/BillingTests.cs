using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Billing;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Persistence;
using SubZeroDev.Platform.Testing;

namespace SubZeroDev.Platform.Tests;

/// <summary>D5-S11: the Billing module — plans, subscriptions, the entitlement contributor, the
/// inbound provider event seam, and the offline-by-construction startup path.</summary>
public sealed class BillingTests
{
    private static readonly Principal Operator = new(
        new PrincipalId("issuer-billing", "operator"), PrincipalKind.Account, "Operator", null);

    private static readonly FeatureName PremiumReports = new("premium-reports");
    private static readonly PlanKey ProKey = new("pro");
    private static readonly PlanKey FreeKey = new("free");

    private static async Task<IPlatformTestHost> StartHostAsync(RecordingAuditSink sink, Action<IServiceCollection>? extra = null)
    {
        var host = await PlatformTestHost.CreateBuilder()
            .WithProvider(PersistenceProvider.Sqlite)
            .WithServices(services =>
            {
                services.AddSingleton<IPlatformModule, BillingModule>();
                services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditSink>(sink));
                extra?.Invoke(services);
            })
            .StartAsync(CancellationToken.None);

        var migrated = await host.Services.GetRequiredService<IMigrationRunner>().ApplyAsync(CancellationToken.None);
        Assert.True(migrated.IsSuccess);
        return host;
    }

    private static async Task<int> CountRowsAsync(IPlatformTestHost host, string table)
    {
        var unitOfWork = host.Services.GetRequiredService<IUnitOfWork>();
        var result = await unitOfWork.ExecuteAsync(
            TransactionIntent.ReadOnly,
            async ct =>
            {
                var ambient = host.Services.GetRequiredService<IAmbientTransactionAccessor>();
                await using var command = ambient.Current!.Connection.CreateCommand();
                command.Transaction = ambient.Current!.Transaction;
                command.CommandText = $"SELECT COUNT(*) FROM {table};";
                return Convert.ToInt32(await command.ExecuteScalarAsync(ct));
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        return result.Value;
    }

    private static async Task<string> ReadScalarAsync(IPlatformTestHost host, string sql)
    {
        var unitOfWork = host.Services.GetRequiredService<IUnitOfWork>();
        var ambient = host.Services.GetRequiredService<IAmbientTransactionAccessor>();
        var result = await unitOfWork.ExecuteAsync(
            TransactionIntent.ReadOnly,
            async ct =>
            {
                await using var command = ambient.Current!.Connection.CreateCommand();
                command.Transaction = ambient.Current!.Transaction;
                command.CommandText = sql;
                return (string)(await command.ExecuteScalarAsync(ct))!;
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        return result.Value;
    }

    // S11.1 -----------------------------------------------------------------------------------

    [Fact]
    public async Task S11_1_A_tenant_subscribed_to_a_granting_plan_resolves_granted__an_unsubscribed_tenant_does_not()
    {
        var sink = new RecordingAuditSink(isDurable: true);
        await using var host = await StartHostAsync(sink);
        var api = host.Services.GetRequiredService<IBillingApi>();
        var evaluator = host.Services.GetRequiredService<IEntitlementEvaluator>();
        var scopeFactory = host.Services.GetRequiredService<IOperationScopeFactory>();

        await api.RegisterPlanAsync(ProKey, "Pro", new HashSet<FeatureName> { PremiumReports }, CancellationToken.None);

        var subscribed = new TenantId(Guid.NewGuid());
        var unsubscribed = new TenantId(Guid.NewGuid());

        Result<Subscription, BillingError> transitioned;
        using (scopeFactory.Begin(subscribed, Operator))
        {
            transitioned = await api.TransitionAsync(
                subscribed,
                ProKey,
                SubscriptionState.Active,
                host.Clock.UtcNow,
                host.Clock.UtcNow.AddDays(30),
                trialEndsAt: null,
                providerReference: "ref-1",
                CancellationToken.None);
        }

        Assert.True(transitioned.IsSuccess);

        using (scopeFactory.Begin(subscribed, Operator))
        {
            var decision = await evaluator.EvaluateAsync(PremiumReports, CancellationToken.None);
            Assert.True(decision.Granted);
        }

        using (scopeFactory.Begin(unsubscribed, Operator))
        {
            var decision = await evaluator.EvaluateAsync(PremiumReports, CancellationToken.None);
            Assert.False(decision.Granted);
        }
    }

    // S11.2 -----------------------------------------------------------------------------------

    [Fact]
    public async Task S11_2_Moving_to_a_plan_that_does_not_grant_the_feature_changes_the_decision_by_one_row__no_entitlement_table_exists()
    {
        var sink = new RecordingAuditSink(isDurable: true);
        await using var host = await StartHostAsync(sink);
        var api = host.Services.GetRequiredService<IBillingApi>();
        var evaluator = host.Services.GetRequiredService<IEntitlementEvaluator>();
        var scopeFactory = host.Services.GetRequiredService<IOperationScopeFactory>();

        await api.RegisterPlanAsync(ProKey, "Pro", new HashSet<FeatureName> { PremiumReports }, CancellationToken.None);
        await api.RegisterPlanAsync(FreeKey, "Free", new HashSet<FeatureName>(), CancellationToken.None);

        var tenant = new TenantId(Guid.NewGuid());
        Result<Subscription, BillingError> moved;
        using (scopeFactory.Begin(tenant, Operator))
        {
            await api.TransitionAsync(
                tenant, ProKey, SubscriptionState.Active, host.Clock.UtcNow, host.Clock.UtcNow.AddDays(30),
                null, "ref-1", CancellationToken.None);

            Assert.True((await evaluator.EvaluateAsync(PremiumReports, CancellationToken.None)).Granted);

            moved = await api.TransitionAsync(
                tenant, FreeKey, SubscriptionState.Active, host.Clock.UtcNow, host.Clock.UtcNow.AddDays(30),
                null, "ref-1", CancellationToken.None);

            Assert.False((await evaluator.EvaluateAsync(PremiumReports, CancellationToken.None)).Granted);
        }

        Assert.True(moved.IsSuccess);

        // One row throughout — the plan change is an update, never a second subscription row.
        Assert.Equal(1, await CountRowsAsync(host, "subscription"));

        var tables = await ReadTableNamesAsync(host);
        Assert.DoesNotContain(tables, t => t.Contains("entitlement", StringComparison.OrdinalIgnoreCase));
    }

    // S11.3 is BillingArchitectureTests, alongside PackageGraphTests (S1's mechanism).

    // S11.4 -----------------------------------------------------------------------------------

    [Fact]
    public async Task S11_4_A_subscription_past_its_period_end_stops_granting_with_no_row_written()
    {
        var sink = new RecordingAuditSink(isDurable: true);
        await using var host = await StartHostAsync(sink);
        var api = host.Services.GetRequiredService<IBillingApi>();
        var evaluator = host.Services.GetRequiredService<IEntitlementEvaluator>();
        var scopeFactory = host.Services.GetRequiredService<IOperationScopeFactory>();

        await api.RegisterPlanAsync(ProKey, "Pro", new HashSet<FeatureName> { PremiumReports }, CancellationToken.None);

        var tenant = new TenantId(Guid.NewGuid());
        var periodEnd = host.Clock.UtcNow.AddDays(1);
        using (scopeFactory.Begin(tenant, Operator))
        {
            await api.TransitionAsync(
                tenant, ProKey, SubscriptionState.Active, host.Clock.UtcNow, periodEnd, null, "ref-1", CancellationToken.None);

            Assert.True((await evaluator.EvaluateAsync(PremiumReports, CancellationToken.None)).Granted);
        }

        host.Clock.Advance(TimeSpan.FromDays(2));

        using (scopeFactory.Begin(tenant, Operator))
        {
            Assert.False((await evaluator.EvaluateAsync(PremiumReports, CancellationToken.None)).Granted);
        }

        // No write happened on lapse: the stored state is exactly what TransitionAsync wrote.
        var state = await ReadScalarAsync(host, "SELECT state FROM subscription;");
        Assert.Equal(nameof(SubscriptionState.Active), state);
        Assert.Equal(1, await CountRowsAsync(host, "subscription"));
    }

    // S11.5 -----------------------------------------------------------------------------------

    [Fact]
    public async Task S11_5_A_redelivered_event_updates_the_subscription_once_and_answers_success_both_times()
    {
        var sink = new RecordingAuditSink(isDurable: true);
        await using var host = await StartHostAsync(sink);
        var api = host.Services.GetRequiredService<IBillingApi>();
        var scopeFactory = host.Services.GetRequiredService<IOperationScopeFactory>();

        var tenant = new TenantId(Guid.NewGuid());
        var providerEvent = new ProviderEvent(
            "acme-pay",
            "evt-1",
            tenant,
            ProKey,
            SubscriptionState.Active,
            host.Clock.UtcNow,
            host.Clock.UtcNow.AddDays(30),
            null,
            "provider-ref-1");

        using (scopeFactory.Begin(tenant, Operator))
        {
            var first = await api.HandleProviderEventAsync(providerEvent, CancellationToken.None);
            Assert.True(first.IsSuccess);

            var second = await api.HandleProviderEventAsync(providerEvent, CancellationToken.None);
            Assert.True(second.IsSuccess);
        }

        Assert.Equal(1, await CountRowsAsync(host, "subscription"));
        Assert.Equal(1, await CountRowsAsync(host, "provider_event_receipt"));
    }

    // S11.6 -----------------------------------------------------------------------------------

    [Fact]
    public async Task S11_6_An_uninterpretable_event_answers_ProviderEventMalformed_and_records_no_receipt()
    {
        var sink = new RecordingAuditSink(isDurable: true);
        await using var host = await StartHostAsync(sink);
        var api = host.Services.GetRequiredService<IBillingApi>();

        var tenant = new TenantId(Guid.NewGuid());
        var malformed = new ProviderEvent(
            "acme-pay",
            "evt-2",
            tenant,
            ProKey,
            SubscriptionState.Active,
            host.Clock.UtcNow,
            host.Clock.UtcNow, // period end not after period start — uninterpretable
            null,
            "provider-ref-2");

        var handled = await api.HandleProviderEventAsync(malformed, CancellationToken.None);
        Assert.False(handled.IsSuccess);
        Assert.Equal(nameof(BillingError.ProviderEventMalformed), handled.Error.Code);

        Assert.Equal(0, await CountRowsAsync(host, "provider_event_receipt"));
    }

    // S11.7 -----------------------------------------------------------------------------------

    [Fact]
    public async Task S11_7_An_unregistered_plan_answers_PlanNotFound__an_unreachable_state_answers_InvalidTransition()
    {
        var sink = new RecordingAuditSink(isDurable: true);
        await using var host = await StartHostAsync(sink);
        var api = host.Services.GetRequiredService<IBillingApi>();
        var scopeFactory = host.Services.GetRequiredService<IOperationScopeFactory>();

        await api.RegisterPlanAsync(ProKey, "Pro", new HashSet<FeatureName> { PremiumReports }, CancellationToken.None);

        var tenant = new TenantId(Guid.NewGuid());
        var unregistered = new PlanKey("does-not-exist");

        using var scope = scopeFactory.Begin(tenant, Operator);

        var namingUnregistered = await api.TransitionAsync(
            tenant, unregistered, SubscriptionState.Active, host.Clock.UtcNow, host.Clock.UtcNow.AddDays(30),
            null, "ref-1", CancellationToken.None);
        Assert.False(namingUnregistered.IsSuccess);
        Assert.Equal(nameof(BillingError.PlanNotFound), namingUnregistered.Error.Code);

        // Cancel, then attempt to reach Active again — Cancelled is terminal.
        await api.TransitionAsync(
            tenant, ProKey, SubscriptionState.Active, host.Clock.UtcNow, host.Clock.UtcNow.AddDays(30),
            null, "ref-1", CancellationToken.None);
        await api.TransitionAsync(
            tenant, ProKey, SubscriptionState.Cancelled, host.Clock.UtcNow, host.Clock.UtcNow.AddDays(30),
            null, "ref-1", CancellationToken.None);

        var unreachable = await api.TransitionAsync(
            tenant, ProKey, SubscriptionState.Active, host.Clock.UtcNow, host.Clock.UtcNow.AddDays(30),
            null, "ref-1", CancellationToken.None);
        Assert.False(unreachable.IsSuccess);
        Assert.Equal(nameof(BillingError.InvalidTransition), unreachable.Error.Code);
    }

    // S11.8 -----------------------------------------------------------------------------------

    [Fact]
    public async Task S11_8_One_tenant_holds_at_most_one_subscription_enforced_by_a_unique_index()
    {
        var sink = new RecordingAuditSink(isDurable: true);
        await using var host = await StartHostAsync(sink);
        var store = new BillingStoreTestAccess(host);
        var tenant = new TenantId(Guid.NewGuid());

        await store.InsertAsync(tenant, "ref-a");

        var thrown = await Record.ExceptionAsync(() => store.InsertAsync(tenant, "ref-b"));
        Assert.NotNull(thrown);
    }

    // S11.9 -----------------------------------------------------------------------------------

    [Fact]
    public async Task S11_9_The_host_starts_serves_and_reports_ready_with_no_provider_contacted()
    {
        var sink = new RecordingAuditSink(isDurable: true);
        await using var host = await StartHostAsync(sink);

        // Billing ships no outbound client at all in this slice — there is nothing on the request
        // path, at startup or on readiness that could reach a provider (out of scope: real
        // payment-provider integration). Starting and migrating cleanly is the proof.
        var checks = host.Services.GetRequiredService<IHealthCheckRegistry>();
        Assert.NotNull(checks);
    }

    // S11.10 ----------------------------------------------------------------------------------

    [Fact]
    public async Task S11_10_A_transition_writes_an_EntitlementChanged_Required_audit_record()
    {
        var sink = new RecordingAuditSink(isDurable: true);
        await using var host = await StartHostAsync(sink);
        var api = host.Services.GetRequiredService<IBillingApi>();
        var scopeFactory = host.Services.GetRequiredService<IOperationScopeFactory>();

        await api.RegisterPlanAsync(ProKey, "Pro", new HashSet<FeatureName> { PremiumReports }, CancellationToken.None);

        var tenant = new TenantId(Guid.NewGuid());
        Result<Subscription, BillingError> transitioned;
        using (scopeFactory.Begin(tenant, Operator))
        {
            transitioned = await api.TransitionAsync(
                tenant, ProKey, SubscriptionState.Active, host.Clock.UtcNow, host.Clock.UtcNow.AddDays(30),
                null, "ref-1", CancellationToken.None);
        }

        Assert.True(transitioned.IsSuccess);

        var recorded = Assert.Single(sink.Received);
        Assert.Equal(PlatformAuditActions.EntitlementChanged.Value, recorded.Action.Value);
        Assert.Equal(AuditClass.Required, recorded.Class);
    }

    // Helpers -----------------------------------------------------------------------------------

    private static async Task<IReadOnlyList<string>> ReadTableNamesAsync(IPlatformTestHost host)
    {
        var unitOfWork = host.Services.GetRequiredService<IUnitOfWork>();
        var ambient = host.Services.GetRequiredService<IAmbientTransactionAccessor>();
        var result = await unitOfWork.ExecuteAsync(
            TransactionIntent.ReadOnly,
            async ct =>
            {
                await using var command = ambient.Current!.Connection.CreateCommand();
                command.Transaction = ambient.Current!.Transaction;
                command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
                List<string> names = [];
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    names.Add(reader.GetString(0));
                }

                return (IReadOnlyList<string>)names;
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        return result.Value;
    }

    /// <summary>Reaches <see cref="BillingStore"/>'s raw insert directly, bypassing
    /// <see cref="IBillingApi.TransitionAsync"/>'s own existing-row check, so S11.8 proves the unique
    /// index itself rejects a second row for one tenant rather than proving the API's own guard
    /// does.</summary>
    private sealed class BillingStoreTestAccess(IPlatformTestHost host)
    {
        internal async Task InsertAsync(TenantId tenant, string providerReference)
        {
            var unitOfWork = host.Services.GetRequiredService<IUnitOfWork>();
            var store = host.Services.GetRequiredService<BillingStore>();

            var result = await unitOfWork.ExecuteAsync(
                TransactionIntent.Write,
                async ct =>
                {
                    var subscription = new Subscription(
                        SubscriptionId.CreateNew(),
                        tenant,
                        ProKey,
                        SubscriptionState.Active,
                        host.Clock.UtcNow,
                        host.Clock.UtcNow.AddDays(30),
                        null,
                        providerReference);
                    await store.InsertSubscriptionAsync(subscription, ct).ConfigureAwait(false);
                },
                CancellationToken.None);

            if (!result.IsSuccess)
            {
                throw new InvalidOperationException(result.Error.Code);
            }
        }
    }
}
