using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Hosting;

namespace SubZeroDev.Platform.SignIn;

/// <summary>The optional sign-in module: an OpenID Connect authorization-code client with a
/// proof key, stateless, with no client secret. It is an ordinary client of the authentication seam
/// and never widens it (<c>20-contract.md</c> § 13, I-S1). Registered through
/// <see cref="SignInExtensions.AddPlatformSignIn"/>; a host that does not call it takes nothing.</summary>
public sealed class SignInModule : IPlatformModule
{
    /// <inheritdoc/>
    public ModuleName Name { get; } = new("SignIn");

    // No dependency on Identity: a module references no other module (I-C7). The module reads the
    // generic path's Issuer keys from configuration, the way a vendor package would write them.
    /// <inheritdoc/>
    public IReadOnlyCollection<ModuleName> DependsOn { get; } = [];

    /// <inheritdoc/>
    public void Register(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var configuration = services
            .LastOrDefault(descriptor => descriptor.ServiceType == typeof(IConfiguration))
            ?.ImplementationInstance as IConfiguration;
        var hooks = SignInExtensions.HookRegistry(services);

        var methods = new List<SignInSettings>();
        if (configuration is not null)
        {
            var settings = SignInSettings.ReadAll(configuration);
            if (!settings.IsSuccess)
            {
                throw new PlatformStartupException(HostStartupError.Configuration(settings.Error));
            }

            methods.AddRange(settings.Value);
        }

        // A hook for a method nobody configured would silently never run; that is a defect.
        foreach (var method in hooks.Methods)
        {
            if (!methods.Any(configured => string.Equals(configured.Name, method, StringComparison.OrdinalIgnoreCase)))
            {
                throw new PlatformStartupException(HostStartupError.Configuration(
                    ConfigurationError.InvalidSetting(
                        $"{SignInSettings.SectionPath}:{method}",
                        "a hook was registered for this sign-in method and no such method is configured")));
            }
        }

        services.AddDataProtection();
        services.TryAddSingleton(SignInTransport.Default);
        services.AddSingleton(new SignInRuntime(methods, hooks));
    }
}

/// <summary>The registration and mapping calls a host makes to take the sign-in module.</summary>
public static class SignInExtensions
{
    /// <summary>Takes the sign-in module. Call before <c>AddPlatformWebHost</c>, like any module; the
    /// module's methods are read from <c>SubZeroDev:SignIn:&lt;Name&gt;</c>.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection, so calls chain.</returns>
    public static IServiceCollection AddPlatformSignIn(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        _ = HookRegistry(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IPlatformModule, SignInModule>());
        return services;
    }

    /// <summary>Registers the host's code for a provider that departs from the standard. The hooks run
    /// after configuration binds and cannot change what Platform trusts (I-S3).</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="method">The sign-in method the hooks belong to, as configured.</param>
    /// <param name="configure">Sets the hooks.</param>
    /// <returns>The same collection, so calls chain.</returns>
    public static IServiceCollection ConfigurePlatformSignIn(
        this IServiceCollection services,
        string method,
        Action<SignInHooks> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentNullException.ThrowIfNull(configure);

        configure(HookRegistry(services).For(method));
        return services;
    }

    /// <summary>Maps each configured method's endpoints: <c>begin</c>, the callback at the method's
    /// <c>RedirectPath</c>, <c>token</c> and <c>signout</c>. Call it on the host that takes Identity.</summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <returns>The same builder, so calls chain.</returns>
    public static IEndpointRouteBuilder MapPlatformSignIn(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var runtime = endpoints.ServiceProvider.GetRequiredService<SignInRuntime>();
        foreach (var method in runtime.Methods)
        {
            SignInEndpoints.Map(endpoints, method);
        }

        return endpoints;
    }

    internal static SignInHookRegistry HookRegistry(IServiceCollection services)
    {
        var existing = services
            .LastOrDefault(descriptor => descriptor.ServiceType == typeof(SignInHookRegistry))
            ?.ImplementationInstance as SignInHookRegistry;
        if (existing is not null)
        {
            return existing;
        }

        var created = new SignInHookRegistry();
        services.AddSingleton(created);
        return created;
    }
}

/// <summary>The hooks a host registered, by method. One <see cref="SignInHooks"/> per method, so two
/// calls for the same method configure the same object.</summary>
internal sealed class SignInHookRegistry
{
    private readonly Dictionary<string, SignInHooks> _byMethod = new(StringComparer.OrdinalIgnoreCase);

    internal IReadOnlyCollection<string> Methods => _byMethod.Keys;

    internal SignInHooks For(string method)
    {
        if (!_byMethod.TryGetValue(method, out var hooks))
        {
            hooks = new SignInHooks();
            _byMethod[method] = hooks;
        }

        return hooks;
    }

    internal SignInHooks? Find(string method) => _byMethod.GetValueOrDefault(method);
}

/// <summary>What the endpoints need at request time, built once at startup.</summary>
internal sealed class SignInRuntime(IReadOnlyList<SignInSettings> methods, SignInHookRegistry hooks)
{
    internal IReadOnlyList<SignInSettings> Methods { get; } = methods;

    internal SignInHookRegistry Hooks { get; } = hooks;

    internal IssuerMetadataCache Metadata { get; } = new();
}
