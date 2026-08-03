using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Observability;

namespace SubZeroDev.Platform.Hosting;

/// <summary>The standard registration call, in web and worker forms. There is no second mandatory
/// call: health, readiness, correlation and telemetry are configured by these alone.</summary>
public static class PlatformHostExtensions
{
    /// <summary>Composes a host that serves HTTP and runs no product background work.</summary>
    /// <param name="builder">The host builder. Modules must already be registered on it.</param>
    /// <returns>The same builder, so calls chain.</returns>
    /// <exception cref="PlatformStartupException">A setting or the module graph is invalid.</exception>
    public static IHostApplicationBuilder AddPlatformWebHost(this IHostApplicationBuilder builder) =>
        AddPlatformHost(builder, HostRole.Web);

    /// <summary>Composes a host that owns background work and serves probes only. The same
    /// bootstrap as the web form with the product HTTP surface omitted — splitting it into a second
    /// package would duplicate the behaviour that must not diverge between two processes of one
    /// installation.</summary>
    /// <param name="builder">The host builder. Modules must already be registered on it.</param>
    /// <returns>The same builder, so calls chain.</returns>
    /// <exception cref="PlatformStartupException">A setting, the module graph, or the probe port is invalid.</exception>
    public static IHostApplicationBuilder AddPlatformWorkerHost(this IHostApplicationBuilder builder) =>
        AddPlatformHost(builder, HostRole.Worker);

    /// <summary>Places the probes in the host's own route table. Optional: a host that does not call
    /// this still serves them, because the standard registration call has to be sufficient alone.</summary>
    /// <param name="endpoints">The route builder.</param>
    /// <returns>The same route builder, so calls chain.</returns>
    public static IEndpointRouteBuilder MapPlatformProbes(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var mapping = endpoints.ServiceProvider.GetRequiredService<ProbeMapping>();
        mapping.MappedExplicitly = true;

        endpoints.MapGet(ProbeBody.LivenessPath, (HttpContext context) => Probe(context, HealthCheckKind.Liveness));
        endpoints.MapGet(ProbeBody.ReadinessPath, (HttpContext context) => Probe(context, HealthCheckKind.Readiness));

        return endpoints;

        static async Task Probe(HttpContext context, HealthCheckKind kind)
        {
            var probe = context.RequestServices.GetRequiredService<HealthProbe>();
            var environment = context.RequestServices.GetRequiredService<IHostEnvironment>();

            var report = await probe.RunAsync(kind, context.RequestAborted).ConfigureAwait(false);
            await ProbeBody
                .WriteAsync(context, report, ProbeBody.DetailFor(context, environment.IsDevelopment()))
                .ConfigureAwait(false);
        }
    }

    private static IHostApplicationBuilder AddPlatformHost(IHostApplicationBuilder builder, HostRole role)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = BindOptions(builder, role);
        builder.Services.AddSingleton(options);

        AddCoreDefaults(builder.Services);
        builder.AddPlatformObservability();

        // Modules are registered explicitly into the service collection and composed here, in
        // topological order. They must be registered before this call: Register contributes
        // services, and nothing can be added to a collection once the container is built.
        ComposeModules(builder.Services);

        builder.Services.AddSingleton<HealthProbe>();
        builder.Services.AddSingleton<ProbeMapping>();
        builder.Services.AddSingleton<IStartupFilter, PlatformStartupFilter>();
        builder.Services.AddHostedService<PlatformRegistryStartup>();
        builder.Services.AddHostedService<BackgroundWorkService>();

        builder.Services.Configure<HostOptions>(host =>
            host.ShutdownTimeout = options.Hosting.GracefulShutdownDrainWindow);

        if (role == HostRole.Worker)
        {
            ConfigureWorkerProbeListener(builder, options);
        }

        return builder;
    }

    private static PlatformOptions BindOptions(IHostApplicationBuilder builder, HostRole role)
    {
        var bound = PlatformOptionsBinder.Bind(
            builder.Configuration,
            builder.Environment.EnvironmentName,
            role);

        if (!bound.IsSuccess)
        {
            throw new PlatformStartupException(HostStartupError.Configuration(bound.Error));
        }

        var entry = Assembly.GetEntryAssembly();
        return bound.Value with
        {
            ServiceName = bound.Value.ServiceName ?? entry?.GetName().Name ?? "unknown",
            ServiceVersion = bound.Value.ServiceVersion
                ?? entry?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? entry?.GetName().Version?.ToString()
                ?? "0.0.0",
        };
    }

    private static void AddCoreDefaults(IServiceCollection services)
    {
        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton<AmbientOperationScope>();
        services.TryAddSingleton<IOperationScopeAccessor, OperationScopeAccessor>();
        services.TryAddSingleton<IOperationScopeFactory, OperationScopeFactory>();
        services.TryAddSingleton<ICurrentTenant, CurrentTenant>();
        services.TryAddSingleton<ICurrentPrincipal, CurrentPrincipal>();
        services.TryAddSingleton<ICurrentCorrelation, CurrentCorrelation>();
        services.TryAddSingleton<IModuleRegistry, ModuleRegistry>();
        services.TryAddSingleton<IHealthCheckRegistry, HealthCheckRegistry>();
        services.TryAddSingleton<IBackgroundWorkRegistry, BackgroundWorkRegistry>();
    }

    private static void ComposeModules(IServiceCollection services)
    {
        var modules = new List<IPlatformModule>();

        foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IPlatformModule)).ToList())
        {
            modules.Add(Instantiate(descriptor));
        }

        if (modules.Count == 0)
        {
            return;
        }

        var resolved = new ModuleRegistry().Resolve(modules);
        if (!resolved.IsSuccess)
        {
            throw new PlatformStartupException(HostStartupError.ModuleGraph(resolved.Error));
        }

        foreach (var module in resolved.Value)
        {
            module.Module.Register(services);
        }
    }

    private static IPlatformModule Instantiate(ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationInstance is IPlatformModule instance)
        {
            return instance;
        }

        if (descriptor.ImplementationType is { } type)
        {
            try
            {
                return (IPlatformModule)Activator.CreateInstance(type)!;
            }
            catch (MissingMethodException)
            {
                throw new PlatformStartupException(HostStartupError.Registration(
                    null,
                    $"Module '{type.FullName}' must have a public parameterless constructor: modules are "
                    + "composed before the container exists, so nothing can be injected into one."));
            }
        }

        throw new PlatformStartupException(HostStartupError.Registration(
            null,
            "A module registered by factory cannot be composed: modules are composed before the "
            + "container exists. Register the type, or an instance."));
    }

    private static void ConfigureWorkerProbeListener(IHostApplicationBuilder builder, PlatformOptions options)
    {
        var address = options.Hosting.WorkerProbeLoopbackOnly ? IPAddress.Loopback : IPAddress.Any;
        var port = options.Hosting.WorkerProbePort;

        // Bound here rather than left to Kestrel so the failure names the setting. A silent
        // fallback port would make the probe surface unfindable on a box running two installations.
        try
        {
            var check = new TcpListener(address, port);
            check.Start();
            check.Stop();
        }
        catch (SocketException)
        {
            throw new PlatformStartupException(HostStartupError.ProbeBindFailed(
                PlatformOptionsBinder.Key("Hosting:WorkerProbePort"),
                port));
        }

        builder.Services.Configure<KestrelServerOptions>(kestrel => kestrel.Listen(address, port));
    }
}
