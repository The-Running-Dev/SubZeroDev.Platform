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

    /// <summary>Whether the edge authenticates the caller and vouches for them to the workload
    /// (<see cref="CredentialMode.Terminate"/>), or leaves the caller's credential to the workload
    /// (<see cref="CredentialMode.PassThrough"/>). No default: either guess is wrong for someone.</summary>
    public CredentialMode CredentialMode { get; init; }

    /// <summary>How the edge signs the principal assertion it hands the workload. Required in
    /// <see cref="CredentialMode.Terminate"/> and refused in <see cref="CredentialMode.PassThrough"/>.</summary>
    public HandoffOptions? Handoff { get; init; }
}

/// <summary>Who answers for the caller's credential.</summary>
public enum CredentialMode
{
    /// <summary>The edge authenticates the caller, refuses an anonymous one, consumes the credential
    /// and forwards a signed principal assertion in its place.</summary>
    Terminate,

    /// <summary>The edge authenticates nobody and signs nothing.</summary>
    PassThrough,
}

/// <summary>The principal assertion's issuer, audience, signing key and lifetime. The lifetime must
/// not exceed the workload's own maximum, or every assertion is refused.</summary>
/// <param name="Issuer">The assertion's <c>iss</c>, which the workload pins.</param>
/// <param name="Audience">The assertion's <c>aud</c>, which the workload pins.</param>
/// <param name="KeyId">The JWS header's <c>kid</c>, naming the key the workload verifies with.</param>
/// <param name="SigningKeyPath">A PEM file holding the P-256 private key.</param>
/// <param name="Lifetime">How long each assertion is valid, from the moment it is signed.</param>
public sealed record HandoffOptions(
    string Issuer, string Audience, string KeyId, string SigningKeyPath, TimeSpan Lifetime);

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
