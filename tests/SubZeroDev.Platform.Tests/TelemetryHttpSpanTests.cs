using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

namespace SubZeroDev.Platform.Tests;

/// <summary>S8.3 (server-span half): one request produces one server span whose trace id is the
/// correlation. Captured with a plain <see cref="ActivityListener"/> subscribed to every source
/// (the ASP.NET Core server activity comes from <c>Microsoft.AspNetCore.Hosting.HttpRequestIn</c>,
/// not Platform's own source) so the assertion does not depend on the 10% sampler's decision.</summary>
[Collection(TelemetryTestCollection.Name)]
public sealed class TelemetryHttpSpanTests
{
    [Fact]
    public async Task One_request_produces_one_recorded_server_span_whose_trace_id_is_the_correlation()
    {
        using var capture = ActivityCapture.ForSource("Microsoft.AspNetCore");

        var (app, client) = await WebHostUnderTest.StartAsync();
        try
        {
            var response = await client.GetAsync("/", CancellationToken.None);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None);
            var correlation = body.GetProperty("correlation").GetString();

            // Matched by trace id rather than Assert.Single: ActivitySource listeners are
            // process-wide, so a concurrently-running test's own server spans (in a different xUnit
            // collection) can land in the same capture. This request's trace id is exactly the
            // correlation the response reported, which is the assertion that matters.
            var serverSpan = capture.Stopped.First(a => a.Kind == ActivityKind.Server && a.TraceId.ToString() == correlation);

            Assert.True(serverSpan.Recorded);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }
}
