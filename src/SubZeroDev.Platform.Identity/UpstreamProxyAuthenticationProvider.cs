using SubZeroDev.Platform.Abstractions;

namespace SubZeroDev.Platform.Identity;

/// <summary>Establishes a <see cref="PrincipalKind.Delegated"/> principal from an upstream-proxy
/// assertion — a boundary Platform trusts but does not own, and behind which no account will ever
/// exist (<c>20-contract.md</c> §1, <c>application-modules.md</c> §2). The subject comes from a
/// header the trusted proxy sets after it has already authenticated the caller; the issuer names
/// the boundary itself and is fixed at registration, never read from the request.</summary>
/// <param name="name">The provider's name, unique within the registry.</param>
/// <param name="issuer">The trusted boundary's own identity — the first half of every principal id
/// this provider establishes.</param>
/// <param name="subjectHeader">The header the trusted proxy sets to the asserted subject.</param>
public sealed class UpstreamProxyAuthenticationProvider(string name, string issuer, string subjectHeader)
    : IAuthenticationProvider
{
    /// <inheritdoc/>
    public string Name => name;

    /// <inheritdoc/>
    public Task<Result<Principal, AuthenticationError>> AuthenticateAsync(
        IAuthenticationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.Headers.TryGetValue(subjectHeader, out var values) || values.Count == 0)
        {
            // No assertion of this provider's kind was presented -- defer.
            return Task.FromResult(Result<Principal, AuthenticationError>.Success(Principal.Anonymous));
        }

        var subject = values[0];
        if (subject.Length == 0)
        {
            return Task.FromResult(Result<Principal, AuthenticationError>.Failure(
                AuthenticationError.CredentialRejected(name)));
        }

        // No claims: the asserting boundary produced none, and Delegated is not a degraded Account
        // to be filled in with a display name or a claims set it never had (20-contract.md §1).
        var principal = new Principal(new PrincipalId(issuer, subject), PrincipalKind.Delegated, null, null);
        return Task.FromResult(Result<Principal, AuthenticationError>.Success(principal));
    }
}
