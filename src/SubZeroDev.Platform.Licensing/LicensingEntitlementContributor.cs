using SubZeroDev.Platform.Abstractions;

namespace SubZeroDev.Platform.Licensing;

/// <summary>Licensing's <see cref="IEntitlementContributor"/>. Grants a feature when the claims in
/// force name it and the effective instant has not passed the grace end.</summary>
/// <remarks>
/// <para><b>Expiry starts grace; it does not revoke.</b> The comparison is against
/// <see cref="LicenceClaims.GraceEndsAt"/>, not <see cref="LicenceClaims.ExpiresAt"/> — a licence one
/// day past expiry still grants, and only once the clock passes the grace end does this contributor
/// stop granting (S12.9). Both instants were computed at verification and stored, so a deployment
/// that errors on every verification cannot renew its own grace (I-L2).</para>
/// <para><b>No store access per call.</b> Unlike <c>BillingEntitlementContributor</c>, which opens its
/// own read-only unit of work, this reads only the in-memory state the last verification published —
/// which is what makes Licensing's answers unaffected by the store being down, and what makes a
/// thousand requests against one expired licence write exactly one audit record rather than a thousand
/// (S12.3, S12.14). It follows that this never returns
/// <see cref="EntitlementError.ContributorUnavailable"/>: there is nothing here that can be
/// unavailable.</para>
/// <para><b>Tenant-independent.</b> A licence is the installation's, not a tenant's — the installation
/// is the database, not the machine — so the tenant argument is deliberately unused.</para>
/// </remarks>
internal sealed class LicensingEntitlementContributor(ILicenceState state, IClock clock) : IEntitlementContributor
{
    public EntitlementContributorName Name { get; } = new("Platform.Licensing");

    public Task<Result<bool, EntitlementError>> GrantsAsync(
        FeatureName feature, TenantId tenant, CancellationToken cancellationToken)
    {
        var claims = state.Current;
        if (claims is null || !claims.Features.Contains(feature))
        {
            // No claims at all is the Community tier, which grants nothing here — the Community
            // baseline's own features come from Core's baseline contributor (S12.5).
            return Task.FromResult(Result<bool, EntitlementError>.Success(false));
        }

        return Task.FromResult(Result<bool, EntitlementError>.Success(!HasLapsed(claims)));
    }

    /// <summary>Whether the claims have run out. A clock reading earlier than the stored verification
    /// instant is evaluated <b>at</b> the stored verification instant, which neither extends grace nor
    /// expires early — the two ways to be wrong (S12.7). D5 detects and logs a backwards clock; it does
    /// not claim to resist an operator who controls the machine.</summary>
    private bool HasLapsed(LicenceClaims claims)
    {
        var now = clock.UtcNow;
        var effective = now < claims.VerifiedAt ? claims.VerifiedAt : now;

        // A licence naming no expiry never lapses, and one naming an expiry lapses at its grace end
        // rather than at its expiry.
        var lapsesAt = claims.GraceEndsAt ?? claims.ExpiresAt;
        return lapsesAt is { } deadline && effective >= deadline;
    }
}
