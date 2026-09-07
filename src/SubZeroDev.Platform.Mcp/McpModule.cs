using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using SubZeroDev.Platform.Abstractions;

namespace SubZeroDev.Platform.Mcp;

/// <summary>D5-S14's module: the frozen tool catalogue and its startup checks. A host also
/// registers one or more <see cref="IToolProducer"/> implementations of its own — a manifest
/// projection, a product-owned fixed table, or both — and may register a <see cref="McpOptions"/>
/// naming which of the tools they supply are exposed. Depends only on the framework's own
/// contracts — no knowledge of Identity, Organizations, Billing or Licensing.</summary>
public sealed class McpModule : IPlatformModule
{
    /// <inheritdoc/>
    public ModuleName Name { get; } = new("Mcp");

    /// <inheritdoc/>
    public IReadOnlyCollection<ModuleName> DependsOn { get; } = [];

    /// <inheritdoc/>
    public void Register(IServiceCollection services)
    {
        services.TryAddSingleton<McpOptions>();
        services.TryAddSingleton<ToolCatalogue>();
        services.TryAddSingleton<IToolCatalogue>(provider => provider.GetRequiredService<ToolCatalogue>());

        // Runs once, at startup, and never again — the frozen catalogue this produces is what
        // I-M1 requires. A plain IHostedService, exactly like Licensing's own startup-only
        // verification: a Platform background-work registration is a recurring tick by
        // construction, and this must not become one.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, McpStartupValidation>());
    }
}
