using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;

namespace SubZeroDev.Platform.Persistence;

/// <summary>Wires Persistence into a host's container. A separate, explicit call rather than
/// something Hosting invokes automatically — Hosting does not reference this package, and a host
/// composed without it is a supported shape. Idempotent: safe to call once per module that needs
/// it.</summary>
public static class PlatformPersistenceExtensions
{
    /// <summary>Registers the unit of work, the ambient transaction accessor, the migration runner,
    /// and the <c>Database</c> and <c>PendingMigrations</c> readiness checks.</summary>
    /// <param name="services">The host's service collection.</param>
    /// <returns>The same collection, so calls chain.</returns>
    public static IServiceCollection AddPlatformPersistence(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<AmbientTransactionState>();
        services.TryAddSingleton<IAmbientTransactionAccessor, AmbientTransactionAccessor>();

        // One instance serves everything: a capability holds no per-transaction state, since
        // BeginAsync hands the connection and transaction back to its caller. Product code resolves
        // the same instance to format an instant or encode an identifier the way its own columns
        // must, without opening anything.
        services.TryAddSingleton<IProviderCapability>(provider =>
            ProviderCapabilityFactory.Create(provider.GetRequiredService<PlatformOptions>()));

        services.TryAddSingleton<IUnitOfWork, UnitOfWork>();
        services.TryAddSingleton<IMigrationRunner, MigrationRunner>();

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHealthCheck, DatabaseHealthCheck>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHealthCheck, PendingMigrationsHealthCheck>());

        if (!services.Any(descriptor => descriptor.ImplementationType == typeof(PersistenceStartupCheck)))
        {
            services.AddHostedService<PersistenceStartupCheck>();
        }

        return services;
    }
}
