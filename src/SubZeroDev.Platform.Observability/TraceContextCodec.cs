using System.Diagnostics;
using SubZeroDev.Platform.Abstractions;

namespace SubZeroDev.Platform.Observability;

/// <summary>The W3C operations, implemented here because propagation is Observability's and
/// declared in Abstractions because two packages with no edge to Observability perform them.</summary>
internal sealed class TraceContextCodec : ITraceContextCodec
{
    private static readonly ActivitySource Source = new(PlatformTelemetry.ActivitySourceName);

    public bool TryParse(string traceParent, string? traceState, out TraceContext result) =>
        TraceContext.TryParse(traceParent, traceState, out result);

    public ITraceHandle StartRoot(string activityName)
    {
        // Adopting whichever Activity is already ambient (the default behaviour of an
        // ActivitySource.StartActivity overload with no explicit parent) is what makes an inbound
        // request with no upstream traceparent share its trace id with ASP.NET Core's own server
        // span — S8.3's "one request, one server span" claim depends on that. But "the scope that
        // calls this is the origin" (see IOperationScopeFactory.Begin's own doc) promises a fresh,
        // independent trace, not implicit nesting under whatever the ambient Activity happens to be
        // — so when that ambient Activity already came from this same source (another still-open
        // Platform origin scope, not an externally-started one such as ASP.NET Core's), this call
        // detaches from the ambient Activity for the duration of the start. A default
        // ActivityContext parent is not "no parent" to StartActivity — it is treated as invalid and
        // Activity.Current is substituted right back in, which is why passing parentContext: default
        // alone did not stop the nesting. Clearing Activity.Current itself is the only way to force
        // a genuinely new root rather than silently becoming that scope's child.
        Activity? activity;
        if (Activity.Current?.Source.Name == PlatformTelemetry.ActivitySourceName)
        {
            var ambient = Activity.Current;
            Activity.Current = null;
            try
            {
                activity = Source.StartActivity(activityName, ActivityKind.Internal);
            }
            finally
            {
                Activity.Current = ambient;
            }
        }
        else
        {
            activity = Source.StartActivity(activityName, ActivityKind.Internal);
        }

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

    public TraceContext CurrentHop(TraceContext origin)
    {
        var activity = Activity.Current;

        // "Shares origin's trace id" rather than "exists" — an unrelated ambient activity (a
        // different request, a background scope that outlived its request) must not be mistaken
        // for this hop's own span.
        return activity is not null && string.Equals(activity.TraceId.ToHexString(), origin.TraceId, StringComparison.OrdinalIgnoreCase)
            ? new TraceContext($"00-{origin.TraceId}-{activity.SpanId}-{Flags(activity.Recorded)}", origin.TraceState)
            : origin;
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
