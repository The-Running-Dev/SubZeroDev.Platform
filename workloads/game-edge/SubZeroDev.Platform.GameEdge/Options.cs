namespace SubZeroDev.Platform.GameEdge;

/// <summary>The edge's own configuration. Bound from the <c>GameEdge</c> configuration section —
/// <see cref="WorkloadBaseAddress"/> has no default, since a bad guess would forward every request
/// to a workload that was never configured.</summary>
public sealed record GameEdgeOptions
{
    /// <summary>Where the workload listens. Never dereferenced except by the forwarder and the
    /// readiness probe.</summary>
    public required Uri WorkloadBaseAddress { get; init; }

    /// <summary>How long a forwarded request may run before the edge answers <c>504</c> on the
    /// workload's behalf.</summary>
    public required TimeSpan ForwardTimeout { get; init; }

    /// <summary>How long the readiness check's own probe of the workload's readiness endpoint may
    /// run before it is treated as unreachable.</summary>
    public required TimeSpan ReadinessTimeout { get; init; }

    /// <summary>The routes whose responses are relayed as the workload produces them rather than
    /// read to the end first. Empty — the default — means no route streams, which is the edge
    /// unchanged. Checked at startup (<c>20-contract.md</c>, <i>Types</i> § 14).</summary>
    public IReadOnlyList<StreamingRoute> StreamingRoutes { get; init; } = [];
}

/// <summary>One streamed route: a path prefix, matched ordinal and on segment boundaries, and how long
/// the workload has to send its response headers.</summary>
public sealed class StreamingRoute
{
    /// <summary>The path prefix. Begins with <c>/</c>, is not <c>/</c> alone, and has no trailing
    /// <c>/</c> and no <c>?</c> or <c>#</c>.</summary>
    public required string PathPrefix { get; init; }

    /// <summary>How long the workload has to send its response headers — the edge's commit point.
    /// Greater than zero and no greater than <see cref="GameEdgeOptions.ForwardTimeout"/>; after the
    /// headers there is no edge deadline.</summary>
    public required TimeSpan FirstByteTimeout { get; init; }
}

/// <summary>The startup check on <see cref="GameEdgeOptions.StreamingRoutes"/>, and the matcher that
/// picks a request's route. Neither is surface.</summary>
internal static class StreamingRoutes
{
    /// <summary>Every breach of the streamed-route rules, each naming its full configuration key.
    /// Empty when the routes are valid.</summary>
    public static IReadOnlyList<string> Validate(GameEdgeOptions options)
    {
        var errors = new List<string>();
        var routes = options.StreamingRoutes;

        for (var index = 0; index < routes.Count; index++)
        {
            var prefixKey = $"GameEdge:StreamingRoutes:{index}:PathPrefix";
            var timeoutKey = $"GameEdge:StreamingRoutes:{index}:FirstByteTimeout";
            var prefix = routes[index].PathPrefix;

            if (string.IsNullOrEmpty(prefix) || prefix[0] != '/')
            {
                errors.Add($"'{prefixKey}' must begin with '/'.");
            }
            else if (prefix == "/")
            {
                errors.Add($"'{prefixKey}' must not be '/' alone: that would stream every route.");
            }
            else if (prefix[^1] == '/' || prefix.Contains('?', StringComparison.Ordinal) || prefix.Contains('#', StringComparison.Ordinal))
            {
                errors.Add($"'{prefixKey}' must have no trailing '/' and no '?' or '#'.");
            }
            else
            {
                for (var earlier = 0; earlier < index; earlier++)
                {
                    var other = routes[earlier].PathPrefix;
                    if (other is not null && (Covers(other, prefix) || Covers(prefix, other)))
                    {
                        errors.Add($"'{prefixKey}' ('{prefix}') overlaps entry {earlier} ('{other}'): no prefix may equal, or lie under, another.");
                        break;
                    }
                }
            }

            var timeout = routes[index].FirstByteTimeout;
            if (timeout <= TimeSpan.Zero)
            {
                errors.Add($"'{timeoutKey}' is required and must be greater than zero.");
            }
            else if (timeout > options.ForwardTimeout)
            {
                errors.Add($"'{timeoutKey}' ({timeout:c}) must be no greater than 'GameEdge:ForwardTimeout' ({options.ForwardTimeout:c}).");
            }
        }

        return errors;
    }

    /// <summary>The route whose prefix covers <paramref name="path"/>, or <see langword="null"/> when
    /// none does and the request is buffered. The validated prefixes never overlap, so at most one
    /// matches.</summary>
    public static StreamingRoute? Match(IReadOnlyList<StreamingRoute> routes, string path)
    {
        foreach (var route in routes)
        {
            if (Covers(route.PathPrefix, path))
            {
                return route;
            }
        }

        return null;
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="prefix"/> or lies under it on a
    /// segment boundary — ordinal, so case counts.</summary>
    private static bool Covers(string prefix, string path) =>
        path.StartsWith(prefix, StringComparison.Ordinal)
        && (path.Length == prefix.Length || path[prefix.Length] == '/');
}
