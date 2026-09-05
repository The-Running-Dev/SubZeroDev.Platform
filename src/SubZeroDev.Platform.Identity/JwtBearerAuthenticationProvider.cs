using System.Security.Claims;
using SubZeroDev.Platform.Abstractions;

namespace SubZeroDev.Platform.Identity;

/// <summary>Authenticates a bearer credential issued by one trusted issuer, mapping it onto an
/// <see cref="PrincipalKind.Account"/> principal whose id carries the issuer and the subject claim
/// as the two opaque halves <c>PrincipalId</c> requires. Registered once per trusted issuer — an
/// operator trusting several issuers registers several instances, each under its own
/// <see cref="Name"/>, exactly as it would register several <c>IAuthenticationProvider</c>s of any
/// other kind.</summary>
/// <remarks>Construction carries the signing key already cached: this type never fetches key
/// material on the request path (<c>20-contract.md</c> §2). A deployment that wants a rotated or
/// not-yet-fetched key answers <see cref="AuthenticationError.KeyMaterialUnavailable"/> by passing
/// <see langword="null"/> for <paramref name="signingKey"/> rather than by this provider reaching
/// out for one.</remarks>
/// <param name="name">The provider's name, unique within the registry.</param>
/// <param name="issuer">The issuer this instance trusts. Never parsed or normalised.</param>
/// <param name="signingKey">The issuer's cached signing key, or <see langword="null"/> when none is
/// cached yet.</param>
public sealed class JwtBearerAuthenticationProvider(string name, string issuer, byte[]? signingKey)
    : IAuthenticationProvider
{
    private const string AuthorizationHeader = "Authorization";
    private const string BearerPrefix = "Bearer ";

    /// <inheritdoc/>
    public string Name => name;

    /// <inheritdoc/>
    public Task<Result<Principal, AuthenticationError>> AuthenticateAsync(
        IAuthenticationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!TryReadBearerToken(request, out var token))
        {
            // No bearer credential of this provider's kind was presented -- defer, exactly as "no
            // credential presented" answers Principal.Anonymous rather than a failure.
            return Task.FromResult(Result<Principal, AuthenticationError>.Success(Principal.Anonymous));
        }

        if (!BearerCredential.TryReadUnverifiedIssuer(token, out var tokenIssuer))
        {
            // Bearer scheme, but not even shaped like a signed credential -- this is the one shape
            // no provider trusting a different issuer could claim either, so it is a rejection
            // rather than a defer.
            return Task.FromResult(Result<Principal, AuthenticationError>.Failure(
                AuthenticationError.CredentialRejected(name)));
        }

        if (!string.Equals(tokenIssuer, issuer, StringComparison.Ordinal))
        {
            // Well formed, but asserting an issuer this instance does not trust -- not mine.
            // Deferring, rather than rejecting, is what lets a deployment trusting several issuers
            // register one provider per issuer without the first one reached ending the chain for
            // every other.
            return Task.FromResult(Result<Principal, AuthenticationError>.Success(Principal.Anonymous));
        }

        if (signingKey is null)
        {
            return Task.FromResult(Result<Principal, AuthenticationError>.Failure(
                AuthenticationError.KeyMaterialUnavailable(name)));
        }

        if (!BearerCredential.TryValidateSignature(token, issuer, signingKey, out var subject, out var claims))
        {
            return Task.FromResult(Result<Principal, AuthenticationError>.Failure(
                AuthenticationError.CredentialRejected(name)));
        }

        var identity = new ClaimsIdentity(
            claims.Select(claim => new Claim(claim.Key, claim.Value)),
            authenticationType: name);

        var principal = new Principal(
            new PrincipalId(issuer, subject),
            PrincipalKind.Account,
            DisplayName: claims.TryGetValue("name", out var displayName) ? displayName : null,
            Claims: new ClaimsPrincipal(identity));

        return Task.FromResult(Result<Principal, AuthenticationError>.Success(principal));
    }

    private static bool TryReadBearerToken(IAuthenticationRequest request, out string token)
    {
        token = "";

        if (!request.Headers.TryGetValue(AuthorizationHeader, out var values) || values.Count == 0)
        {
            return false;
        }

        var value = values[0];
        if (!value.StartsWith(BearerPrefix, StringComparison.Ordinal) || value.Length == BearerPrefix.Length)
        {
            return false;
        }

        token = value[BearerPrefix.Length..];
        return true;
    }
}
