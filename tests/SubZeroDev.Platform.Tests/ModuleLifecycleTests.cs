using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Hosting;
using SubZeroDev.Platform.Testing;

namespace SubZeroDev.Platform.Tests;

/// <summary>S36: a module's two lifecycle hooks — I-C10 to I-C13.</summary>
public sealed class ModuleLifecycleTests
{
    private readonly List<string> _log = [];

    [Fact]
    public async Task S36_1_hooks_run_in_dependency_order_and_shut_down_in_reverse()
    {
        var (a, b, c) = Chain();

        var host = await TestHost(HostRole.Web, c, a, b).StartAsync(CancellationToken.None);
        await host.DisposeAsync();

        Assert.Equal(["A.init", "B.init", "C.init", "C.shutdown", "B.shutdown", "A.shutdown"], Entries());
    }

    [Fact]
    public async Task S36_2_an_initialization_still_running_holds_the_host_before_it_has_started()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var a = new LifecycleModule("A", _log) { InitializeGate = gate.Task };

        var starting = TestHost(HostRole.Web, a).StartAsync(CancellationToken.None);
        await Task.Delay(200, CancellationToken.None);

        // The started host is what carries RunBackgroundWorkOnceAsync, so while this is incomplete
        // nothing can reach a tick.
        Assert.False(starting.IsCompleted);

        gate.SetResult();
        await using var host = await starting;
        Assert.Equal(["A.init"], Entries());
    }

    [Fact]
    public async Task S36_2_no_background_tick_runs_before_initialization_completes()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var a = new LifecycleModule("A", _log) { InitializeGate = gate.Task };
        var work = new CountingBackgroundWork("early", HostRoles.Both) { Interval = TimeSpan.FromMilliseconds(20) };

        var builder = RawBuilder("Production", a);
        builder.Services.AddSingleton<IBackgroundWork>(work);
        builder.AddPlatformWebHost();
        using var host = builder.Build();

        var starting = host.StartAsync(CancellationToken.None);
        await Task.Delay(200, CancellationToken.None);

        Assert.False(starting.IsCompleted);
        Assert.Equal(0, work.Ticks);

        gate.SetResult();
        await starting;

        // The timers are on: once initialization is done they tick, which is what makes the zero
        // above mean "held back" rather than "never scheduled".
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (work.Ticks == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, CancellationToken.None);
        }

        await host.StopAsync(CancellationToken.None);
        Assert.True(work.Ticks > 0, "The timer never ticked after initialization completed.");
    }

    [Fact]
    public async Task S36_3_a_throwing_initialization_fails_the_host_and_shuts_down_what_started()
    {
        var a = new LifecycleModule("A", _log);
        var b = new LifecycleModule("B", _log, "A") { InitializeThrows = new InvalidOperationException("secret-xyz") };
        var c = new LifecycleModule("C", _log, "B");

        var thrown = await Assert.ThrowsAsync<PlatformStartupException>(
            () => TestHost(HostRole.Web, c, a, b).StartAsync(CancellationToken.None));

        var error = Assert.IsType<HostStartupError>(thrown.Error);
        Assert.Equal("ModuleInitialization", error.Code);
        Assert.Contains("'B'", error.Detail, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), error.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-xyz", error.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-xyz", thrown.Message, StringComparison.Ordinal);
        Assert.Equal(["A.init", "A.shutdown"], Entries());
    }

    [Fact]
    public async Task S36_4_a_later_startup_failure_still_shuts_every_module_down_on_disposal()
    {
        var (a, b, c) = Chain();

        var builder = RawBuilder("Production", c, a, b);
        builder.AddPlatformWebHost();
        builder.Services.AddHostedService<FailingHostedService>();
        var host = builder.Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(CancellationToken.None));
        Assert.Equal(["A.init", "B.init", "C.init"], Entries());

        host.Dispose();

        Assert.Equal(["A.init", "B.init", "C.init", "C.shutdown", "B.shutdown", "A.shutdown"], Entries());
    }

    [Fact]
    public async Task S36_5_stop_then_dispose_shuts_each_module_down_exactly_once()
    {
        var (a, b, c) = Chain();

        var builder = RawBuilder("Production", a, b, c);
        builder.AddPlatformWebHost();
        var host = builder.Build();

        await host.StartAsync(CancellationToken.None);
        await host.StopAsync(CancellationToken.None);
        await host.StopAsync(CancellationToken.None);
        host.Dispose();

        foreach (var name in new[] { "A", "B", "C" })
        {
            Assert.Single(Entries(), entry => entry == $"{name}.shutdown");
        }
    }

    [Fact]
    public async Task S36_6_a_throwing_shutdown_is_logged_and_the_rest_still_run()
    {
        var logs = new LevelCapturingLoggerProvider();
        var a = new LifecycleModule("A", _log);
        var b = new LifecycleModule("B", _log, "A") { ShutdownThrows = new InvalidOperationException("shutdown failed") };
        var c = new LifecycleModule("C", _log, "B");

        var builder = RawBuilder("Production", a, b, c);
        builder.AddPlatformWebHost();

        // After the standard call, so it replaces the Serilog factory that call registers, which
        // writes to its own sinks and not to providers.
        using var loggerFactory = LoggerFactory.Create(logging => logging.AddProvider(logs));
        builder.Services.AddSingleton(loggerFactory);
        using var host = builder.Build();

        await host.StartAsync(CancellationToken.None);
        await host.StopAsync(CancellationToken.None);

        Assert.Equal(["A.init", "B.init", "C.init", "C.shutdown", "A.shutdown"], Entries());
        Assert.Contains(
            logs.Entries,
            entry => entry.Level == LogLevel.Error && entry.Message.Contains("Module B ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task S36_7_a_module_declaring_neither_hook_composes_and_starts_unchanged()
    {
        await using var host = await PlatformTestHost.CreateBuilder()
            .WithServices(services => services.AddSingleton<IPlatformModule>(new StubModule("plain")))
            .StartAsync(CancellationToken.None);

        Assert.NotNull(host.Services.GetRequiredService<IHealthCheckRegistry>());
    }

    [Fact]
    public async Task S36_8_the_hooks_run_on_a_worker_host_as_on_a_web_host()
    {
        var (a, b, c) = Chain();

        var host = await TestHost(HostRole.Worker, c, a, b).StartAsync(CancellationToken.None);
        await host.DisposeAsync();

        Assert.Equal(["A.init", "B.init", "C.init", "C.shutdown", "B.shutdown", "A.shutdown"], Entries());
    }

    [Fact]
    public async Task S36_8_migrate_mode_runs_neither_hook()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = "Production",
        });
        builder.Configuration.AddInMemoryCollection(Settings.Required());
        builder.Services.AddSingleton<IPlatformModule>(new LifecycleModule("A", _log));

        var exitCode = await builder.RunPlatformMigrateModeAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Empty(Entries());
    }

    [Fact]
    public async Task S36_9_a_hook_resolves_a_scoped_service_with_scope_validation_on()
    {
        IOperationScope? ambient = null;
        var resolved = false;
        var a = new LifecycleModule("A", _log)
        {
            OnInitialize = services =>
            {
                resolved = services.GetRequiredService<ScopedDependency>() is not null;
                ambient = services.GetRequiredService<IOperationScopeAccessor>().Current;
            },
        };

        var builder = RawBuilder("Development", a);
        builder.Services.AddScoped<ScopedDependency>();
        builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions { ValidateScopes = true }));
        builder.AddPlatformWebHost();
        using var host = builder.Build();

        // The control: the same resolution from the root fails, so success below is the scope's.
        Assert.Throws<InvalidOperationException>(() => host.Services.GetRequiredService<ScopedDependency>());

        await host.StartAsync(CancellationToken.None);
        await host.StopAsync(CancellationToken.None);

        Assert.True(resolved);
        Assert.Null(ambient);
        Assert.Equal(["A.init", "A.shutdown"], Entries());
    }

    private (LifecycleModule A, LifecycleModule B, LifecycleModule C) Chain() =>
        (new LifecycleModule("A", _log), new LifecycleModule("B", _log, "A"), new LifecycleModule("C", _log, "B"));

    private string[] Entries()
    {
        lock (_log)
        {
            return [.. _log];
        }
    }

    private static IPlatformTestHostBuilder TestHost(HostRole role, params IPlatformModule[] modules) =>
        PlatformTestHost.CreateBuilder()
            .WithRole(role)
            .WithServices(services =>
            {
                foreach (var module in modules)
                {
                    services.AddSingleton(module);
                }
            });

    private static HostApplicationBuilder RawBuilder(string environment, params IPlatformModule[] modules)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = environment,
        });

        builder.Configuration.AddInMemoryCollection(Settings.Required());
        Settings.ComposeOperated(builder.Services);
        foreach (var module in modules)
        {
            builder.Services.AddSingleton(module);
        }

        return builder;
    }

    private sealed class LifecycleModule(string name, List<string> log, params string[] dependsOn) : IPlatformModule
    {
        public ModuleName Name { get; } = new(name);

        public IReadOnlyCollection<ModuleName> DependsOn { get; } = [.. dependsOn.Select(dependency => new ModuleName(dependency))];

        public Task? InitializeGate { get; init; }

        public Exception? InitializeThrows { get; init; }

        public Exception? ShutdownThrows { get; init; }

        public Action<IServiceProvider>? OnInitialize { get; init; }

        public void Register(IServiceCollection services)
        {
        }

        public async Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken)
        {
            if (InitializeGate is not null)
            {
                await InitializeGate.WaitAsync(cancellationToken);
            }

            OnInitialize?.Invoke(services);

            if (InitializeThrows is not null)
            {
                throw InitializeThrows;
            }

            Record("init");
        }

        public Task ShutdownAsync(IServiceProvider services, CancellationToken cancellationToken)
        {
            if (ShutdownThrows is not null)
            {
                throw ShutdownThrows;
            }

            Record("shutdown");
            return Task.CompletedTask;
        }

        private void Record(string hook)
        {
            lock (log)
            {
                log.Add($"{name}.{hook}");
            }
        }
    }

    private sealed class FailingHostedService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A later start-up step failed.");

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ScopedDependency;

    private sealed class LevelCapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get
            {
                lock (_entries)
                {
                    return [.. _entries];
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new Logger(_entries);

        public void Dispose()
        {
        }

        private sealed class Logger(List<(LogLevel Level, string Message)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (entries)
                {
                    entries.Add((logLevel, formatter(state, exception)));
                }
            }
        }
    }
}
