using SubZeroDev.Platform.Abstractions;

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
