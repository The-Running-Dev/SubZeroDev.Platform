using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Hosting;

namespace SubZeroDev.Platform.GameEdge;

/// <summary>Maps the forwarding route. Ordinary application code, registered the way any
/// application registers a route — there is no <c>AddGameEdge</c>.</summary>
public static class GameEdgeEndpointExtensions
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Maps a catch-all route that forwards every request, of every method and every path,
    /// to the workload. Platform's own probe middleware answers <c>/health/live</c> and
    /// <c>/health/ready</c> earlier in the pipeline, so those two paths never reach this route.</summary>
    /// <param name="endpoints">The route builder.</param>
    /// <returns>The same route builder, so calls chain.</returns>
    public static IEndpointRouteBuilder MapGameWorkloadForwarding(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // D5-S8 (I-R6): every endpoint the standard registration maps now needs a declared
        // requirement or a named exemption. This route forwards every request verbatim, of every
        // method and path, to the workload — it has no permission or feature of its own to
        // declare, because it does not interpret what it carries. The workload's own boundary is
        // where that belongs, not Platform's per-endpoint declaration at the edge in front of it.
        endpoints
            .Map("/{**catchAll}", HandleAsync)
            .ExemptFromPlatformAuthorization(
                "Forwards every request verbatim to the game workload; it has no permission or "
                + "feature of its own to declare, and the workload's own boundary authorizes what it carries.");

        return endpoints;
    }

    private static async Task HandleAsync(HttpContext context)
    {
        var forwarder = context.RequestServices.GetRequiredService<IGameWorkloadForwarder>();
        var scopeAccessor = context.RequestServices.GetRequiredService<IOperationScopeAccessor>();
        var correlation = context.RequestServices.GetRequiredService<ICurrentCorrelation>();

        var trace = scopeAccessor.Current?.Trace
            ?? throw new InvalidOperationException(
                "No ambient operation scope is open; OperationScopeMiddleware must run before this route.");

        using var bodyStream = new MemoryStream();
        await context.Request.Body.CopyToAsync(bodyStream, context.RequestAborted).ConfigureAwait(false);

        var request = new ForwardedRequest(
            new HttpMethod(context.Request.Method),
            context.Request.Path + context.Request.QueryString,
            bodyStream.ToArray(),
            context.Request.ContentType,
            trace);

        var options = context.RequestServices.GetRequiredService<GameEdgeOptions>();
        if (StreamingRoutes.Match(options.StreamingRoutes, context.Request.Path.Value ?? string.Empty) is { } route)
        {
            await StreamAsync(context, forwarder, request, route, correlation.Current).ConfigureAwait(false);
            return;
        }

        var result = await forwarder.ForwardAsync(request, context.RequestAborted).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            var response = result.Value;
            context.Response.StatusCode = response.StatusCode;
            if (response.ContentType is { } contentType)
            {
                context.Response.ContentType = contentType;
            }

            await context.Response.Body
                .WriteAsync(response.Body, context.RequestAborted)
                .ConfigureAwait(false);
            return;
        }

        await WriteEdgeErrorAsync(context, result.Error, correlation.Current).ConfigureAwait(false);
    }

    /// <summary>Relays a streamed route. Before the workload's headers the edge answers for itself, as
    /// on a buffered route. At the headers it commits the workload's status and <c>Content-Type</c>,
    /// then flushes each piece of the body as it is read (I-E2). A workload failure after that aborts
    /// the response, with no byte of the edge's own (I-E3), and is logged and counted once (I-E5); a
    /// caller that goes away ends the relay quietly, and disposing the upstream response carries that
    /// to the workload.</summary>
    private static async Task StreamAsync(
        HttpContext context,
        IGameWorkloadForwarder forwarder,
        ForwardedRequest request,
        StreamingRoute route,
        CorrelationId correlation)
    {
        var aborted = context.RequestAborted;
        Result<StreamedResponse, EdgeError> result;
        try
        {
            result = await forwarder
                .ForwardStreamingAsync(request, route.FirstByteTimeout, aborted)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (aborted.IsCancellationRequested)
        {
            // The caller went away before the workload's headers: nothing to answer, nothing to log.
            return;
        }

        if (!result.IsSuccess)
        {
            await WriteEdgeErrorAsync(context, result.Error, correlation).ConfigureAwait(false);
            return;
        }

        await using var streamed = result.Value;
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        var headersAt = Stopwatch.GetTimestamp();
        long forwarded = 0;
        try
        {
            context.Response.StatusCode = streamed.StatusCode;
            if (streamed.ContentType is { } contentType)
            {
                context.Response.ContentType = contentType;
            }

            await context.Response.StartAsync(aborted).ConfigureAwait(false);

            while (true)
            {
                int read;
                try
                {
                    read = await streamed.Body.ReadAsync(buffer, aborted).ConfigureAwait(false);
                }
                catch (Exception thrown) when (!aborted.IsCancellationRequested
                    && thrown is IOException or HttpRequestException)
                {
                    // The workload failed after the commit point: the honest signal left is an
                    // incomplete response, so no terminating chunk and no edge-authored byte. The
                    // operator's signal is the edge's own record, which a 200 in the access log hides.
                    context.Abort();
                    context.RequestServices.GetRequiredService<EdgeTelemetry>().StreamAborted(
                        route,
                        correlation,
                        EdgeError.WorkloadUnreachable(),
                        forwarded,
                        Stopwatch.GetElapsedTime(headersAt),
                        thrown);
                    return;
                }

                if (read == 0)
                {
                    return;
                }

                await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), aborted).ConfigureAwait(false);
                await context.Response.Body.FlushAsync(aborted).ConfigureAwait(false);
                forwarded += read;
            }
        }
        catch (OperationCanceledException) when (aborted.IsCancellationRequested)
        {
            // The caller went away. Not the workload's failure and not the edge's: nothing to answer
            // and nothing to log above Debug (I-E5). Disposing `streamed` releases the workload.
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static Task WriteEdgeErrorAsync(HttpContext context, EdgeError error, CorrelationId correlation)
    {
        context.Response.StatusCode = error switch
        {
            WorkloadUnreachableEdgeError => StatusCodes.Status503ServiceUnavailable,
            WorkloadTimeoutEdgeError => StatusCodes.Status504GatewayTimeout,
            _ => StatusCodes.Status500InternalServerError,
        };
        context.Response.ContentType = "application/json; charset=utf-8";

        var body = new EdgeErrorBody(error.Code, correlation.TraceId);
        return context.Response.WriteAsync(JsonSerializer.Serialize(body, Json), context.RequestAborted);
    }

    private sealed record EdgeErrorBody(string Code, string Correlation);
}
