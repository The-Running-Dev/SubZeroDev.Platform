using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;

namespace SubZeroDev.Platform.Persistence;

/// <summary>Whether the configured store is reachable. Self-guards: an unreachable database
/// degrades readiness rather than throwing or going unhealthy — a database thirty seconds behind
/// the application should not need a human on a self-hosted box.</summary>
internal sealed class DatabaseHealthCheck(IProviderCapability capability) : IHealthCheck
{
    public HealthCheckName Name => PlatformHealthChecks.Database;

    public HealthCheckKind Kind => HealthCheckKind.Readiness;

    public HealthCheckCriticality Criticality => HealthCheckCriticality.Required;

    public TimeSpan Timeout { get; } = TimeSpan.FromSeconds(5);

    public bool TouchesExternalDependency => true;

    public async Task<HealthCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        var begun = await capability.BeginAsync(TransactionIntent.ReadOnly, cancellationToken).ConfigureAwait(false);

        if (!begun.IsSuccess)
        {
            return new HealthCheckResult(HealthStatus.Degraded, begun.Error.Detail, new Dictionary<string, string>());
        }

        var opened = begun.Value;

        try
        {
            await opened.Transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Read-only probe: nothing was written, so a failed rollback changes nothing.
        }
        finally
        {
            await opened.Connection.DisposeAsync().ConfigureAwait(false);
        }

        return new HealthCheckResult(HealthStatus.Healthy, null, new Dictionary<string, string>());
    }
}

/// <summary>Whether this host's registered migrations match the applied ones. The comparison is
/// symmetric: pending and surplus both degrade the same check, and an absent schema reports as
/// entirely pending rather than throwing.</summary>
internal sealed class PendingMigrationsHealthCheck(IMigrationRunner runner) : IHealthCheck
{
    public HealthCheckName Name => PlatformHealthChecks.PendingMigrations;

    public HealthCheckKind Kind => HealthCheckKind.Readiness;

    public HealthCheckCriticality Criticality => HealthCheckCriticality.Required;

    public TimeSpan Timeout { get; } = TimeSpan.FromSeconds(10);

    public bool TouchesExternalDependency => true;

    public async Task<HealthCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        var status = await runner.GetStatusAsync(cancellationToken).ConfigureAwait(false);

        if (!status.IsSuccess)
        {
            return new HealthCheckResult(HealthStatus.Degraded, status.Error.Detail, new Dictionary<string, string>());
        }

        var pending = status.Value.Where(module => module.Pending.Count > 0).ToList();
        var surplus = status.Value.Where(module => module.Surplus.Count > 0).ToList();

        if (pending.Count == 0 && surplus.Count == 0)
        {
            return new HealthCheckResult(HealthStatus.Healthy, null, new Dictionary<string, string>());
        }

        var data = new Dictionary<string, string>();
        foreach (var module in pending)
        {
            data[$"pending:{module.Module}"] = string.Join(",", module.Pending);
        }

        foreach (var module in surplus)
        {
            data[$"surplus:{module.Module}"] = string.Join(",", module.Surplus);
        }

        var detail = (pending.Count > 0, surplus.Count > 0) switch
        {
            (true, true) => "Pending and surplus migrations present.",
            (true, false) => "Pending migrations present.",
            (false, true) => "Surplus migrations present — applied but no longer registered.",
            _ => null,
        };

        return new HealthCheckResult(HealthStatus.Degraded, detail, data);
    }
}

/// <summary>How long the oldest pending row has been past due. Measures time past due, never time
/// since occurred — a deferred row, a row backing off, or a bulk-redriven row with an old
/// <c>occurred_at</c> are all routine states a working dispatcher keeps young by acting on them.</summary>
internal sealed class OutboxBacklogAgeHealthCheck(IOutboxStore store, PlatformOptions options, IClock clock) : IHealthCheck
{
    public HealthCheckName Name => PlatformHealthChecks.OutboxBacklogAge;

    public HealthCheckKind Kind => HealthCheckKind.Readiness;

    public HealthCheckCriticality Criticality => HealthCheckCriticality.Required;

    public TimeSpan Timeout { get; } = TimeSpan.FromSeconds(5);

    public bool TouchesExternalDependency => true;

    public async Task<HealthCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        var oldest = await store.OldestPendingDueAsync(cancellationToken).ConfigureAwait(false);
        if (!oldest.IsSuccess)
        {
            return new HealthCheckResult(HealthStatus.Degraded, oldest.Error.Detail, new Dictionary<string, string>());
        }

        if (oldest.Value is not { } due)
        {
            return new HealthCheckResult(HealthStatus.Healthy, null, new Dictionary<string, string>());
        }

        var age = clock.UtcNow - due;
        return age > options.Health.BacklogAgeThreshold
            ? new HealthCheckResult(
                HealthStatus.Degraded,
                $"The oldest pending row is {age} past due.",
                new Dictionary<string, string> { ["age"] = age.ToString() })
            : new HealthCheckResult(HealthStatus.Healthy, null, new Dictionary<string, string>());
    }
}

/// <summary>How many rows are pending. Pending rows are unbounded by decision — no retention window
/// applies to them — so this count is the bound that exists.</summary>
internal sealed class OutboxPendingCountHealthCheck(IOutboxStore store, PlatformOptions options) : IHealthCheck
{
    public HealthCheckName Name => PlatformHealthChecks.OutboxPendingCount;

    public HealthCheckKind Kind => HealthCheckKind.Readiness;

    public HealthCheckCriticality Criticality => HealthCheckCriticality.Required;

    public TimeSpan Timeout { get; } = TimeSpan.FromSeconds(5);

    public bool TouchesExternalDependency => true;

    public async Task<HealthCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        var count = await store.PendingCountAsync(cancellationToken).ConfigureAwait(false);
        if (!count.IsSuccess)
        {
            return new HealthCheckResult(HealthStatus.Degraded, count.Error.Detail, new Dictionary<string, string>());
        }

        return count.Value > options.Health.PendingCountThreshold
            ? new HealthCheckResult(
                HealthStatus.Degraded,
                $"{count.Value} pending rows exceed the threshold of {options.Health.PendingCountThreshold}.",
                new Dictionary<string, string> { ["count"] = count.Value.ToString() })
            : new HealthCheckResult(HealthStatus.Healthy, null, new Dictionary<string, string>());
    }
}

/// <summary>How many rows are poisoned. A discarded row is excluded — the operator disposition it
/// was waiting for has already been made — and any poisoned count degrades readiness without ever
/// reporting unhealthy: one poisoned message never fails the wire status.</summary>
internal sealed class OutboxPoisonCountHealthCheck(IOutboxStore store) : IHealthCheck
{
    public HealthCheckName Name => PlatformHealthChecks.OutboxPoisonCount;

    public HealthCheckKind Kind => HealthCheckKind.Readiness;

    public HealthCheckCriticality Criticality => HealthCheckCriticality.Required;

    public TimeSpan Timeout { get; } = TimeSpan.FromSeconds(5);

    public bool TouchesExternalDependency => true;

    public async Task<HealthCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        var count = await store.PoisonedCountAsync(cancellationToken).ConfigureAwait(false);
        if (!count.IsSuccess)
        {
            return new HealthCheckResult(HealthStatus.Degraded, count.Error.Detail, new Dictionary<string, string>());
        }

        return count.Value > 0
            ? new HealthCheckResult(
                HealthStatus.Degraded,
                $"{count.Value} poisoned rows present.",
                new Dictionary<string, string> { ["count"] = count.Value.ToString() })
            : new HealthCheckResult(HealthStatus.Healthy, null, new Dictionary<string, string>());
    }
}
