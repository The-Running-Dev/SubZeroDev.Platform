using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Persistence;

namespace SubZeroDev.Platform.Organizations;

/// <summary>D5-S10's module: the organization API, its own <see cref="ITenantResolver"/> and
/// <see cref="IPermissionProvider"/>, and the three tables it owns. Depends on the framework's
/// principal and tenant contracts and has no knowledge of Identity or Billing
/// (<c>design/20-contract.md</c>, Public surface §10).</summary>
public sealed class OrganizationsModule : IPlatformModule
{
    /// <inheritdoc/>
    public ModuleName Name { get; } = new("Organizations");

    /// <inheritdoc/>
    public IReadOnlyCollection<ModuleName> DependsOn { get; } = [];

    /// <inheritdoc/>
    public void Register(IServiceCollection services)
    {
        services.TryAddSingleton<IOrganizationTenantMinter, GuidOrganizationTenantMinter>();
        services.TryAddSingleton<ActiveOrganizationState>();
        services.TryAddSingleton<OrganizationStore>();
        services.TryAddSingleton<IOrganizationApi, OrganizationApi>();

        services.TryAddEnumerable(ServiceDescriptor.Singleton<ITenantResolver, OrganizationsTenantResolver>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPermissionProvider, OrganizationsPermissionProvider>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IModuleMigrationSource, OrganizationsMigrationSource>());
    }
}
