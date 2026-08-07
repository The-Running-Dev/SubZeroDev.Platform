using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
                for (var i = 0; i < 20; i++)
                {
                    (await client.GetAsync("/", CancellationToken.None)).EnsureSuccessStatusCode();
                }

                await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(20));

                var stopwatch = Stopwatch.StartNew();
                var response = await client.GetAsync("/", CancellationToken.None);
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
