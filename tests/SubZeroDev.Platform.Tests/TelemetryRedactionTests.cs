using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog.Events;
using Serilog.Parsing;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Observability;

namespace SubZeroDev.Platform.Tests;

/// <summary>S8.7: the fixed redaction boundary. Unit coverage of the key-segment matcher and the
/// inline scrubber, plus one end-to-end proof that a real log line routed through
/// <c>AddPlatformObservability</c>'s pipeline comes out redacted on disk.</summary>
[Collection(TelemetryTestCollection.Name)]
public sealed class TelemetryRedactionTests
{
    [Theory]
    [InlineData("Authorization")]
    [InlineData("authorization")]
    [InlineData("Cookie")]
    [InlineData("Password")]
    [InlineData("Secret")]
    [InlineData("Token")]
    [InlineData("Api-Key")]
    [InlineData("ApiKey")]
    [InlineData("Persistence:ConnectionString")]
    [InlineData("ConnectionString")]
    [InlineData("ClientCertificate")]
    [InlineData("client-certificate")]
    public void Every_contract_key_segment_is_recognised_case_insensitively(string key)
    {
        Assert.True(Redaction.IsSensitiveKey(key));
    }

    [Theory]
    [InlineData("service.name")]
    [InlineData("correlation")]
    [InlineData("tenant")]
    [InlineData("db.system")]
    [InlineData("operation")]
    public void An_ordinary_key_is_not_flagged(string key)
    {
        Assert.False(Redaction.IsSensitiveKey(key));
    }

    [Fact]
    public void Inline_key_value_text_is_scrubbed_without_disturbing_the_rest_of_the_message()
    {
        var text = "connection failed: Password=hunter2;Host=db;Authorization: Bearer abc.def.ghi";

        var scrubbed = Redaction.RedactInline(text);

        Assert.DoesNotContain("hunter2", scrubbed, StringComparison.Ordinal);
        Assert.DoesNotContain("abc.def.ghi", scrubbed, StringComparison.Ordinal);
        Assert.Contains("Host=db", scrubbed, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", scrubbed, StringComparison.Ordinal);
    }

    [Fact]
    public void A_structured_property_matching_a_marker_is_redacted_in_the_formatted_JSON_line()
    {
        var template = new MessageTemplateParser().Parse("Connecting with {ConnectionString}");
        var properties = new[]
        {
            new LogEventProperty("ConnectionString", new ScalarValue("Host=db;Password=hunter2")),
        };
        var logEvent = new LogEvent(
            DateTimeOffset.UtcNow, LogEventLevel.Information, null, template, properties);

        var writer = new StringWriter();
        new RedactingJsonFormatter().Format(logEvent, writer);
        var formatted = writer.ToString();

        Assert.DoesNotContain("hunter2", formatted, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_sensitive_property_logged_through_the_real_pipeline_is_redacted_on_disk()
    {
        var logDirectory = Path.Combine(Path.GetTempPath(), $"platform-redaction-{Guid.NewGuid():N}");
        Directory.CreateDirectory(logDirectory);

        try
        {
            var (app, _) = await WebHostUnderTest.StartAsync(settings: new Dictionary<string, string?>
            {
                ["Platform:Telemetry:LogDirectory"] = logDirectory,
            });

            try
            {
                var logger = app.Services.GetRequiredService<ILogger<TelemetryRedactionTests>>();
                logger.LogInformation(
                    "Connecting with {ConnectionString} and {Password}",
                    "Host=db;Password=super-secret-value",
                    "another-secret-value");

                var content = await ReadWithRetryAsync(logDirectory);

                Assert.DoesNotContain("super-secret-value", content, StringComparison.Ordinal);
                Assert.DoesNotContain("another-secret-value", content, StringComparison.Ordinal);
                Assert.Contains("[REDACTED]", content, StringComparison.Ordinal);
            }
            finally
            {
                await app.DisposeAsync();
            }
        }
        finally
        {
            TryDeleteDirectory(logDirectory);
        }
    }

    /// <summary>The shared async sink flushes on its own schedule, not synchronously with the log
    /// call — this polls briefly rather than asserting immediately after logging.</summary>
    private static async Task<string> ReadWithRetryAsync(string logDirectory)
    {
        for (var attempt = 0; attempt < 150; attempt++)
        {
            var files = Directory.Exists(logDirectory) ? Directory.GetFiles(logDirectory, "*.jsonl") : [];
            foreach (var file in files)
            {
                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);
                    var text = await reader.ReadToEndAsync();
                    if (text.Contains("ConnectionString", StringComparison.Ordinal))
                    {
                        return text;
                    }
                }
                catch (IOException)
                {
                    // Still being written; retry.
                }
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("The redacted log line never appeared on disk.");
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup: a lingering file handle leaves an orphaned temp directory, not a
            // test failure.
        }
    }
}
