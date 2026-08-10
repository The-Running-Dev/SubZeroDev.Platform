using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.GameEdge.Tests.Support;

namespace SubZeroDev.Platform.GameEdge.Tests;

/// <summary>S7.2, S7.5, S7.6 — the forwarder itself, exercised over a real socket (a
/// <see cref="FakeWorkload"/>) so "byte for byte" and "exactly one attempt" are observed rather than
/// assumed.</summary>
public sealed class ForwardingTests
{
    private static readonly TraceContext SampleTrace = Parse("00-0123456789abcdef0123456789abcdef-0123456789abcdef-01");

    private static TraceContext Parse(string traceParent)
    {
        TraceContext.TryParse(traceParent, null, out var result);
        return result;
    }

    [Fact]
    public async Task S7_2_forwards_method_path_query_body_and_traceparent_unaltered_and_returns_the_body_byte_for_byte()
    {
        await using var workload = await FakeWorkload.StartAsync();
        workload.ResponseStatus = 200;
        workload.ResponseBody = "{\"scene\":\"opening\"}"u8.ToArray();
        workload.ResponseContentType = "application/json";

        var forwarder = new GameWorkloadForwarder(
            new HttpClient(),
            new GameEdgeOptions
            {
                WorkloadBaseAddress = new Uri(workload.BaseAddress),
                ForwardTimeout = TimeSpan.FromSeconds(5),
                LivenessTimeout = TimeSpan.FromSeconds(5),
            });

        var requestBody = "{\"choice\":\"north\"}"u8.ToArray();
        var request = new ForwardedRequest(
            HttpMethod.Post,
            "/v1/an-operation-the-edge-has-never-heard-of?x=1",
            requestBody,
            "application/json",
            SampleTrace);

        var result = await forwarder.ForwardAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(200, result.Value.StatusCode);
        Assert.Equal(workload.ResponseBody, result.Value.Body.ToArray());

        var recorded = Assert.Single(workload.Requests);
        Assert.Equal("POST", recorded.Method);
        Assert.Equal("/v1/an-operation-the-edge-has-never-heard-of?x=1", recorded.PathAndQuery);
        Assert.Equal(requestBody, recorded.Body);
        Assert.Equal(SampleTrace.TraceParent, recorded.TraceParent);
    }

    [Fact]
    public async Task S7_5_an_unreachable_workload_yields_workload_unreachable_after_exactly_one_attempt()
    {
        var handler = new CountingHandler((_, _) => throw new HttpRequestException("connection refused"));
        var forwarder = new GameWorkloadForwarder(
            new HttpClient(handler),
            new GameEdgeOptions
            {
                WorkloadBaseAddress = new Uri("http://127.0.0.1:1"),
                ForwardTimeout = TimeSpan.FromSeconds(5),
                LivenessTimeout = TimeSpan.FromSeconds(5),
            });

        var result = await forwarder.ForwardAsync(
            new ForwardedRequest(HttpMethod.Post, "/v1/create-session", ReadOnlyMemory<byte>.Empty, null, SampleTrace),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("workload_unreachable", result.Error.Code);
        Assert.False(result.Error.IsRetryable);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task S7_6_a_workload_that_never_answers_yields_workload_timeout_after_exactly_one_attempt()
    {
        var handler = new CountingHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable: the delay above must be cancelled first");
        });

        var forwarder = new GameWorkloadForwarder(
            new HttpClient(handler),
            new GameEdgeOptions
            {
                WorkloadBaseAddress = new Uri("http://127.0.0.1:1"),
                ForwardTimeout = TimeSpan.FromMilliseconds(200),
                LivenessTimeout = TimeSpan.FromSeconds(5),
            });

        var result = await forwarder.ForwardAsync(
            new ForwardedRequest(HttpMethod.Post, "/v1/create-session", ReadOnlyMemory<byte>.Empty, null, SampleTrace),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("workload_timeout", result.Error.Code);
        Assert.False(result.Error.IsRetryable);
        Assert.Equal(1, handler.CallCount);
    }

    private sealed class CountingHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return await respond(request, cancellationToken);
        }
    }
}
