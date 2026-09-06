using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Persistence;

namespace SubZeroDev.Platform.Licensing;

/// <summary>D5-S12's module: licence verification, its own <see cref="IEntitlementContributor"/>, the
/// revocation extension point, and the one verified-licence table. Depends only on the framework's own
/// contracts — no knowledge of Identity, Organizations or Billing
/// (<c>design/20-contract.md</c>, Public surface §10).</summary>
/// <remarks><b>Registers no health check.</b> Readiness is deliberately not gated on licence
/// verification: a host whose document is unreadable must start, report ready, and serve (I-L9,
/// S12.12), so there is nothing here that a licensing problem could turn unhealthy.</remarks>
public sealed class LicensingModule : IPlatformModule
{
    /// <inheritdoc/>
    public ModuleName Name { get; } = new("Licensing");

    /// <inheritdoc/>
    public IReadOnlyCollection<ModuleName> DependsOn { get; } = [];

    /// <summary>Registers Licensing. The host must also register a <see cref="LicensingOptions"/>
    /// carrying its accepted signing keys — Platform compiles none in (I-L7, S12.11) — and may
    /// register an <see cref="IRevocationCheck"/>, which this slice never calls (I-L5, S12.13).</summary>
    /// <param name="services">The host's service collection.</param>
    public void Register(IServiceCollection services)
    {
        services.TryAddSingleton<LicensingStore>();
        services.TryAddSingleton<LicenceStateHolder>();
        services.TryAddSingleton<ILicenceState>(provider => provider.GetRequiredService<LicenceStateHolder>());
        services.TryAddSingleton<ILicenceVerifier, LicenceVerifier>();

        // Registered under the same keyed slot the Community baseline and Billing use — never under
        // the plain IEntitlementContributor service type, so nothing may reach this contributor by
        // ordinary unkeyed resolution. IEntitlementEvaluator stays the only public entry.
        services.AddKeyedSingleton<IEntitlementContributor, LicensingEntitlementContributor>(
            EntitlementContributorRegistration.ServiceKey);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IModuleMigrationSource, LicensingMigrationSource>());

        // Verification runs once, at startup, and never on a timer: design/10-design.md's startup
        // sequence (step 6) and its Background work section between them settle that. A plain
        // IHostedService rather than a Platform background-work registration, because a registration
        // is a recurring tick by construction and this must not become one.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, LicenceStartupVerification>());
    }
}
