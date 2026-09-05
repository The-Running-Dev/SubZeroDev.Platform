using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;

namespace SubZeroDev.Platform.Identity;

/// <summary>Verifies a compact, HMAC-SHA256-signed bearer credential — three base64url segments,
/// the same wire shape a JWT uses. This is not a general JWT or OIDC implementation: no algorithm
/// negotiation, no audience, expiry or key-rotation handling. <c>20-contract.md</c> §Out of scope
/// states the sample's issuer is a test double, and this is the smallest verifier whose answer is
/// checked rather than assumed — a real deployment's issuer is chosen and operated by the consumer,
/// never by Platform.</summary>
internal static class BearerCredential
{
    /// <summary>Reads the <c>iss</c> claim without checking the signature — what a provider needs
    /// to decide whether a bearer token is one of <em>its</em> issuer's before spending a signature
    /// check on it. A deployment trusting several issuers registers one provider per issuer; the
    /// one whose issuer does not match must answer "not mine" (defer) rather than "invalid"
    /// (reject), or the first provider a token reaches would end the chain for every other.</summary>
    /// <param name="token">The bearer token's compact-serialised form.</param>
    /// <param name="issuer">The unverified issuer claim, when the token is at least well formed.</param>
    /// <returns>Whether the token is well-formed enough to carry an issuer claim.</returns>
    internal static bool TryReadUnverifiedIssuer(string token, [NotNullWhen(true)] out string? issuer)
    {
        issuer = null;

        if (!TryReadPayload(token, out var payload, out _, out _))
        {
            return false;
        }

        if (!payload.TryGetValue("iss", out var issuerClaim) || issuerClaim.Length == 0)
        {
            return false;
        }

        issuer = issuerClaim;
        return true;
    }

    /// <summary>Verifies <paramref name="token"/> was signed by <paramref name="signingKey"/> and
    /// still carries <paramref name="expectedIssuer"/> — called only once
    /// <see cref="TryReadUnverifiedIssuer"/> has already matched the issuer, so this is the
    /// signature and shape check, not the routing decision. Neither the issuer nor the subject
    /// claim is trimmed, normalised or case-folded — two credentials differing only in case are
    /// different credentials, exactly as <c>PrincipalId</c> requires.</summary>
    /// <param name="token">The bearer token's compact-serialised form.</param>
    /// <param name="expectedIssuer">The issuer this provider trusts.</param>
    /// <param name="signingKey">The cached signing key. Never fetched here.</param>
    /// <param name="subject">The subject claim, when validation succeeds.</param>
    /// <param name="claims">Every string claim the payload carried, when validation succeeds.</param>
    /// <returns>Whether the token validated.</returns>
    internal static bool TryValidateSignature(
        string token,
        string expectedIssuer,
        byte[] signingKey,
        [NotNullWhen(true)] out string? subject,
        out IReadOnlyDictionary<string, string> claims)
    {
        subject = null;
        claims = EmptyClaims;

        if (!TryReadPayload(token, out var payload, out var header, out var payloadSegment))
        {
            return false;
        }

        var signedPart = System.Text.Encoding.ASCII.GetBytes($"{header}.{payloadSegment}");
        byte[] signatureBytes;
        try
        {
            signatureBytes = Base64UrlDecode(token.Split('.')[2]);
        }
        catch (FormatException)
        {
            return false;
        }

        var expectedSignature = HMACSHA256.HashData(signingKey, signedPart);
        if (signatureBytes.Length != expectedSignature.Length
            || !CryptographicOperations.FixedTimeEquals(expectedSignature, signatureBytes))
        {
            return false;
        }

        if (!payload.TryGetValue("iss", out var issuer)
            || !string.Equals(issuer, expectedIssuer, StringComparison.Ordinal)
            || !payload.TryGetValue("sub", out var subjectClaim)
            || subjectClaim.Length == 0)
        {
            return false;
        }

        subject = subjectClaim;
        claims = payload;
        return true;
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyClaims =
        new Dictionary<string, string>();

    private static bool TryReadPayload(
        string token,
        out IReadOnlyDictionary<string, string> payload,
        out string header,
        out string payloadSegment)
    {
        payload = EmptyClaims;
        header = "";
        payloadSegment = "";

        var segments = token.Split('.');
        if (segments.Length != 3)
        {
            return false;
        }

        Dictionary<string, string>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(Base64UrlDecode(segments[1]));
        }
        catch (FormatException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }

        if (parsed is null)
        {
            return false;
        }

        header = segments[0];
        payloadSegment = segments[1];
        payload = parsed;
        return true;
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        var remainder = padded.Length % 4;
        if (remainder > 0)
        {
            padded = padded.PadRight(padded.Length + (4 - remainder), '=');
        }

        return Convert.FromBase64String(padded);
    }
}
