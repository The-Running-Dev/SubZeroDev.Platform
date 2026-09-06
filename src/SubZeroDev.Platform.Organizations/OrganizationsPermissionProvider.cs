using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Persistence;

namespace SubZeroDev.Platform.Organizations;

/// <summary>Organizations' own <see cref="IPermissionProvider"/>. Grants
/// <see cref="PlatformPermissions.AdministerOrganization"/> to <see cref="OrganizationRole.Owner"/>
/// and <see cref="OrganizationRole.Administrator"/>, never to <see cref="OrganizationRole.Member"/>
/// (S10.9). <see cref="PlatformPermissions.AdministerOrganization"/> is declared by Core's own
/// <c>PlatformPermissionCatalog</c> — Organizations declares no <see cref="IPermissionCatalog"/> of
/// its own, since a second declaration of the same name would collide (I-A4).</summary>
/// <remarks>The check is resource-scoped: it answers only when the resource passed to
/// <see cref="GrantsAsync"/> names one organization (<c>Type == "Organization"</c>), the same way any
/// other resource-scoped permission
/// check in this codebase must be — the evaluator's own pipeline check is never resource-scoped
/// (<c>design/20-contract.md</c>, Public surface §11), so an organization-administration call site
/// makes its own explicit, resource-scoped <c>IAuthorizationEvaluator.EvaluateAsync</c> call, exactly
/// as that section describes.</remarks>
internal sealed class OrganizationsPermissionProvider(IUnitOfWork unitOfWork, OrganizationStore store) : IPermissionProvider
{
    public PermissionProviderName Name { get; } = new("Platform.Organizations");

    public async Task<Result<IReadOnlySet<PermissionName>, AuthorizationError>> GrantsAsync(
        Principal principal, TenantId tenant, ResourceRef? resource, CancellationToken cancellationToken)
    {
        if (resource is not { Type: "Organization" } value || !Guid.TryParse(value.Id, out var organizationId))
        {
            return Result<IReadOnlySet<PermissionName>, AuthorizationError>.Success(new HashSet<PermissionName>());
        }

        var organization = new OrganizationId(organizationId);

        var result = await unitOfWork.ExecuteAsync(
            TransactionIntent.ReadOnly,
            ct => store.FindMembershipAsync(organization, principal.Id, ct),
            cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            return Result<IReadOnlySet<PermissionName>, AuthorizationError>.Failure(
                AuthorizationError.ProviderUnavailable(Name));
        }

        IReadOnlySet<PermissionName> granted = result.Value is { State: MembershipState.Active } membership
            && membership.Role is OrganizationRole.Owner or OrganizationRole.Administrator
                ? new HashSet<PermissionName> { PlatformPermissions.AdministerOrganization }
                : new HashSet<PermissionName>();

        return Result<IReadOnlySet<PermissionName>, AuthorizationError>.Success(granted);
    }
}
