using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SubZeroDev.Platform.Hosting;

namespace SubZeroDev.Platform.GameEdge;

/// <summary>The edge's deployment check: which commit is running, and whether it reaches its
/// workload. Edge-local, beside Platform's own probes rather than in them — the probe body is a
/// shared wire shape every Platform host serves, and the revision a deploy waits for is this
/// workload's concern, not the library's.</summary>
public static partial class DeployedRevision
{
    /// <summary>The path. Under <c>/health/</c> with the probes, and answered by the edge itself:
    /// it is mapped ahead of the catch-all, so it never reaches the workload.</summary>
    public const string Path = "/health/deployed";

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Checks <see cref="GameEdgeOptions.Revision"/>: absent, or a full commit hash.</summary>
    /// <param name="options">The bound options.</param>
    /// <returns>Why the setting is refused, or <see langword="null"/> when it is accepted.</returns>
    public static string? Validate(GameEdgeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Revision is null || CommitHash().IsMatch(options.Revision)
            ? null
            : "Revision must be a full commit hash: 40 lowercase hex characters.";
    }

    /// <summary>Maps <see cref="Path"/>. It answers <c>200</c> with <c>{"revision":"…","status":"Healthy"}</c>
    /// when the edge knows its revision and its workload answers readiness, and <c>503</c> with the
    /// same shape otherwise — the revision is <c>null</c> when none was configured. It runs only the
    /// workload probe, never a game operation, and is not rate limited, like the probes.</summary>
    /// <param name="endpoints">The route builder.</param>
    /// <returns>The same route builder, so calls chain.</returns>
    public static IEndpointRouteBuilder MapGameEdgeDeployment(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints
            .MapGet(Path, HandleAsync)
            .ExemptFromPlatformAuthorization(
                "A deploy reads it before any principal exists, as it reads the probes; it reports the "
                + "commit and the workload's readiness, and carries nothing of a caller's.");

        return endpoints;
    }

    private static async Task HandleAsync(HttpContext context)
    {
        var options = context.RequestServices.GetRequiredService<GameEdgeOptions>();
        var probe = context.RequestServices.GetRequiredService<IGameWorkloadProbe>();

        var reachable = (await probe.ProbeReadinessAsync(context.RequestAborted).ConfigureAwait(false)).IsSuccess;
        var healthy = reachable && options.Revision is not null;

        context.Response.StatusCode = healthy ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response
            .WriteAsync(
                JsonSerializer.Serialize(new Body(options.Revision, healthy ? "Healthy" : "Unhealthy"), Json),
                context.RequestAborted)
            .ConfigureAwait(false);
    }

    [GeneratedRegex("^[0-9a-f]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex CommitHash();

    private sealed record Body(string? Revision, string Status);
}
