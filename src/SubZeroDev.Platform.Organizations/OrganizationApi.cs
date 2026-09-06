using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Persistence;

namespace SubZeroDev.Platform.Organizations;

/// <summary>An invitation token, returned exactly once at mint (S10.3). Nothing reads it back — the
/// store holds only <see cref="Invitation.TokenHash"/>.</summary>
/// <param name="Id">The invitation's identity.</param>
/// <param name="Token">The bearer token. Present only in this return value; never persisted.</param>
public sealed record InvitationToken(InvitationId Id, string Token);

/// <summary>The organization API: create, invite, redeem, revoke, switch active organization, list an
/// organization's memberships — <c>design/20-contract.md</c>, Public surface §10, Modules,
/// "Organizations".</summary>
public interface IOrganizationApi
{
    /// <summary>Creates an organization: mints a tenant, writes the organization and the owner's
    /// membership, and writes the audit row, all in one transaction (S10.1, I-O1). The owner is the
    /// ambient principal.</summary>
    Task<Result<Organization, OrganizationError>> CreateOrganizationAsync(
        string name, CancellationToken cancellationToken);

    /// <summary>Mints an invitation. The caller must be an active member of <paramref name="organization"/>.
    /// Returns the token exactly once (S10.3) — the stored row holds only its hash.</summary>
    Task<Result<InvitationToken, OrganizationError>> InviteAsync(
        OrganizationId organization, OrganizationRole role, DateTimeOffset expiresAt, CancellationToken cancellationToken);

    /// <summary>Redeems a token: creates exactly one active membership (S10.4). An expired, an
    /// already-redeemed and a never-existed token all answer identically (S10.5) — the ambient
    /// principal becomes the member.</summary>
    Task<Result<Membership, OrganizationError>> RedeemInvitationAsync(string token, CancellationToken cancellationToken);

    /// <summary>Revokes one principal's membership. The caller must be an active member of
    /// <paramref name="organization"/> — a non-member is told not found (S10.7).</summary>
    Task<Result<OrganizationError>> RevokeMembershipAsync(
        OrganizationId organization, PrincipalId principal, CancellationToken cancellationToken);

    /// <summary>Validates that the ambient principal actively belongs to <paramref name="organization"/>
    /// and, if so, answers its tenant (S10.6); switching to one it does not belong to answers
    /// <see cref="OrganizationError.OrganizationNotFound"/>, never forbidden. Deliberately does not
    /// itself write <see cref="ActiveOrganizationState.Current"/> — an <see cref="AsyncLocal{T}"/>
    /// mutation made inside an awaited async call is invisible to that call's own caller once it
    /// returns (a mutation only flows into code nested <em>underneath</em> the point that set it,
    /// never back out to the frame that awaited it). The caller assigns the returned tenant to
    /// <see cref="ActiveOrganizationState.Current"/> itself, synchronously, immediately after
    /// awaiting this call and before any of the tenant-dependent work that must observe it — the
    /// same reason <see cref="IOperationScopeFactory.Begin(TenantId, Principal, CultureTag)"/> is a
    /// synchronous method rather than an asynchronous one.</summary>
    Task<Result<TenantId, OrganizationError>> SwitchActiveOrganizationAsync(
        OrganizationId organization, CancellationToken cancellationToken);

    /// <summary>Lists an organization's memberships. The caller must be an active member — a
    /// non-member is told not found (S10.7).</summary>
    Task<Result<IReadOnlyList<Membership>, OrganizationError>> ListMembershipsAsync(
        OrganizationId organization, CancellationToken cancellationToken);
}

/// <summary>Platform's own audited action names for Organizations. Membership and ownership changes
/// are <see cref="AuditClass.Required"/> (S10.12, I-U2).</summary>
public static class OrganizationsAuditActions
{
    /// <summary>An organization was created, with its owner's membership.</summary>
    public static AuditAction OrganizationCreated { get; } = new("platform.organizations.created");

    /// <summary>An invitation was redeemed into a membership.</summary>
    public static AuditAction MembershipCreated { get; } = new("platform.organizations.membership-created");

    /// <summary>A membership was revoked.</summary>
    public static AuditAction MembershipRevoked { get; } = new("platform.organizations.membership-revoked");
}

/// <inheritdoc cref="IOrganizationApi"/>
internal sealed class OrganizationApi(
    IUnitOfWork unitOfWork,
    OrganizationStore store,
    IOrganizationTenantMinter tenantMinter,
    ICurrentPrincipal currentPrincipal,
    IAuditWriter auditWriter,
    IClock clock,
    ILogger<OrganizationApi> logger) : IOrganizationApi
{
    public async Task<Result<Organization, OrganizationError>> CreateOrganizationAsync(
        string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var owner = currentPrincipal.Current.Id;
        var now = clock.UtcNow;
        var tenantConflict = false;

        var result = await unitOfWork.ExecuteAsync(
            TransactionIntent.Write,
            async token =>
            {
                var organization = new Organization(OrganizationId.CreateNew(), tenantMinter.Mint(), name, owner, now);

                try
                {
                    await store.InsertOrganizationAsync(organization, token).ConfigureAwait(false);
                }
                catch (Exception exception) when (OrganizationStore.IsUniqueViolation(exception))
                {
                    // Rethrown deliberately (see OrganizationStore.InsertOrganizationAsync's remarks):
                    // this flag is inspected only after ExecuteAsync has rolled the transaction back
                    // through its own normal path.
                    tenantConflict = true;
                    throw;
                }

                await store.UpsertMembershipAsync(
                    new Membership(organization.Id, owner, OrganizationRole.Owner, MembershipState.Active, now),
                    token).ConfigureAwait(false);

                await auditWriter.WriteAsync(
                    OrganizationsAuditActions.OrganizationCreated,
                    new ResourceRef("Organization", organization.Id.ToString()),
                    AuditOutcome.Allowed,
                    AuditClass.Required,
                    token).ConfigureAwait(false);

                return organization;
            },
            cancellationToken).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            return Result<Organization, OrganizationError>.Success(result.Value);
        }

        if (tenantConflict)
        {
            logger.LogInformation("Organization create lost the race for a minted tenant; the caller may retry.");
            return Result<Organization, OrganizationError>.Failure(OrganizationError.TenantAlreadyAssigned());
        }

        // No OrganizationError variant models a bare infrastructure failure (design/20-contract.md
        // fixes exactly four); the safest fallback that still uses only those four is the one
        // retryable variant this operation can produce.
        logger.LogWarning("Organization create failed: {Code}.", result.Error.Code);
        return Result<Organization, OrganizationError>.Failure(OrganizationError.TenantAlreadyAssigned());
    }

    public async Task<Result<InvitationToken, OrganizationError>> InviteAsync(
        OrganizationId organization, OrganizationRole role, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        var caller = currentPrincipal.Current.Id;
        var token = MintToken();
        var tokenHash = Hash(token);
        var invitation = new Invitation(InvitationId.CreateNew(), organization, role, expiresAt, tokenHash, null, null);

        var result = await unitOfWork.ExecuteAsync(
            TransactionIntent.Write,
            async ct =>
            {
                var membership = await store.FindMembershipAsync(organization, caller, ct).ConfigureAwait(false);
                if (membership is not { State: MembershipState.Active })
                {
                    return Result<InvitationToken, OrganizationError>.Failure(OrganizationError.OrganizationNotFound());
                }

                await store.InsertInvitationAsync(invitation, ct).ConfigureAwait(false);
                return Result<InvitationToken, OrganizationError>.Success(new InvitationToken(invitation.Id, token));
            },
            cancellationToken).ConfigureAwait(false);

        return Unwrap(result);
    }

    public async Task<Result<Membership, OrganizationError>> RedeemInvitationAsync(
        string token, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        var redeemer = currentPrincipal.Current.Id;
        var tokenHash = Hash(token);
        var now = clock.UtcNow;

        var result = await unitOfWork.ExecuteAsync(
            TransactionIntent.Write,
            async ct =>
            {
                var redeemed = await store.TryRedeemInvitationAsync(tokenHash, redeemer, now, ct).ConfigureAwait(false);
                if (redeemed is not { } grant)
                {
                    // S10.5: the caller-facing answer never distinguishes; the cause is logged only.
                    var cause = await store.DiagnoseAsync(tokenHash, now, ct).ConfigureAwait(false);
                    logger.LogInformation("Invitation redemption refused ({Cause}).", cause);
                    return Result<Membership, OrganizationError>.Failure(OrganizationError.InvitationNotRedeemable());
                }

                var membership = new Membership(grant.Organization, redeemer, grant.Role, MembershipState.Active, now);
                await store.UpsertMembershipAsync(membership, ct).ConfigureAwait(false);

                await auditWriter.WriteAsync(
                    OrganizationsAuditActions.MembershipCreated,
                    new ResourceRef("Organization", grant.Organization.ToString()),
                    AuditOutcome.Allowed,
                    AuditClass.Required,
                    ct).ConfigureAwait(false);

                return Result<Membership, OrganizationError>.Success(membership);
            },
            cancellationToken).ConfigureAwait(false);

        return Unwrap(result);
    }

    public async Task<Result<OrganizationError>> RevokeMembershipAsync(
        OrganizationId organization, PrincipalId principal, CancellationToken cancellationToken)
    {
        var caller = currentPrincipal.Current.Id;

        var result = await unitOfWork.ExecuteAsync(
            TransactionIntent.Write,
            async ct =>
            {
                var callerMembership = await store.FindMembershipAsync(organization, caller, ct).ConfigureAwait(false);
                if (callerMembership is not { State: MembershipState.Active })
                {
                    return Result<OrganizationError>.Failure(OrganizationError.OrganizationNotFound());
                }

                var changed = await store.RevokeMembershipAsync(organization, principal, ct).ConfigureAwait(false);
                if (!changed)
                {
                    return Result<OrganizationError>.Failure(OrganizationError.NotAMember());
                }

                await auditWriter.WriteAsync(
                    OrganizationsAuditActions.MembershipRevoked,
                    new ResourceRef("Organization", organization.ToString()),
                    AuditOutcome.Allowed,
                    AuditClass.Required,
                    ct).ConfigureAwait(false);

                return Result<OrganizationError>.Success();
            },
            cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? result.Value
            : Result<OrganizationError>.Failure(OrganizationError.OrganizationNotFound());
    }

    public async Task<Result<TenantId, OrganizationError>> SwitchActiveOrganizationAsync(
        OrganizationId organization, CancellationToken cancellationToken)
    {
        var caller = currentPrincipal.Current.Id;

        var result = await unitOfWork.ExecuteAsync(
            TransactionIntent.ReadOnly,
            async ct =>
            {
                var tenant = await store.FindTenantForActiveMemberAsync(organization, caller, ct).ConfigureAwait(false);
                return tenant is { } value
                    ? Result<TenantId, OrganizationError>.Success(value)
                    : Result<TenantId, OrganizationError>.Failure(OrganizationError.OrganizationNotFound());
            },
            cancellationToken).ConfigureAwait(false);

        // Deliberately does not assign ActiveOrganizationState.Current here — see this member's
        // interface doc comment. The caller applies the result itself, synchronously.
        return Unwrap(result);
    }

    public async Task<Result<IReadOnlyList<Membership>, OrganizationError>> ListMembershipsAsync(
        OrganizationId organization, CancellationToken cancellationToken)
    {
        var caller = currentPrincipal.Current.Id;

        var result = await unitOfWork.ExecuteAsync(
            TransactionIntent.ReadOnly,
            async ct =>
            {
                var callerMembership = await store.FindMembershipAsync(organization, caller, ct).ConfigureAwait(false);
                if (callerMembership is not { State: MembershipState.Active })
                {
                    return Result<IReadOnlyList<Membership>, OrganizationError>.Failure(OrganizationError.OrganizationNotFound());
                }

                var memberships = await store.ListMembershipsAsync(organization, ct).ConfigureAwait(false);
                return Result<IReadOnlyList<Membership>, OrganizationError>.Success(memberships);
            },
            cancellationToken).ConfigureAwait(false);

        return Unwrap(result);
    }

    private static Result<T, OrganizationError> Unwrap<T>(Result<Result<T, OrganizationError>, TransactionError> outer) =>
        outer.IsSuccess
            ? outer.Value
            // As in CreateOrganizationAsync: no variant models a bare infrastructure failure, so an
            // unexpected transaction failure fails closed as "not found" — existence is never
            // confirmed to a caller Organizations could not positively identify as a member.
            : Result<T, OrganizationError>.Failure(OrganizationError.OrganizationNotFound());

    private static string MintToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
