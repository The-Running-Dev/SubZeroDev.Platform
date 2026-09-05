using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Hosting;

namespace SubZeroDev.Platform.Tests;

/// <summary>D5-S8's fixed request order: a single request through either deployment shape runs
/// authenticate, resolve tenant, open scope, authorize, check entitlement, do the work, audit — in
/// that order, with no step skipped and no branch taken.</summary>
public sealed class RequestOrderTests
{
    private static readonly PermissionName TracedPermission = new("Test.RequestOrder.Traced");
    private static readonly FeatureName TracedFeature = new("Test.RequestOrder.Feature");

    /// <summary>S8.1 — a single request through the operated host produces an ordered trace of
    /// exactly the seven steps. Steps 1-3 are traced by decorating the seams the pipeline itself
    /// calls; steps 4-5 are traced by the providers the endpoint's declaration reaches; steps 6-7
    /// are traced by the handler itself, which is the only code that can know when its own work and
    /// its own audit write happen.</summary>
    [Fact]
    public async Task S8_1_A_single_request_through_the_operated_host_produces_the_seven_steps_in_order()
    {
        var trace = new List<string>();

        var (app, client) = await WebHostUnderTest.StartAsync(
            services => ComposeTracedSeam(services, trace, grantPermission: true, grantFeature: true),
            postCompose: services => SpliceScopeTracer(services, trace),
            mapEndpoints: application => MapTracedEndpoint(application, trace, TracedFeature));

        try
        {
            using var response = await client.GetAsync("/traced");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(
                new[]
                {
                    "authenticate", "resolve-tenant", "open-scope", "authorize", "check-entitlement",
                    "do-the-work", "audit",
                },
                trace);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    /// <summary>S8.2 — the same request through the local host produces the same seven steps in the
    /// same order, with no step skipped and no branch taken. Local forbids an authentication
    /// provider and a tenant resolver outright (I-C3), so steps 1-2 are traced through the chains'
    /// own empty-registry answers rather than through a registered provider — the absence of the
    /// four commercial packages is visible in the package graph and invisible in the flow.</summary>
    [Fact]
    public async Task S8_2_The_same_seven_steps_run_in_the_same_order_through_the_local_host()
    {
        var trace = new List<string>();

        var (app, client) = await WebHostUnderTest.StartAsync(
            services =>
            {
                // Local forbids a registered authentication provider and tenant resolver (I-C3), so
                // steps 1-2 are observed through the chain's own behaviour with nothing registered —
                // traced by decorating the chain services themselves rather than a provider.
                services.AddSingleton<IPermissionProvider>(new StubPermissionProvider(
                    "local-trace-permission",
                    (_, _, _) =>
                    {
                        lock (trace)
                        {
                            trace.Add("authorize");
                        }

                        return Result<IReadOnlySet<PermissionName>, AuthorizationError>.Success(
                            new HashSet<PermissionName> { TracedPermission });
                    }));
                services.AddSingleton<IPermissionCatalog>(new StubPermissionCatalog(TracedPermission));

                // Local allows only the Community baseline contributor (I-C3) — widened here to
                // grant the traced feature, since a test-registered contributor of its own would be
                // exactly the registration the profile forbids.
                services.AddSingleton(new CommunityBaselineOptions(new HashSet<FeatureName> { TracedFeature }));
            },
            settings: new Dictionary<string, string?> { ["Platform:CompositionProfile"] = nameof(CompositionProfile.Local) },
            composeOperatedDefaults: false,
            postCompose: services =>
            {
                SpliceScopeTracer(services, trace);
                SpliceAuthenticationRegistryTracer(services, trace);
                SpliceTenantResolverRegistryTracer(services, trace);
                SpliceEntitlementEvaluatorTracer(services, trace);
            },
            mapEndpoints: application => MapTracedEndpoint(application, trace, TracedFeature));

        try
        {
            // Startup itself reads both registries' `Registered` once, to validate the profile
            // (I-C3) before anything serves — discarded here so the trace below is the request's
            // own, not startup's.
            trace.Clear();

            using var response = await client.GetAsync("/traced");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(
                new[]
                {
                    "authenticate", "resolve-tenant", "open-scope", "authorize", "check-entitlement",
                    "do-the-work", "audit",
                },
                trace);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    /// <summary>S8.3 — a request that is both unauthorized and unentitled is refused as
    /// unauthorized, and the trace shows no entitlement evaluation ran. Authorization precedes
    /// entitlement, and both precede any side effect: reversing them would turn every entitlement
    /// into an unauthenticated probe.</summary>
    [Fact]
    public async Task S8_3_Denied_and_unentitled_is_refused_as_unauthorized_and_entitlement_never_runs()
    {
        var trace = new List<string>();

        var (app, client) = await WebHostUnderTest.StartAsync(
            services => ComposeTracedSeam(services, trace, grantPermission: false, grantFeature: false),
            postCompose: services => SpliceScopeTracer(services, trace),
            mapEndpoints: application => MapTracedEndpoint(application, trace, TracedFeature));

        try
        {
            using var response = await client.GetAsync("/traced");

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains(nameof(AuthorizationError.PermissionDenied), body, StringComparison.Ordinal);

            Assert.Equal(new[] { "authenticate", "resolve-tenant", "open-scope", "authorize" }, trace);
            Assert.DoesNotContain("check-entitlement", trace);
            Assert.DoesNotContain("do-the-work", trace);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    /// <summary>S8.4 — an endpoint that only reads runs no entitlement evaluation at all, even for
    /// data produced under a gated feature: it declares no feature, and entitlement was checked at
    /// the admission that produced the data, not re-asked here.</summary>
    [Fact]
    public async Task S8_4_An_endpoint_declaring_no_feature_runs_no_entitlement_evaluation_at_all()
    {
        var trace = new List<string>();

        var (app, client) = await WebHostUnderTest.StartAsync(
            services => ComposeTracedSeam(services, trace, grantPermission: true, grantFeature: true),
            postCompose: services => SpliceScopeTracer(services, trace),
            mapEndpoints: application => MapTracedEndpoint(application, trace, requiredFeature: null));

        try
        {
            using var response = await client.GetAsync("/traced");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(
                new[] { "authenticate", "resolve-tenant", "open-scope", "authorize", "do-the-work", "audit" },
                trace);
            Assert.DoesNotContain("check-entitlement", trace);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    /// <summary>I-R6 — a mapped endpoint carrying neither a requirement nor an exemption fails
    /// startup naming the route, rather than serving an endpoint nobody declared anything about.</summary>
    [Fact]
    public async Task An_undeclared_endpoint_fails_startup_naming_the_route()
    {
        var thrown = await Assert.ThrowsAsync<PlatformStartupException>(() => WebHostUnderTest.StartAsync(
            mapEndpoints: application => application.MapGet("/undeclared", () => Results.Ok())));

        var error = Assert.IsType<HostStartupError>(thrown.Error);
        Assert.Equal(nameof(HostStartupError.UndeclaredEndpointRequirement), error.Code);
        Assert.Contains("/undeclared", error.Detail, StringComparison.Ordinal);
    }

    /// <summary>An endpoint's declared permission is a registration like any other module's — a
    /// name no catalog declares fails startup as <c>UnregisteredPermission</c> rather than denying
    /// silently at request time.</summary>
    [Fact]
    public async Task An_endpoint_requiring_an_unregistered_permission_fails_startup()
    {
        var undeclared = new PermissionName("Test.RequestOrder.Undeclared");

        var thrown = await Assert.ThrowsAsync<PlatformStartupException>(() => WebHostUnderTest.StartAsync(
            mapEndpoints: application => application
                .MapGet("/unregistered", () => Results.Ok())
                .RequiresPlatformAuthorization(undeclared, feature: null)));

        var error = Assert.IsType<HostStartupError>(thrown.Error);
        Assert.Equal(nameof(HostStartupError.Registration), error.Code);
        Assert.Equal(nameof(PermissionCatalogError.UnregisteredPermission), error.Inner?.Code);
    }

    /// <summary>Registers a provider and a contributor that both grant (or both refuse, per
    /// <paramref name="grantPermission"/>/<paramref name="grantFeature"/>) the traced endpoint's
    /// declared requirement, appending to <paramref name="trace"/> when consulted — plus the
    /// authentication provider and tenant resolver that trace steps 1-2.</summary>
    private static void ComposeTracedSeam(
        IServiceCollection services, List<string> trace, bool grantPermission, bool grantFeature)
    {
        services.AddSingleton<IAuthenticationProvider>(new StubAuthenticationProvider(
            "trace-auth",
            _ =>
            {
                lock (trace)
                {
                    trace.Add("authenticate");
                }

                return Result<Principal, AuthenticationError>.Success(Principal.Anonymous);
            }));

        services.AddSingleton<IAuditSink>(new RecordingAuditSink("trace-audit", isDurable: true));

        services.AddSingleton<ITenantResolver>(new StubTenantResolver(
            "trace-tenant",
            () =>
            {
                lock (trace)
                {
                    trace.Add("resolve-tenant");
                }

                return null;
            }));

        services.AddSingleton<IPermissionCatalog>(new StubPermissionCatalog(TracedPermission));
        services.AddSingleton<IPermissionProvider>(new StubPermissionProvider(
            "trace-permission",
            (_, _, _) =>
            {
                lock (trace)
                {
                    trace.Add("authorize");
                }

                return Result<IReadOnlySet<PermissionName>, AuthorizationError>.Success(
                    grantPermission ? new HashSet<PermissionName> { TracedPermission } : new HashSet<PermissionName>());
            }));

        services.AddKeyedSingleton<IEntitlementContributor>(
            EntitlementContributorRegistration.ServiceKey,
            new StubEntitlementContributor(
                "trace-entitlement",
                (_, _) =>
                {
                    lock (trace)
                    {
                        trace.Add("check-entitlement");
                    }

                    return Result<bool, EntitlementError>.Success(grantFeature);
                }));
    }

    /// <summary>Splices <see cref="TracingOperationScopeFactory"/> over the real
    /// <see cref="IOperationScopeFactory"/> registration <c>AddPlatformWebHost</c> made — the same
    /// decorator-over-the-real-registration idiom <c>PlatformTestHost</c> uses for the outbox
    /// writer, needed because a plain registration made before composition would be shadowed by the
    /// platform's own <c>TryAddSingleton</c> rather than the other way around.</summary>
    private static void SpliceScopeTracer(IServiceCollection services, List<string> trace)
    {
        var descriptor = services.Single(d => d.ServiceType == typeof(IOperationScopeFactory));
        services.Remove(descriptor);
        services.AddSingleton<IOperationScopeFactory>(provider =>
        {
            var real = (IOperationScopeFactory)ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!);
            return new TracingOperationScopeFactory(real, trace);
        });
    }

    /// <summary>Splices a trace entry over <see cref="IAuthenticationProviderRegistry"/>'s
    /// registered instance, read once per <see cref="AuthenticationChain"/> call — used only where
    /// no provider is registered (Local, I-C3), so step 1 is traced through the chain's own
    /// empty-registry read rather than through a provider that profile forbids.</summary>
    private static void SpliceAuthenticationRegistryTracer(IServiceCollection services, List<string> trace)
    {
        var descriptor = services.Single(d => d.ServiceType == typeof(IAuthenticationProviderRegistry));
        services.Remove(descriptor);
        services.AddSingleton<IAuthenticationProviderRegistry>(provider =>
        {
            var real = (IAuthenticationProviderRegistry)ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!);
            return new TracingAuthenticationProviderRegistry(real, trace);
        });
    }

    /// <summary>Splices a trace entry over <see cref="ITenantResolverRegistry"/>'s registered
    /// instance, read once per <see cref="TenantResolutionChain"/> call — used only where no
    /// resolver is registered (Local, I-C3).</summary>
    private static void SpliceTenantResolverRegistryTracer(IServiceCollection services, List<string> trace)
    {
        var descriptor = services.Single(d => d.ServiceType == typeof(ITenantResolverRegistry));
        services.Remove(descriptor);
        services.AddSingleton<ITenantResolverRegistry>(provider =>
        {
            var real = (ITenantResolverRegistry)ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!);
            return new TracingTenantResolverRegistry(real, trace);
        });
    }

    /// <summary>Splices a trace entry over <see cref="IEntitlementEvaluator"/>'s registered
    /// instance — used only in Local, where the one contributor the profile allows (the Community
    /// baseline, I-C3) cannot itself be decorated: the composition validator compares registered
    /// contributors by type, precisely so a contributor registered under the baseline's name is
    /// never mistaken for the baseline it is not.</summary>
    private static void SpliceEntitlementEvaluatorTracer(IServiceCollection services, List<string> trace)
    {
        var descriptor = services.Single(d => d.ServiceType == typeof(IEntitlementEvaluator));
        services.Remove(descriptor);
        services.AddSingleton<IEntitlementEvaluator>(provider =>
        {
            var real = (IEntitlementEvaluator)ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!);
            return new TracingEntitlementEvaluator(real, trace);
        });
    }

    /// <summary>Maps <c>/traced</c>, declaring <paramref name="requiredFeature"/> and appending
    /// "do-the-work" then "audit" itself — the only code that can know when its own work, and its
    /// own audit write, actually happen.</summary>
    private static void MapTracedEndpoint(WebApplication app, List<string> trace, FeatureName? requiredFeature)
    {
        app.MapGet("/traced", async (IAuditWriter auditWriter, CancellationToken cancellationToken) =>
        {
            lock (trace)
            {
                trace.Add("do-the-work");
            }

            await auditWriter.WriteAsync(
                new AuditAction("test.traced"),
                resource: null,
                AuditOutcome.Allowed,
                AuditClass.Recorded,
                cancellationToken);

            lock (trace)
            {
                trace.Add("audit");
            }

            return Results.Ok();
        }).RequiresPlatformAuthorization(TracedPermission, requiredFeature);
    }
}
