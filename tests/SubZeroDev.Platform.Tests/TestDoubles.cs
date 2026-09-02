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

/// <summary>Records every event handed to it, and answers a test-chosen result. Threadsafe: a
/// transactional flush and a direct dispatch could both reach it.</summary>
internal sealed class RecordingAuditSink(string name = "recording", bool isDurable = false) : IAuditSink
{
    private readonly List<AuditEvent> _received = [];
    private readonly Lock _gate = new();
    private Func<AuditEvent, Result<AuditError>>? _answer;

    public string Name => name;

    public bool IsDurable => isDurable;

    internal IReadOnlyList<AuditEvent> Received
    {
        get { lock (_gate) { return _received.ToList(); } }
    }

    /// <summary>Every call after this one answers with <paramref name="result"/> instead of success.</summary>
    internal void FailNextWith(Func<AuditEvent, Result<AuditError>> result)
    {
        lock (_gate)
        {
            _answer = result;
        }
    }

    public Task<Result<AuditError>> WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _received.Add(auditEvent);
            return Task.FromResult(_answer?.Invoke(auditEvent) ?? Result<AuditError>.Success());
        }
    }
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

/// <summary>A permission provider whose answer a test chooses.</summary>
internal sealed class StubPermissionProvider(
    string name,
    Func<Principal, TenantId, ResourceRef?, Result<IReadOnlySet<PermissionName>, AuthorizationError>> answer)
    : IPermissionProvider
{
    public PermissionProviderName Name { get; } = new(name);

    public Task<Result<IReadOnlySet<PermissionName>, AuthorizationError>> GrantsAsync(
        Principal principal, TenantId tenant, ResourceRef? resource, CancellationToken cancellationToken) =>
        Task.FromResult(answer(principal, tenant, resource));
}

/// <summary>A permission catalog declaring a fixed set of names.</summary>
internal sealed class StubPermissionCatalog(params PermissionName[] declares) : IPermissionCatalog
{
    public IReadOnlyCollection<PermissionName> Declares { get; } = declares;
}

/// <summary>A tenant resolver whose answer a test chooses, and whether it was consulted.</summary>
internal sealed class StubTenantResolver(string name, Func<TenantId?> answer) : ITenantResolver
{
    public string Name { get; } = name;

    internal int CallCount { get; private set; }

    public Task<TenantId?> ResolveAsync(CancellationToken cancellationToken)
    {
        CallCount++;
        return Task.FromResult(answer());
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
        app.MapGet("/", (ICurrentCorrelation correlation, ICurrentTenant tenant, ICurrentCulture culture) => new
        {
            correlation = correlation.Current.TraceId,
            tenant = tenant.Current.Value,
            culture = culture.Current.Value,
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
        ["Platform:CompositionProfile"] = "Operated",
        ["Platform:Persistence:Provider"] = "Sqlite",
        ["Platform:Persistence:ConnectionString"] = "Data Source=:memory:",
        ["Platform:Outbox:ProcessedRetention"] = "1.00:00:00",
        ["Platform:Outbox:PoisonedRetention"] = "7.00:00:00",
    };
}
