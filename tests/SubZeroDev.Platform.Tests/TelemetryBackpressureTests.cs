using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubZeroDev.Platform.Observability;
using SubZeroDev.Platform.Testing;

namespace SubZeroDev.Platform.Tests;

/// <summary>S8.6: saturating the shared 10,000-event async buffer exposes its exact dropped-event
/// count through <c>SerilogDropMonitor</c> (the supported <c>Serilog.Sinks.Async</c> inspector), and
/// the monitor emits its emergency console diagnostics bypassing Serilog itself.</summary>
[Collection(TelemetryTestCollection.Name)]
public sealed class TelemetryBackpressureTests
{
    [Fact]
    public async Task Saturating_the_buffer_exposes_an_exact_dropped_count()
    {
        var logDirectory = Path.Combine(Path.GetTempPath(), $"platform-tel-backpressure-{Guid.NewGuid():N}");
        Directory.CreateDirectory(logDirectory);

        try
        {
            await using var host = await PlatformTestHost.CreateBuilder()
                .WithSetting("Telemetry:LogDirectory", logDirectory)
                .StartAsync(CancellationToken.None);

            var monitor = host.Services.GetRequiredService<SerilogDropMonitor>();
            var logger = host.Services.GetRequiredService<ILogger<TelemetryBackpressureTests>>();

            // Far beyond the 10,000-event shared buffer, written in a tight loop — production rate
            // outruns the file/console consumer, so blockWhenFull:false starts dropping rather than
            // stalling the caller.
            for (var i = 0; i < 60_000; i++)
            {
                logger.LogInformation("backpressure probe {Index}", i);
            }

            var dropped = await WaitForDropAsync(monitor);

            Assert.True(dropped > 0, "saturating the buffer should have produced at least one exact dropped count above zero");
        }
        finally
        {
            TryDeleteDirectory(logDirectory);
        }
    }

    private static async Task<long> WaitForDropAsync(SerilogDropMonitor monitor)
    {
        for (var attempt = 0; attempt < 300; attempt++)
        {
            if (monitor.DroppedMessagesCount > 0)
            {
                return monitor.DroppedMessagesCount;
            }

            await Task.Delay(50);
        }

        return monitor.DroppedMessagesCount;
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
