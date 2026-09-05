using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;

namespace SubZeroDev.Platform.Hosting;

/// <summary>Places Platform's middleware at the front of the pipeline without the consumer calling
/// anything. The brief's done-criterion is that a second mandatory call is bespoke wiring.</summary>
internal sealed class PlatformStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
        app =>
        {
            // Outermost first. The scope wraps the envelope because an envelope carries the
            // correlation, and probes sit inside both so a throwing probe cannot escape.
            app.UseMiddleware<OperationScopeMiddleware>();
            app.UseMiddleware<ErrorEnvelopeMiddleware>();
            app.UseMiddleware<ProbeMiddleware>();
            next(app);
        };
}

/// <summary>Establishes the ambient operation scope for every request.</summary>
internal sealed class OperationScopeMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        IOperationScopeFactory factory,
        ITraceContextCodec codec,
        TenantResolutionChain tenantResolution,
        AuthenticationChain authentication)
    {
        var traceParent = context.Request.Headers.TraceParent.ToString();
        var traceState = context.Request.Headers["tracestate"].ToString();
        var parsed = codec.TryParse(
            traceParent,
            string.IsNullOrEmpty(traceState) ? null : traceState,
            out var established);

        // Authenticate at the transport — the fixed order's first step. A rejected or unverifiable
        // credential is refused here, before a tenant is resolved or a scope opens: nothing later
        // in the pipeline runs for a request that never established a principal.
        var authenticated = await authentication
            .AuthenticateAsync(new HttpAuthenticationRequest(context.Request.Headers), context.RequestAborted)
            .ConfigureAwait(false);

        if (!authenticated.IsSuccess)
        {
            var correlation = parsed ? new CorrelationId(established.TraceId) : MintCorrelation(codec);
            await ProbeBody
                .WriteAsync(
                    context,
                    new ErrorEnvelope(authenticated.Error.Code, correlation),
                    StatusCodes.Status401Unauthorized)
                .ConfigureAwait(false);
            return;
        }

        var principal = authenticated.Value;

        // Resolved once, before the scope opens, so the tenant is fixed for the request's lifetime
        // regardless of what a resolver would answer if asked again mid-request.
        var tenant = await tenantResolution.ResolveAsync(context.RequestAborted).ConfigureAwait(false);

        // A malformed inbound header is not the caller's fault: it is ignored and fresh context is
        // minted, which is origination rather than fabrication.
        //
        // A well-formed one is adopted, but not forwarded byte-for-byte: `codec.CurrentHop` reports
        // this request's own span (ASP.NET Core's own instrumentation, ambient by the time any
        // middleware runs) rather than the caller's, so a downstream forward names this hop as the
        // parent instead of skipping over it — the relationship S8.2 asserts.
        using var scope = parsed
            ? factory.Begin(codec.CurrentHop(established), new CorrelationId(established.TraceId), tenant, principal)
            : factory.Begin(tenant, principal);

        await next(context).ConfigureAwait(false);
    }

    /// <summary>Mints a correlation for a request refused before a scope opens, on the same
    /// origination terms a scope would mint one under: the caller is the origin, not a fabricator.</summary>
    private static CorrelationId MintCorrelation(ITraceContextCodec codec)
    {
        using var handle = codec.StartRoot("platform.operation");
        return new CorrelationId(handle.Context.TraceId);
    }
}

/// <summary>Adapts ASP.NET Core's request headers to the transport's credential surface. Headers,
/// and nothing else: there is no member here that could reach a request body.</summary>
internal sealed class HttpAuthenticationRequest : IAuthenticationRequest
{
    public HttpAuthenticationRequest(IHeaderDictionary headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        Headers = headers.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value
                .Where(value => value is not null)
                .Select(value => value!)
                .ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyDictionary<string, IReadOnlyList<string>> Headers { get; }
}

/// <summary>Turns an unhandled failure into an envelope carrying the correlation, and nothing else.</summary>
internal sealed class ErrorEnvelopeMiddleware(RequestDelegate next, ILogger<ErrorEnvelopeMiddleware> logger)
{
    /// <summary>The stable code an unhandled request failure carries. One code, because the
    /// envelope's job is to be greppable against the log line, not to classify.</summary>
    internal const string UnhandledCode = "UnhandledRequestFailure";

    public async Task InvokeAsync(HttpContext context, ICurrentCorrelation correlation)
    {
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var current = correlation.Current;

            // The detail goes to the log, which is the only place it belongs: the envelope carries
            // a code and the correlation that ties the two together.
            logger.LogError(
                exception,
                "Unhandled failure on {Method} {Path}. Correlation {Correlation}.",
                context.Request.Method,
                context.Request.Path.Value,
                current.TraceId);

            if (context.Response.HasStarted)
            {
                throw;
            }

            context.Response.Clear();
            await ProbeBody.WriteAsync(context, new ErrorEnvelope(UnhandledCode, current)).ConfigureAwait(false);
        }
    }
}

/// <summary>Serves the probes. Stands down when the host placed them in its own route table.</summary>
internal sealed class ProbeMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        ProbeMapping mapping,
        HealthProbe probe,
        IHostEnvironment environment)
    {
        if (mapping.MappedExplicitly)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var kind = context.Request.Path.Value switch
        {
            ProbeBody.LivenessPath => HealthCheckKind.Liveness,
            ProbeBody.ReadinessPath => HealthCheckKind.Readiness,
            _ => (HealthCheckKind?)null,
        };

        if (kind is null)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var report = await probe.RunAsync(kind.Value, context.RequestAborted).ConfigureAwait(false);
        await ProbeBody
            .WriteAsync(context, report, ProbeBody.DetailFor(context, environment.IsDevelopment()))
            .ConfigureAwait(false);
    }
}
