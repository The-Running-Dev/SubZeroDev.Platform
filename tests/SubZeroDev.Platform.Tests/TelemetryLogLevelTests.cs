using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using SubZeroDev.Platform.Observability;

namespace SubZeroDev.Platform.Tests;

/// <summary>The standard <c>Logging:LogLevel</c> section reaches the Serilog pipeline.
/// <c>AddSerilog</c> replaces the logger factory outright, so <c>Microsoft.Extensions.Logging</c>'s
/// own filter rules never run — before this, a consumer's <c>Logging</c> section was configuration
/// that read as live and did nothing.</summary>
public sealed class TelemetryLogLevelTests
{
    private const string AspNetCoreSource = "Microsoft.AspNetCore.Hosting.Diagnostics";

    [Fact]
    public void With_no_section_the_pipeline_stays_at_Information()
    {
        var captured = Run(new Dictionary<string, string?>());

        Assert.Contains(LogEventLevel.Information, captured);
        Assert.DoesNotContain(LogEventLevel.Debug, captured);
    }

    [Fact]
    public void Default_sets_the_minimum_level()
    {
        var captured = Run(new Dictionary<string, string?>
        {
            ["Logging:LogLevel:Default"] = "Warning",
        });

        Assert.DoesNotContain(LogEventLevel.Information, captured);
        Assert.Contains(LogEventLevel.Warning, captured);
    }

    [Fact]
    public void Default_accepts_a_level_below_Information()
    {
        var captured = Run(new Dictionary<string, string?>
        {
            ["Logging:LogLevel:Default"] = "Trace",
        });

        Assert.Contains(LogEventLevel.Verbose, captured);
        Assert.Contains(LogEventLevel.Debug, captured);
    }

    [Fact]
    public void A_category_key_overrides_that_prefix_and_nothing_else()
    {
        // The reproduced symptom: `Microsoft.AspNetCore: Warning` in appsettings.json, and a log
        // line per request regardless.
        var settings = new Dictionary<string, string?>
        {
            ["Logging:LogLevel:Default"] = "Information",
            ["Logging:LogLevel:Microsoft.AspNetCore"] = "Warning",
        };

        Assert.DoesNotContain(LogEventLevel.Information, Run(settings, AspNetCoreSource));
        Assert.Contains(LogEventLevel.Warning, Run(settings, AspNetCoreSource));
        Assert.Contains(LogEventLevel.Information, Run(settings, "SubZeroDev.Platform.Hosting"));
    }

    [Fact]
    public void None_admits_nothing_at_all()
    {
        var captured = Run(new Dictionary<string, string?>
        {
            ["Logging:LogLevel:Default"] = "None",
        });

        Assert.Empty(captured);
    }

    [Fact]
    public void An_unrecognised_level_name_applies_nothing_rather_than_throwing()
    {
        var captured = Run(new Dictionary<string, string?>
        {
            ["Logging:LogLevel:Default"] = "Chatty",
            ["Logging:LogLevel:Microsoft.AspNetCore"] = "Loud",
        });

        Assert.Contains(LogEventLevel.Information, captured);
        Assert.DoesNotContain(LogEventLevel.Debug, captured);
    }

    /// <summary>Applies <paramref name="settings"/> to a pipeline, writes one event at every level
    /// from <paramref name="source"/>, and reports which ones came through.</summary>
    private static List<LogEventLevel> Run(
        Dictionary<string, string?> settings,
        string source = "SubZeroDev.Platform.Tests")
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var sink = new CapturingSink();
        var loggerConfiguration = new LoggerConfiguration();
        PlatformObservabilityExtensions.ApplyConfiguredLevels(loggerConfiguration, configuration);

        using var logger = loggerConfiguration.WriteTo.Sink(sink).CreateLogger();
        var contextual = logger.ForContext(Constants.SourceContextPropertyName, source);

        foreach (var level in Enum.GetValues<LogEventLevel>())
        {
            contextual.Write(level, "one event at {Level}", level);
        }

        return sink.Levels;
    }

    private sealed class CapturingSink : ILogEventSink
    {
        public List<LogEventLevel> Levels { get; } = [];

        public void Emit(LogEvent logEvent)
        {
            ArgumentNullException.ThrowIfNull(logEvent);
            Levels.Add(logEvent.Level);
        }
    }
}
