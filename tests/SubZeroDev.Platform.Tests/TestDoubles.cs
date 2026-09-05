using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
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

/// <summary>An entitlement contributor whose answer a test chooses.</summary>
internal sealed class StubEntitlementContributor(
    string name,
    Func<FeatureName, TenantId, Result<bool, EntitlementError>> answer) : IEntitlementContributor
{
    public EntitlementContributorName Name { get; } = new(name);

    public Task<Result<bool, EntitlementError>> GrantsAsync(
        FeatureName feature, TenantId tenant, CancellationToken cancellationToken) =>
        Task.FromResult(answer(feature, tenant));
}

/// <summary>An authentication request carrying headers a test chooses, and nothing else — which is
/// the whole of the interface.</summary>
internal sealed class StubAuthenticationRequest(params (string Name, string Value)[] headers)
    : IAuthenticationRequest
{
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Headers { get; } =
        headers.ToDictionary(
            header => header.Name,
            header => (IReadOnlyList<string>)[header.Value],
            StringComparer.OrdinalIgnoreCase);
}

/// <summary>An authentication provider whose answer a test chooses, and whether it was consulted.
/// Defers by default — answering <see cref="Principal.Anonymous"/> is how a provider says "no
/// credential of my kind was presented".</summary>
internal sealed class StubAuthenticationProvider(
    string name,
    Func<IAuthenticationRequest, Result<Principal, AuthenticationError>>? answer = null)
    : IAuthenticationProvider
{
    public string Name => name;

    internal int CallCount { get; private set; }

    public Task<Result<Principal, AuthenticationError>> AuthenticateAsync(
        IAuthenticationRequest request, CancellationToken cancellationToken)
    {
        CallCount++;
        return Task.FromResult(
            answer?.Invoke(request) ?? Result<Principal, AuthenticationError>.Success(Principal.Anonymous));
    }
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

/// <summary>Wraps the real <see cref="IOperationScopeFactory"/> and appends one entry to a shared
/// trace before delegating — the only way a test observes "open scope" as a discrete step, since
/// nothing else in the public surface signals it happening.</summary>
internal sealed class TracingOperationScopeFactory(IOperationScopeFactory inner, List<string> trace)
    : IOperationScopeFactory
{
    public IOperationScope Begin(TenantId tenant, Principal principal, CultureTag culture = default)
    {
        lock (trace)
        {
            trace.Add("open-scope");
        }

        return inner.Begin(tenant, principal, culture);
    }

    public IOperationScope Begin(
        TraceContext established, CorrelationId correlation, TenantId tenant, Principal principal, CultureTag culture = default)
    {
        lock (trace)
        {
            trace.Add("open-scope");
        }

        return inner.Begin(established, correlation, tenant, principal, culture);
    }
}

/// <summary>Wraps the real <see cref="IAuthenticationProviderRegistry"/> and appends one trace
/// entry every time <see cref="Registered"/> is read — the only per-request signal that step 1 ran,
/// usable even where the registry is empty (Local, I-C3 forbids a registered provider there).</summary>
internal sealed class TracingAuthenticationProviderRegistry(IAuthenticationProviderRegistry inner, List<string> trace)
    : IAuthenticationProviderRegistry
{
    public Result<AuthenticationProviderRegistrationError> Register(IAuthenticationProvider provider) =>
        inner.Register(provider);

    public IReadOnlyList<IAuthenticationProvider> Registered
    {
        get
        {
            lock (trace)
            {
                trace.Add("authenticate");
            }

            return inner.Registered;
        }
    }

    public void Freeze() => inner.Freeze();
}

/// <summary>Wraps the real <see cref="ITenantResolverRegistry"/> and appends one trace entry
/// every time <see cref="Registered"/> is read — the only per-request signal that step 2 ran,
/// usable even where the registry is empty (Local, I-C3 forbids a registered resolver there).</summary>
internal sealed class TracingTenantResolverRegistry(ITenantResolverRegistry inner, List<string> trace)
    : ITenantResolverRegistry
{
    public Result<TenantResolverRegistrationError> Register(ITenantResolver resolver) =>
        inner.Register(resolver);

    public IReadOnlyList<ITenantResolver> Registered
    {
        get
        {
            lock (trace)
            {
                trace.Add("resolve-tenant");
            }

            return inner.Registered;
        }
    }

    public void Freeze() => inner.Freeze();
}

/// <summary>Wraps the real <see cref="IEntitlementEvaluator"/> and appends one trace entry every
/// time <see cref="EvaluateAsync"/> is called, before delegating. Used where the contributor itself
/// cannot be decorated without changing its type — the composition validator compares a Local
/// host's registered contributors by type (I-C3), so a wrapped one would be mistaken for a second,
/// forbidden registration.</summary>
internal sealed class TracingEntitlementEvaluator(IEntitlementEvaluator inner, List<string> trace)
    : IEntitlementEvaluator
{
    public Task<EntitlementDecision> EvaluateAsync(FeatureName feature, CancellationToken cancellationToken)
    {
        lock (trace)
        {
            trace.Add("check-entitlement");
        }

        return inner.EvaluateAsync(feature, cancellationToken);
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
    /// <summary>Starts a host on an ephemeral loopback port.</summary>
    /// <param name="services">Extra registrations, applied before the platform composes.</param>
    /// <param name="settings">Settings layered over <see cref="Settings.Required"/>.</param>
    /// <param name="environment">The host environment name.</param>
    /// <param name="composeOperatedDefaults">Whether to register the authentication provider and
    /// durable audit sink the <see cref="CompositionProfile.Operated"/> profile requires (I-C1,
    /// I-C2). True for every test that is not about composition: the default profile is
    /// <c>Operated</c>, and without these the host correctly refuses to start. A test asserting a
    /// startup refusal, or running <see cref="CompositionProfile.Local"/>, passes
    /// <see langword="false"/> and registers what it means to.</param>
    /// <param name="postCompose">Extra registrations applied after <c>AddPlatformWebHost</c> has
    /// run — the only point at which a test can splice a decorator over a service the platform
    /// itself registered, on the same terms <c>PlatformTestHost</c> already uses for the outbox
    /// writer.</param>
    /// <param name="mapEndpoints">Extra endpoints, mapped after the fixed ones below and before the
    /// host starts.</param>
    internal static async Task<(WebApplication App, HttpClient Client)> StartAsync(
        Action<IServiceCollection>? services = null,
        IDictionary<string, string?>? settings = null,
        string environment = "Production",
        bool composeOperatedDefaults = true,
        Action<IServiceCollection>? postCompose = null,
        Action<WebApplication>? mapEndpoints = null)
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

        if (composeOperatedDefaults)
        {
            // The provider defers, so every request still observes Anonymous exactly as it did
            // before the seam existed; its presence is what satisfies I-C1 rather than what it
            // answers. The sink declares IsDurable so I-C2 holds — the default log sink never does.
            Settings.ComposeOperated(builder.Services);
        }

        services?.Invoke(builder.Services);

        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.AddPlatformWebHost();
        postCompose?.Invoke(builder.Services);

        var app = builder.Build();
        app.MapGet("/", (ICurrentCorrelation correlation, ICurrentTenant tenant, ICurrentCulture culture) => new
        {
            correlation = correlation.Current.TraceId,
            tenant = tenant.Current.Value,
            culture = culture.Current.Value,
        }).ExemptFromPlatformAuthorization(
            "Generic test-harness root endpoint, shared by tests that are not about authorization "
            + "(D5-S8 I-R6 requires a declaration on every mapped endpoint).");
        app.MapGet("/boom", void () => throw new InvalidOperationException("secret detail that must not reach the wire"))
            .ExemptFromPlatformAuthorization(
                "Test-harness diagnostic endpoint proving the unhandled-failure envelope; not part of any "
                + "product's permission surface.");
        mapEndpoints?.Invoke(app);

        await app.StartAsync();

        var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        return (app, client);
    }
}

/// <summary>The settings every host needs, in one place, so a test that is not about configuration
/// does not have to restate them.</summary>
internal static class Settings
{
    /// <summary>Registers what <see cref="CompositionProfile.Operated"/> requires of any host —
    /// an authentication provider (I-C1) and a sink declaring <c>IsDurable</c> (I-C2). Every test
    /// that builds its own host from <see cref="Required"/> and then <em>starts</em> it needs this,
    /// because the profile validation runs at start; one that only builds does not.</summary>
    /// <param name="services">The host's service collection.</param>
    internal static void ComposeOperated(IServiceCollection services)
    {
        services.AddSingleton<IAuthenticationProvider>(new StubAuthenticationProvider("test-operated-default"));
        services.AddSingleton<IAuditSink>(new RecordingAuditSink("test-durable-default", isDurable: true));
    }

    internal static Dictionary<string, string?> Required() => new()
    {
        ["Platform:CompositionProfile"] = "Operated",
        ["Platform:Persistence:Provider"] = "Sqlite",
        ["Platform:Persistence:ConnectionString"] = "Data Source=:memory:",
        ["Platform:Outbox:ProcessedRetention"] = "1.00:00:00",
        ["Platform:Outbox:PoisonedRetention"] = "7.00:00:00",
    };
}
