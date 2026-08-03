using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Hosting;

namespace SubZeroDev.Platform.Tests;

/// <summary>A check whose verdict a test chooses.</summary>
internal sealed class StubHealthCheck(
    string name,
    HealthCheckKind kind,
    HealthStatus status,
    HealthCheckCriticality criticality = HealthCheckCriticality.Required,
    bool touchesExternalDependency = false) : IHealthCheck
{
    public HealthCheckName Name { get; } = new(name);

    public HealthCheckKind Kind { get; } = kind;

    public HealthCheckCriticality Criticality { get; } = criticality;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    public bool TouchesExternalDependency { get; } = touchesExternalDependency;

    public Task<HealthCheckResult> CheckAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new HealthCheckResult(
            status,
            "stub detail",
            new Dictionary<string, string> { ["stub"] = "data" }));
}

/// <summary>Counts its own ticks, so a test can assert how many ran.</summary>
internal sealed class CountingBackgroundWork(string name, HostRoles roles) : IBackgroundWork
{
    private int _ticks;

    public BackgroundWorkName Name { get; } = new(name);

    public HostRoles Roles { get; } = roles;

    public TimeSpan Interval { get; init; } = TimeSpan.FromMilliseconds(20);

    public bool RequiresLease => false;

    internal int Ticks => Volatile.Read(ref _ticks);

    public Task TickAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _ticks);
        return Task.CompletedTask;
    }
}

/// <summary>A module a test names and gives dependencies to.</summary>
internal sealed class StubModule(string name, params string[] dependsOn) : IPlatformModule
{
    public ModuleName Name { get; } = new(name);

    public IReadOnlyCollection<ModuleName> DependsOn { get; } =
        dependsOn.Select(dependency => new ModuleName(dependency)).ToArray();

    public void Register(IServiceCollection services)
    {
    }
}

/// <summary>Builds a real web host on an ephemeral loopback port, so the assertions that are about
/// HTTP — status codes, body narrowing, the envelope — go over HTTP rather than around it.</summary>
internal static class WebHostUnderTest
{
    internal static async Task<(WebApplication App, HttpClient Client)> StartAsync(
        Action<IServiceCollection>? services = null,
        IDictionary<string, string?>? settings = null,
        string environment = "Production")
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            EnvironmentName = environment,
            ApplicationName = typeof(WebHostUnderTest).Assembly.GetName().Name,
        });

        builder.Configuration.AddInMemoryCollection(Settings.Required());
        if (settings is not null)
        {
            builder.Configuration.AddInMemoryCollection(settings);
        }

        services?.Invoke(builder.Services);

        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.AddPlatformWebHost();

        var app = builder.Build();
        app.MapGet("/", (ICurrentCorrelation correlation, ICurrentTenant tenant) => new
        {
            correlation = correlation.Current.TraceId,
            tenant = tenant.Current.Value,
        });
        app.MapGet("/boom", void () => throw new InvalidOperationException("secret detail that must not reach the wire"));

        await app.StartAsync();

        var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        return (app, client);
    }
}

/// <summary>The settings every host needs, in one place, so a test that is not about configuration
/// does not have to restate them.</summary>
internal static class Settings
{
    internal static Dictionary<string, string?> Required() => new()
    {
        ["Platform:Persistence:Provider"] = "Sqlite",
        ["Platform:Persistence:ConnectionString"] = "Data Source=:memory:",
        ["Platform:Outbox:ProcessedRetention"] = "1.00:00:00",
        ["Platform:Outbox:PoisonedRetention"] = "7.00:00:00",
    };
}
