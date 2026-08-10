using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SubZeroDev.Platform.Abstractions;

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

        endpoints.Map("/{**catchAll}", HandleAsync);

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
