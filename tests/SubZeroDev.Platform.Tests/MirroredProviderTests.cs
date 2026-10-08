using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Hosting;
using SubZeroDev.Platform.Mcp;
using SubZeroDev.Platform.Testing;

namespace SubZeroDev.Platform.Tests;

/// <summary>S39: a mirrored permission provider whose last sync is older than
/// <c>Platform:Authorization:MirrorMaximumAge</c> grants nothing, is not asked for grants, and is
/// recorded as <see cref="AuthorizationError.ProviderUnavailable"/> naming it (I-A13).</summary>
public sealed class MirroredProviderTests
{
    private static readonly TenantId TestTenant = new(Guid.Parse("33333333-3333-3333-3333-333333333333"));
    private static readonly PermissionName Permission = new("Test.Mirror.Use");
    private const string Key = "Platform:Authorization:MirrorMaximumAge";

    [Fact]
    public void S39_1_The_interface_and_the_options_exist_as_the_contract_declares_them()
    {
        var mirrored = typeof(IMirroredPermissionProvider);
        Assert.True(mirrored.IsInterface);
        Assert.Contains(typeof(IPermissionProvider), mirrored.GetInterfaces());
        var lastSynced = mirrored.GetMethod(nameof(IMirroredPermissionProvider.LastSyncedAsync))!;
        Assert.Equal(typeof(Task<Result<DateTimeOffset?, AuthorizationError>>), lastSynced.ReturnType);
        Assert.Equal(
            new[] { typeof(TenantId), typeof(CancellationToken) },
            lastSynced.GetParameters().Select(parameter => parameter.ParameterType));

        var maximumAge = typeof(AuthorizationOptions).GetProperty(nameof(AuthorizationOptions.MirrorMaximumAge))!;
        Assert.Equal(typeof(TimeSpan), maximumAge.PropertyType);
        Assert.NotNull(maximumAge.GetCustomAttributes(typeof(FingerprintedAttribute), inherit: false).SingleOrDefault());
        Assert.Equal(TimeSpan.FromMinutes(15), new AuthorizationOptions().MirrorMaximumAge);

        var onOptions = typeof(PlatformOptions).GetProperty(nameof(PlatformOptions.Authorization))!;
        Assert.Equal(typeof(AuthorizationOptions), onOptions.PropertyType);
        Assert.Equal(new AuthorizationOptions(), Baseline().Authorization);
    }

    [Fact]
    public void S39_1_The_contract_points_at_the_tree_instead_of_scaffolding_the_declarations()
    {
        var contract = File.ReadAllText(Path.Combine(
            S17MutationFixtures.FindRepositoryRoot(AppContext.BaseDirectory), "design", "20-contract.md"));

        Assert.DoesNotContain("public interface IMirroredPermissionProvider", contract, StringComparison.Ordinal);
        Assert.DoesNotContain("public sealed record AuthorizationOptions", contract, StringComparison.Ordinal);
    }

    [Fact]
    public async Task S39_2_A_mirror_exactly_at_the_default_limit_grants_and_one_tick_past_it_is_unavailable()
    {
        var mirror = new StubMirroredProvider("mirror", grants: true);
        await using var host = await StartAsync(services => services.AddSingleton<IPermissionProvider>(mirror));
        var syncedAt = host.Clock.UtcNow;
        mirror.Stamp = () => Synced(syncedAt);

        host.Clock.Advance(TimeSpan.FromMinutes(15));
        var atLimit = await EvaluateAsync(host);

        Assert.Equal(AuthorizationOutcome.Allowed, atLimit.Outcome);
        Assert.Equal("mirror", Assert.Single(atLimit.Sources).Value);
        Assert.Null(atLimit.ProviderFailure);
        var grantsAtLimit = mirror.GrantsCalls;

        host.Clock.Advance(TimeSpan.FromTicks(1));
        var pastLimit = await EvaluateAsync(host);

        Assert.Equal(AuthorizationOutcome.Denied, pastLimit.Outcome);
        Assert.Empty(pastLimit.Sources);
        AssertUnavailable("mirror", pastLimit);
        Assert.Equal(grantsAtLimit, mirror.GrantsCalls);
    }

    [Fact]
    public async Task S39_2_The_stamp_is_read_for_the_tenant_being_evaluated()
    {
        var mirror = new StubMirroredProvider("mirror", grants: true);
        await using var host = await StartAsync(services => services.AddSingleton<IPermissionProvider>(mirror));
        var syncedAt = host.Clock.UtcNow;
        mirror.Stamp = () => Synced(syncedAt);

        await EvaluateAsync(host);

        Assert.Equal(TestTenant, Assert.Single(mirror.StampTenants));
    }

    [Fact]
    public async Task S39_3_A_mirror_that_never_synced_is_unavailable_at_any_clock_and_is_never_asked_for_grants()
    {
        var mirror = new StubMirroredProvider("mirror", grants: true)
        {
            Stamp = () => Result<DateTimeOffset?, AuthorizationError>.Success(null),
        };
        await using var host = await StartAsync(services => services.AddSingleton<IPermissionProvider>(mirror));

        var now = await EvaluateAsync(host);
        host.Clock.SetTo(DateTimeOffset.MinValue.AddYears(1));
        var early = await EvaluateAsync(host);
        host.Clock.SetTo(DateTimeOffset.MaxValue.AddYears(-1));
        var late = await EvaluateAsync(host);

        foreach (var decision in new[] { now, early, late })
        {
            Assert.Equal(AuthorizationOutcome.Denied, decision.Outcome);
            AssertUnavailable("mirror", decision);
        }

        Assert.Equal(0, mirror.GrantsCalls);
    }

    [Fact]
    public async Task S39_3_A_mirror_whose_stamp_errs_is_unavailable_and_is_never_asked_for_grants()
    {
        var mirror = new StubMirroredProvider("mirror", grants: true)
        {
            Stamp = () => Result<DateTimeOffset?, AuthorizationError>.Failure(
                AuthorizationError.PermissionDenied(Permission)),
        };
        await using var host = await StartAsync(services => services.AddSingleton<IPermissionProvider>(mirror));

        var decision = await EvaluateAsync(host);

        Assert.Equal(AuthorizationOutcome.Denied, decision.Outcome);
        AssertUnavailable("mirror", decision);
        Assert.Equal(0, mirror.GrantsCalls);
    }

    [Fact]
    public async Task S39_4_A_stale_mirror_beside_a_granting_provider_allows_from_that_provider_alone()
    {
        await using var host = await StartAsync(services =>
        {
            services.AddSingleton<IPermissionProvider>(NeverSynced("mirror"));
            services.AddSingleton<IPermissionProvider>(Granting("direct"));
        });

        var decision = await EvaluateAsync(host);

        Assert.Equal(AuthorizationOutcome.Allowed, decision.Outcome);
        Assert.Equal("direct", Assert.Single(decision.Sources).Value);
        Assert.Null(decision.ProviderFailure);
    }

    [Fact]
    public async Task S39_4_A_stale_mirror_registered_before_an_erroring_provider_is_the_one_named()
    {
        await using var host = await StartAsync(services =>
        {
            services.AddSingleton<IPermissionProvider>(NeverSynced("mirror"));
            services.AddSingleton<IPermissionProvider>(Erroring("erroring"));
        });

        AssertUnavailable("mirror", await EvaluateAsync(host));
    }

    [Fact]
    public async Task S39_4_A_stale_mirror_registered_after_an_erroring_provider_is_not_the_one_named()
    {
        await using var host = await StartAsync(services =>
        {
            services.AddSingleton<IPermissionProvider>(Erroring("erroring"));
            services.AddSingleton<IPermissionProvider>(NeverSynced("mirror"));
        });

        AssertUnavailable("erroring", await EvaluateAsync(host));
    }

    [Fact]
    public async Task S39_5_A_thirty_second_limit_allows_at_thirty_seconds_and_is_unavailable_one_tick_later()
    {
        var mirror = new StubMirroredProvider("mirror", grants: true);
        await using var host = await StartAsync(
            services => services.AddSingleton<IPermissionProvider>(mirror),
            maximumAge: "00:00:30");
        var syncedAt = host.Clock.UtcNow;
        mirror.Stamp = () => Synced(syncedAt);

        host.Clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(AuthorizationOutcome.Allowed, (await EvaluateAsync(host)).Outcome);

        host.Clock.Advance(TimeSpan.FromTicks(1));
        AssertUnavailable("mirror", await EvaluateAsync(host));
    }

    [Theory]
    [InlineData("00:00:30", 30)]
    [InlineData("1.00:00:00", 86_400)]
    public async Task S39_5_The_bounds_themselves_start_the_host(string value, int seconds)
    {
        await using var host = await StartAsync(_ => { }, maximumAge: value);

        Assert.Equal(
            TimeSpan.FromSeconds(seconds),
            host.Services.GetRequiredService<PlatformOptions>().Authorization.MirrorMaximumAge);
    }

    [Theory]
    [InlineData("00:00:29")]
    [InlineData("1.00:00:01")]
    [InlineData("00:00:00")]
    [InlineData("-00:00:01")]
    public void S39_5_A_value_outside_the_bounds_fails_startup_naming_the_setting(string value)
    {
        var settings = Settings.Required();
        settings[Key] = value;
        var builder = Microsoft.Extensions.Hosting.Host.CreateEmptyApplicationBuilder(
            new Microsoft.Extensions.Hosting.HostApplicationBuilderSettings { EnvironmentName = "Production" });
        Microsoft.Extensions.Configuration.MemoryConfigurationBuilderExtensions.AddInMemoryCollection(
            builder.Configuration, settings);

        var thrown = Assert.Throws<PlatformStartupException>(() => builder.AddPlatformWebHost());
        var error = Assert.IsType<HostStartupError>(thrown.Error);

        Assert.Equal(nameof(ConfigurationError.InvalidSetting), error.Inner?.Code);
        Assert.Contains(Key, error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task S39_5_Leaving_the_setting_unset_binds_fifteen_minutes()
    {
        await using var host = await StartAsync(_ => { });

        Assert.Equal(
            TimeSpan.FromMinutes(15),
            host.Services.GetRequiredService<PlatformOptions>().Authorization.MirrorMaximumAge);
    }

    [Fact]
    public void S39_5_Changing_the_limit_changes_the_settings_fingerprint()
    {
        ISettingsFingerprint fingerprint = new SettingsFingerprint();

        var fifteen = fingerprint.Compute(Baseline(), []);
        var thirty = fingerprint.Compute(
            Baseline() with { Authorization = new AuthorizationOptions { MirrorMaximumAge = TimeSpan.FromSeconds(30) } },
            []);

        Assert.NotEqual(fifteen, thirty);
    }

    [Fact]
    public async Task S39_6_An_endpoint_whose_only_grant_is_a_stale_mirror_answers_503_and_never_runs()
    {
        var handlerRuns = 0;
        var (app, client) = await WebHostUnderTest.StartAsync(
            services =>
            {
                services.AddSingleton<IAuthenticationProvider>(new StubAuthenticationProvider("mirrored-provider-tests"));
                services.AddSingleton<IAuditSink>(new RecordingAuditSink("mirrored-provider-tests", isDurable: true));
                services.AddSingleton<IPermissionCatalog>(new StubPermissionCatalog(Permission));
                services.AddSingleton<IPermissionProvider>(NeverSynced("mirror"));
            },
            composeOperatedDefaults: false,
            mapEndpoints: application => application
                .MapGet("/guarded", () =>
                {
                    handlerRuns++;
                    return Results.Ok();
                })
                .RequiresPlatformAuthorization(Permission, feature: null));

        await using (app)
        using (client)
        {
            var response = await client.GetAsync("/guarded");

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(CancellationToken.None);
            Assert.Equal(nameof(AuthorizationError.ProviderUnavailable), body.GetProperty("code").GetString());
            Assert.Equal(0, handlerRuns);
        }
    }

    [Fact]
    public async Task S39_6_A_tool_whose_only_grant_is_a_stale_mirror_is_retryable_and_never_reaches_the_invoker()
    {
        var mirror = new StubMirroredProvider("mirror", grants: true, McpInvocationTests.UsePermission)
        {
            Stamp = () => Result<DateTimeOffset?, AuthorizationError>.Success(null),
        };
        var invoker = new McpInvocationTests.StubToolInvoker("producer", (_, _) => ToolInvocationResult.Success("ok"));
        await using var harness = await McpInvocationTests.Harness.StartAsync(
            exposed: [new ToolName("guarded")],
            extra: [
                new StubToolProducer("producer", new ToolDefinition(
                    new ToolName("guarded"), "Guarded.", McpInvocationTests.Schema(), McpInvocationTests.UsePermission, null)),
                invoker,
                (Action<IServiceCollection>)(services => services.AddSingleton<IPermissionProvider>(mirror)),
            ],
            grantsPermission: false);

        await using var client = await harness.ConnectAsync("alice");
        var result = await client.CallToolAsync(
            "guarded", new Dictionary<string, object?>(), cancellationToken: CancellationToken.None);

        var text = string.Join(" ", result.Content.OfType<TextContentBlock>().Select(block => block.Text));
        Assert.True(result.IsError);
        Assert.Equal(0, invoker.CallCount);
        Assert.Equal(0, mirror.GrantsCalls);
        Assert.Contains("may be retried", text, StringComparison.Ordinal);
        Assert.DoesNotContain("denied", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task S39_7_A_provider_that_is_not_mirrored_is_never_asked_for_a_timestamp()
    {
        var lookalike = new LookalikeProvider();
        await using var host = await StartAsync(services => services.AddSingleton<IPermissionProvider>(lookalike));

        var decision = await EvaluateAsync(host);

        Assert.Equal(AuthorizationOutcome.Allowed, decision.Outcome);
        Assert.Equal(1, lookalike.GrantsCalls);
        Assert.Equal(0, lookalike.StampCalls);
    }

    private static Result<DateTimeOffset?, AuthorizationError> Synced(DateTimeOffset at) =>
        Result<DateTimeOffset?, AuthorizationError>.Success(at);

    private static StubMirroredProvider NeverSynced(string name) =>
        new(name, grants: true) { Stamp = () => Result<DateTimeOffset?, AuthorizationError>.Success(null) };

    private static StubPermissionProvider Granting(string name) =>
        new(name, (_, _, _) => Result<IReadOnlySet<PermissionName>, AuthorizationError>.Success(
            new HashSet<PermissionName> { Permission }));

    private static StubPermissionProvider Erroring(string name) =>
        new(name, (_, _, _) => Result<IReadOnlySet<PermissionName>, AuthorizationError>.Failure(
            AuthorizationError.PermissionDenied(Permission)));

    private static void AssertUnavailable(string provider, AuthorizationDecision decision)
    {
        Assert.Equal(AuthorizationOutcome.Denied, decision.Outcome);
        Assert.Equal(AuthorizationError.ProviderUnavailable(new PermissionProviderName(provider)), decision.ProviderFailure);
    }

    private static async Task<IPlatformTestHost> StartAsync(
        Action<IServiceCollection> configure, string? maximumAge = null)
    {
        var builder = PlatformTestHost.CreateBuilder()
            .WithServices(services =>
            {
                services.AddSingleton<IPermissionCatalog>(new StubPermissionCatalog(Permission));
                configure(services);
            });

        if (maximumAge is not null)
        {
            builder = builder.WithSetting("Authorization:MirrorMaximumAge", maximumAge);
        }

        return await builder.StartAsync(CancellationToken.None);
    }

    private static async Task<AuthorizationDecision> EvaluateAsync(IPlatformTestHost host)
    {
        var evaluator = host.Services.GetRequiredService<IAuthorizationEvaluator>();
        var scopeFactory = host.Services.GetRequiredService<IOperationScopeFactory>();

        using (scopeFactory.Begin(TestTenant, FakePrincipals.System))
        {
            return await evaluator.EvaluateAsync(Permission, null, CancellationToken.None);
        }
    }

    private static PlatformOptions Baseline() => new()
    {
        CompositionProfile = CompositionProfile.Operated,
        Persistence = new PersistenceOptions
        {
            Provider = PersistenceProvider.Sqlite,
            ConnectionString = "Data Source=:memory:",
        },
        Outbox = new OutboxOptions
        {
            ProcessedRetention = TimeSpan.FromDays(1),
            PoisonedRetention = TimeSpan.FromDays(7),
        },
    };

    /// <summary>A mirrored provider whose stamp a test sets, counting both calls.</summary>
    private sealed class StubMirroredProvider(string name, bool grants, PermissionName? permission = null)
        : IMirroredPermissionProvider
    {
        private int _grantsCalls;

        public PermissionProviderName Name { get; } = new(name);

        public Func<Result<DateTimeOffset?, AuthorizationError>> Stamp { get; set; } =
            () => Result<DateTimeOffset?, AuthorizationError>.Success(null);

        public int GrantsCalls => Volatile.Read(ref _grantsCalls);

        public List<TenantId> StampTenants { get; } = [];

        public Task<Result<DateTimeOffset?, AuthorizationError>> LastSyncedAsync(
            TenantId tenant, CancellationToken cancellationToken)
        {
            StampTenants.Add(tenant);
            return Task.FromResult(Stamp());
        }

        public Task<Result<IReadOnlySet<PermissionName>, AuthorizationError>> GrantsAsync(
            Principal principal, TenantId tenant, ResourceRef? resource, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _grantsCalls);
            IReadOnlySet<PermissionName> granted = grants
                ? new HashSet<PermissionName> { permission ?? Permission }
                : new HashSet<PermissionName>();
            return Task.FromResult(Result<IReadOnlySet<PermissionName>, AuthorizationError>.Success(granted));
        }
    }

    /// <summary>Not mirrored, but carries a method shaped exactly like
    /// <see cref="IMirroredPermissionProvider.LastSyncedAsync"/> — the evaluator must decide by the
    /// interface, never by shape.</summary>
    private sealed class LookalikeProvider : IPermissionProvider
    {
        public PermissionProviderName Name { get; } = new("lookalike");

        public int GrantsCalls { get; private set; }

        public int StampCalls { get; private set; }

        public Task<Result<DateTimeOffset?, AuthorizationError>> LastSyncedAsync(
            TenantId tenant, CancellationToken cancellationToken)
        {
            StampCalls++;
            return Task.FromResult(Result<DateTimeOffset?, AuthorizationError>.Success(null));
        }

        public Task<Result<IReadOnlySet<PermissionName>, AuthorizationError>> GrantsAsync(
            Principal principal, TenantId tenant, ResourceRef? resource, CancellationToken cancellationToken)
        {
            GrantsCalls++;
            return Task.FromResult(Result<IReadOnlySet<PermissionName>, AuthorizationError>.Success(
                new HashSet<PermissionName> { Permission }));
        }
    }
}
