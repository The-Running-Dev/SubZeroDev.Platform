using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Hosting;

namespace SubZeroDev.Platform.Testing;

/// <summary>The entry point to a test host. A test cannot construct an interface, and leaving this
/// unstated would have each test assembly inventing its own.</summary>
public static class PlatformTestHost
{
    /// <summary>Starts a builder with every required setting already supplied, so an integration
    /// test needs no bespoke setup.</summary>
    /// <returns>A builder.</returns>
    public static IPlatformTestHostBuilder CreateBuilder() => new PlatformTestHostBuilder();
}

/// <summary>Composes a host the way a product would, with the schedule replaced and the clock
/// controlled.</summary>
public interface IPlatformTestHostBuilder
{
    /// <summary>Which role the host runs as.</summary>
    /// <param name="role">The role.</param>
    /// <returns>The same builder, so calls chain.</returns>
    IPlatformTestHostBuilder WithRole(HostRole role);

    /// <summary>Overrides one configuration value, using the same keys a product would.</summary>
    /// <param name="key">The configuration key, without the <c>Platform:</c> prefix.</param>
    /// <param name="value">The value.</param>
    /// <returns>The same builder, so calls chain.</returns>
    IPlatformTestHostBuilder WithSetting(string key, string value);

    /// <summary>Contributes modules, health checks and background work through the same plain
    /// registration the real host collects them by, so a test exercises the production path.</summary>
    /// <param name="configure">Applied to the service collection before the host is composed.</param>
    /// <returns>The same builder, so calls chain.</returns>
    IPlatformTestHostBuilder WithServices(Action<IServiceCollection> configure);

    /// <summary>Builds and starts the host.</summary>
    /// <param name="cancellationToken">Cancels the start.</param>
    /// <returns>The started host.</returns>
    Task<IPlatformTestHost> StartAsync(CancellationToken cancellationToken);
}

/// <summary>A started test host.</summary>
public interface IPlatformTestHost : IAsyncDisposable
{
    /// <summary>The host's services.</summary>
    IServiceProvider Services { get; }

    /// <summary>The clock the host reads, which a test moves.</summary>
    FakeClock Clock { get; }

    /// <summary>Runs one probe and returns its report, without going over HTTP.</summary>
    /// <param name="kind">Which probe to run.</param>
    /// <param name="cancellationToken">Cancels the probe.</param>
    /// <returns>The report.</returns>
    Task<HealthReport> ProbeAsync(HealthCheckKind kind, CancellationToken cancellationToken);

    /// <summary>Invokes exactly one tick of one registration. The test host owns the schedule and
    /// the fake clock supplies the instants the tick compares against, so no timing-dependent test
    /// contains a wall-clock wait.</summary>
    /// <param name="name">The registration's name.</param>
    /// <param name="cancellationToken">Cancels the tick.</param>
    /// <returns>A task that completes when the tick does.</returns>
    Task RunBackgroundWorkOnceAsync(BackgroundWorkName name, CancellationToken cancellationToken);
}

internal sealed class PlatformTestHostBuilder : IPlatformTestHostBuilder
{
    private readonly Dictionary<string, string?> _settings = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Action<IServiceCollection>> _services = [];
    private HostRole _role = HostRole.Web;

    public IPlatformTestHostBuilder WithRole(HostRole role)
    {
        _role = role;
        return this;
    }

    public IPlatformTestHostBuilder WithSetting(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _settings[$"Platform:{key}"] = value;
        return this;
    }

    public IPlatformTestHostBuilder WithServices(Action<IServiceCollection> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        _services.Add(configure);
        return this;
    }

    public async Task<IPlatformTestHost> StartAsync(CancellationToken cancellationToken)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Production,
            ApplicationName = "SubZeroDev.Platform.Testing",
        });

        builder.Configuration.AddInMemoryCollection(Defaults());
        builder.Configuration.AddInMemoryCollection(_settings);

        // Contributed before the host is composed, because modules must exist on the collection
        // before the standard registration call reads them.
        foreach (var configure in _services)
        {
            configure(builder.Services);
        }

        var clock = new FakeClock();
        builder.Services.AddSingleton<IClock>(clock);

        if (_role == HostRole.Web)
        {
            builder.AddPlatformWebHost();
        }
        else
        {
            builder.AddPlatformWorkerHost();
        }

        SuppressTimers(builder.Services);

        var host = builder.Build();
        await host.StartAsync(cancellationToken).ConfigureAwait(false);

        return new StartedTestHost(host, clock);
    }

    /// <summary>Every required setting, so a test needs none of them, and a probe port that cannot
    /// collide with a parallel test's.</summary>
    private Dictionary<string, string?> Defaults() => new()
    {
        ["Platform:Persistence:Provider"] = nameof(PersistenceProvider.Sqlite),
        ["Platform:Persistence:ConnectionString"] = "Data Source=:memory:",
        ["Platform:Outbox:ProcessedRetention"] = "1.00:00:00",
        ["Platform:Outbox:PoisonedRetention"] = "7.00:00:00",
        ["Platform:Hosting:WorkerProbePort"] = FreePort().ToString(),
    };

    /// <summary>Hosting owns the timers, and a running timer would make a single-tick assertion
    /// flaky. Removing the loop is what leaves <c>RunBackgroundWorkOnceAsync</c> the only caller of
    /// a tick — the separation of schedule from clock, used as designed.</summary>
    private static void SuppressTimers(IServiceCollection services)
    {
        var timers = services
            .Where(descriptor =>
                descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationType == typeof(BackgroundWorkService))
            .ToList();

        foreach (var descriptor in timers)
        {
            services.Remove(descriptor);
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

internal sealed class StartedTestHost(IHost host, FakeClock clock) : IPlatformTestHost
{
    public IServiceProvider Services => host.Services;

    public FakeClock Clock => clock;

    public Task<HealthReport> ProbeAsync(HealthCheckKind kind, CancellationToken cancellationToken) =>
        host.Services.GetRequiredService<HealthProbe>().RunAsync(kind, cancellationToken);

    public Task RunBackgroundWorkOnceAsync(BackgroundWorkName name, CancellationToken cancellationToken)
    {
        var registry = host.Services.GetRequiredService<IBackgroundWorkRegistry>();
        var work = registry.Registered.FirstOrDefault(registered => registered.Name == name)
            ?? throw new InvalidOperationException($"No background work named '{name}' is registered.");

        return work.TickAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await host.StopAsync(CancellationToken.None).ConfigureAwait(false);
        host.Dispose();
    }
}
