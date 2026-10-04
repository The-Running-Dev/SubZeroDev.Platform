using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Hosting;

namespace SubZeroDev.Platform.Identity;

/// <summary>D5-S9's module: authentication providers and the mapping from an authentication result
/// to a principal. Owns no entity type, no context and no migration — Identity is not a directory
/// (<c>20-contract.md</c> §1), and a consumer never registers this module without also registering
/// at least one <see cref="IAuthenticationProvider"/> naming an issuer it trusts, which this type
/// deliberately does not do on the consumer's behalf.</summary>
public sealed class IdentityModule : IPlatformModule
{
    /// <inheritdoc/>
    public ModuleName Name { get; } = new("Identity");

    /// <inheritdoc/>
    public IReadOnlyCollection<ModuleName> DependsOn { get; } = [];

    /// <inheritdoc/>
    public void Register(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Modules are composed before the container exists and Register is handed no configuration,
        // so the host's own instance is read from the collection, where AddPlatformHost put it. A
        // collection that carries none has no generic-path sections to read: no provider, no failure.
        var configuration = services
            .LastOrDefault(descriptor => descriptor.ServiceType == typeof(IConfiguration))
            ?.ImplementationInstance as IConfiguration;
        if (configuration is null)
        {
            return;
        }

        // Every section is validated before any provider is registered or any key is used, so a
        // defect leaves nothing behind (I-I10, settings half).
        var settings = BearerSettings.ReadAll(configuration);
        if (!settings.IsSuccess)
        {
            throw new PlatformStartupException(HostStartupError.Configuration(settings.Error));
        }

        var discovered = new List<(BearerSettings Settings, KeySetCache Cache)>();
        foreach (var provider in settings.Value)
        {
            var cache = provider.Discovery is null ? null : new KeySetCache();
            services.AddSingleton<IAuthenticationProvider>(
                new ConfiguredBearerAuthenticationProvider(provider, cache: cache));
            services.AddSingleton<IHealthCheck>(new KeySetHealthCheck(provider.Name, cache));

            if (cache is not null)
            {
                discovered.Add((provider, cache));
                services.AddSingleton<IBackgroundWork>(sp => new KeyRefreshWork(
                    provider, cache, sp.GetRequiredService<DiscoveryTransport>()));
            }
        }

        if (discovered.Count > 0)
        {
            services.TryAddSingleton(DiscoveryTransport.Default);
            services.AddSingleton<IHostedService>(sp =>
                new KeyDiscoveryStartup(discovered, sp.GetRequiredService<DiscoveryTransport>()));
        }
    }
}
