using SubZeroDev.Platform.Abstractions;

namespace SubZeroDev.Platform.GameEdge;

/// <summary>Probes the workload's own liveness, and nothing else. A readiness check that played a
/// game would create sessions nobody asked for.</summary>
public interface IGameWorkloadProbe
{
    /// <summary>Probes the workload's liveness endpoint.</summary>
    /// <param name="cancellationToken">Cancelled at the check's own timeout.</param>
    /// <returns>Success when the workload answered healthy; otherwise why it did not.</returns>
    Task<Result<EdgeError>> ProbeLivenessAsync(CancellationToken cancellationToken);
}

/// <inheritdoc cref="IGameWorkloadProbe"/>
internal sealed class GameWorkloadProbe(HttpClient httpClient, GameEdgeOptions options) : IGameWorkloadProbe
{
    private const string LivenessPath = "/livez";

    /// <inheritdoc/>
    public async Task<Result<EdgeError>> ProbeLivenessAsync(CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(options.LivenessTimeout);

        try
        {
            using var response = await httpClient
                .GetAsync(new Uri(options.WorkloadBaseAddress, LivenessPath), budget.Token)
                .ConfigureAwait(false);

            return response.IsSuccessStatusCode
                ? Result<EdgeError>.Success()
                : Result<EdgeError>.Failure(EdgeError.WorkloadUnreachable());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Result<EdgeError>.Failure(EdgeError.WorkloadUnreachable());
        }
        catch (HttpRequestException)
        {
            return Result<EdgeError>.Failure(EdgeError.WorkloadUnreachable());
        }
    }
}

/// <summary>Readiness reports the workload reachable. <see cref="Kind"/> is <c>Readiness</c> and
/// <see cref="TouchesExternalDependency"/> is <see langword="true"/>, which is what makes Platform
/// reject this check if it is ever registered as liveness (`ExternalDependencyInLivenessCheck`) —
/// liveness must never depend on the workload.</summary>
public sealed class GameWorkloadReadinessCheck : IHealthCheck
{
    private readonly IGameWorkloadProbe _probe;
    private readonly GameEdgeOptions _options;

    /// <summary>Creates the check.</summary>
    /// <param name="probe">Probes the workload's liveness.</param>
    /// <param name="options">Supplies the timeout the check runs under.</param>
    public GameWorkloadReadinessCheck(IGameWorkloadProbe probe, GameEdgeOptions options)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(options);
        _probe = probe;
        _options = options;
    }

    /// <inheritdoc/>
    public HealthCheckName Name { get; } = new("game-workload");

    /// <inheritdoc/>
    public HealthCheckKind Kind { get; } = HealthCheckKind.Readiness;

    /// <inheritdoc/>
    public HealthCheckCriticality Criticality { get; } = HealthCheckCriticality.Required;

    /// <inheritdoc/>
    public TimeSpan Timeout => _options.LivenessTimeout;

    /// <inheritdoc/>
    public bool TouchesExternalDependency { get; } = true;

    /// <inheritdoc/>
    public async Task<HealthCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        var probed = await _probe.ProbeLivenessAsync(cancellationToken).ConfigureAwait(false);
        return probed.IsSuccess
            ? new HealthCheckResult(HealthStatus.Healthy, null, new Dictionary<string, string>())
            : new HealthCheckResult(HealthStatus.Unhealthy, probed.Error.Code, new Dictionary<string, string>());
    }
}
