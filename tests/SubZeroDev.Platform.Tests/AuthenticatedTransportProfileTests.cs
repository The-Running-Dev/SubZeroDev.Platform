using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Audit;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Hosting;
using SubZeroDev.Platform.Identity;
using SubZeroDev.Platform.Persistence;
using static SubZeroDev.Platform.Tests.ConfiguredBearerTests;

namespace SubZeroDev.Platform.Tests;

/// <summary>The <c>AuthenticatedTransport</c> profile: a host that authenticates its callers and
/// forwards who they are, holding nothing about them. It requires an authentication provider
/// (I-C14), refuses every registration that would give it state, a durable audit trail, a tenant or
/// a permission to grant (I-C15), and refuses a <see cref="AuditClass.Required"/> write at run time
/// (I-C16).</summary>
/// <remarks>Startup assertions go through a real host start, as <see cref="CompositionProfileTests"/>
/// does and for the same reason: the property is that the host refuses, not that a validator
/// returns something.</remarks>
public sealed class AuthenticatedTransportProfileTests
{
    private static readonly Principal TestPrincipal =
        new(new PrincipalId("issuer-a", "alice"), PrincipalKind.Account, "Alice", null);

    // Positive ------------------------------------------------------------------------------------

    /// <summary>The shape the profile exists for: a provider and nothing else starts, with no
    /// persistence, no durable sink, no tenant resolver and only the framework's baselines.</summary>
    [Fact]
    public async Task A_provider_and_nothing_else_starts_without_persistence_or_a_durable_trail()
    {
        var (app, client) = await WebHostUnderTest.StartAsync(
            services => services.AddSingleton<IAuthenticationProvider>(new StubAuthenticationProvider("edge")),
            Transport(),
            composeOperatedDefaults: false);

        try
        {
            Assert.Equal(
                CompositionProfile.AuthenticatedTransport,
                app.Services.GetRequiredService<PlatformOptions>().CompositionProfile);
            Assert.Single(app.Services.GetRequiredService<IAuthenticationProviderRegistry>().Registered);
            Assert.DoesNotContain(app.Services.GetServices<IAuditSink>(), sink => sink.IsDurable);
            Assert.Empty(app.Services.GetServices<IModuleMigrationSource>());
            Assert.Empty(app.Services.GetServices<DurableStateRegistration>());
            Assert.Empty(app.Services.GetRequiredService<ITenantResolverRegistry>().Registered);
            Assert.IsType<CommunityEntitlementContributor>(
                Assert.Single(app.Services.GetRequiredService<IEntitlementContributorRegistry>().Registered));

            using var response = await client.GetAsync("/");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    /// <summary>The real Identity bearer provider composes under the profile: a token from the
    /// pinned issuer authenticates, and one from an issuer no section names is answered 401 with the
    /// error envelope before the handler runs.</summary>
    [Fact]
    public async Task The_identity_bearer_provider_composes_and_refuses_a_foreign_token_before_the_handler()
    {
        var handled = 0;
        var settings = Valid("corp");
        settings["Platform:CompositionProfile"] = nameof(CompositionProfile.AuthenticatedTransport);

        var (app, client) = await WebHostUnderTest.StartAsync(
            services => services.AddSingleton<IPlatformModule>(new IdentityModule()),
            settings,
            composeOperatedDefaults: false,
            mapEndpoints: application => application
                .MapGet("/counted", (ICurrentPrincipal principal) =>
                {
                    Interlocked.Increment(ref handled);
                    return Results.Ok(new { subject = principal.Current.Id.Subject });
                })
                .ExemptFromPlatformAuthorization("Transport test endpoint: the profile grants no permission."));

        try
        {
            using var accepted = new HttpRequestMessage(HttpMethod.Get, "/counted");
            accepted.Headers.Add("Authorization", $"Bearer {Mint(Rsa, "key-1")}");
            using var acceptedResponse = await client.SendAsync(accepted);
            Assert.Equal(HttpStatusCode.OK, acceptedResponse.StatusCode);
            Assert.Contains("\"subject\":\"alice\"", await acceptedResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            Assert.Equal(1, handled);

            using var foreign = new HttpRequestMessage(HttpMethod.Get, "/counted");
            foreign.Headers.Add("Authorization", $"Bearer {Mint(Rsa, "key-1", issuer: "https://elsewhere.test")}");
            using var foreignResponse = await client.SendAsync(foreign);
            Assert.Equal(HttpStatusCode.Unauthorized, foreignResponse.StatusCode);
            Assert.Contains("\"code\":", await foreignResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            using var malformed = new HttpRequestMessage(HttpMethod.Get, "/counted");
            malformed.Headers.Add("Authorization", "Bearer not-a-token");
            using var malformedResponse = await client.SendAsync(malformed);
            Assert.Equal(HttpStatusCode.Unauthorized, malformedResponse.StatusCode);

            Assert.Equal(1, handled);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    // Negative: I-C14 and I-C15 ------------------------------------------------------------------

    /// <summary>I-C14 — a transport that authenticates nobody is a misdeclared Local host.</summary>
    [Fact]
    public async Task With_no_authentication_provider_startup_fails_naming_the_profile()
    {
        var error = await RefusedAsync(services: null);

        Assert.Equal(nameof(HostStartupError.AuthenticationProviderRequired), error.Code);
        Assert.Contains(nameof(CompositionProfile.AuthenticatedTransport), error.Detail, StringComparison.Ordinal);
        Assert.Contains(nameof(IAuthenticationProvider), error.Detail, StringComparison.Ordinal);
    }

    /// <summary>I-C15 — each forbidden registration, alone beside a valid provider, fails startup
    /// naming the contract and the registration.</summary>
    [Theory]
    [MemberData(nameof(ForbiddenRegistrations))]
    public async Task A_forbidden_registration_fails_startup_naming_it(string contract, string registration)
    {
        var error = await RefusedAsync(WithProvider(Forbidden[(contract, registration)]));

        Assert.Equal(nameof(HostStartupError.RegistrationForbiddenByProfile), error.Code);
        Assert.Contains(nameof(CompositionProfile.AuthenticatedTransport), error.Detail, StringComparison.Ordinal);
        Assert.Contains($"{contract} '{registration}'", error.Detail, StringComparison.Ordinal);
    }

    public static TheoryData<string, string> ForbiddenRegistrations()
    {
        var data = new TheoryData<string, string>();
        foreach (var (contract, registration) in Forbidden.Keys)
        {
            data.Add(contract, registration);
        }

        return data;
    }

    private static readonly Dictionary<(string Contract, string Registration), Action<IServiceCollection>> Forbidden = new()
    {
        [("module", "Audit")] = services => services.AddSingleton<IPlatformModule>(new AuditModule()),
        [("module", "consumer-module")] = services => services.AddSingleton<IPlatformModule>(new StubModule("consumer-module")),
        [("durable Platform state of", "SubZeroDev.Platform.Persistence")] = services => services.AddPlatformPersistence(),
        [("durable IAuditSink", "durable-under-test")] = services =>
            services.AddSingleton<IAuditSink>(new RecordingAuditSink("durable-under-test", isDurable: true)),
        [("ITenantResolver", "tenant-under-test")] = services =>
            services.AddSingleton<ITenantResolver>(new StubTenantResolver("tenant-under-test", () => null)),
        [("IEntitlementContributor", "licensing-under-test")] = services => services.AddKeyedSingleton<IEntitlementContributor>(
            EntitlementContributorRegistration.ServiceKey,
            new StubEntitlementContributor("licensing-under-test", (_, _) => Result<bool, EntitlementError>.Success(true))),
        [("IPermissionProvider", "grants-under-test")] = services => services.AddSingleton<IPermissionProvider>(
            new StubPermissionProvider(
                "grants-under-test",
                (_, _, _) => Result<IReadOnlySet<PermissionName>, AuthorizationError>.Success(new HashSet<PermissionName>()))),
    };

    /// <summary>A refused module is named before its <see cref="IPlatformModule.Register"/> runs, so
    /// a module that would fail or do work while registering never gets to.</summary>
    [Fact]
    public async Task A_refused_module_is_named_before_it_registers()
    {
        var module = new RegisterCountingModule();
        var error = await RefusedAsync(WithProvider(services => services.AddSingleton<IPlatformModule>(module)));

        Assert.Equal(nameof(HostStartupError.RegistrationForbiddenByProfile), error.Code);
        Assert.Contains("module 'counting-module'", error.Detail, StringComparison.Ordinal);
        Assert.Equal(0, module.Registered);
    }

    /// <summary>Persistence added after the host composed is still refused, by the startup check,
    /// before any of its own startup work runs.</summary>
    [Fact]
    public async Task Persistence_added_after_the_host_composed_is_still_refused()
    {
        WebApplication? app = null;
        try
        {
            (app, _) = await WebHostUnderTest.StartAsync(
                WithProvider(_ => { }),
                Transport(),
                composeOperatedDefaults: false,
                postCompose: services => services.AddPlatformPersistence());
            Assert.Fail("The host started; it was expected to refuse.");
        }
        catch (PlatformStartupException exception)
        {
            var error = Assert.IsType<HostStartupError>(exception.Error);
            Assert.Equal(nameof(HostStartupError.RegistrationForbiddenByProfile), error.Code);
            Assert.Contains("durable Platform state of 'SubZeroDev.Platform.Persistence'", error.Detail, StringComparison.Ordinal);
        }
        finally
        {
            if (app is not null)
            {
                await app.DisposeAsync();
            }
        }
    }

    /// <summary>Persistence declares its state once however often it is added, so an Operated host
    /// that calls it from several places is unaffected.</summary>
    [Fact]
    public void Persistence_declares_its_durable_state_once()
    {
        var services = new ServiceCollection();
        services.AddPlatformPersistence();
        services.AddPlatformPersistence();

        Assert.Single(services, descriptor => descriptor.ImplementationInstance is DurableStateRegistration);
    }

    /// <summary>I-C15's endpoint half — a host that grants nothing cannot serve a route that
    /// requires a permission, so the route is refused at startup naming the route and permission,
    /// rather than mapped and denied on every call.</summary>
    [Fact]
    public async Task An_endpoint_requiring_a_permission_fails_startup_naming_the_route_and_permission()
    {
        var permission = new PermissionName("Transport.Read");
        var error = await RefusedAsync(
            WithProvider(services => services.AddSingleton<IPermissionCatalog>(new StubPermissionCatalog(permission))),
            application => application
                .MapGet("/transport-guarded", () => Results.Ok())
                .RequiresPlatformAuthorization(permission, feature: null));

        Assert.Equal(nameof(HostStartupError.RegistrationForbiddenByProfile), error.Code);
        Assert.Contains("/transport-guarded", error.Detail, StringComparison.Ordinal);
        Assert.Contains("Transport.Read", error.Detail, StringComparison.Ordinal);
    }

    /// <summary>The endpoint rule is profile-scoped: the same endpoint starts under Operated.</summary>
    [Fact]
    public async Task The_same_guarded_endpoint_starts_under_operated()
    {
        var permission = new PermissionName("Transport.Read");
        var (app, _) = await WebHostUnderTest.StartAsync(
            services => services.AddSingleton<IPermissionCatalog>(new StubPermissionCatalog(permission)),
            mapEndpoints: application => application
                .MapGet("/transport-guarded", () => Results.Ok())
                .RequiresPlatformAuthorization(permission, feature: null));

        await app.DisposeAsync();
    }

    // Run time: I-C16 -----------------------------------------------------------------------------

    /// <summary>I-C16 — a Required write is refused before any sink sees it; a Recorded write still
    /// reaches the sinks.</summary>
    [Fact]
    public async Task A_required_audit_write_is_refused_and_a_recorded_one_still_reaches_the_sink()
    {
        var sink = new RecordingAuditSink("log-only", isDurable: false);
        var (app, _) = await WebHostUnderTest.StartAsync(
            WithProvider(services => services.AddSingleton<IAuditSink>(sink)),
            Transport(),
            composeOperatedDefaults: false);

        try
        {
            var scopeFactory = app.Services.GetRequiredService<IOperationScopeFactory>();
            var writer = app.Services.GetRequiredService<IAuditWriter>();

            using (scopeFactory.Begin(TenantId.Implicit, TestPrincipal))
            {
                var required = await writer.WriteAsync(
                    new AuditAction("transport.required"), null, AuditOutcome.Denied, AuditClass.Required, CancellationToken.None);
                Assert.False(required.IsSuccess);
                Assert.Equal(nameof(AuditError.DurableTrailRequired), required.Error.Code);
                Assert.False(required.Error.IsRetryable);
                Assert.Empty(sink.Received);

                var recorded = await writer.WriteAsync(
                    new AuditAction("transport.recorded"), null, AuditOutcome.Allowed, AuditClass.Recorded, CancellationToken.None);
                Assert.True(recorded.IsSuccess);
                Assert.Equal("transport.recorded", Assert.Single(sink.Received).Action.Value);
            }
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    /// <summary>An evaluation reached directly still denies — the composition provider grants
    /// nothing here — and the denial's Required record is refused, so the decision carries
    /// <c>DurableTrailRequired</c> rather than passing as a recorded denial.</summary>
    [Fact]
    public async Task A_direct_evaluation_denies_and_carries_the_refused_required_record()
    {
        var (app, _) = await WebHostUnderTest.StartAsync(
            WithProvider(_ => { }),
            Transport(),
            composeOperatedDefaults: false);

        try
        {
            var scopeFactory = app.Services.GetRequiredService<IOperationScopeFactory>();
            var evaluator = app.Services.GetRequiredService<IAuthorizationEvaluator>();

            using (scopeFactory.Begin(TenantId.Implicit, TestPrincipal))
            {
                var decision = await evaluator.EvaluateAsync(PlatformPermissions.ReadAudit, null, CancellationToken.None);

                Assert.Equal(AuthorizationOutcome.Denied, decision.Outcome);
                Assert.Empty(decision.Sources);
                Assert.Equal(nameof(AuditError.DurableTrailRequired), decision.AuditFailure?.Code);
            }
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    // Validator and fingerprint -------------------------------------------------------------------

    /// <summary>A profile the validator does not name fails rather than passing unchecked.</summary>
    [Fact]
    public void An_unknown_profile_is_not_validated_as_passing()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CompositionValidator.Validate(
            (CompositionProfile)99, [], [], [], [], [], [], []));
    }

    /// <summary>The enum's existing values keep their numbers, so a stored or serialised profile
    /// means what it meant.</summary>
    [Fact]
    public void Existing_profile_values_keep_their_numbers()
    {
        Assert.Equal(0, (int)CompositionProfile.Local);
        Assert.Equal(1, (int)CompositionProfile.Operated);
        Assert.Equal(2, (int)CompositionProfile.AuthenticatedTransport);
    }

    /// <summary>The profile is fingerprinted, so a host that changes into or out of it is a
    /// different configuration.</summary>
    [Fact]
    public void The_profile_changes_the_settings_fingerprint()
    {
        ISettingsFingerprint fingerprint = new SettingsFingerprint();
        var hashes = Enum.GetValues<CompositionProfile>()
            .Select(profile => fingerprint.Compute(SettingsFingerprintTests.Baseline(profile), []))
            .ToList();

        Assert.Equal(3, hashes.Distinct(StringComparer.Ordinal).Count());
    }

    private sealed class RegisterCountingModule : IPlatformModule
    {
        public int Registered { get; private set; }

        public ModuleName Name { get; } = new("counting-module");

        public IReadOnlyCollection<ModuleName> DependsOn { get; } = [];

        public void Register(IServiceCollection services) => Registered++;
    }

    private static Dictionary<string, string?> Transport() =>
        new() { ["Platform:CompositionProfile"] = nameof(CompositionProfile.AuthenticatedTransport) };

    private static Action<IServiceCollection> WithProvider(Action<IServiceCollection> services) => collection =>
    {
        collection.AddSingleton<IAuthenticationProvider>(new StubAuthenticationProvider("edge"));
        services(collection);
    };

    private static async Task<HostStartupError> RefusedAsync(
        Action<IServiceCollection>? services,
        Action<WebApplication>? mapEndpoints = null)
    {
        WebApplication? app = null;
        try
        {
            (app, _) = await WebHostUnderTest.StartAsync(
                services, Transport(), composeOperatedDefaults: false, mapEndpoints: mapEndpoints);
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
