using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Persistence;

// Declared in the Hosting namespace though it lives in this package — the same idiom
// Microsoft.EntityFrameworkCore uses for `AddDbContext`, which extends
// Microsoft.Extensions.DependencyInjection.IServiceCollection from a different assembly than the
// one that declares it. Hosting.csproj keeps zero reference to Persistence; a product calls
// `builder.RunPlatformMigrateModeAsync(...)` exactly as the contract states it, and gets this
// method because it references both packages. See design/d3/90-decisions.md, 2026-08-03.
namespace SubZeroDev.Platform.Hosting;

/// <summary>Migrate mode: the one-shot command that applies every registered module's pending
/// migrations, then runs every registered seeder. Not a third host role — it never serves HTTP or
/// probes.</summary>
public static class PlatformMigrationExtensions
{
    /// <summary>Binds settings, composes modules in dependency order, applies every pending
    /// migration under the provider-native migration lock, and on success runs every
    /// <see cref="ISeeder"/>, grouped by module in that same order.</summary>
    /// <param name="builder">The host builder. Modules must already be registered on it, the same
    /// as before <c>AddPlatformWebHost</c> or <c>AddPlatformWorkerHost</c>.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>0 on success; 2 when another process holds the migration lock, in which case no
    /// seeder runs; 1 otherwise. Never throws for an ordinary migration or seeding failure — the exit
    /// status is the reporting channel for a one-shot command.</returns>
    public static async Task<int> RunPlatformMigrateModeAsync(
        this IHostApplicationBuilder builder,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Migrate mode is not a host role, so the role passed here is never read by anything this
        // path exercises — it exists only because PlatformOptions.Role has no third value to give it.
        var bound = PlatformOptionsBinder.Bind(builder.Configuration, builder.Environment.EnvironmentName, HostRole.Web);
        if (!bound.IsSuccess)
        {
            Console.Error.WriteLine($"{bound.Error.Code}: {bound.Error.Detail}");
            return 1;
        }

        builder.Services.AddSingleton(bound.Value);

        // The ambient defaults a seeder needs to open a scope and a unit of work, and nothing else
        // Core composes: no registry, worker or listener runs here.
        CoreDefaults.AddAmbientDefaults(builder.Services);
        builder.Services.AddPlatformPersistence();

        var modules = new List<IPlatformModule>();
        foreach (var descriptor in builder.Services
                     .Where(descriptor => descriptor.ServiceType == typeof(IPlatformModule))
                     .ToList())
        {
            try
            {
                modules.Add(Instantiate(descriptor));
            }
            catch (MissingMethodException)
            {
                // Mirrors the real host's own registration failure — a named, actionable message
                // rather than an unhandled activation stack trace, on the exit-code channel this
                // one-shot command reports through.
                Console.Error.WriteLine(
                    $"Registration: Module '{descriptor.ImplementationType?.FullName}' must have a "
                    + "public parameterless constructor: modules are composed before the container "
                    + "exists, so nothing can be injected into one.");
                return 1;
            }
            catch (InvalidOperationException exception)
            {
                Console.Error.WriteLine($"Registration: {exception.Message}");
                return 1;
            }
        }

        // Registered in the order the real host resolves, so a seeder's module order is the order
        // its dependencies were composed in, and a graph the host would refuse is refused here too.
        var resolved = new ModuleRegistry().Resolve(modules);
        if (!resolved.IsSuccess)
        {
            Console.Error.WriteLine($"{resolved.Error.Code}: {resolved.Error.Detail}");
            return 1;
        }

        foreach (var module in resolved.Value)
        {
            module.Module.Register(builder.Services);
        }

        await using var provider = builder.Services.BuildServiceProvider();
        var runner = provider.GetRequiredService<IMigrationRunner>();

        var applied = await runner.ApplyAsync(cancellationToken).ConfigureAwait(false);
        if (!applied.IsSuccess)
        {
            Console.Error.WriteLine($"{applied.Error.Code}: {applied.Error.Detail}");
            return applied.Error.Code == nameof(MigrationError.Locked) ? 2 : 1;
        }

        return await SeedAsync(provider, resolved.Value, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> SeedAsync(
        IServiceProvider provider,
        IReadOnlyList<ModuleDescriptor> order,
        CancellationToken cancellationToken)
    {
        await using var scope = provider.CreateAsyncScope();

        var rank = new Dictionary<ModuleName, int>();
        for (var index = 0; index < order.Count; index++)
        {
            rank[order[index].Name] = index;
        }

        IEnumerable<ISeeder> registered;
        try
        {
            registered = scope.ServiceProvider.GetServices<ISeeder>().ToList();
        }
        catch (InvalidOperationException exception)
        {
            // A seeder the container cannot construct is a registration fault, reported on the same
            // channel as a module that cannot be constructed rather than as an unhandled throw.
            Console.Error.WriteLine($"Registration: {exception.Message}");
            return 1;
        }

        // OrderBy is stable, so within one module the seeders keep their registration order. A module
        // no composed module names ranks after every composed one, then by name.
        var seeders = registered
            .OrderBy(seeder => rank.TryGetValue(seeder.Module, out var index) ? index : order.Count)
            .ThenBy(seeder => rank.ContainsKey(seeder.Module) ? string.Empty : seeder.Module.Value, StringComparer.Ordinal)
            .ToList();

        foreach (var seeder in seeders)
        {
            try
            {
                await seeder.SeedAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException
                                              || !cancellationToken.IsCancellationRequested)
            {
                var failed = MigrationError.SeedFailed(
                    seeder.Module,
                    seeder.Name,
                    $"{exception.GetType().FullName}: {exception.Message}");
                Console.Error.WriteLine($"{failed.Code}: {failed.Detail}");
                return 1;
            }
        }

        return 0;
    }

    private static IPlatformModule Instantiate(ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationInstance is IPlatformModule instance)
        {
            return instance;
        }

        if (descriptor.ImplementationType is { } type)
        {
            return (IPlatformModule)Activator.CreateInstance(type)!;
        }

        throw new InvalidOperationException(
            "A module registered by factory cannot be composed for migrate mode. Register the type, or an instance.");
    }
}
