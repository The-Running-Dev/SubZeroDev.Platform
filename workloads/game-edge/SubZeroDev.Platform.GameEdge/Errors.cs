using SubZeroDev.Platform.Abstractions;

namespace SubZeroDev.Platform.GameEdge;

/// <summary>The edge's own two errors. Neither is retryable: there is no idempotency key, and a
/// retry against a <c>submitAction</c> whose outcome is unknown would be a second action
/// (`20-contract.md`, *The edge — <c>EdgeError</c>*). The codes are this slice's resolution of
/// Unresolved 2's edge half — see `design/g1/90-decisions.md`.</summary>
public abstract record EdgeError(string Code) : PlatformError(Code)
{
    /// <inheritdoc/>
    public override bool IsRetryable => false;

    /// <summary>The forward could not connect, or the readiness probe could not reach the
    /// workload's readiness endpoint. <c>503</c>.</summary>
    public static EdgeError WorkloadUnreachable() => new WorkloadUnreachableEdgeError();

    /// <summary>The forward exceeded <see cref="GameEdgeOptions.ForwardTimeout"/>. <c>504</c>.</summary>
    public static EdgeError WorkloadTimeout() => new WorkloadTimeoutEdgeError();
}

/// <summary>The forward could not connect, or the readiness probe could not reach the workload.</summary>
public sealed record WorkloadUnreachableEdgeError() : EdgeError("workload_unreachable");

/// <summary>The forward exceeded <see cref="GameEdgeOptions.ForwardTimeout"/>.</summary>
public sealed record WorkloadTimeoutEdgeError() : EdgeError("workload_timeout");
