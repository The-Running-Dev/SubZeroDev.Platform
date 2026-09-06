using SubZeroDev.Platform.Abstractions;

namespace SubZeroDev.Platform.Organizations;

/// <summary>An organization's identity. <c>design/20-contract.md</c>, Types §7.</summary>
/// <param name="Value">The identifier.</param>
public readonly record struct OrganizationId(Guid Value)
{
    /// <summary>Mints a new identifier.</summary>
    /// <returns>The identifier.</returns>
    public static OrganizationId CreateNew() => new(Guid.NewGuid());

    /// <inheritdoc/>
    public override string ToString() => Value.ToString();
}

/// <summary>An invitation's identity. <c>design/20-contract.md</c>, Types §7.</summary>
/// <param name="Value">The identifier.</param>
public readonly record struct InvitationId(Guid Value)
{
    /// <summary>Mints a new identifier.</summary>
    /// <returns>The identifier.</returns>
    public static InvitationId CreateNew() => new(Guid.NewGuid());

    /// <inheritdoc/>
    public override string ToString() => Value.ToString();
}

/// <summary>A membership's role within its organization. Closed: there is no role-assignment table
/// (I-A9, I-O11) — the role is this enum, stored as a column on the membership row.</summary>
public enum OrganizationRole
{
    /// <summary>Created the organization. Exactly one per organization at creation, and never
    /// reassigned by this slice — richer administration is out of scope (issue #178).</summary>
    Owner,

    /// <summary>Administers membership. Granted <see cref="PlatformPermissions.AdministerOrganization"/>
    /// alongside <see cref="Owner"/> (S10.9).</summary>
    Administrator,

    /// <summary>An ordinary member. Not granted <see cref="PlatformPermissions.AdministerOrganization"/>.</summary>
    Member,
}

/// <summary>A membership's state.</summary>
public enum MembershipState
{
    /// <summary>The membership is live.</summary>
    Active,

    /// <summary>The membership was revoked. Not deleted — the row is retained so "was a member" stays
    /// answerable.</summary>
    Revoked,
}

/// <summary>An organization. Holds a tenant; it is not one — <c>design/20-contract.md</c>, Types
/// §7: "an organization holds a tenant, it is not one."</summary>
/// <param name="Id">The organization's identity.</param>
/// <param name="Tenant">The tenant minted for this organization at creation. Unique across every
/// organization (I-O2).</param>
/// <param name="Name">The organization's display name.</param>
/// <param name="Owner">The principal that created the organization.</param>
/// <param name="CreatedAt">When the organization was created.</param>
public sealed record Organization(
    OrganizationId Id,
    TenantId Tenant,
    string Name,
    PrincipalId Owner,
    DateTimeOffset CreatedAt);

/// <summary>One principal's membership in one organization. Keyed by organization and principal
/// (issuer and subject, two columns) — never by a reference to a user row (I-O6, I-I3).</summary>
/// <param name="Organization">The organization.</param>
/// <param name="Principal">The member. A framework <see cref="PrincipalId"/>, never a foreign key
/// into any directory Organizations does not own.</param>
/// <param name="Role">The member's role.</param>
/// <param name="State">Whether the membership is live.</param>
/// <param name="CreatedAt">When this membership was created (or re-activated by redeeming a fresh
/// invitation).</param>
public sealed record Membership(
    OrganizationId Organization,
    PrincipalId Principal,
    OrganizationRole Role,
    MembershipState State,
    DateTimeOffset CreatedAt);

/// <summary>An invitation to join an organization. The token itself is never stored — only its hash
/// (I-O4) — so a database read can never be replayed into a membership.</summary>
/// <param name="Id">The invitation's identity.</param>
/// <param name="Organization">The organization being invited into.</param>
/// <param name="Role">The role the redeemer will hold.</param>
/// <param name="ExpiresAt">When the invitation stops being redeemable.</param>
/// <param name="TokenHash">The stored hash of the token. Never the token.</param>
/// <param name="RedeemedBy">Who redeemed it, once redeemed.</param>
/// <param name="RedeemedAt">When it was redeemed.</param>
public sealed record Invitation(
    InvitationId Id,
    OrganizationId Organization,
    OrganizationRole Role,
    DateTimeOffset ExpiresAt,
    string TokenHash,
    PrincipalId? RedeemedBy,
    DateTimeOffset? RedeemedAt);

/// <summary>Why an organization operation did not complete. <c>design/20-contract.md</c>, Error
/// semantics §5 — exactly these four variants, each parameterless so the caller-facing answer never
/// carries a distinguishing detail (S10.5): the operator-facing distinction goes to the log, not the
/// return value.</summary>
public sealed record OrganizationError : PlatformError
{
    private OrganizationError(string code, bool isRetryable, string detail)
        : base(code)
    {
        IsRetryable = isRetryable;
        Detail = detail;
    }

    /// <summary>A generic, non-distinguishing description. Never carries which of several possible
    /// causes actually applied — that distinction is logged, not returned (S10.5).</summary>
    public string Detail { get; }

    /// <inheritdoc/>
    public override bool IsRetryable { get; }

    /// <summary>The organization does not exist, or the principal is not a member. Deliberately
    /// covers "exists but you may not see it" — existence is not confirmed to a caller who may not
    /// see it.</summary>
    /// <returns>The error.</returns>
    public static OrganizationError OrganizationNotFound() =>
        new(
            nameof(OrganizationNotFound),
            isRetryable: false,
            "The organization does not exist, or is not visible to this principal.");

    /// <summary>A member-only action by a principal whose membership is revoked (or, for an
    /// administrative action naming another principal, one whose membership is not active).</summary>
    /// <returns>The error.</returns>
    public static OrganizationError NotAMember() =>
        new(
            nameof(NotAMember),
            isRetryable: false,
            "This principal's membership in the organization is not active.");

    /// <summary>The token is expired, already redeemed, or never existed. Deliberately one answer for
    /// all three causes (S10.5) — this variant must never be split for diagnosability.</summary>
    /// <returns>The error.</returns>
    public static OrganizationError InvitationNotRedeemable() =>
        new(
            nameof(InvitationNotRedeemable),
            isRetryable: false,
            "The invitation token cannot be redeemed.");

    /// <summary>The unique tenant constraint rejected a concurrent create. Retryable: the retry mints
    /// a fresh tenant.</summary>
    /// <returns>The error.</returns>
    public static OrganizationError TenantAlreadyAssigned() =>
        new(
            nameof(TenantAlreadyAssigned),
            isRetryable: true,
            "A concurrent create already claimed the minted tenant. Retry.");
}
