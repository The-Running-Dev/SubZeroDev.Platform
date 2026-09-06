using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Persistence;

namespace SubZeroDev.Platform.Billing;

/// <summary>D5-S11's module: the billing administration API, its own <see cref="IEntitlementContributor"/>
/// and the three tables it owns. Depends only on the framework's own contracts — no knowledge of
/// Identity, Organizations or Licensing (<c>design/20-contract.md</c>, Public surface §10).</summary>
public sealed class BillingModule : IPlatformModule
{
    /// <inheritdoc/>
    public ModuleName Name { get; } = new("Billing");

    /// <inheritdoc/>
    public IReadOnlyCollection<ModuleName> DependsOn { get; } = [];

    /// <inheritdoc/>
    public void Register(IServiceCollection services)
    {
        services.TryAddSingleton<BillingStore>();
        services.TryAddSingleton<IBillingApi, BillingApi>();

        // Registered under the same keyed slot the Community baseline uses (Core, S7/S11) — never
        // under the plain IEntitlementContributor service type, so nothing may reach this contributor
        // by ordinary unkeyed resolution. IEntitlementEvaluator stays the only public entry.
        services.AddKeyedSingleton<IEntitlementContributor, BillingEntitlementContributor>(
            EntitlementContributorRegistration.ServiceKey);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IModuleMigrationSource, BillingMigrationSource>());
    }
}
