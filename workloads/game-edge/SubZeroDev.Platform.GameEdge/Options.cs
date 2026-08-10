namespace SubZeroDev.Platform.GameEdge;

/// <summary>The edge's own configuration. Bound from the <c>GameEdge</c> configuration section —
/// <see cref="WorkloadBaseAddress"/> has no default, since a bad guess would forward every request
/// to a workload that was never configured.</summary>
public sealed record GameEdgeOptions
{
    /// <summary>Where the workload listens. Never dereferenced except by the forwarder and the
    /// readiness probe.</summary>
    public required Uri WorkloadBaseAddress { get; init; }

    /// <summary>How long a forwarded request may run before the edge answers <c>504</c> on the
    /// workload's behalf.</summary>
    public required TimeSpan ForwardTimeout { get; init; }

    /// <summary>How long the readiness check's own probe of the workload's liveness endpoint may
    /// run before it is treated as unreachable.</summary>
    public required TimeSpan LivenessTimeout { get; init; }
}
