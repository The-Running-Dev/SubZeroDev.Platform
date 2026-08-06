using OpenTelemetry.Trace;

namespace SubZeroDev.Platform.Observability;

/// <summary>The one sampler for every Platform trace. Two rules, chosen in order:
/// <list type="number">
/// <item>A linked, parent-less activity with exactly one link — the shape
/// <c>TraceContextCodec.StartLinked</c> always produces — copies that link's own recorded flag
/// rather than being freshly sampled. <c>StartLinked</c> deliberately mints a new trace-id (a
/// backlog can drain days after the originating request ended, and continuing the original trace
/// would give it unbounded duration), but the row it drains from already carries its own
/// accept/reject decision, and that decision is stored, not re-rolled.</item>
/// <item>Everything else — a genuine new root, including every fresh HTTP request with no upstream
/// <c>traceparent</c> — falls through to <see cref="ParentBasedSampler"/> over a 10%
/// <see cref="TraceIdRatioBasedSampler"/>, so a sampled upstream parent is always honoured and an
/// unparented root is sampled deterministically by its own trace-id.</item>
/// </list>
/// </summary>
internal sealed class PlatformSampler : Sampler
{
    private readonly Sampler _rootSampler = new ParentBasedSampler(new TraceIdRatioBasedSampler(0.1));

    public override SamplingResult ShouldSample(in SamplingParameters samplingParameters)
    {
        if (samplingParameters.ParentContext == default && TryOnlyLink(samplingParameters, out var link))
        {
            var decision = (link.Context.TraceFlags & System.Diagnostics.ActivityTraceFlags.Recorded) != 0
                ? SamplingDecision.RecordAndSample
                : SamplingDecision.Drop;

            return new SamplingResult(decision);
        }

        return _rootSampler.ShouldSample(samplingParameters);
    }

    private static bool TryOnlyLink(in SamplingParameters samplingParameters, out System.Diagnostics.ActivityLink link)
    {
        link = default;

        if (samplingParameters.Links is not { } links)
        {
            return false;
        }

        using var enumerator = links.GetEnumerator();
        if (!enumerator.MoveNext())
        {
            return false;
        }

        link = enumerator.Current;
        return !enumerator.MoveNext();
    }
}
