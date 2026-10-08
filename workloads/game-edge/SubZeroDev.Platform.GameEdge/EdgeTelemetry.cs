using System.Diagnostics.Metrics;
using SubZeroDev.Platform.Abstractions;

namespace SubZeroDev.Platform.GameEdge;

/// <summary>The edge's record of a streamed response the workload cut short (I-E5): one
/// <c>Warning</c> and one count per abort. Both carry the configured prefix and never the request's
/// path, query or body, and the log carries the exception's type name, never its message, which can
/// echo any of those.</summary>
/// <remarks>The meter comes from <see cref="IMeterFactory"/>, so each host owns its own instance
/// of the shared meter name, and a listener can tell one host's measurements from another's.</remarks>
internal sealed partial class EdgeTelemetry
{
    /// <summary>The category the abort is logged under.</summary>
    internal const string Category = "SubZeroDev.Platform.GameEdge";

    /// <summary>The counter's name on <see cref="PlatformTelemetry.MeterName"/>.</summary>
    internal const string AbortsCounterName = "subzerodev.edge.stream.aborts";

    private readonly ILogger logger;
    private readonly Counter<long> aborts;

    public EdgeTelemetry(ILoggerFactory loggerFactory, IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(meterFactory);

        logger = loggerFactory.CreateLogger(Category);
        aborts = meterFactory
            .Create(PlatformTelemetry.MeterName)
            .CreateCounter<long>(
                AbortsCounterName,
                unit: "{abort}",
                description: "Streamed responses the workload cut short after its headers.");
    }

    /// <summary>Records one abort. The edge calls it once per aborted response, and never for a
    /// caller that went away.</summary>
    public void StreamAborted(
        StreamingRoute route,
        CorrelationId correlation,
        EdgeError error,
        long bytesForwarded,
        TimeSpan sinceHeaders,
        Exception failure)
    {
        LogStreamAborted(
            logger,
            route.PathPrefix,
            correlation.TraceId,
            error.Code,
            bytesForwarded,
            (long)sinceHeaders.TotalMilliseconds,
            failure.GetType().FullName ?? failure.GetType().Name);

        aborts.Add(
            1,
            new KeyValuePair<string, object?>("route", route.PathPrefix),
            new KeyValuePair<string, object?>("code", error.Code));
    }

    [LoggerMessage(
        EventId = 1101,
        EventName = "EdgeStreamAborted",
        Level = LogLevel.Warning,
        Message = "The workload cut short a streamed response on {Route} after its headers: {Code}, "
            + "correlation {Correlation}, {BytesForwarded} bytes forwarded over {ElapsedMilliseconds} ms, "
            + "{ExceptionType}.")]
    private static partial void LogStreamAborted(
        ILogger logger,
        string route,
        string correlation,
        string code,
        long bytesForwarded,
        long elapsedMilliseconds,
        string exceptionType);
}
