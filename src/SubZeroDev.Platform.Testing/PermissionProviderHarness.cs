using System.Security.Claims;
using SubZeroDev.Platform.Abstractions;

namespace SubZeroDev.Platform.Testing;

/// <summary>The grant-source rule as a test a consumer runs against its own
/// <see cref="IPermissionProvider"/>: every provider takes its grants from a source that can be revoked
/// while the caller's credential is still valid, and token claims are never that source
/// (<c>design/10-design.md</c>, <i>Data model</i> § 3; I-A10). Platform cannot stop a consumer's provider
/// reading <see cref="Principal.Claims"/>, so this is how the rule is checked rather than assumed. Each
/// method throws <see cref="InvalidOperationException"/> naming the breach, so any test framework reports
/// it.</summary>
public static class PermissionProviderHarness
{
    /// <summary>Grants, checks the provider answers with the permission, revokes, and checks the very next
    /// answer for the same principal object — no re-authentication in between — no longer includes it.
    /// A provider that carries a grant between requests, or reads it from the principal's token, keeps
    /// answering with it and fails here.</summary>
    /// <param name="provider">The provider under test.</param>
    /// <param name="principal">The principal asked about, the same instance before and after the
    /// revoke.</param>
    /// <param name="tenant">The tenant the checks are evaluated in.</param>
    /// <param name="resource">The resource the checks are scoped to, when the provider is
    /// resource-scoped.</param>
    /// <param name="permission">The permission the grant confers.</param>
    /// <param name="grant">Makes the provider's own source grant <paramref name="permission"/>.</param>
    /// <param name="revoke">Revokes it in that same source.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>A task that completes when the provider has passed.</returns>
    /// <exception cref="InvalidOperationException">The provider did not grant after the grant, or still
    /// granted after the revoke.</exception>
    public static async Task AssertRevokedGrantDeniesNextRequestAsync(
        IPermissionProvider provider,
        Principal principal,
        TenantId tenant,
        ResourceRef? resource,
        PermissionName permission,
        Func<Task> grant,
        Func<Task> revoke,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(revoke);

        await grant().ConfigureAwait(false);
        if (!await GrantsAsync(provider, principal, tenant, resource, permission, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"Provider '{provider.Name}' did not grant '{permission}' after the grant, so a revoke proves nothing.");
        }

        await revoke().ConfigureAwait(false);
        if (await GrantsAsync(provider, principal, tenant, resource, permission, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"Provider '{provider.Name}' still granted '{permission}' after the grant was revoked. A grant must come from " +
                "a source that is read on every request, never carried between requests or read from the token.");
        }
    }

    /// <summary>Asks the provider about a principal whose token asserts a role and the permission itself, with
    /// nothing granted in the provider's own source, and checks the provider grants nothing. A provider
    /// that reads a grant out of <see cref="Principal.Claims"/> fails here.</summary>
    /// <param name="provider">The provider under test.</param>
    /// <param name="tenant">The tenant the check is evaluated in.</param>
    /// <param name="resource">The resource the check is scoped to, when the provider is
    /// resource-scoped.</param>
    /// <param name="permission">The permission the token claims to confer.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>A task that completes when the provider has passed.</returns>
    /// <exception cref="InvalidOperationException">The provider granted from the token's claims.</exception>
    public static async Task AssertTokenClaimsGrantNothingAsync(
        IPermissionProvider provider,
        TenantId tenant,
        ResourceRef? resource,
        PermissionName permission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Role, "owner"),
                new Claim(ClaimTypes.Role, "administrator"),
                new Claim("role", "owner"),
                new Claim("roles", "owner administrator"),
                new Claim("permission", permission.Value),
                new Claim("permissions", permission.Value),
                new Claim("scope", permission.Value),
            ],
            authenticationType: "harness");
        var principal = new Principal(
            new PrincipalId("harness-issuer", "token-asserts-everything"),
            PrincipalKind.Account,
            "Token asserts everything",
            new ClaimsPrincipal(identity));

        if (await GrantsAsync(provider, principal, tenant, resource, permission, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"Provider '{provider.Name}' granted '{permission}' to a principal whose own source grants nothing, because " +
                "its token claimed it. Claims are never a grant source.");
        }
    }

    private static async Task<bool> GrantsAsync(
        IPermissionProvider provider,
        Principal principal,
        TenantId tenant,
        ResourceRef? resource,
        PermissionName permission,
        CancellationToken cancellationToken)
    {
        var answer = await provider.GrantsAsync(principal, tenant, resource, cancellationToken).ConfigureAwait(false);
        return answer.IsSuccess && answer.Value.Contains(permission);
    }
}
