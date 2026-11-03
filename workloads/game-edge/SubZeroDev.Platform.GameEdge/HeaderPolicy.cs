using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

namespace SubZeroDev.Platform.GameEdge;

/// <summary>One header the edge writes on a forwarded request or a relayed response, with every value it
/// carries. Values stay separate lines on the wire: <c>Set-Cookie</c> must never be joined.</summary>
/// <param name="Name">The header name.</param>
/// <param name="Values">The header's values, verbatim.</param>
internal sealed record ForwardedHeader(string Name, string[] Values);

/// <summary>The credential policy, header by header (<c>10-design.md</c>, <i>Control flow</i> §6). The
/// only place that decides which of a caller's headers reach the workload and which of the workload's
/// reach the caller; the forwarder and both relay paths apply what it returns. Everything not named
/// here is dropped in both directions, so a hop-by-hop header or an <c>Authorization</c> never needs a
/// rule of its own to be stripped.</summary>
internal static class HeaderPolicy
{
    public const string XForwardedFor = "X-Forwarded-For";
    public const string XForwardedProto = "X-Forwarded-Proto";
    public const string XForwardedHost = "X-Forwarded-Host";

    // A preflight cannot be answered by the workload without these, and they carry no credential.
    private static readonly string[] PassThroughRequestHeaders =
    [
        "Cookie",
        "Origin",
        "Access-Control-Request-Method",
        "Access-Control-Request-Headers",
    ];

    // The edge sets all three itself, so a caller's copy of any of them is never read as one.
    private static readonly string[] ForwardingHeaders = [XForwardedFor, XForwardedProto, XForwardedHost];

    private static readonly string[] BothModesResponseHeaders = ["Location", "Cache-Control", "Vary"];

    /// <summary>The headers of the outbound request other than the handoff, <c>traceparent</c> and the body's
    /// <c>Content-Type</c>: the caller's own <c>Cookie</c> and <c>Origin</c> in pass-through mode, and
    /// the <c>X-Forwarded-*</c> set the edge writes in both.</summary>
    /// <param name="request">The inbound request. Read only.</param>
    /// <param name="remote">The address the connection came from, or <see langword="null"/> when the
    /// transport has none.</param>
    /// <param name="options">The edge's configuration.</param>
    /// <returns>The headers to write, each once.</returns>
    public static IReadOnlyList<ForwardedHeader> Request(HttpRequest request, IPAddress? remote, GameEdgeOptions options)
    {
        var headers = new List<ForwardedHeader>();

        // One request's own headers and nothing else: the edge holds no cookie state (invariant 101),
        // and both ride together or both are dropped (invariant 100).
        if (options.CredentialMode == CredentialMode.PassThrough)
        {
            foreach (var name in PassThroughRequestHeaders)
            {
                if (request.Headers.TryGetValue(name, out var values) && values.Count > 0)
                {
                    headers.Add(new ForwardedHeader(name, values.ToArray()!));
                }
            }
        }

        // A configured proxy has already seen the real client, so its values replace the connection's;
        // anyone else's never pass (invariant 99).
        var trusted = remote is not null && IsTrusted(options, remote);
        headers.Add(Forwarding(request, XForwardedFor, trusted, remote?.ToString()));
        headers.Add(Forwarding(request, XForwardedProto, trusted, request.Scheme));
        headers.Add(Forwarding(request, XForwardedHost, trusted, request.Host.HasValue ? request.Host.Value : null));

        return headers.Where(header => header.Values.Length > 0).ToList();
    }

    /// <summary>The headers of the workload's response that reach the caller, other than <c>Content-Type</c>.
    /// <c>Set-Cookie</c> and <c>Access-Control-*</c> return only in pass-through mode, every value
    /// verbatim; terminate mode returns a bearer-only contract with neither (invariants 102, 103).</summary>
    /// <param name="response">The workload's response headers.</param>
    /// <param name="mode">The edge's credential mode.</param>
    /// <returns>The headers to write, each once.</returns>
    public static IReadOnlyList<ForwardedHeader> Response(HttpResponseHeaders response, CredentialMode mode)
    {
        var headers = new List<ForwardedHeader>();
        foreach (var header in response.NonValidated)
        {
            var name = header.Key;
            var wanted = BothModesResponseHeaders.Contains(name, StringComparer.OrdinalIgnoreCase)
                || (mode == CredentialMode.PassThrough
                    && (name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase)));
            if (wanted)
            {
                headers.Add(new ForwardedHeader(name, header.Value.ToArray()));
            }
        }

        return headers;
    }

    /// <summary>Writes <paramref name="headers"/> on the caller's response, replacing nothing the edge
    /// has written itself.</summary>
    /// <param name="response">The caller's response, before its headers are committed.</param>
    /// <param name="headers">What <see cref="Response"/> returned.</param>
    public static void Apply(HttpResponse response, IReadOnlyList<ForwardedHeader> headers)
    {
        foreach (var header in headers)
        {
            response.Headers.Append(header.Name, new StringValues(header.Values));
        }
    }

    private static ForwardedHeader Forwarding(HttpRequest request, string name, bool trusted, string? fromConnection)
    {
        if (trusted && request.Headers.TryGetValue(name, out var values) && values.Count > 0)
        {
            return new ForwardedHeader(name, values.ToArray()!);
        }

        return new ForwardedHeader(name, fromConnection is null ? [] : [fromConnection]);
    }

    private static bool IsTrusted(GameEdgeOptions options, IPAddress remote)
    {
        var address = Normalize(remote);
        foreach (var proxy in options.TrustedProxies)
        {
            if (IPAddress.TryParse(proxy, out var parsed) && Normalize(parsed).Equals(address))
            {
                return true;
            }
        }

        return false;
    }

    private static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}

/// <summary>The startup check on <see cref="GameEdgeOptions.TrustedProxies"/>.</summary>
internal static class TrustedProxySettings
{
    /// <summary>Every entry that is not an IP address, each naming its full configuration key. Empty when
    /// the list is valid or absent.</summary>
    public static IReadOnlyList<string> Validate(IConfigurationSection edge)
    {
        var errors = new List<string>();
        foreach (var entry in edge.GetSection("TrustedProxies").GetChildren())
        {
            if (!IPAddress.TryParse(entry.Value, out _))
            {
                errors.Add($"'GameEdge:TrustedProxies:{entry.Key}' must be an IP address.");
            }
        }

        return errors;
    }
}
