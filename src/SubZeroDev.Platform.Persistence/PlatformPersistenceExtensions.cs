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
    /// the outbox writer and store, the event handler registry, host registration and its heartbeat,
    /// and the <c>Database</c>, <c>PendingMigrations</c>, <c>PeerHost</c> and
    /// <c>SettingsFingerprint</c> readiness checks.</summary>
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

        services.TryAddSingleton<IOutboxStore, OutboxStore>();

        // A factory registration rather than TryAddSingleton<IOutboxWriter, OutboxWriter>(): Testing
        // decorates this to feed IEventCapture, and a factory is a delegate a different assembly can
        // invoke without needing compile-time access to the internal implementation type a plain
        // TImplementation registration would require reflecting into.
        services.TryAddSingleton<IOutboxWriter>(provider => new OutboxWriter(
            provider.GetRequiredService<IAmbientTransactionAccessor>(),
            provider.GetRequiredService<IOperationScopeAccessor>(),
            provider.GetRequiredService<IEventHandlerRegistry>(),
            provider.GetRequiredService<IClock>()));

        services.TryAddSingleton<IEventHandlerRegistry, EventHandlerRegistry>();

        services.TryAddSingleton<IUnitOfWork, UnitOfWork>();
        services.TryAddSingleton<IMigrationRunner, MigrationRunner>();

        // Fingerprinting is a Core concept usable without Persistence, and Hosting's own defaults
        // register it too — TryAdd here means whichever registers first wins, and either is the
        // same implementation, so Persistence does not silently depend on Hosting's call order.
        services.TryAddSingleton<ISettingsFingerprint, SettingsFingerprint>();

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IModuleMigrationSource, PlatformMigrationSource>());
        services.TryAddSingleton<IHostRegistrationStore, HostRegistrationStore>();

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHealthCheck, DatabaseHealthCheck>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHealthCheck, PendingMigrationsHealthCheck>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHealthCheck, PeerHostHealthCheck>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHealthCheck, SettingsFingerprintHealthCheck>());

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IBackgroundWork, HostRegistrationHeartbeat>());

        if (!services.Any(descriptor => descriptor.ImplementationType == typeof(PersistenceStartupCheck)))
        {
            services.AddHostedService<PersistenceStartupCheck>();
        }

        if (!services.Any(descriptor => descriptor.ImplementationType == typeof(EventHandlerRegistryStartup)))
        {
            services.AddHostedService<EventHandlerRegistryStartup>();
        }

        if (!services.Any(descriptor => descriptor.ImplementationType == typeof(HostRegistrationShutdownCleanup)))
        {
            services.AddHostedService<HostRegistrationShutdownCleanup>();
        }

        return services;
    }
}
