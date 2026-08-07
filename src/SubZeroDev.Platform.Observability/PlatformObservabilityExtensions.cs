using System.Reflection;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Debugging;
using Serilog.Events;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;

namespace SubZeroDev.Platform.Observability;

/// <summary>Observability's registration call.</summary>
public static class PlatformObservabilityExtensions
{
    /// <summary>Wires telemetry and trace-context propagation. Called by both forms of the standard
    /// registration call, and exposed separately for a consumer that wants telemetry without a
    /// Platform host.</summary>
    /// <param name="builder">The host builder.</param>
    /// <returns>The same builder, so calls chain.</returns>
    public static IHostApplicationBuilder AddPlatformObservability(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton<ITraceContextCodec, TraceContextCodec>();

        var identity = ResolveIdentity(builder);

        ConfigureLogging(builder, identity);
        ConfigureOpenTelemetry(builder, identity);

        return builder;
    }

    private static void ConfigureLogging(IHostApplicationBuilder builder, TelemetryIdentity identity)
    {
        // Bypasses Serilog: Serilog itself may be the thing failing, and the diagnostic must not
        // depend on the pipeline it is reporting on.
        SelfLog.Enable(message => Console.Error.WriteLine($"[platform-telemetry] serilog: {message}"));

        var dropMonitor = new SerilogDropMonitor();
        builder.Services.TryAddSingleton(dropMonitor);

        var fileName = $"{SanitiseForFileName(identity.ServiceName)}-{identity.HostRole}-.jsonl";
        var filePath = Path.Combine(identity.Telemetry.LogDirectory, fileName);

        builder.Services.AddSerilog(
            (services, loggerConfiguration) =>
            {
                var accessor = services.GetService<IOperationScopeAccessor>();

                loggerConfiguration
                    .MinimumLevel.Information()
                    .Enrich.WithProperty("service.name", identity.ServiceName)
                    .Enrich.WithProperty("service.version", identity.ServiceVersion)
                    .Enrich.WithProperty("deployment.environment.name", identity.Environment)
                    .Enrich.WithProperty("subzerodev.host.role", identity.HostRole)
                    .Enrich.With(new AmbientScopeEnricher(accessor))
                    .WriteTo.Async(
                        sink =>
                        {
                            sink.Console(new RedactingJsonFormatter());
                            sink.File(
                                new RedactingJsonFormatter(),
                                filePath,
                                restrictedToMinimumLevel: LevelAlias.Minimum,
                                fileSizeLimitBytes: 100L * 1024 * 1024,
                                levelSwitch: null,
                                buffered: false,
                                shared: true,
                                flushToDiskInterval: null,
                                rollingInterval: RollingInterval.Day,
                                rollOnFileSizeLimit: true,
                                retainedFileCountLimit: 31,
                                encoding: Encoding.UTF8,
                                hooks: null,
                                retainedFileTimeLimit: TimeSpan.FromDays(14));
                        },
                        bufferSize: 10_000,
                        blockWhenFull: false,
                        monitor: dropMonitor);
            },
            writeToProviders: false);
    }

    private static void ConfigureOpenTelemetry(IHostApplicationBuilder builder, TelemetryIdentity identity)
    {
        var resourceBuilder = ResourceBuilder.CreateDefault()
            .AddService(identity.ServiceName, serviceVersion: identity.ServiceVersion, autoGenerateServiceInstanceId: false)
            .AddAttributes(new KeyValuePair<string, object>[]
            {
                new("deployment.environment.name", identity.Environment),
                new("subzerodev.host.role", identity.HostRole),
            });

        var endpoint = identity.Telemetry.OtlpEndpoint;

        builder.Services.AddOpenTelemetry()
            .WithTracing(tracing =>
            {
                // HttpClient instrumentation is deliberately not wired here: enabling it activates
                // .NET's built-in System.Net.Http.DiagnosticsHandler process-wide, which then injects
                // its own fresh traceparent on every outbound HttpClient call in the process —
                // overwriting one Platform's own callers set deliberately (dispatch, propagation) and
                // not merely adding to it. The package stays referenced, matching the pinned
                // dependency list, for a consumer that wants it and accepts that trade-off.
                tracing
                    .SetResourceBuilder(resourceBuilder)
                    .SetSampler(new PlatformSampler())
                    .AddSource(PlatformTelemetry.ActivitySourceName)
                    .AddAspNetCoreInstrumentation()
                    .AddProcessor(new RedactingActivityProcessor());

                if (endpoint is not null)
                {
                    tracing.AddOtlpExporter(otlp => ConfigureOtlp(otlp, endpoint, "v1/traces"));
                }
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .SetResourceBuilder(resourceBuilder)
                    .AddMeter(PlatformTelemetry.MeterName)
                    .AddAspNetCoreInstrumentation()
                    .AddRuntimeInstrumentation();

                if (endpoint is not null)
                {
                    metrics.AddOtlpExporter(otlp => ConfigureOtlp(otlp, endpoint, "v1/metrics"));
                }
            });

        if (endpoint is not null)
        {
            builder.Logging.AddOpenTelemetry(logging =>
            {
                logging.SetResourceBuilder(resourceBuilder);
                logging.AddProcessor(new RedactingLogRecordProcessor());
                logging.AddOtlpExporter(otlp => ConfigureOtlp(otlp, endpoint, "v1/logs"));
            });
        }
    }

    private static void ConfigureOtlp(OtlpExporterOptions options, Uri endpoint, string signalPath)
    {
        options.Protocol = OtlpExportProtocol.HttpProtobuf;
        options.Endpoint = new Uri(endpoint, signalPath);
    }

    /// <summary>Reads the identity a Platform host already bound and validated (registered as a
    /// singleton instance ahead of this call — see <c>PlatformHostExtensions.AddPlatformHost</c>),
    /// or, for a consumer that wants telemetry without a Platform host, derives the same fields
    /// directly rather than requiring the rest of <see cref="PlatformOptions"/> (persistence and
    /// outbox settings a bare telemetry consumer has no reason to supply).</summary>
    private static TelemetryIdentity ResolveIdentity(IHostApplicationBuilder builder)
    {
        var descriptor = builder.Services.FirstOrDefault(service => service.ServiceType == typeof(PlatformOptions));
        if (descriptor?.ImplementationInstance is PlatformOptions options)
        {
            return new TelemetryIdentity(
                options.ServiceName ?? EntryAssemblyName(),
                options.ServiceVersion ?? EntryAssemblyVersion(),
                options.Environment,
                options.Role.ToString().ToLowerInvariant(),
                options.Telemetry);
        }

        var section = builder.Configuration.GetSection("Platform");
        var telemetrySection = section.GetSection("Telemetry");

        return new TelemetryIdentity(
            section["ServiceName"] ?? EntryAssemblyName(),
            section["ServiceVersion"] ?? EntryAssemblyVersion(),
            builder.Environment.EnvironmentName,
            "unknown",
            new TelemetryOptions
            {
                LogDirectory = string.IsNullOrWhiteSpace(telemetrySection["LogDirectory"])
                    ? "logs"
                    : telemetrySection["LogDirectory"]!,
                OtlpEndpoint = ParseOptionalHttpUri(telemetrySection["OtlpEndpoint"]),
            });
    }

    private static Uri? ParseOptionalHttpUri(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return Uri.TryCreate(raw, UriKind.Absolute, out var parsed)
            && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps)
            ? parsed
            : null;
    }

    private static string EntryAssemblyName() => Assembly.GetEntryAssembly()?.GetName().Name ?? "unknown";

    private static string EntryAssemblyVersion() =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
        ?? "0.0.0";

    private static string SanitiseForFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var buffer = value.ToCharArray();
        for (var index = 0; index < buffer.Length; index++)
        {
            if (Array.IndexOf(invalid, buffer[index]) >= 0)
            {
                buffer[index] = '_';
            }
        }

        return new string(buffer);
    }
}

/// <summary>The identity fields every OTLP resource and every JSONL log record carries, plus the
/// telemetry settings that decide where they go. Resolved once, from whichever source
/// <see cref="PlatformObservabilityExtensions.AddPlatformObservability"/> found.</summary>
internal sealed record TelemetryIdentity(
    string ServiceName,
    string ServiceVersion,
    string Environment,
    string HostRole,
    TelemetryOptions Telemetry);
