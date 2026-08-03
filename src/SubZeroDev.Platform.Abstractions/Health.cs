namespace SubZeroDev.Platform.Abstractions;

/// <summary>A check's verdict. Healthy and degraded both mean "take traffic"; only unhealthy does not.</summary>
public enum HealthStatus
{
    /// <summary>Nothing needs attention.</summary>
    Healthy,

    /// <summary>Take traffic, something needs attention.</summary>
    Degraded,

    /// <summary>Do not take traffic.</summary>
    Unhealthy,
}

/// <summary>Which probe a check answers.</summary>
public enum HealthCheckKind
{
    /// <summary>Whether the process should be restarted. Never depends on an external service.</summary>
    Liveness,

    /// <summary>Whether the host should take traffic.</summary>
    Readiness,
}

/// <summary>How a failing check affects the aggregate.</summary>
public enum HealthCheckCriticality
{
    /// <summary>An unhealthy verdict makes the report unhealthy.</summary>
    Required,

    /// <summary>An unhealthy verdict degrades the report, so traffic keeps flowing to a host whose
    /// non-essential provider is down.</summary>
    Optional,
}

/// <summary>How much of a report crosses the wire. A body-narrowing switch, never a status switch.</summary>
public enum HealthReportDetail
{
    /// <summary>The aggregate, and each entry's name, status, duration, detail and data.</summary>
    Full,

    /// <summary>The aggregate, and each entry's name and status.</summary>
    Minimal,
}

/// <summary>A contributed check. Any package can contribute one depending on Abstractions alone.</summary>
public interface IHealthCheck
{
    /// <summary>The check's name, unique within the registry.</summary>
    HealthCheckName Name { get; }

    /// <summary>Which probe this check answers.</summary>
    HealthCheckKind Kind { get; }

    /// <summary>How a failure of this check affects the aggregate.</summary>
    HealthCheckCriticality Criticality { get; }

    /// <summary>How long the check may run before it is treated as unhealthy.</summary>
    TimeSpan Timeout { get; }

    /// <summary>Whether the check reaches an external dependency. Declared rather than inferred, so
    /// registration can reject it as a liveness check.</summary>
    bool TouchesExternalDependency { get; }

    /// <summary>Runs the check.</summary>
    /// <param name="cancellationToken">Cancelled at the check's timeout.</param>
    /// <returns>The check's verdict.</returns>
    Task<HealthCheckResult> CheckAsync(CancellationToken cancellationToken);
}

/// <summary>One check's verdict.</summary>
/// <param name="Status">The verdict.</param>
/// <param name="Detail">Operator-facing detail, never exception text or payload content.</param>
/// <param name="Data">Structured detail, on the same terms.</param>
public sealed record HealthCheckResult(
    HealthStatus Status,
    string? Detail,
    IReadOnlyDictionary<string, string> Data);

/// <summary>A probe's report. Derived per probe and never cached.</summary>
/// <param name="Aggregate">The combined verdict.</param>
/// <param name="Entries">Every registered check of the probed kind, so an absent check reads as
/// absent rather than as a passing one.</param>
public sealed record HealthReport(
    HealthStatus Aggregate,
    IReadOnlyList<HealthReportEntry> Entries);

/// <summary>One check's contribution to a report.</summary>
/// <param name="Name">The check's name.</param>
/// <param name="Status">The check's verdict.</param>
/// <param name="Duration">How long the check took.</param>
/// <param name="Detail">Operator-facing detail, rendered only at full detail.</param>
/// <param name="Data">Structured detail, rendered only at full detail.</param>
public sealed record HealthReportEntry(
    HealthCheckName Name,
    HealthStatus Status,
    TimeSpan Duration,
    string? Detail,
    IReadOnlyDictionary<string, string> Data);
