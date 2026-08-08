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
/// migrations. Not a third host role — it never serves HTTP or probes.</summary>
public static class PlatformMigrationExtensions
{
    /// <summary>Binds settings, composes modules far enough to collect their migrations, and
    /// applies every pending one under the provider-native migration lock.</summary>
    /// <param name="builder">The host builder. Modules must already be registered on it, the same
    /// as before <c>AddPlatformWebHost</c> or <c>AddPlatformWorkerHost</c>.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>0 on success; non-zero otherwise. Never throws for an ordinary migration failure —
    /// the exit status is the reporting channel for a one-shot command.</returns>
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
        builder.Services.TryAddSingleton<IClock, SystemClock>();
        builder.Services.AddPlatformPersistence();

        // Modules are composed far enough to collect what Register contributes — every
        // IModuleMigrationSource among them — without the topological ordering or graph validation
        // the real host performs, since migration order across modules is not order-dependent.
        foreach (var descriptor in builder.Services
                     .Where(descriptor => descriptor.ServiceType == typeof(IPlatformModule))
                     .ToList())
        {
            IPlatformModule module;
            try
            {
                module = Instantiate(descriptor);
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

            module.Register(builder.Services);
        }

        await using var provider = builder.Services.BuildServiceProvider();
        var runner = provider.GetRequiredService<IMigrationRunner>();

        var applied = await runner.ApplyAsync(cancellationToken).ConfigureAwait(false);
        if (applied.IsSuccess)
        {
            return 0;
        }

        Console.Error.WriteLine($"{applied.Error.Code}: {applied.Error.Detail}");
        return applied.Error.Code == nameof(MigrationError.Locked) ? 2 : 1;
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
