using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SubZeroDev.Platform.Testing;

namespace SubZeroDev.Platform.Tests;

/// <summary>S8.2: service name, service version, deployment environment and host role appear on
/// every JSONL log record (the OTLP-resource half is exercised in
/// <see cref="TelemetryExportTests"/>'s gated collector, which only proves export happens — the
/// identity fields on the resource are the same four constants wired here, read directly off
/// <c>PlatformOptions</c> and <c>TelemetryIdentity</c> rather than re-derived, so this is the
/// authoritative check of their values). Ambient correlation, tenant and culture appear on a log
/// line written inside an operation scope, and are absent on one written outside it.</summary>
[Collection(TelemetryTestCollection.Name)]
public sealed class TelemetryResourceIdentityTests
{
    [Fact]
    public async Task Every_log_record_carries_the_four_identity_fields()
    {
        var logDirectory = Path.Combine(Path.GetTempPath(), $"platform-tel-identity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(logDirectory);

        try
        {
            await using var host = await PlatformTestHost.CreateBuilder()
                .WithSetting("Telemetry:LogDirectory", logDirectory)
                .WithSetting("ServiceName", "identity-probe-service")
                .WithSetting("ServiceVersion", "9.9.9")
                .StartAsync(CancellationToken.None);

            var logger = host.Services.GetRequiredService<ILogger<TelemetryResourceIdentityTests>>();
            logger.LogInformation("identity probe line");

            var line = await ReadJsonLineEventuallyAsync(logDirectory, "identity probe line");

            Assert.Equal("identity-probe-service", line.GetProperty("service.name").GetString());
            Assert.Equal("9.9.9", line.GetProperty("service.version").GetString());
            Assert.Equal("Production", line.GetProperty("deployment.environment.name").GetString());
            Assert.Equal("web", line.GetProperty("subzerodev.host.role").GetString());
        }
        finally
        {
            TryDeleteDirectory(logDirectory);
        }
    }

    [Fact]
    public async Task A_log_line_inside_an_operation_scope_carries_correlation_tenant_and_culture()
    {
        var logDirectory = Path.Combine(Path.GetTempPath(), $"platform-tel-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(logDirectory);

        try
        {
            var (app, client) = await WebHostUnderTest.StartAsync(settings: new Dictionary<string, string?>
            {
                ["Platform:Telemetry:LogDirectory"] = logDirectory,
            });

            // "/" is inside the operation scope for its whole handling — ASP.NET Core's own routing
            // logger ("Executing/Executed endpoint") logs from within it, giving a real log line to
            // assert the ambient fields on without adding a probe endpoint.
            (await client.GetAsync("/", CancellationToken.None)).EnsureSuccessStatusCode();

            var content = await ReadEventuallyAsync(logDirectory, "\"Request starting");
            var scopedLine = content
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(TryParse)
                .Where(element => element.HasValue)
                .Select(element => element!.Value)
                .First(element => element.TryGetProperty("SourceContext", out var context)
                    && context.GetString() == "Microsoft.AspNetCore.Routing.EndpointMiddleware");

            Assert.True(scopedLine.TryGetProperty("correlation", out var correlation));
            Assert.Equal(32, correlation.GetString()!.Length);
            Assert.True(scopedLine.TryGetProperty("tenant", out _));
            Assert.True(scopedLine.TryGetProperty("culture", out _));

            await app.DisposeAsync();
        }
        finally
        {
            TryDeleteDirectory(logDirectory);
        }
    }

    private static JsonElement? TryParse(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<JsonElement>(line);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<JsonElement> ReadJsonLineEventuallyAsync(string logDirectory, string mustContain)
    {
        var content = await ReadEventuallyAsync(logDirectory, mustContain);
        var line = content
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .First(candidate => candidate.Contains(mustContain, StringComparison.Ordinal));

        return JsonSerializer.Deserialize<JsonElement>(line);
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
