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

    /// <summary>Does the module's start-up work. Hosting calls it once per host start, in topological
    /// order and one module at a time, after the registries have frozen and before the first background
    /// tick or request. Migrate mode never calls it. A throw fails the host with
    /// <c>ModuleInitialization</c>. Every instance of every role runs it, so it must not assume it is the
    /// only process.</summary>
    /// <param name="services">The provider of a fresh service scope, disposed when the call returns. No
    /// operation scope is open.</param>
    /// <param name="cancellationToken">Cancels start-up.</param>
    /// <returns>A task that completes when the work is done.</returns>
    Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Does the module's shut-down work. Hosting calls it exactly once for a module whose
    /// <see cref="InitializeAsync"/> completed, in reverse order, after background work and the listener
    /// have stopped — or on disposal, when a later start-up step failed. Never called for a module whose
    /// <see cref="InitializeAsync"/> threw or never ran. A throw is logged and the rest still run.</summary>
    /// <param name="services">On stop, the provider of a fresh service scope, disposed when the call
    /// returns. On disposal after a failed start-up the container is already being torn down and resolves
    /// nothing, so a module that must release something on that path holds it itself.</param>
    /// <param name="cancellationToken">Bounded by the host's graceful drain window.</param>
    /// <returns>A task that completes when the work is done.</returns>
    Task ShutdownAsync(IServiceProvider services, CancellationToken cancellationToken) => Task.CompletedTask;
}
