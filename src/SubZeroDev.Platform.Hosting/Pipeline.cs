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
        TenantResolutionChain tenantResolution)
    {
        var traceParent = context.Request.Headers.TraceParent.ToString();
        var traceState = context.Request.Headers["tracestate"].ToString();

        // No authentication provider exists yet — Identity lands in S9 and the fixed authenticate
        // step in S8. Every inbound request observes Anonymous until then; establishing System or
        // Account from a credential is that later step's, not this one's.
        var principal = Principal.Anonymous;

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
        using var scope = codec.TryParse(
            traceParent,
            string.IsNullOrEmpty(traceState) ? null : traceState,
            out var established)
            ? factory.Begin(codec.CurrentHop(established), new CorrelationId(established.TraceId), tenant, principal)
            : factory.Begin(tenant, principal);

        await next(context).ConfigureAwait(false);
    }
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
