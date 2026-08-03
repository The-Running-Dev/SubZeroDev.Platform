using Microsoft.Extensions.DependencyInjection;

namespace SubZeroDev.Platform.Abstractions;

/// <summary>A unit of composition with declared dependencies. Modules are registered explicitly
/// into the service collection; scanning may exist but is never the only route.</summary>
public interface IPlatformModule
{
    /// <summary>The module's name, unique within the graph.</summary>
    ModuleName Name { get; }

    /// <summary>The modules this one declares a dependency on. A missing or cyclic dependency
    /// aborts startup with a named error rather than failing at first use.</summary>
    IReadOnlyCollection<ModuleName> DependsOn { get; }

    /// <summary>Registers the module's services. Called once, in topological order.</summary>
    /// <param name="services">The service collection to register into.</param>
    void Register(IServiceCollection services);
}
