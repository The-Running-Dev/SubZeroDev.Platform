using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Hosting;

namespace SubZeroDev.Platform.Mcp;

/// <summary>The one call a host makes to map the MCP transport (mirrors
/// <c>AddPlatformWebHost</c>'s "one mandatory call" shape). Not part of <see cref="McpModule"/>'s own
/// <c>Register(IServiceCollection)</c>, because endpoint mapping needs the built
/// <see cref="WebApplication"/>, which is not available at service-registration time.</summary>
public static class McpEndpointExtensions
{
    /// <summary>Maps the MCP transport under <paramref name="pattern"/>.</summary>
    /// <param name="app">The web application.</param>
    /// <param name="pattern">The route prefix the MCP endpoints are mapped under.</param>
    /// <returns>The endpoint convention builder for the mapped group, so a host can layer its own
    /// conventions on top.</returns>
    public static IEndpointConventionBuilder MapPlatformMcp(this WebApplication app, string pattern = "/mcp")
    {
        ArgumentNullException.ThrowIfNull(app);

        // Binds HttpContext.User from Platform's own ambient principal ahead of the SDK's own
        // endpoints, so its session-principal binding (design/90-decisions.md, 2026-08-29) sees it.
        // A path-gated app.Use rather than a route-group-scoped middleware: RouteGroupBuilder (the
        // SDK targets net8.0/net9.0/net10.0) exposes no middleware seam of its own in this version,
        // only IEndpointConventionBuilder — verified against the built framework assemblies, not
        // from memory, per AGENTS.md § Verification.
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments(pattern))
            {
                var middleware = new McpPrincipalBindingMiddleware(next);
                await middleware
                    .InvokeAsync(context, context.RequestServices.GetRequiredService<ICurrentPrincipal>())
                    .ConfigureAwait(false);
                return;
            }

            await next(context).ConfigureAwait(false);
        });

        return app
            .MapGroup(pattern)
            .MapMcp()
            .ExemptFromPlatformAuthorization(
                "Mcp evaluates authorization and entitlement per tool call, inside PlatformMcpTool's "
                + "own fixed order, rather than once at the endpoint level (design/20-contract.md § "
                + "Public surface 10) — I-R6's endpoint-level requirement does not apply to a surface "
                + "whose permission varies per call.");
    }
}
