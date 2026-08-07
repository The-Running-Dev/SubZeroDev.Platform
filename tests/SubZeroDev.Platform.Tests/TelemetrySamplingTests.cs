using System.Diagnostics;
using OpenTelemetry.Trace;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Observability;

namespace SubZeroDev.Platform.Tests;

/// <summary>S8.4: <c>PlatformSampler</c> — the one sampler <c>AddPlatformObservability</c> registers
/// on the <c>TracerProviderBuilder</c>. Unit coverage of the sampler's decision logic (isolated from
/// the OTel SDK's own wiring, which is exercised end to end by
/// <see cref="TelemetryDispatchSpanTests"/>), plus one end-to-end proof, using a real
/// <see cref="ActivityListener"/> attached to Platform's own <see cref="ActivitySource"/>, that
/// <c>TraceContextCodec.StartLinked</c> produces a trace id that differs from the origin while its
/// recorded flag matches the origin's stored decision.</summary>
[Collection(TelemetryTestCollection.Name)]
public sealed class TelemetrySamplingTests
{
    [Fact]
    public void An_unparented_activity_with_no_links_is_ratio_sampled_not_always_dropped_or_always_kept()
    {
        var sampler = new PlatformSampler();
        var sampledCount = 0;
        const int trials = 20_000;

        for (var i = 0; i < trials; i++)
        {
            var traceId = ActivityTraceId.CreateRandom();
            var parameters = new SamplingParameters(
                default, traceId, "root", ActivityKind.Server, null, null);

            var result = sampler.ShouldSample(parameters);
            if (result.Decision != SamplingDecision.Drop)
            {
                sampledCount++;
            }
        }

        var rate = sampledCount / (double)trials;

        // Deterministic 10% by trace id: not exact, but nowhere near 0% or 100%.
        Assert.InRange(rate, 0.05, 0.15);
    }

    [Fact]
    public void A_sampled_parent_context_is_always_honoured_regardless_of_the_ratio()
    {
        var sampler = new PlatformSampler();
        var parentTraceId = ActivityTraceId.CreateRandom();
        var parentContext = new ActivityContext(
            parentTraceId, ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded);

        var result = sampler.ShouldSample(new SamplingParameters(
            parentContext, ActivityTraceId.CreateRandom(), "child", ActivityKind.Internal, null, null));

        Assert.Equal(SamplingDecision.RecordAndSample, result.Decision);
    }

    [Fact]
    public void A_single_link_with_no_parent_copies_the_links_recorded_flag_rather_than_ratio_sampling()
    {
        var sampler = new PlatformSampler();
        var origin = new ActivityContext(
            ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded);
        var link = new ActivityLink(origin);

        var result = sampler.ShouldSample(new SamplingParameters(
            default, ActivityTraceId.CreateRandom(), "linked", ActivityKind.Consumer, null, [link]));

        Assert.Equal(SamplingDecision.RecordAndSample, result.Decision);
    }

    [Fact]
    public void A_single_link_to_an_unsampled_origin_drops_rather_than_ratio_sampling()
    {
        var sampler = new PlatformSampler();
        var origin = new ActivityContext(
            ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.None);
        var link = new ActivityLink(origin);

        var result = sampler.ShouldSample(new SamplingParameters(
            default, ActivityTraceId.CreateRandom(), "linked", ActivityKind.Consumer, null, [link]));

        Assert.Equal(SamplingDecision.Drop, result.Decision);
    }

    [Fact]
    public void StartLinked_produces_a_different_trace_id_with_the_origins_sampled_flag_copied()
    {
        // Subscribes only Platform's own ActivitySource, with the same sampler
        // AddPlatformObservability configures on the OTel SDK — proving the codec and the sampler
        // agree without standing up the full OTel pipeline.
        var sampler = new PlatformSampler();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == PlatformTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
            {
                var parameters = new SamplingParameters(
                    options.Parent, options.TraceId, options.Name, options.Kind, options.Tags, options.Links);
                var decision = sampler.ShouldSample(parameters);
                return decision.Decision == SamplingDecision.Drop
                    ? ActivitySamplingResult.PropagationData
                    : ActivitySamplingResult.AllDataAndRecorded;
            },
        };
        ActivitySource.AddActivityListener(listener);

        var codec = new TraceContextCodec();

        const string OriginTraceId = "4bf92f3577b34da6a3ce929d0e0e4736";
        var origin = new TraceContext($"00-{OriginTraceId}-00f067aa0ba902b7-01", null);

        using var handle = codec.StartLinked(origin, "platform.outbox.dispatch");

        Assert.NotEqual(OriginTraceId, handle.Context.TraceId);
        Assert.True(handle.Context.Sampled);
    }

    [Fact]
    public void StartLinked_from_an_unsampled_origin_stays_unsampled()
    {
        var sampler = new PlatformSampler();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == PlatformTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
            {
                var parameters = new SamplingParameters(
                    options.Parent, options.TraceId, options.Name, options.Kind, options.Tags, options.Links);
                var decision = sampler.ShouldSample(parameters);
                return decision.Decision == SamplingDecision.Drop
                    ? ActivitySamplingResult.PropagationData
                    : ActivitySamplingResult.AllDataAndRecorded;
            },
        };
        ActivitySource.AddActivityListener(listener);

        var codec = new TraceContextCodec();

        const string OriginTraceId = "4bf92f3577b34da6a3ce929d0e0e4736";
        var origin = new TraceContext($"00-{OriginTraceId}-00f067aa0ba902b7-00", null);

        using var handle = codec.StartLinked(origin, "platform.outbox.dispatch");

        Assert.NotEqual(OriginTraceId, handle.Context.TraceId);
        Assert.False(handle.Context.Sampled);
    }
}
