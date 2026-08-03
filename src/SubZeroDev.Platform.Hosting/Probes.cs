using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;

namespace SubZeroDev.Platform.Hosting;

/// <summary>Runs the registered checks of one kind and aggregates their verdicts.</summary>
internal sealed class HealthProbe(IHealthCheckRegistry registry)
{
    /// <summary>The probe endpoint's overall budget. Longer than the SQLite busy-wait bound, which
    /// the design sizes as "shorter than a probe timeout".</summary>
    internal static readonly TimeSpan EndpointTimeout = TimeSpan.FromSeconds(15);

    internal async Task<HealthReport> RunAsync(HealthCheckKind kind, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(EndpointTimeout);

        var entries = new List<HealthReportEntry>();
        foreach (var check in registry.Registered.Where(check => check.Kind == kind))
        {
            entries.Add(await RunOneAsync(check, budget.Token).ConfigureAwait(false));
        }

        return new HealthReport(Aggregate(entries, registry, kind), entries);
    }

    private static async Task<HealthReportEntry> RunOneAsync(IHealthCheck check, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(check.Timeout);

            var result = await check.CheckAsync(timeout.Token).ConfigureAwait(false);
            return new HealthReportEntry(
                check.Name,
                result.Status,
                Stopwatch.GetElapsedTime(started),
                result.Detail,
                result.Data);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A hanging check is unhealthy at its timeout, and it never escapes the endpoint.
            return Failed(check, started, "The check did not complete within its timeout.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A throwing check is unhealthy. The message stays out of the body — last error and
            // exception text never cross a wire.
            return Failed(check, started, "The check threw.");
        }
    }

    private static HealthReportEntry Failed(IHealthCheck check, long started, string detail) =>
        new(
            check.Name,
            HealthStatus.Unhealthy,
            Stopwatch.GetElapsedTime(started),
            detail,
            new Dictionary<string, string>());

    private static HealthStatus Aggregate(
        IReadOnlyList<HealthReportEntry> entries,
        IHealthCheckRegistry registry,
        HealthCheckKind kind)
    {
        var criticality = registry.Registered
            .Where(check => check.Kind == kind)
            .ToDictionary(check => check.Name, check => check.Criticality);

        var aggregate = HealthStatus.Healthy;
        foreach (var entry in entries)
        {
            var required = criticality.GetValueOrDefault(entry.Name, HealthCheckCriticality.Required)
                == HealthCheckCriticality.Required;

            var contribution = entry.Status switch
            {
                // An optional check failing degrades rather than drains: traffic keeps flowing to a
                // host whose non-essential provider is down, which is what the flag exists for.
                HealthStatus.Unhealthy when !required => HealthStatus.Degraded,
                var status => status,
            };

            if (contribution > aggregate)
            {
                aggregate = contribution;
            }
        }

        return aggregate;
    }
}

/// <summary>Set by <see cref="PlatformHostExtensions.MapPlatformProbes"/> so the probes are served
/// once — from the host's own route table when it places them, and from Platform's middleware
/// otherwise, which is what makes the standard registration call sufficient on its own.</summary>
internal sealed class ProbeMapping
{
    internal bool MappedExplicitly { get; set; }
}

/// <summary>Serialises a report and an envelope. The two wire shapes D3 ships.</summary>
internal static class ProbeBody
{
    /// <summary>Liveness: whether the process should be restarted.</summary>
    internal const string LivenessPath = "/health/live";

    /// <summary>Readiness: whether the host should take traffic.</summary>
    internal const string ReadinessPath = "/health/ready";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    internal static async Task WriteAsync(HttpContext context, HealthReport report, HealthReportDetail detail)
    {
        // Healthy and degraded both take traffic. Mapping degraded to failure would drain a host
        // whose optional provider is down, which is the outcome the criticality flag prevents.
        context.Response.StatusCode = report.Aggregate == HealthStatus.Unhealthy
            ? StatusCodes.Status503ServiceUnavailable
            : StatusCodes.Status200OK;

        context.Response.ContentType = "application/json; charset=utf-8";

        var body = new ProbeDocument(
            report.Aggregate,
            report.Entries
                .Select(entry => new ProbeEntry(
                    entry.Name.Value,
                    entry.Status,
                    detail == HealthReportDetail.Full ? entry.Detail : null,
                    detail == HealthReportDetail.Full && entry.Data.Count > 0 ? entry.Data : null))
                .ToList());

        await context.Response.WriteAsync(JsonSerializer.Serialize(body, Json), context.RequestAborted)
            .ConfigureAwait(false);
    }

    internal static async Task WriteAsync(HttpContext context, ErrorEnvelope envelope)
    {
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/json; charset=utf-8";

        var body = new EnvelopeDocument(envelope.Code, envelope.Correlation.TraceId);
        await context.Response.WriteAsync(JsonSerializer.Serialize(body, Json), CancellationToken.None)
            .ConfigureAwait(false);
    }

    /// <summary>Full detail on loopback and in development; a minimal body everywhere else. Only
    /// the body narrows — the status is identical either way, so nothing consuming the probe
    /// programmatically changes behaviour.</summary>
    internal static HealthReportDetail DetailFor(HttpContext context, bool isDevelopment)
    {
        if (isDevelopment)
        {
            return HealthReportDetail.Full;
        }

        var remote = context.Connection.RemoteIpAddress;
        var local = remote is null || IPAddress.IsLoopback(remote);
        return local ? HealthReportDetail.Full : HealthReportDetail.Minimal;
    }

    private sealed record ProbeDocument(HealthStatus Status, IReadOnlyList<ProbeEntry> Checks);

    private sealed record ProbeEntry(
        string Name,
        HealthStatus Status,
        string? Detail,
        IReadOnlyDictionary<string, string>? Data);

    private sealed record EnvelopeDocument(string Code, string Correlation);
}
