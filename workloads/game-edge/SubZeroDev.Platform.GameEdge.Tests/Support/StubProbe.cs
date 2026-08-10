using SubZeroDev.Platform.Abstractions;

namespace SubZeroDev.Platform.GameEdge.Tests.Support;

/// <summary>A scriptable <see cref="IGameWorkloadProbe"/>, for tests that only need
/// <see cref="GameWorkloadReadinessCheck"/>'s own reaction to a probe result rather than a real
/// outbound call.</summary>
internal sealed class StubProbe(Result<EdgeError> result) : IGameWorkloadProbe
{
    public Task<Result<EdgeError>> ProbeLivenessAsync(CancellationToken cancellationToken) =>
        Task.FromResult(result);
}
