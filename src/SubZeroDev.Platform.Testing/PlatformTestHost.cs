using System.Net;
using System.Net.Sockets;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Hosting;
using SubZeroDev.Platform.Persistence;

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

    /// <summary>Selects the persistence provider and wires Persistence in — the unit of work, the
    /// migration runner, and the <c>Database</c>/<c>PendingMigrations</c> readiness checks. Sqlite's
    /// default connection string is a fresh WAL-mode temp file, unique to this host; Postgres needs
    /// <see cref="WithSetting"/> to supply <c>Persistence:ConnectionString</c>.</summary>
    /// <param name="provider">The provider.</param>
    /// <returns>The same builder, so calls chain.</returns>
    IPlatformTestHostBuilder WithProvider(PersistenceProvider provider);

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

    /// <summary>Every event enqueued or dispatched while this host ran.</summary>
    IEventCapture Events { get; }

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
    private readonly string _sqliteFile = Path.Combine(Path.GetTempPath(), $"platform-test-{Guid.NewGuid():N}.db");
    private HostRole _role = HostRole.Web;
    private bool _persistenceRequested;

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

    public IPlatformTestHostBuilder WithProvider(PersistenceProvider provider)
    {
        _settings["Platform:Persistence:Provider"] = provider.ToString();
        _persistenceRequested = true;
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

        var capture = new EventCapture();
        builder.Services.AddSingleton<IEventCapture>(capture);

        if (_persistenceRequested)
        {
            builder.Services.AddPlatformPersistence();

            // Splices the capturing decorator over the factory AddPlatformPersistence registered,
            // rather than resolving and re-wrapping after the container builds: IOutboxWriter is a
            // singleton other singletons (the ambient scope's callers) capture a reference to during
            // their own construction, so anything resolved post-build would be a second, unused
            // instance rather than the one actually in use.
            var writerDescriptor = builder.Services.FirstOrDefault(descriptor => descriptor.ServiceType == typeof(IOutboxWriter));
            if (writerDescriptor?.ImplementationFactory is { } innerFactory)
            {
                builder.Services.Remove(writerDescriptor);
                builder.Services.AddSingleton<IOutboxWriter>(provider =>
                {
                    var innerWriter = (IOutboxWriter)innerFactory(provider);
                    return new CapturingOutboxWriter(
                        innerWriter,
                        capture,
                        provider.GetRequiredService<IOperationScopeAccessor>(),
                        provider.GetRequiredService<IEventHandlerRegistry>(),
                        provider.GetRequiredService<IClock>());
                });
            }

            var resolvedProvider = _settings.TryGetValue("Platform:Persistence:Provider", out var raw)
                && Enum.TryParse<PersistenceProvider>(raw, out var parsed)
                ? parsed
                : PersistenceProvider.Sqlite;

            if (resolvedProvider == PersistenceProvider.Sqlite)
            {
                // The effective connection string, honouring an explicit override the same way
                // configuration precedence does — a test pointing this at its own file (or at a
                // deliberately unreachable path) must be pre-seeded on that file, not the builder's
                // own default.
                var effective = _settings.GetValueOrDefault("Platform:Persistence:ConnectionString")
                    ?? $"Data Source={_sqliteFile}";
                EnsureWalModeFile(effective);
            }
        }

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

        return new StartedTestHost(host, clock, capture, _sqliteFile);
    }

    /// <summary>Every required setting, so a test needs none of them, and a probe port that cannot
    /// collide with a parallel test's. The Sqlite connection string is a fresh file unique to this
    /// host — WAL requires a real file, and a brand new one starts in <c>delete</c> mode until
    /// something sets it, which <see cref="EnsureWalModeFile"/> does before startup when persistence
    /// is requested.</summary>
    private Dictionary<string, string?> Defaults() => new()
    {
        ["Platform:Persistence:Provider"] = nameof(PersistenceProvider.Sqlite),
        ["Platform:Persistence:ConnectionString"] = $"Data Source={_sqliteFile}",
        ["Platform:Outbox:ProcessedRetention"] = "1.00:00:00",
        ["Platform:Outbox:PoisonedRetention"] = "7.00:00:00",
        ["Platform:Hosting:WorkerProbePort"] = FreePort().ToString(),
    };

    /// <summary>Creates the file if absent and switches it to WAL — which, once set, persists in the
    /// file itself, so nothing needs to repeat this on a later open. Best-effort: a deliberately
    /// unreachable connection string (a test proving readiness degrades against one) fails here the
    /// same way it fails for the host itself, and that failure is exactly what the test wants to
    /// observe at runtime rather than at setup.</summary>
    private static void EnsureWalModeFile(string connectionString)
    {
        try
        {
            var nonPooled = new SqliteConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;
            using var connection = new SqliteConnection(nonPooled);
            connection.Open();
            using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA journal_mode=WAL;";
            pragma.ExecuteNonQuery();
        }
        catch
        {
            // Left for AssertStartupPreconditionsAsync and readiness to report at their own layer.
        }
    }

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

internal sealed class StartedTestHost(IHost host, FakeClock clock, IEventCapture events, string? sqliteFile = null)
    : IPlatformTestHost
{
    public IServiceProvider Services => host.Services;

    public FakeClock Clock => clock;

    public IEventCapture Events => events;

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

        if (sqliteFile is not null)
        {
            TryDeleteSqliteFile(sqliteFile);
        }
    }

    private static void TryDeleteSqliteFile(string path)
    {
        try
        {
            File.Delete(path);
            File.Delete(path + "-wal");
            File.Delete(path + "-shm");
        }
        catch (IOException)
        {
            // Best-effort cleanup: a lingering handle leaves an orphaned temp file, not a test
            // failure.
        }
    }
}
