using Microsoft.Extensions.DependencyInjection;
using SubZeroDev.Platform.Abstractions;

namespace SubZeroDev.Platform.Sample.Web;

/// <summary>A module with no dependencies.</summary>
public sealed class CatalogueModule : IPlatformModule
{
    /// <inheritdoc/>
    public ModuleName Name { get; } = new("Catalogue");

    /// <inheritdoc/>
    public IReadOnlyCollection<ModuleName> DependsOn { get; } = [];

    /// <inheritdoc/>
    public void Register(IServiceCollection services)
    {
    }
}

/// <summary>A module that depends on another, so the sample exercises ordering rather than
/// asserting it only in a unit test.</summary>
public sealed class OrdersModule : IPlatformModule
{
    /// <inheritdoc/>
    public ModuleName Name { get; } = new("Orders");

    /// <inheritdoc/>
    public IReadOnlyCollection<ModuleName> DependsOn { get; } = [new ModuleName("Catalogue")];

    /// <inheritdoc/>
    public void Register(IServiceCollection services)
    {
    }
}
