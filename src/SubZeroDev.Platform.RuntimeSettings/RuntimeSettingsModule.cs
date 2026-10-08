using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Persistence;

namespace SubZeroDev.Platform.RuntimeSettings;

/// <summary>#58's module: settings a running system changes, layered global, tenant and user, and read
/// through every time. Holds the catalogue, the reader, the writer, the catalogue of the writer's two
/// permissions and the one table it owns, and registers no permission provider and no entitlement
/// contributor (I-ST6; <c>design/20-contract.md</c>, Public
/// surface §18). A host composes it with
/// <c>services.AddSingleton&lt;IPlatformModule, RuntimeSettingsModule&gt;()</c> and each product
/// catalogue with <c>services.AddSingleton&lt;ISettingCatalog, TCatalog&gt;()</c>.</summary>
public sealed class RuntimeSettingsModule : IPlatformModule
{
    /// <inheritdoc/>
    public ModuleName Name { get; } = new("RuntimeSettings");

    /// <inheritdoc/>
    public IReadOnlyCollection<ModuleName> DependsOn { get; } = [];

    /// <inheritdoc/>
    public void Register(IServiceCollection services)
    {
        services.TryAddSingleton<SettingCatalogue>();
        services.TryAddSingleton<SettingStore>();
        services.TryAddSingleton<ISettingReader, SettingReader>();
        services.TryAddSingleton<ISettingWriter, SettingWriter>();

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPermissionCatalog, RuntimeSettingsPermissionCatalog>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IStartupRegistration, SettingCatalogueStartup>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IModuleMigrationSource, RuntimeSettingsMigrationSource>());
    }
}
