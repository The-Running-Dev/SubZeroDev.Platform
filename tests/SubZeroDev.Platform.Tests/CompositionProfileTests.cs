using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Hosting;
using SubZeroDev.Platform.Mcp;

namespace SubZeroDev.Platform.Tests;

/// <summary>D5-S8's startup half: a host whose installed packages and declared shape disagree
/// refuses to start, instead of serving something nobody meant to run.</summary>
/// <remarks>Every assertion here goes through a real host start rather than calling the validator
/// directly, because the property under test is that the <em>host</em> fails — a validator that
/// returns a violation nothing acts on is the degradation this slice exists to rule out (I-C9).
/// The validator's own findings are asserted through the same path for the same reason.</remarks>
public sealed class CompositionProfileTests
{
    /// <summary>S8.5 — <c>Operated</c> with no authentication provider registered.</summary>
    [Fact]
    public async Task Operated_with_no_authentication_provider_fails_startup_naming_the_profile()
    {
        var error = await RefusedAsync(
            settings: Profile(CompositionProfile.Operated),
            services: services => services.AddSingleton<IAuditSink>(
                new RecordingAuditSink("durable", isDurable: true)));

        Assert.Equal(nameof(HostStartupError.AuthenticationProviderRequired), error.Code);
        Assert.Contains(nameof(CompositionProfile.Operated), error.Detail, StringComparison.Ordinal);
        Assert.Contains(nameof(IAuthenticationProvider), error.Detail, StringComparison.Ordinal);
    }

    /// <summary>S8.6 — <c>Operated</c> with no sink declaring <c>IsDurable</c>. The default log sink
    /// is registered by the framework and declares <see langword="false"/>, so this host has a sink
    /// and still fails: the check is on the declaration, never on there being one.</summary>
    [Fact]
    public async Task Operated_with_no_durable_sink_fails_startup_and_the_log_sink_does_not_satisfy_it()
    {
        var error = await RefusedAsync(
            settings: Profile(CompositionProfile.Operated),
            services: services => services.AddSingleton<IAuthenticationProvider>(
                new StubAuthenticationProvider("provider")));

        Assert.Equal(nameof(HostStartupError.DurableAuditSinkRequired), error.Code);
        Assert.Contains(nameof(CompositionProfile.Operated), error.Detail, StringComparison.Ordinal);
        Assert.Contains(nameof(IAuditSink.IsDurable), error.Detail, StringComparison.Ordinal);
    }

    /// <summary>S8.6, second half — a non-durable sink registered beside the log sink still fails.
    /// Two sinks that both decline durability are not one durable sink.</summary>
    [Fact]
    public async Task A_second_non_durable_sink_does_not_satisfy_the_operated_requirement()
    {
        var error = await RefusedAsync(
            settings: Profile(CompositionProfile.Operated),
            services: services =>
            {
                services.AddSingleton<IAuthenticationProvider>(new StubAuthenticationProvider("provider"));
                services.AddSingleton<IAuditSink>(new RecordingAuditSink("not-durable", isDurable: false));
            });

        Assert.Equal(nameof(HostStartupError.DurableAuditSinkRequired), error.Code);
        Assert.Contains("not-durable", error.Detail, StringComparison.Ordinal);
    }

    /// <summary>S8.7 — <c>Local</c> with an authentication provider.</summary>
    [Fact]
    public async Task Local_with_an_authentication_provider_is_refused_naming_the_registration()
    {
        var error = await RefusedAsync(
            settings: Profile(CompositionProfile.Local),
            services: services => services.AddSingleton<IAuthenticationProvider>(
                new StubAuthenticationProvider("issuer-under-test")));

        Assert.Equal(nameof(HostStartupError.RegistrationForbiddenByProfile), error.Code);
        Assert.Contains(nameof(CompositionProfile.Local), error.Detail, StringComparison.Ordinal);
        Assert.Contains("issuer-under-test", error.Detail, StringComparison.Ordinal);
        Assert.Contains(nameof(IAuthenticationProvider), error.Detail, StringComparison.Ordinal);
    }

    /// <summary>S8.7 — <c>Local</c> with a tenant resolver.</summary>
    [Fact]
    public async Task Local_with_a_tenant_resolver_is_refused_naming_the_registration()
    {
        var error = await RefusedAsync(
            settings: Profile(CompositionProfile.Local),
            services: services => services.AddSingleton<ITenantResolver>(
                new StubTenantResolver("organizations-under-test", () => null)));

        Assert.Equal(nameof(HostStartupError.RegistrationForbiddenByProfile), error.Code);
        Assert.Contains("organizations-under-test", error.Detail, StringComparison.Ordinal);
        Assert.Contains(nameof(ITenantResolver), error.Detail, StringComparison.Ordinal);
    }

    /// <summary>S8.7 — <c>Local</c> with an entitlement contributor other than the Community
    /// baseline. The baseline itself is registered by the framework in both profiles, so a host
    /// that passes this check has exactly one contributor rather than none.</summary>
    [Fact]
    public async Task Local_with_a_non_baseline_entitlement_contributor_is_refused_naming_the_registration()
    {
        var error = await RefusedAsync(
            settings: Profile(CompositionProfile.Local),
            services: services => services.AddKeyedSingleton<IEntitlementContributor>(
                EntitlementContributorRegistration.ServiceKey,
                new StubEntitlementContributor(
                    "licensing-under-test",
                    (_, _) => Result<bool, EntitlementError>.Success(true))));

        Assert.Equal(nameof(HostStartupError.RegistrationForbiddenByProfile), error.Code);
        Assert.Contains("licensing-under-test", error.Detail, StringComparison.Ordinal);
        Assert.Contains(nameof(IEntitlementContributor), error.Detail, StringComparison.Ordinal);
    }

    /// <summary>S8.7, the negative case that makes the three above mean something: a <c>Local</c>
    /// host registering none of the three starts, with the Community baseline present.</summary>
    [Fact]
    public async Task Local_with_none_of_the_three_starts_and_keeps_the_community_baseline()
    {
        var (app, _) = await WebHostUnderTest.StartAsync(
            settings: Profile(CompositionProfile.Local),
            composeOperatedDefaults: false);

        try
        {
            var contributors = app.Services.GetRequiredService<IEntitlementContributorRegistry>();
            Assert.Single(contributors.Registered);
            Assert.Empty(app.Services.GetRequiredService<IAuthenticationProviderRegistry>().Registered);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    /// <summary>S8.12 — every check in this slice fails the host rather than degrading it. Asserted
    /// as the absence of a serving host: the refusal is an exception out of <c>StartAsync</c>, so
    /// there is no port bound and no probe to answer "degraded".</summary>
    [Fact]
    public async Task Every_composition_check_aborts_startup_rather_than_degrading_the_host()
    {
        var refusals = new[]
        {
            await RefusedAsync(Profile(CompositionProfile.Operated), services: null),
            await RefusedAsync(
                Profile(CompositionProfile.Operated),
                services => services.AddSingleton<IAuthenticationProvider>(
                    new StubAuthenticationProvider("provider"))),
            await RefusedAsync(
                Profile(CompositionProfile.Local),
                services => services.AddSingleton<IAuthenticationProvider>(
                    new StubAuthenticationProvider("provider"))),
        };

        Assert.Equal(
            [
                nameof(HostStartupError.AuthenticationProviderRequired),
                nameof(HostStartupError.DurableAuditSinkRequired),
                nameof(HostStartupError.RegistrationForbiddenByProfile),
            ],
            refusals.Select(refusal => refusal.Code));

        // None is retryable: a misconfigured installation does not resolve itself.
        Assert.All(refusals, refusal => Assert.False(refusal.IsRetryable));

        // Each names something the operator can act on rather than restating the rule.
        Assert.All(refusals, refusal => Assert.NotEmpty(refusal.Detail));
    }

    /// <summary>The fifth registry closes with the other four: a provider arriving after startup is
    /// rejected rather than mutating a set the composition was already validated against.</summary>
    [Fact]
    public async Task The_authentication_provider_registry_is_frozen_after_startup()
    {
        var (app, _) = await WebHostUnderTest.StartAsync();

        try
        {
            var registry = app.Services.GetRequiredService<IAuthenticationProviderRegistry>();
            var late = registry.Register(new StubAuthenticationProvider("late"));

            Assert.False(late.IsSuccess);
            Assert.Equal(
                nameof(AuthenticationProviderRegistrationError.RegistryFrozen),
                late.Error.Code);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    /// <summary>S29.1 (I-C9): the variants are read off <see cref="HostStartupError"/> itself, so
    /// one added later arrives here as a row with no provocation and fails, rather than going
    /// unexercised because nobody remembered to list it.</summary>
    public static TheoryData<string> StartupErrorVariants()
    {
        var variants = new TheoryData<string>();
        foreach (var factory in typeof(HostStartupError)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.ReturnType == typeof(HostStartupError))
            .OrderBy(method => method.Name, StringComparer.Ordinal))
        {
            variants.Add(factory.Name);
        }

        return variants;
    }

    /// <summary>S29.1 (I-C9) — every startup check fails the host and names the registration that
    /// caused it. Each variant is provoked through a real host build and start, and the refusal must
    /// carry that variant's code, decline to be retried, and name the thing the provocation
    /// registered or set — a token chosen per row so a generic message cannot satisfy it.</summary>
    [Theory]
    [MemberData(nameof(StartupErrorVariants))]
    public async Task S29_1_Every_startup_error_variant_fails_the_host_naming_its_cause(string variant)
    {
        Assert.True(
            Provocations.TryGetValue(variant, out var provocation),
            $"HostStartupError.{variant} has no provocation in {nameof(Provocations)}; add one.");

        var (refuse, names) = provocation;
        var error = await refuse();

        Assert.Equal(variant, error.Code);
        Assert.False(error.IsRetryable);
        Assert.Contains(names, error.Detail, StringComparison.Ordinal);
    }

    /// <summary>One provocation per variant, and the token its refusal must name.</summary>
    private static readonly Dictionary<string, (Func<Task<HostStartupError>> Refuse, string Names)> Provocations = new()
    {
        [nameof(HostStartupError.Configuration)] = (
            () => RefusedAsync(
                new Dictionary<string, string?> { ["Platform:Outbox:ClaimWindow"] = "s29 is not a duration" },
                services: null,
                composeOperatedDefaults: true),
            "Platform:Outbox:ClaimWindow"),

        [nameof(HostStartupError.ModuleGraph)] = (
            () => RefusedAsync(
                new Dictionary<string, string?>(),
                services => services.AddSingleton<IPlatformModule>(new StubModule("S29.Orders", "S29.Invoices")),
                composeOperatedDefaults: true),
            "S29.Invoices"),

        [nameof(HostStartupError.Registration)] = (
            () => RefusedAsync(
                new Dictionary<string, string?>(),
                services =>
                {
                    services.AddSingleton<IAuditSink>(new RecordingAuditSink("s29-shared", isDurable: true));
                    services.AddSingleton<IAuditSink>(new RecordingAuditSink("s29-shared", isDurable: true));
                },
                composeOperatedDefaults: true),
            "s29-shared"),

        [nameof(HostStartupError.AuthenticationProviderRequired)] = (
            () => RefusedAsync(
                Profile(CompositionProfile.Operated),
                services => services.AddSingleton<IAuditSink>(new RecordingAuditSink("durable", isDurable: true))),
            nameof(IAuthenticationProvider)),

        [nameof(HostStartupError.DurableAuditSinkRequired)] = (
            () => RefusedAsync(
                Profile(CompositionProfile.Operated),
                services =>
                {
                    services.AddSingleton<IAuthenticationProvider>(new StubAuthenticationProvider("provider"));
                    services.AddSingleton<IAuditSink>(new RecordingAuditSink("s29-volatile", isDurable: false));
                }),
            "s29-volatile"),

        [nameof(HostStartupError.RegistrationForbiddenByProfile)] = (
            () => RefusedAsync(
                Profile(CompositionProfile.Local),
                services => services.AddSingleton<IAuthenticationProvider>(new StubAuthenticationProvider("s29-issuer"))),
            "s29-issuer"),

        [nameof(HostStartupError.ProbeBindFailed)] = (WorkerProbeRefusedAsync, "Platform:Hosting:WorkerProbePort"),

        [nameof(HostStartupError.UndeclaredEndpointRequirement)] = (
            () => RefusedAsync(
                new Dictionary<string, string?>(),
                services: null,
                composeOperatedDefaults: true,
                mapEndpoints: application => application.MapGet("/s29-undeclared", () => Results.Ok())),
            "/s29-undeclared"),

        [nameof(HostStartupError.SensitiveToolParameter)] = (
            () => RefusedAsync(
                new Dictionary<string, string?>(),
                services =>
                {
                    var permission = new PermissionName("S29.Tool.Use");
                    services.AddSingleton<IPlatformModule, McpModule>();
                    services.AddSingleton(new McpOptions { ExposedTools = new HashSet<ToolName> { new("s29-leaky-tool") } });
                    services.AddSingleton<IToolProducer>(new StubToolProducer(
                        "s29-producer",
                        new ToolDefinition(
                            new ToolName("s29-leaky-tool"),
                            "Names a sensitive parameter.",
                            McpInvocationTests.Schema("apiKey"),
                            permission,
                            null)));
                    services.AddSingleton<IPermissionCatalog>(new StubPermissionCatalog(permission));
                },
                composeOperatedDefaults: true),
            "s29-leaky-tool"),

        [nameof(HostStartupError.UnregisteredPermission)] = (
            () => RefusedAsync(
                new Dictionary<string, string?>(),
                services: null,
                composeOperatedDefaults: true,
                mapEndpoints: application => application
                    .MapGet("/s29-unregistered", () => Results.Ok())
                    .RequiresPlatformAuthorization(new PermissionName("S29.Undeclared"), feature: null)),
            "S29.Undeclared"),
    };

    /// <summary>The worker host binds its probe port while it is being built, so the one variant
    /// that is about a port is provoked on that path: the port is held open first.</summary>
    private static Task<HostStartupError> WorkerProbeRefusedAsync()
    {
        var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();

        try
        {
            var settings = Settings.Required();
            settings["Platform:Hosting:WorkerProbePort"] = ((IPEndPoint)occupied.LocalEndpoint).Port.ToString();

            var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
            {
                EnvironmentName = "Production",
            });
            builder.Configuration.AddInMemoryCollection(settings);

            var thrown = Assert.Throws<PlatformStartupException>(() => builder.AddPlatformWorkerHost());
            return Task.FromResult(Assert.IsType<HostStartupError>(thrown.Error));
        }
        finally
        {
            occupied.Stop();
        }
    }

    private static Dictionary<string, string?> Profile(CompositionProfile profile) =>
        new() { ["Platform:CompositionProfile"] = profile.ToString() };

    /// <summary>Starts a host that is expected to refuse, and returns why.</summary>
    private static async Task<HostStartupError> RefusedAsync(
        IDictionary<string, string?> settings,
        Action<IServiceCollection>? services,
        bool composeOperatedDefaults = false,
        Action<WebApplication>? mapEndpoints = null)
    {
        WebApplication? app = null;
        try
        {
            (app, _) = await WebHostUnderTest.StartAsync(
                services, settings, composeOperatedDefaults: composeOperatedDefaults, mapEndpoints: mapEndpoints);
        }
        catch (PlatformStartupException exception)
        {
            return Assert.IsType<HostStartupError>(exception.Error);
        }
        finally
        {
            if (app is not null)
            {
                await app.DisposeAsync();
            }
        }

        throw new InvalidOperationException("The host started; it was expected to refuse.");
    }
}
