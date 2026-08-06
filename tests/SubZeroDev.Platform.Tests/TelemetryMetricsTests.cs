using System.Diagnostics.Metrics;

namespace SubZeroDev.Platform.Tests;

/// <summary>S8.8: the standard HTTP-server metric accepts only its documented bounded labels — host
/// role, method, route template, status — never a raw path, query, tenant, correlation, instance,
/// message, event or user identifier. Platform wires no metric of its own beyond the official
/// AspNetCore/Runtime instrumentation (see the comment in
/// <c>PlatformObservabilityExtensions.ConfigureOpenTelemetry</c>), so this asserts the tag set the
/// official <c>http.server.request.duration</c> instrument actually emits, captured with a plain
/// <see cref="MeterListener"/> rather than the OTel SDK — proving the built-in instrument itself
/// carries no forbidden dimension, independent of whether OTLP export is configured.</summary>
[Collection(TelemetryTestCollection.Name)]
public sealed class TelemetryMetricsTests
{
    private static readonly string[] Forbidden =
    [
        "tenant", "correlation", "instance", "message", "event", "user",
        "url.path", "url.query", "http.request.path", "http.route.query",
    ];

    private static readonly string[] Allowed =
    [
        "http.request.method",
        "http.response.status_code",
        "http.route",
        "url.scheme",
        "network.protocol.version",
        "error.type",
        "server.address",
        "server.port",
        "aspnetcore.request.is_unhandled",
    ];

    [Fact]
    public async Task The_built_in_HTTP_server_duration_instrument_only_carries_documented_bounded_tags()
    {
        var observedTagKeys = new HashSet<string>(StringComparer.Ordinal);
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Microsoft.AspNetCore.Hosting"
                && instrument.Name == "http.server.request.duration")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, state) =>
        {
            foreach (var tag in tags)
            {
                observedTagKeys.Add(tag.Key);
            }
        });
        listener.Start();

        var (app, client) = await WebHostUnderTest.StartAsync();
        try
        {
            (await client.GetAsync("/?leaked=should-not-appear-in-a-label", CancellationToken.None)).EnsureSuccessStatusCode();

            // The instrument records on request completion; give the listener a moment to see it.
            for (var attempt = 0; attempt < 150 && observedTagKeys.Count == 0; attempt++)
            {
                await Task.Delay(50);
            }
        }
        finally
        {
            await app.DisposeAsync();
        }

        Assert.NotEmpty(observedTagKeys);

        foreach (var key in observedTagKeys)
        {
            Assert.Contains(key, Allowed);
        }

        foreach (var forbidden in Forbidden)
        {
            Assert.DoesNotContain(forbidden, observedTagKeys);
        }
    }
}
