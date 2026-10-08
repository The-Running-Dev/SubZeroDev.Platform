using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Testing;

namespace SubZeroDev.Platform.Tests;

/// <summary>S8.1: with no OTLP endpoint configured, the local sinks alone carry every log — no
/// exporter starts. S8.5: a blocked (or failing) sink never makes application work wait — proved
/// with a gate around a real local collector and, separately, around an unwritable file sink.</summary>
[Collection(TelemetryTestCollection.Name)]
public sealed class TelemetryExportTests
{
    [Fact]
    public async Task With_no_OtlpEndpoint_configured_the_host_starts_and_stops_promptly_and_writes_the_local_file()
    {
        var logDirectory = Path.Combine(Path.GetTempPath(), $"platform-tel-nootlp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(logDirectory);

        try
        {
            var stopwatch = Stopwatch.StartNew();

            await using (var host = await PlatformTestHost.CreateBuilder()
                .WithSetting("Telemetry:LogDirectory", logDirectory)
                .StartAsync(CancellationToken.None))
            {
                // Bounded well under any plausible connect-timeout to an unreachable collector — if
                // AddPlatformObservability had wired an exporter against a non-existent endpoint,
                // start/stop would not both land inside this window.
                Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15), "startup took long enough to suggest an attempted outbound connection");

                var logger = host.Services.GetRequiredService<ILogger<TelemetryExportTests>>();
                logger.LogInformation("no-otlp probe line");
            }

            stopwatch.Stop();
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15), "shutdown took long enough to suggest an attempted outbound connection");

            var content = await ReadEventuallyAsync(logDirectory, "no-otlp probe line");
            Assert.Contains("no-otlp probe line", content, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(logDirectory);
        }
    }

    [Fact]
    public async Task A_blocked_OTLP_collector_never_makes_a_request_wait_and_export_resumes_after_release()
    {
        var gate = new GatedCollector();
        using var listener = gate.Start();

        var logDirectory = Path.Combine(Path.GetTempPath(), $"platform-tel-gated-{Guid.NewGuid():N}");
        Directory.CreateDirectory(logDirectory);

        try
        {
            var (app, client) = await WebHostUnderTest.StartAsync(settings: new Dictionary<string, string?>
            {
                ["Platform:Telemetry:LogDirectory"] = logDirectory,
                ["Platform:Telemetry:OtlpEndpoint"] = gate.Endpoint.ToString(),
            });

            try
            {
                // Occupies the batch path: enough spans to guarantee at least one batch export
                // attempt reaches the (currently gated) collector once the exporter's scheduled
                // delay elapses.
                //
                // Every request carries a sampled `traceparent`, and that is what makes "guarantee"
                // true rather than likely. An unparented root falls through PlatformSampler to a 10%
                // TraceIdRatioBasedSampler, so twenty bare requests produce no sampled span at all on
                // 0.9^20 — about one run in eight — and the exporter then has nothing to send, no
                // request reaches the gate, and this test times out for a reason that has nothing to
                // do with what it asserts. ParentBasedSampler honours a recorded parent, so a sampled
                // traceparent makes the span set deterministic. Traces are the only signal that can
                // arrive inside the window: metrics export on a 60-second reader, and Serilog is
                // installed with `writeToProviders: false`, so the OTLP log pipeline sees nothing.
                for (var i = 0; i < 20; i++)
                {
                    (await client.SendAsync(SampledRequest(), CancellationToken.None)).EnsureSuccessStatusCode();
                }

                await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(20));

                var stopwatch = Stopwatch.StartNew();
                var response = await client.SendAsync(SampledRequest(), CancellationToken.None);
                stopwatch.Stop();

                Assert.True(response.IsSuccessStatusCode);
                Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), "the request waited on the blocked collector");

                gate.Release();

                var received = await gate.Completed.Task.WaitAsync(TimeSpan.FromSeconds(20));
                Assert.True(received);
            }
            finally
            {
                gate.Release();
                await app.DisposeAsync();
            }
        }
        finally
        {
            TryDeleteDirectory(logDirectory);
        }
    }

    [Fact]
    public async Task An_unwritable_file_sink_never_makes_application_work_wait()
    {
        // A path whose parent does not exist and cannot be created (a file standing where a
        // directory is expected) — the file sink's own retry loop keeps failing on every flush
        // without ever making a caller wait for it.
        var blockingFile = Path.Combine(Path.GetTempPath(), $"platform-tel-blocker-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(blockingFile, "not a directory");
        var logDirectory = Path.Combine(blockingFile, "logs");

        try
        {
            var stopwatch = Stopwatch.StartNew();

            await using var host = await PlatformTestHost.CreateBuilder()
                .WithSetting("Telemetry:LogDirectory", logDirectory)
                .StartAsync(CancellationToken.None);

            var logger = host.Services.GetRequiredService<ILogger<TelemetryExportTests>>();
            for (var i = 0; i < 100; i++)
            {
                logger.LogInformation("unwritable-sink probe {Index}", i);
            }

            stopwatch.Stop();
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), "logging against an unwritable sink blocked the caller");
        }
        finally
        {
            File.Delete(blockingFile);
        }
    }

    /// <summary>S29.2 (I-OB1) — with no OTLP endpoint configured, the started host's built service
    /// provider holds no exporter instance on any signal. Observed by walking the providers the
    /// container built, not inferred from the absence of traffic; the walk is calibrated by the next
    /// test, which finds one exporter per signal on the same path once an endpoint is set.</summary>
    [Fact]
    public async Task S29_2_With_no_OtlpEndpoint_the_started_host_holds_no_exporter_instance()
    {
        await using var host = await PlatformTestHost.CreateBuilder().StartAsync(CancellationToken.None);

        Assert.Empty(ExportersIn(host.Services));
    }

    /// <summary>S29.2's calibration: the same walk over a host given an endpoint finds the trace,
    /// metric and log exporters. Without this, a walk that could not see an exporter at all would
    /// pass the test above for the wrong reason.</summary>
    [Fact]
    public async Task S29_2_With_an_OtlpEndpoint_the_same_walk_finds_one_exporter_per_signal()
    {
        var collector = new GatedCollector();
        collector.Release();
        using var listener = collector.Start();

        await using var host = await PlatformTestHost.CreateBuilder()
            .WithSetting("Telemetry:OtlpEndpoint", collector.Endpoint.ToString())
            .StartAsync(CancellationToken.None);

        Assert.Equal(
            ["OtlpLogExporter", "OtlpMetricExporter", "OtlpTraceExporter"],
            ExportersIn(host.Services).Select(exporter => exporter.GetType().Name).Order(StringComparer.Ordinal));
    }

    /// <summary>Every <see cref="BaseExporter{T}"/> reachable from the tracer, meter and logger
    /// providers the container holds. OpenTelemetry keeps its processors, readers and exporters in
    /// non-public fields, so the walk reads fields: it follows only OpenTelemetry's own types and
    /// the arrays and collections they hold, which keeps it inside the telemetry graph.</summary>
    private static List<object> ExportersIn(IServiceProvider services)
    {
        var roots = new List<object?>
        {
            services.GetService<TracerProvider>(),
            services.GetService<MeterProvider>(),
        };
        roots.AddRange(services.GetServices<ILoggerProvider>());
        var loggerProvider = typeof(TracerProvider).Assembly.GetType("OpenTelemetry.Logs.LoggerProvider")
            ?? typeof(BaseExporter<>).Assembly.GetType("OpenTelemetry.Logs.LoggerProvider");
        if (loggerProvider is not null)
        {
            roots.Add(services.GetService(loggerProvider));
        }

        Assert.NotNull(roots[0]);
        Assert.NotNull(roots[1]);

        var exporters = new List<object>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<object>(roots.OfType<object>());

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!seen.Add(current))
            {
                continue;
            }

            var type = current.GetType();
            if (IsExporter(type))
            {
                exporters.Add(current);
            }

            if (current is System.Collections.IEnumerable items and not string
                && (type.IsArray || type.Namespace?.StartsWith("System.Collections", StringComparison.Ordinal) == true))
            {
                foreach (var item in items)
                {
                    if (item is not null && !item.GetType().IsValueType)
                    {
                        pending.Push(item);
                    }
                }

                continue;
            }

            if (type.Assembly.GetName().Name?.StartsWith("OpenTelemetry", StringComparison.Ordinal) != true)
            {
                continue;
            }

            for (var declaring = type; declaring is not null && declaring != typeof(object); declaring = declaring.BaseType)
            {
                foreach (var field in declaring.GetFields(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (!field.FieldType.IsValueType && field.GetValue(current) is { } value)
                    {
                        pending.Push(value);
                    }
                }
            }
        }

        return exporters;

        static bool IsExporter(Type type)
        {
            for (var candidate = type; candidate is not null; candidate = candidate.BaseType)
            {
                if (candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(BaseExporter<>))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>A GET carrying a well-formed, <em>sampled</em> W3C <c>traceparent</c> — flags <c>01</c>.
    /// Each call mints a fresh trace-id so the requests stay distinct traces rather than siblings.</summary>
    private static HttpRequestMessage SampledRequest()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.TryAddWithoutValidation(
            "traceparent",
            $"00-{ActivityTraceId.CreateRandom()}-{ActivitySpanId.CreateRandom()}-01");
        return request;
    }

    private static async Task<string> ReadEventuallyAsync(string logDirectory, string mustContain)
    {
        for (var attempt = 0; attempt < 150; attempt++)
        {
            foreach (var file in Directory.GetFiles(logDirectory, "*.jsonl"))
            {
                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);
                    var text = await reader.ReadToEndAsync();
                    if (text.Contains(mustContain, StringComparison.Ordinal))
                    {
                        return text;
                    }
                }
                catch (IOException)
                {
                }
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("The expected log line never appeared on disk.");
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

/// <summary>A real loopback HTTP collector a test gates by hand — the sanctioned way to exercise
/// S8.5's blocked-export behaviour without a public testing hook on
/// <c>AddPlatformObservability</c>: the OTLP exporter is given a genuine reachable endpoint, and this
/// listener simply does not answer until <see cref="Release"/> is called.</summary>
internal sealed class GatedCollector
{
    private readonly HttpListener _listener = new();
    private readonly TaskCompletionSource<bool> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal TaskCompletionSource<bool> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Uri Endpoint { get; private set; } = null!;

    internal IDisposable Start()
    {
        var port = FreePort();
        Endpoint = new Uri($"http://localhost:{port}/");
        _listener.Prefixes.Add(Endpoint.ToString());
        _listener.Start();

        _ = AcceptLoopAsync();

        return new Stopper(_listener);
    }

    internal void Release() => _release.TrySetResult(true);

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (_listener.IsListening)
            {
                var context = await _listener.GetContextAsync().ConfigureAwait(false);
                Started.TrySetResult();

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _release.Task.ConfigureAwait(false);
                        context.Response.StatusCode = 200;
                        context.Response.Close();
                        Completed.TrySetResult(true);
                    }
                    catch
                    {
                        // The listener was disposed while a response was in flight — nothing to do.
                    }
                });
            }
        }
        catch (Exception) when (!_listener.IsListening)
        {
            // Disposed while awaiting the next context — expected at shutdown.
        }
    }

    private static int FreePort()
    {
        var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        return port;
    }

    private sealed class Stopper(HttpListener listener) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                listener.Stop();
                listener.Close();
            }
            catch
            {
            }
        }
    }
}
