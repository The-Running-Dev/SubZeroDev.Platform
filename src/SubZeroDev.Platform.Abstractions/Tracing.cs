namespace SubZeroDev.Platform.Abstractions;

/// <summary>Observability's W3C operations, declared here because two packages with no edge to
/// Observability perform them.</summary>
public interface ITraceContextCodec
{
    /// <summary>Parses inbound W3C context. Never throws and never fails a request.</summary>
    /// <param name="traceParent">The inbound <c>traceparent</c>.</param>
    /// <param name="traceState">The inbound <c>tracestate</c>, when present.</param>
    /// <param name="result">The parsed context, or <see langword="default"/> when parsing failed.</param>
    /// <returns><see langword="true"/> when the context was well formed.</returns>
    bool TryParse(string traceParent, string? traceState, out TraceContext result);

    /// <summary>Starts a new root trace. This is origination, not fabrication — the caller is the
    /// origin.</summary>
    /// <param name="activityName">The name of the activity to start.</param>
    /// <returns>A handle that ends the trace when disposed.</returns>
    ITraceHandle StartRoot(string activityName);

    /// <summary>Starts a <em>new</em> trace linked to a stored one, honouring the origin's sampling
    /// flags. It never continues the origin's trace.</summary>
    /// <param name="origin">The stored trace context to link to.</param>
    /// <param name="activityName">The name of the activity to start.</param>
    /// <returns>A handle that ends the trace when disposed.</returns>
    ITraceHandle StartLinked(TraceContext origin, string activityName);

    /// <summary>Reports this hop's own span for <paramref name="origin"/>'s trace, when a listener
    /// already started one for the current operation (ASP.NET Core's own request instrumentation,
    /// for a host that carries it) — the ambient <c>System.Diagnostics.Activity</c>, not one this
    /// call starts itself. A caller propagating <paramref name="origin"/> onward should propagate
    /// this instead: it is the same trace, with this hop named as the parent rather than skipped
    /// over. Returns <paramref name="origin"/> unchanged when no such activity shares its trace id —
    /// a host with no listener still gets a coherent trace context to forward.</summary>
    /// <param name="origin">The trace context this hop adopted from its own caller.</param>
    /// <returns>The current hop's own trace context, or <paramref name="origin"/> unchanged.</returns>
    TraceContext CurrentHop(TraceContext origin);
}

/// <summary>A started trace, exposing the context it established so the caller can populate a
/// scope from it.</summary>
public interface ITraceHandle : IDisposable
{
    /// <summary>The trace context this handle established.</summary>
    TraceContext Context { get; }
}
