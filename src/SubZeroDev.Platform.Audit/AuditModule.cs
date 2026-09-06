using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Persistence;

namespace SubZeroDev.Platform.Audit;

/// <summary>D5-S13's module: the durable audit store — one table, an <see cref="IAuditSink"/>
/// declaring <see cref="IAuditSink.IsDurable"/>, and the read API scoped by tenant, instant range,
/// actor and correlation (<c>design/20-contract.md</c>, Public surface §10). Depends only on the
/// framework's own contracts — no knowledge of Identity, Organizations, Billing or
/// Licensing.</summary>
public sealed class AuditModule : IPlatformModule
{
    /// <inheritdoc/>
    public ModuleName Name { get; } = new("Audit");

    /// <inheritdoc/>
    public IReadOnlyCollection<ModuleName> DependsOn { get; } = [];

    /// <summary>Registers Audit. Installing this module is what lets an <c>Operated</c> host satisfy
    /// I-C2: without it, the default log sink is the only one registered, and it never claims
    /// durability (S8.6, S13.6).</summary>
    /// <param name="services">The host's service collection.</param>
    public void Register(IServiceCollection services)
    {
        services.TryAddSingleton<AuditStore>();
        services.TryAddSingleton<IAuditReadApi, AuditReadApi>();

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditSink, DurableAuditSink>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IModuleMigrationSource, AuditMigrationSource>());
    }
}
