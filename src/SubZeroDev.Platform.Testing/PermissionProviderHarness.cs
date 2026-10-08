using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;

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

    /// <summary>The same rule for an <see cref="IMirroredPermissionProvider"/>, revoking where the role
    /// lives — at the issuer — rather than in the mirror (I-A13, #264). Grants at the issuer, syncs, and
    /// expects <see cref="AuthorizationOutcome.Allowed"/> with <paramref name="provider"/> among the
    /// sources. Revokes at the issuer <b>without syncing</b>, moves <see cref="IPlatformTestHost.Clock"/>
    /// past the host's <see cref="AuthorizationOptions.MirrorMaximumAge"/> by one tick, and expects a
    /// denial whose <see cref="AuthorizationDecision.ProviderFailure"/> names <paramref name="provider"/>.
    /// Syncs again and expects a denial with no provider failure: the sync carried the revocation and
    /// advanced the stamp. Every check goes through the host's own evaluator in an operation scope for
    /// <paramref name="principal"/> and <paramref name="tenant"/>; only the host's clock moves. A
    /// mirrored provider runs both this and <see cref="AssertRevokedGrantDeniesNextRequestAsync"/>.</summary>
    /// <param name="host">The started host <paramref name="provider"/> is registered in, with
    /// <paramref name="permission"/> declared.</param>
    /// <param name="provider">The provider under test — the instance registered in
    /// <paramref name="host"/>.</param>
    /// <param name="principal">The principal asked about.</param>
    /// <param name="tenant">The tenant the checks are evaluated in.</param>
    /// <param name="resource">The resource the checks are scoped to, when the provider is
    /// resource-scoped.</param>
    /// <param name="permission">The permission the issuer's role confers.</param>
    /// <param name="grantAtIssuer">Grants the role at the (fake) issuer the consumer's sync reads.</param>
    /// <param name="revokeAtIssuer">Revokes it at that issuer.</param>
    /// <param name="sync">Runs the consumer's real sync against that issuer once, stamping from the
    /// host's <see cref="IClock"/>.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>A task that completes when the provider has passed.</returns>
    /// <exception cref="InvalidOperationException">Another provider granted the permission; the provider
    /// did not grant after the grant and sync; the grant survived past the maximum with no sync; or the
    /// sync after the revoke did not carry it or did not advance the stamp.</exception>
    public static async Task AssertIssuerRevocationDeniesWithinBoundAsync(
        IPlatformTestHost host,
        IMirroredPermissionProvider provider,
        Principal principal,
        TenantId tenant,
        ResourceRef? resource,
        PermissionName permission,
        Func<Task> grantAtIssuer,
        Func<Task> revokeAtIssuer,
        Func<Task> sync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(grantAtIssuer);
        ArgumentNullException.ThrowIfNull(revokeAtIssuer);
        ArgumentNullException.ThrowIfNull(sync);

        if (!host.Services.GetServices<IPermissionProvider>().Any(registered => ReferenceEquals(registered, provider)))
        {
            throw new ArgumentException(
                $"Provider '{provider.Name}' is not the instance registered in the host, so the host's evaluator never asks it.",
                nameof(provider));
        }

        var maximumAge = host.Services.GetRequiredService<PlatformOptions>().Authorization.MirrorMaximumAge;

        await grantAtIssuer().ConfigureAwait(false);
        await sync().ConfigureAwait(false);
        var granted = await EvaluateAsync(host, principal, tenant, resource, permission, cancellationToken).ConfigureAwait(false);

        if (granted.Sources.FirstOrDefault(source => source != provider.Name) is { Value: not null } other)
        {
            throw new InvalidOperationException(
                $"Provider '{other}' also granted '{permission}', so a denial after the revoke at the issuer could not be " +
                $"told apart from a grant by '{other}'. Run the check with '{provider.Name}' as the only provider that grants it.");
        }

        if (granted.Outcome != AuthorizationOutcome.Allowed || !granted.Sources.Contains(provider.Name))
        {
            throw new InvalidOperationException(
                $"Provider '{provider.Name}' did not grant '{permission}' after the grant at the issuer and a sync, so a revoke " +
                "proves nothing.");
        }

        await revokeAtIssuer().ConfigureAwait(false);
        host.Clock.Advance(maximumAge + TimeSpan.FromTicks(1));
        var stale = await EvaluateAsync(host, principal, tenant, resource, permission, cancellationToken).ConfigureAwait(false);

        if (stale.Outcome == AuthorizationOutcome.Allowed || stale.ProviderFailure?.Detail != Unavailable(provider))
        {
            throw new InvalidOperationException(
                $"Provider '{provider.Name}' was not unavailable {maximumAge:c} and one tick after the revoke at the issuer, with " +
                $"no sync in between (outcome {stale.Outcome}). The grant survived past MirrorMaximumAge with no sync: the " +
                "provider's last-synced stamp advanced without a sync.");
        }

        await sync().ConfigureAwait(false);
        var synced = await EvaluateAsync(host, principal, tenant, resource, permission, cancellationToken).ConfigureAwait(false);

        if (synced.Outcome == AuthorizationOutcome.Allowed)
        {
            throw new InvalidOperationException(
                $"Provider '{provider.Name}' still granted '{permission}' after a sync that followed the revoke at the issuer. " +
                "The sync did not carry the revocation.");
        }

        if (synced.ProviderFailure is { } failure)
        {
            throw new InvalidOperationException(
                $"Provider '{provider.Name}' was still unavailable after a sync that followed the revoke at the issuer " +
                $"({failure.Detail}). The sync did not advance the provider's last-synced stamp.");
        }
    }

    private static string Unavailable(IPermissionProvider provider) =>
        AuthorizationError.ProviderUnavailable(provider.Name).Detail;

    private static async Task<AuthorizationDecision> EvaluateAsync(
        IPlatformTestHost host,
        Principal principal,
        TenantId tenant,
        ResourceRef? resource,
        PermissionName permission,
        CancellationToken cancellationToken)
    {
        var evaluator = host.Services.GetRequiredService<IAuthorizationEvaluator>();
        using (host.Services.GetRequiredService<IOperationScopeFactory>().Begin(tenant, principal))
        {
            return await evaluator.EvaluateAsync(permission, resource, cancellationToken).ConfigureAwait(false);
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
