using System.Diagnostics;
using SubZeroDev.Platform.Abstractions;

namespace SubZeroDev.Platform.Observability;

/// <summary>The W3C operations, implemented here because propagation is Observability's and
/// declared in Abstractions because two packages with no edge to Observability perform them.</summary>
internal sealed class TraceContextCodec : ITraceContextCodec
{
    /// <summary>Platform's own activity source. Named here so an exporter can subscribe to it.</summary>
    internal const string ActivitySourceName = "SubZeroDev.Platform";

    private static readonly ActivitySource Source = new(ActivitySourceName);

    public bool TryParse(string traceParent, string? traceState, out TraceContext result) =>
        TraceContext.TryParse(traceParent, traceState, out result);

    public ITraceHandle StartRoot(string activityName)
    {
        var activity = Source.StartActivity(activityName, ActivityKind.Internal);

        // With no listener there is no Activity, and there is still a trace: the context is what
        // the row and the scope are stamped from, so it is minted here either way. Unsampled is the
        // honest flag when nothing is recording.
        return activity is null
            ? new Handle(null, Mint(ActivityTraceId.CreateRandom(), sampled: false), traceState: null)
            : new Handle(activity, FromActivity(activity), activity.TraceStateString);
    }

    public ITraceHandle StartLinked(TraceContext origin, string activityName)
    {
        var links = TryContext(origin, out var originContext)
            ? new[] { new ActivityLink(originContext) }
            : null;

        // A new trace, linked — never a continuation. A backlog can drain days after the
        // originating request ended, and continuing would produce a trace of unbounded duration.
        var activity = Source.StartActivity(
            activityName,
            ActivityKind.Consumer,
            parentContext: default,
            tags: null,
            links: links);

        return activity is null
            ? new Handle(null, Mint(ActivityTraceId.CreateRandom(), origin.Sampled), origin.TraceState)
            : new Handle(activity, FromActivity(activity), activity.TraceStateString ?? origin.TraceState);
    }

    private static bool TryContext(TraceContext origin, out ActivityContext context)
    {
        context = default;

        if (!TraceContext.TryParse(origin.TraceParent, origin.TraceState, out _))
        {
            return false;
        }

        context = new ActivityContext(
            ActivityTraceId.CreateFromString(origin.TraceParent.AsSpan(3, 32)),
            ActivitySpanId.CreateFromString(origin.TraceParent.AsSpan(36, 16)),
            origin.Sampled ? ActivityTraceFlags.Recorded : ActivityTraceFlags.None,
            origin.TraceState,
            isRemote: true);

        return true;
    }

    private static TraceContext FromActivity(Activity activity) =>
        new(
            $"00-{activity.TraceId}-{activity.SpanId}-{Flags(activity.Recorded)}",
            activity.TraceStateString);

    private static TraceContext Mint(ActivityTraceId traceId, bool sampled) =>
        new($"00-{traceId}-{ActivitySpanId.CreateRandom()}-{Flags(sampled)}", null);

    private static string Flags(bool sampled) => sampled ? "01" : "00";

    private sealed class Handle(Activity? activity, TraceContext context, string? traceState) : ITraceHandle
    {
        public TraceContext Context { get; } = traceState is null
            ? context
            : context with { TraceState = traceState };

        public void Dispose() => activity?.Dispose();
    }
}
