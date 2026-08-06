using System.Net;
using Microsoft.Extensions.DependencyInjection;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Testing;

namespace SubZeroDev.Platform.Tests;

/// <summary>S8.9: with telemetry in place — the mandatory local logging, the OTel SDK wiring, the
/// deterministic sampler, the redaction boundary, all configured by
/// <c>AddPlatformObservability</c> alone — the sample still satisfies the brief's first CI
/// assertion whole: health, readiness, correlation and telemetry all working through the standard
/// registration call, with no second registration call and no extra configuration beyond what
/// S1–S7 already required.</summary>
[Collection(TelemetryTestCollection.Name)]
public sealed class TelemetryEndToEndTests
{
    [Fact]
    public async Task Health_readiness_correlation_and_telemetry_all_work_through_the_standard_call_alone()
    {
        var logDirectory = Path.Combine(Path.GetTempPath(), $"platform-tel-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(logDirectory);

        try
        {
            await using var host = await PlatformTestHost.CreateBuilder()
                .WithSetting("Telemetry:LogDirectory", logDirectory)
                .StartAsync(CancellationToken.None);

            var liveness = await host.ProbeAsync(HealthCheckKind.Liveness, CancellationToken.None);
            var readiness = await host.ProbeAsync(HealthCheckKind.Readiness, CancellationToken.None);
            Assert.Equal(HealthStatus.Healthy, liveness.Aggregate);
            Assert.NotEqual(HealthStatus.Unhealthy, readiness.Aggregate);

            // Correlation: ITraceContextCodec and the operation-scope machinery both resolve —
            // AddPlatformObservability's registration is what makes the codec available at all.
            var codec = host.Services.GetRequiredService<ITraceContextCodec>();
            using var trace = codec.StartRoot("e2e-probe");
            Assert.Equal(32, trace.Context.TraceId.Length);

            // Telemetry: the mandatory local sink wrote something during startup with no bespoke
            // wiring beyond WithSetting for the log directory.
            var wrote = false;
            for (var attempt = 0; attempt < 150 && !wrote; attempt++)
            {
                wrote = Directory.Exists(logDirectory) && Directory.GetFiles(logDirectory, "*.jsonl").Length > 0;
                if (!wrote)
                {
                    await Task.Delay(100);
                }
            }

            Assert.True(wrote, "the standard registration call should have produced a local log file with no extra wiring");
        }
        finally
        {
            TryDeleteDirectory(logDirectory);
        }
    }

    [Fact]
    public async Task The_web_hosts_health_and_readiness_endpoints_answer_over_real_HTTP()
    {
        var (app, client) = await WebHostUnderTest.StartAsync();
        try
        {
            var live = await client.GetAsync("/health/live", CancellationToken.None);
            var ready = await client.GetAsync("/health/ready", CancellationToken.None);

            Assert.Equal(HttpStatusCode.OK, live.StatusCode);
            Assert.True(ready.StatusCode is HttpStatusCode.OK or HttpStatusCode.ServiceUnavailable);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
