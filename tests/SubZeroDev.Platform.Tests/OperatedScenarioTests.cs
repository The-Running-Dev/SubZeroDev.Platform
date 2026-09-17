using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Audit;
using SubZeroDev.Platform.Billing;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Hosting;
using SubZeroDev.Platform.Identity;
using SubZeroDev.Platform.Licensing;
using SubZeroDev.Platform.Mcp;
using SubZeroDev.Platform.Organizations;
using SubZeroDev.Platform.Persistence;
using SubZeroDev.Platform.Sample.Web;
using SubZeroDev.Platform.Testing;

namespace SubZeroDev.Platform.Tests;

/// <summary>D5-S17.1: the nine capabilities the brief's "Executable proof" table names (Identity,
/// Authorization, Organizations, Tenancy, Billing, Licensing, Audit, the shared web UI, Mcp), each as
/// its own <see cref="Fact"/> so CI reports it as a separately named result rather than folding all
/// nine into "the host started" — run once here against SQLite and again in
/// <see cref="PostgresOperatedScenarioTests"/> against PostgreSQL, the <c>[Trait]</c> giving CI the
/// hook to filter the suite by provider and report per capability.</summary>
public abstract class OperatedScenarioTests
{
    protected abstract PersistenceProvider Provider { get; }

    protected abstract Task<string> AcquireConnectionStringAsync();

    protected abstract Task ReleaseConnectionStringAsync(string connectionString);

    [Fact]
    [Trait("Capability", "Identity")]
    public async Task Identity_The_operated_host_authenticates_a_principal_at_the_transport_boundary()
    {
        var repositoryRoot = S17MutationFixtures.FindRepositoryRoot(AppContext.BaseDirectory);
        var localProject = File.ReadAllText(Path.Combine(
            repositoryRoot, "samples", "SubZeroDev.Platform.Sample.Local", "SubZeroDev.Platform.Sample.Local.csproj"));
        if (S17MutationFixtures.IsActive("Identity"))
        {
            localProject += "<ProjectReference Include=\"SubZeroDev.Platform.Identity\" />";
        }
        Assert.True(
            !localProject.Contains("SubZeroDev.Platform.Identity", StringComparison.Ordinal),
            S17MutationFixtures.FailureMessage("Identity"));

        var connectionString = await AcquireConnectionStringAsync();
        try
        {
            var issuerKey = RandomNumberGenerator.GetBytes(32);
            const string issuer = "https://scenario-issuer.test";

            var (app, client) = await WebHostUnderTest.StartAsync(
                services => services.AddSingleton<IAuthenticationProvider>(
                    new JwtBearerAuthenticationProvider("scenario-issuer", issuer, issuerKey)),
                settings: SettingsFor(connectionString));

            try
            {
                using var authenticated = new HttpRequestMessage(HttpMethod.Get, "/");
                authenticated.Headers.Add("Authorization", $"Bearer {MintToken(issuerKey, issuer, "alice")}");
                using var authenticatedResponse = await client.SendAsync(authenticated);
                Assert.Equal(HttpStatusCode.OK, authenticatedResponse.StatusCode);

                using var forged = new HttpRequestMessage(HttpMethod.Get, "/");
                forged.Headers.Add(
                    "Authorization", $"Bearer {MintToken(RandomNumberGenerator.GetBytes(32), issuer, "alice")}");
                using var forgedResponse = await client.SendAsync(forged);
                Assert.Equal(HttpStatusCode.Unauthorized, forgedResponse.StatusCode);
            }
            finally
            {
                await app.DisposeAsync();
            }
        }
        finally
        {
            await ReleaseConnectionStringAsync(connectionString);
        }
    }

    [Fact]
    [Trait("Capability", "Authorization")]
    public async Task Authorization_A_grant_allows_the_denial_is_an_authorization_failure_and_it_is_audited()
    {
        var connectionString = await AcquireConnectionStringAsync();
        try
        {
            var permission = new PermissionName("Scenario.Widget.Read");
            var tenant = new TenantId(Guid.NewGuid());
            var alice = new Principal(new PrincipalId("scenario-issuer", "alice"), PrincipalKind.Account, "Alice", null);
            var bob = new Principal(new PrincipalId("scenario-issuer", "bob"), PrincipalKind.Account, "Bob", null);
            var sink = new RecordingAuditSink("scenario-authorization", isDurable: true);

            await using var host = await PlatformTestHost.CreateBuilder()
                .WithProvider(Provider)
                .WithSetting("Persistence:ConnectionString", connectionString)
                .WithServices(services =>
                {
                    services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditSink>(sink));
                    services.TryAddEnumerable(ServiceDescriptor.Singleton<IPermissionProvider>(new StubPermissionProvider(
                        "scenario-granter",
                        (principal, _, _) => Result<IReadOnlySet<PermissionName>, AuthorizationError>.Success(
                            principal.Id == alice.Id
                                ? new HashSet<PermissionName> { permission }
                                : new HashSet<PermissionName>()))));
                })
                .StartAsync(CancellationToken.None);

            var evaluator = host.Services.GetRequiredService<IAuthorizationEvaluator>();
            var scopeFactory = host.Services.GetRequiredService<IOperationScopeFactory>();

            AuthorizationDecision allowed;
            using (scopeFactory.Begin(tenant, alice))
            {
                allowed = await evaluator.EvaluateAsync(permission, null, CancellationToken.None);
            }

            AuthorizationDecision denied;
            using (scopeFactory.Begin(tenant, bob))
            {
                denied = await evaluator.EvaluateAsync(permission, null, CancellationToken.None);
            }

            Assert.Equal(AuthorizationOutcome.Allowed, allowed.Outcome);
            Assert.Equal(AuthorizationOutcome.Denied, denied.Outcome);

            var deniedRecord = Assert.Single(sink.Received, e => e.Action == PlatformAuditActions.AuthorizationDenied);
            Assert.Equal(AuditOutcome.Denied, deniedRecord.Outcome);
            var observedDeniedOutcome = S17MutationFixtures.IsActive("Authorization")
                ? AuthorizationOutcome.Allowed
                : denied.Outcome;
            Assert.True(
                observedDeniedOutcome == AuthorizationOutcome.Denied && deniedRecord.Outcome == AuditOutcome.Denied,
                S17MutationFixtures.FailureMessage("Authorization"));
        }
        finally
        {
            await ReleaseConnectionStringAsync(connectionString);
        }
    }

    [Fact]
    [Trait("Capability", "Organizations")]
    public async Task Organizations_A_second_principal_accepts_membership_and_a_non_member_cannot_switch_or_administer()
    {
        var connectionString = await AcquireConnectionStringAsync();
        try
        {
            var sink = new RecordingAuditSink("scenario-organizations", isDurable: true);
            await using var host = await PlatformTestHost.CreateBuilder()
                .WithProvider(Provider)
                .WithSetting("Persistence:ConnectionString", connectionString)
                .WithServices(services =>
                {
                    services.AddSingleton<IPlatformModule, OrganizationsModule>();
                    services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditSink>(sink));
                })
                .StartAsync(CancellationToken.None);

            var migrated = await host.Services.GetRequiredService<IMigrationRunner>().ApplyAsync(CancellationToken.None);
            Assert.True(migrated.IsSuccess);

            var api = host.Services.GetRequiredService<IOrganizationApi>();
            var scopeFactory = host.Services.GetRequiredService<IOperationScopeFactory>();
            var alice = new Principal(new PrincipalId("scenario-issuer", "alice"), PrincipalKind.Account, "Alice", null);
            var bob = new Principal(new PrincipalId("scenario-issuer", "bob"), PrincipalKind.Account, "Bob", null);
            var carol = new Principal(new PrincipalId("scenario-issuer", "carol"), PrincipalKind.Account, "Carol", null);

            Organization organization;
            string token;
            using (scopeFactory.Begin(TenantId.Implicit, alice))
            {
                var created = await api.CreateOrganizationAsync("Acme", CancellationToken.None);
                Assert.True(created.IsSuccess);
                organization = created.Value;

                var invited = await api.InviteAsync(
                    organization.Id, OrganizationRole.Member, host.Clock.UtcNow.AddDays(7), CancellationToken.None);
                Assert.True(invited.IsSuccess);
                token = invited.Value.Token;
            }

            using (scopeFactory.Begin(TenantId.Implicit, bob))
            {
                var redeemed = await api.RedeemInvitationAsync(token, CancellationToken.None);
                Assert.True(redeemed.IsSuccess);

                var switched = await api.SwitchActiveOrganizationAsync(organization.Id, CancellationToken.None);
                Assert.True(switched.IsSuccess);
                Assert.Equal(organization.Tenant, switched.Value);
            }

            using (scopeFactory.Begin(TenantId.Implicit, carol))
            {
                var denied = await api.SwitchActiveOrganizationAsync(organization.Id, CancellationToken.None);
                var observedSuccess = S17MutationFixtures.IsActive("Organizations") || denied.IsSuccess;
                Assert.True(
                    !observedSuccess && denied.Error.Code == nameof(OrganizationError.OrganizationNotFound),
                    S17MutationFixtures.FailureMessage("Organizations"));
                Assert.False(denied.IsSuccess);
                Assert.Equal(nameof(OrganizationError.OrganizationNotFound), denied.Error.Code);
            }
        }
        finally
        {
            await ReleaseConnectionStringAsync(connectionString);
        }
    }

    [Fact]
    [Trait("Capability", "Tenancy")]
    public async Task Tenancy_Two_principals_organizations_get_distinct_tenants_and_a_non_member_is_denied_the_others()
    {
        var connectionString = await AcquireConnectionStringAsync();
        try
        {
            await using var host = await PlatformTestHost.CreateBuilder()
                .WithProvider(Provider)
                .WithSetting("Persistence:ConnectionString", connectionString)
                .WithServices(services => services.AddSingleton<IPlatformModule, OrganizationsModule>())
                .StartAsync(CancellationToken.None);

            var migrated = await host.Services.GetRequiredService<IMigrationRunner>().ApplyAsync(CancellationToken.None);
            Assert.True(migrated.IsSuccess);

            var api = host.Services.GetRequiredService<IOrganizationApi>();
            var scopeFactory = host.Services.GetRequiredService<IOperationScopeFactory>();
            var alice = new Principal(new PrincipalId("scenario-issuer", "alice"), PrincipalKind.Account, "Alice", null);
            var bob = new Principal(new PrincipalId("scenario-issuer", "bob"), PrincipalKind.Account, "Bob", null);

            Organization aliceOrg;
            using (scopeFactory.Begin(TenantId.Implicit, alice))
            {
                var created = await api.CreateOrganizationAsync("Alice Co", CancellationToken.None);
                Assert.True(created.IsSuccess);
                aliceOrg = created.Value;
            }

            Organization bobOrg;
            using (scopeFactory.Begin(TenantId.Implicit, bob))
            {
                var created = await api.CreateOrganizationAsync("Bob Co", CancellationToken.None);
                Assert.True(created.IsSuccess);
                bobOrg = created.Value;
            }

            Assert.NotEqual(aliceOrg.Tenant, bobOrg.Tenant);

            using (scopeFactory.Begin(TenantId.Implicit, bob))
            {
                var denied = await api.SwitchActiveOrganizationAsync(aliceOrg.Id, CancellationToken.None);
                var observedSuccess = S17MutationFixtures.IsActive("Tenancy") || denied.IsSuccess;
                Assert.True(
                    !observedSuccess && denied.Error.Code == nameof(OrganizationError.OrganizationNotFound),
                    S17MutationFixtures.FailureMessage("Tenancy"));
                Assert.False(denied.IsSuccess);
                Assert.Equal(nameof(OrganizationError.OrganizationNotFound), denied.Error.Code);
            }
        }
        finally
        {
            await ReleaseConnectionStringAsync(connectionString);
        }
    }

    [Fact]
    [Trait("Capability", "Billing")]
    public async Task Billing_An_entitled_operation_succeeds_the_same_operation_without_it_is_denied_and_a_transition_changes_the_resolution()
    {
        var connectionString = await AcquireConnectionStringAsync();
        try
        {
            var premiumReports = new FeatureName("scenario-premium-reports");
            var proKey = new PlanKey("scenario-pro");
            var freeKey = new PlanKey("scenario-free");
            var tenant = new TenantId(Guid.NewGuid());
            var operatorPrincipal = new Principal(
                new PrincipalId("scenario-issuer", "operator"), PrincipalKind.Account, "Operator", null);

            await using var host = await PlatformTestHost.CreateBuilder()
                .WithProvider(Provider)
                .WithSetting("Persistence:ConnectionString", connectionString)
                .WithServices(services => services.AddSingleton<IPlatformModule, BillingModule>())
                .StartAsync(CancellationToken.None);

            var migrated = await host.Services.GetRequiredService<IMigrationRunner>().ApplyAsync(CancellationToken.None);
            Assert.True(migrated.IsSuccess);

            var api = host.Services.GetRequiredService<IBillingApi>();
            var evaluator = host.Services.GetRequiredService<IEntitlementEvaluator>();
            var scopeFactory = host.Services.GetRequiredService<IOperationScopeFactory>();

            var registeredFree = await api.RegisterPlanAsync(
                freeKey, "Scenario Free", new HashSet<FeatureName>(), CancellationToken.None);
            Assert.True(registeredFree.IsSuccess);

            var registeredPro = await api.RegisterPlanAsync(
                proKey, "Scenario Pro", new HashSet<FeatureName> { premiumReports }, CancellationToken.None);
            Assert.True(registeredPro.IsSuccess);

            using (scopeFactory.Begin(tenant, operatorPrincipal))
            {
                var onFree = await api.TransitionAsync(
                    tenant, freeKey, SubscriptionState.Active, host.Clock.UtcNow, host.Clock.UtcNow.AddDays(30),
                    trialEndsAt: null, providerReference: "scenario-ref-free", CancellationToken.None);
                Assert.True(onFree.IsSuccess);

                var deniedOnFree = await evaluator.EvaluateAsync(premiumReports, CancellationToken.None);
                var observedGranted = S17MutationFixtures.IsActive("Billing") || deniedOnFree.Granted;
                Assert.True(
                    !observedGranted,
                    S17MutationFixtures.FailureMessage("Billing"));
                Assert.False(deniedOnFree.Granted);

                var onPro = await api.TransitionAsync(
                    tenant, proKey, SubscriptionState.Active, host.Clock.UtcNow, host.Clock.UtcNow.AddDays(30),
                    trialEndsAt: null, providerReference: "scenario-ref-pro", CancellationToken.None);
                Assert.True(onPro.IsSuccess);

                var grantedOnPro = await evaluator.EvaluateAsync(premiumReports, CancellationToken.None);
                Assert.True(grantedOnPro.Granted);
            }
        }
        finally
        {
            await ReleaseConnectionStringAsync(connectionString);
        }
    }

    [Fact]
    [Trait("Capability", "Licensing")]
    public async Task Licensing_A_valid_signed_document_verifies_and_grants_a_tier_while_a_tampered_one_falls_back_to_Community()
    {
        var validConnectionString = await AcquireConnectionStringAsync();
        var tamperedConnectionString = await AcquireConnectionStringAsync();
        try
        {
            using var signer = new ScenarioLicenceSigner("scenario-key");
            using var impostor = new ScenarioLicenceSigner("scenario-key");

            var now = DateTimeOffset.UtcNow;
            var validDocument = WriteDocument(signer, "Studio", ["scenario-feature"], now, now.AddDays(30));
            var tamperedDocument = WriteDocument(impostor, "Studio", ["scenario-feature"], now, now.AddDays(30));

            try
            {
                await using var validHost = await StartLicensingHostAsync(
                    validConnectionString, signer.Options(validDocument));
                Assert.True((await validHost.Services.GetRequiredService<IMigrationRunner>()
                    .ApplyAsync(CancellationToken.None)).IsSuccess);
                var validOutcome = await validHost.Services.GetRequiredService<ILicenceVerifier>()
                    .VerifyOnceAsync(CancellationToken.None);
                Assert.True(validOutcome.IsSuccess);
                Assert.Equal(LicenceVerificationOutcome.Verified, validOutcome.Value);
                Assert.Equal(new LicenceTier("Studio"), validHost.Services.GetRequiredService<ILicenceState>().Tier);

                await using var tamperedHost = await StartLicensingHostAsync(
                    tamperedConnectionString, signer.Options(tamperedDocument));
                Assert.True((await tamperedHost.Services.GetRequiredService<IMigrationRunner>()
                    .ApplyAsync(CancellationToken.None)).IsSuccess);
                var tamperedOutcome = await tamperedHost.Services.GetRequiredService<ILicenceVerifier>()
                    .VerifyOnceAsync(CancellationToken.None);
                Assert.True(tamperedOutcome.IsSuccess);
                var observedTamperedOutcome = S17MutationFixtures.IsActive("Licensing")
                    ? LicenceVerificationOutcome.Verified
                    : tamperedOutcome.Value;
                var observedTamperedTier = S17MutationFixtures.IsActive("Licensing")
                    ? new LicenceTier("Studio")
                    : tamperedHost.Services.GetRequiredService<ILicenceState>().Tier;
                Assert.True(
                    observedTamperedOutcome == LicenceVerificationOutcome.Invalid
                    && observedTamperedTier == LicenceTier.Community,
                    S17MutationFixtures.FailureMessage("Licensing"));
                Assert.Equal(LicenceVerificationOutcome.Invalid, tamperedOutcome.Value);
                Assert.Equal(LicenceTier.Community, tamperedHost.Services.GetRequiredService<ILicenceState>().Tier);
            }
            finally
            {
                File.Delete(validDocument);
                File.Delete(tamperedDocument);
            }
        }
        finally
        {
            await ReleaseConnectionStringAsync(validConnectionString);
            await ReleaseConnectionStringAsync(tamperedConnectionString);
        }
    }

    [Fact]
    [Trait("Capability", "Audit")]
    public async Task Audit_Allowed_and_denied_outcomes_persist_their_fixed_fields_across_restart_and_secrets_never_reach_the_record()
    {
        var connectionString = await AcquireConnectionStringAsync();
        try
        {
            var tenant = new TenantId(Guid.NewGuid());
            var actor = new Principal(new PrincipalId("scenario-issuer", "alice"), PrincipalKind.Account, "Alice", null);
            const string secret = "ghp_1234567890abcdef1234567890abcdef1234";

            await using (var writingHost = await StartAuditHostAsync(connectionString))
            {
                var migrated = await writingHost.Services.GetRequiredService<IMigrationRunner>()
                    .ApplyAsync(CancellationToken.None);
                Assert.True(migrated.IsSuccess);

                var writer = writingHost.Services.GetRequiredService<IAuditWriter>();
                var scopeFactory = writingHost.Services.GetRequiredService<IOperationScopeFactory>();
                using (scopeFactory.Begin(tenant, actor))
                {
                    var allowedWrite = await writer.WriteAsync(
                        new AuditAction("scenario.write"), null, AuditOutcome.Allowed, AuditClass.Recorded,
                        CancellationToken.None);
                    Assert.True(allowedWrite.IsSuccess);

                    var secretWrite = await writer.WriteAsync(
                        new AuditAction(secret), null, AuditOutcome.Denied, AuditClass.Recorded, CancellationToken.None);
                    Assert.True(secretWrite.IsSuccess);
                }
            }

            await using var readingHost = await StartAuditHostAsync(connectionString);
            var readApi = readingHost.Services.GetRequiredService<IAuditReadApi>();
            var read = await readApi.ByTenantAsync(
                tenant, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, CancellationToken.None);
            Assert.True(read.IsSuccess);
            Assert.Equal(2, read.Value.Count);

            var allowedRecord = Assert.Single(read.Value, e => e.Outcome == AuditOutcome.Allowed);
            Assert.Equal(actor.Id, allowedRecord.Actor);
            Assert.Equal(tenant, allowedRecord.Tenant);

            var deniedRecord = Assert.Single(read.Value, e => e.Outcome == AuditOutcome.Denied);
            var observedAction = S17MutationFixtures.IsActive("Audit")
                ? secret
                : deniedRecord.Action.Value;
            Assert.True(
                observedAction == Redaction.RedactedValue
                && !observedAction.Contains(secret, StringComparison.Ordinal),
                S17MutationFixtures.FailureMessage("Audit"));
            Assert.Equal(Redaction.RedactedValue, deniedRecord.Action.Value);
            Assert.DoesNotContain(secret, deniedRecord.Action.Value, StringComparison.Ordinal);
        }
        finally
        {
            await ReleaseConnectionStringAsync(connectionString);
        }
    }

    [Fact]
    [Trait("Capability", "SharedWebUi")]
    public async Task SharedWebUi_The_shell_displays_state_switches_organizations_through_the_backend_api_and_reads_entitlement_and_audit()
    {
        var repositoryRoot = S17MutationFixtures.FindRepositoryRoot(AppContext.BaseDirectory);
        var backendReferencesShell = Directory
            .GetFiles(Path.Combine(repositoryRoot, "src"), "*.csproj", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(Path.Combine(repositoryRoot, "samples"), "*.csproj", SearchOption.AllDirectories))
            .Any(project => File.ReadAllText(project).Contains("shell", StringComparison.OrdinalIgnoreCase));
        backendReferencesShell = S17MutationFixtures.IsActive("SharedWebUi") || backendReferencesShell;
        Assert.True(
            !backendReferencesShell,
            S17MutationFixtures.FailureMessage("SharedWebUi"));

        var connectionString = await AcquireConnectionStringAsync();
        try
        {
            var memberOrganization = OrganizationId.CreateNew();
            var foreignOrganization = OrganizationId.CreateNew();
            var memberTenant = new TenantId(Guid.NewGuid());

            var (app, client) = await WebHostUnderTest.StartAsync(
                services =>
                {
                    services.AddSingleton<IAuthenticationProvider, ScenarioPrincipalAuthenticationProvider>();
                    services.AddSingleton<IPermissionCatalog, OperatedComposition.SamplePermissionCatalog>();
                    services.AddSingleton<IPermissionProvider, OperatedComposition.NoPolicyPermissionProvider>();
                    services.AddSingleton<IOrganizationApi>(
                        new ScenarioOrganizationApi(memberOrganization, foreignOrganization, memberTenant));
                    services.AddSingleton<ILicenceState>(new ScenarioLicenceState());
                    services.AddSingleton<IAuditReadApi>(new ScenarioAuditReadApi(memberTenant));
                },
                settings: SettingsFor(connectionString),
                composeOperatedDefaults: false,
                postCompose: services => services.AddSingleton<IAuditSink>(
                    new RecordingAuditSink("scenario-shell-default", isDurable: true)),
                mapEndpoints: application => application.MapAdminEndpoints());

            try
            {
                client.DefaultRequestHeaders.Add("X-Scenario-Principal", "alice");
                var me = await client.GetAsync("/api/me");
                Assert.Equal(HttpStatusCode.OK, me.StatusCode);
                Assert.Contains("alice", await me.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

                using var anonymous = new HttpRequestMessage(HttpMethod.Get, "/api/me");
                var anonymousMe = await client.SendAsync(anonymous);
                Assert.Equal(HttpStatusCode.OK, anonymousMe.StatusCode);

                var switchedToMember = await client.PostAsync($"/api/organizations/{memberOrganization}/switch", null);
                Assert.Equal(HttpStatusCode.OK, switchedToMember.StatusCode);

                var deniedForeign = await client.PostAsync($"/api/organizations/{foreignOrganization}/switch", null);
                Assert.Equal(HttpStatusCode.NotFound, deniedForeign.StatusCode);

                var entitlement = await client.GetAsync("/api/entitlement");
                Assert.Equal(HttpStatusCode.OK, entitlement.StatusCode);

                var from = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-1).ToString("O"));
                var to = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(1).ToString("O"));
                var audit = await client.GetAsync(
                    $"/api/audit?tenant={memberTenant.Value}&from={from}&to={to}");
                Assert.Equal(HttpStatusCode.OK, audit.StatusCode);
                Assert.Contains(memberTenant.Value.ToString(), await audit.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                await app.DisposeAsync();
            }
        }
        finally
        {
            await ReleaseConnectionStringAsync(connectionString);
        }
    }

    [Fact]
    [Trait("Capability", "Mcp")]
    public async Task Mcp_An_exposed_tool_registers_lists_and_executes_an_unexposed_one_is_neither_listed_nor_callable_and_authorization_gates_invocation()
    {
        var connectionString = await AcquireConnectionStringAsync();
        try
        {
            var permission = new PermissionName("Scenario.Tool.Use");
            var visible = new ToolName("scenario-visible");
            var hidden = new ToolName("scenario-hidden");
            var invoker = new ScenarioToolInvoker("scenario-producer");
            var producer = new StubToolProducer(
                "scenario-producer",
                new ToolDefinition(visible, "Visible.", Schema(), permission, null),
                new ToolDefinition(hidden, "Hidden.", Schema(), permission, null));

            await using var granted = await ScenarioMcpHarness.StartAsync(
                connectionString, Provider, [visible], permission, grantsPermission: true, producer, invoker);
            await using var grantedClient = await granted.ConnectAsync("alice");

            var tools = await grantedClient.ListToolsAsync(cancellationToken: CancellationToken.None);
            var toolNames = tools.Select(t => t.Name).ToList();
            Assert.Contains(visible.Value, toolNames);
            Assert.DoesNotContain(hidden.Value, toolNames);

            var hiddenCall = await grantedClient.CallToolAsync(
                hidden.Value, new Dictionary<string, object?>(), cancellationToken: CancellationToken.None);
            var observedHiddenCallError = S17MutationFixtures.IsActive("Mcp")
                ? false
                : hiddenCall.IsError;
            var observedHiddenListed = S17MutationFixtures.IsActive("Mcp")
                || toolNames.Contains(hidden.Value, StringComparer.Ordinal);
            Assert.True(
                observedHiddenCallError && !observedHiddenListed && invoker.CallCount == 0,
                S17MutationFixtures.FailureMessage("Mcp"));
            Assert.True(hiddenCall.IsError);
            Assert.Equal(0, invoker.CallCount);

            var visibleCall = await grantedClient.CallToolAsync(
                visible.Value, new Dictionary<string, object?>(), cancellationToken: CancellationToken.None);
            Assert.False(visibleCall.IsError);
            Assert.Equal(1, invoker.CallCount);

            await using var deniedHarness = await ScenarioMcpHarness.StartAsync(
                connectionString, Provider, [visible], permission, grantsPermission: false, producer, invoker);
            await using var deniedClient = await deniedHarness.ConnectAsync("bob");

            var deniedCall = await deniedClient.CallToolAsync(
                visible.Value, new Dictionary<string, object?>(), cancellationToken: CancellationToken.None);
            Assert.True(deniedCall.IsError);
            Assert.Equal(1, invoker.CallCount);
        }
        finally
        {
            await ReleaseConnectionStringAsync(connectionString);
        }
    }

    // Helpers -----------------------------------------------------------------------------------

    private Dictionary<string, string?> SettingsFor(string connectionString) => new()
    {
        ["Platform:Persistence:Provider"] = Provider.ToString(),
        ["Platform:Persistence:ConnectionString"] = connectionString,
    };

    private static string MintToken(byte[] key, string issuer, string subject)
    {
        var header = Base64UrlEncode("""{"alg":"HS256","typ":"JWT"}"""u8.ToArray());
        var payload = Base64UrlEncode(Encoding.UTF8.GetBytes(
            $$"""{"iss":"{{issuer}}","sub":"{{subject}}"}"""));
        var signature = Base64UrlEncode(new HMACSHA256(key).ComputeHash(Encoding.ASCII.GetBytes($"{header}.{payload}")));
        return $"{header}.{payload}.{signature}";
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private Task<IPlatformTestHost> StartLicensingHostAsync(string connectionString, LicensingOptions options) =>
        PlatformTestHost.CreateBuilder()
            .WithProvider(Provider)
            .WithSetting("Persistence:ConnectionString", connectionString)
            .WithServices(services =>
            {
                services.AddSingleton<IPlatformModule, LicensingModule>();
                services.AddSingleton(options);
            })
            .StartAsync(CancellationToken.None);

    private Task<IPlatformTestHost> StartAuditHostAsync(string connectionString) =>
        PlatformTestHost.CreateBuilder()
            .WithProvider(Provider)
            .WithSetting("Persistence:ConnectionString", connectionString)
            .WithServices(services => services.AddSingleton<IPlatformModule, AuditModule>())
            .StartAsync(CancellationToken.None);

    private static string WriteDocument(
        ScenarioLicenceSigner signer, string tier, IReadOnlyList<string> features,
        DateTimeOffset issuedAt, DateTimeOffset expiresAt)
    {
        var path = Path.Combine(Path.GetTempPath(), $"platform-scenario-licence-{Guid.NewGuid():N}.json");
        File.WriteAllBytes(path, signer.Mint(tier, features, issuedAt, expiresAt));
        return path;
    }

    private static JsonElement Schema()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "object");
            writer.WriteStartObject("properties");
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(stream.ToArray()).RootElement;
    }

    private sealed class ScenarioLicenceSigner(string keyId) : IDisposable
    {
        private readonly RSA _key = RSA.Create(2048);

        internal LicensingOptions Options(string documentPath)
        {
            var publicKey = RSA.Create();
            publicKey.ImportSubjectPublicKeyInfo(_key.ExportSubjectPublicKeyInfo(), out _);
            return new LicensingOptions([new LicenceSigningKey(keyId, publicKey)], documentPath);
        }

        internal byte[] Mint(string tier, IReadOnlyList<string> features, DateTimeOffset issuedAt, DateTimeOffset expiresAt)
        {
            var payload = new LicenceDocumentPayload(tier, features.ToList(), issuedAt.ToString("O"), expiresAt.ToString("O"), 30);
            var payloadBytes = LicenceDocumentReader.SerializePayload(payload);
            var signature = LicenceDocumentReader.Sign(_key, payloadBytes);
            return LicenceDocumentReader.SerializeEnvelope(new LicenceDocumentEnvelope(
                keyId, Convert.ToBase64String(payloadBytes), Convert.ToBase64String(signature)));
        }

        public void Dispose() => _key.Dispose();
    }

    private sealed class ScenarioPrincipalAuthenticationProvider : IAuthenticationProvider
    {
        public string Name => "scenario-shell-authentication";

        public Task<Result<Principal, AuthenticationError>> AuthenticateAsync(
            IAuthenticationRequest request, CancellationToken cancellationToken)
        {
            var name = request.Headers.TryGetValue("X-Scenario-Principal", out var values)
                ? values.FirstOrDefault()
                : null;

            return Task.FromResult(Result<Principal, AuthenticationError>.Success(
                name is null
                    ? Principal.Anonymous
                    : new Principal(new PrincipalId("scenario-shell", name), PrincipalKind.Account, name, null)));
        }
    }

    private sealed class ScenarioOrganizationApi(OrganizationId member, OrganizationId foreign, TenantId memberTenant)
        : IOrganizationApi
    {
        public Task<Result<Organization, OrganizationError>> CreateOrganizationAsync(
            string name, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result<TenantId, OrganizationError>> SwitchActiveOrganizationAsync(
            OrganizationId organization, CancellationToken cancellationToken) =>
            Task.FromResult(organization == member
                ? Result<TenantId, OrganizationError>.Success(memberTenant)
                : organization == foreign
                    ? Result<TenantId, OrganizationError>.Failure(OrganizationError.OrganizationNotFound())
                    : Result<TenantId, OrganizationError>.Failure(OrganizationError.OrganizationNotFound()));

        public Task<Result<IReadOnlyList<Membership>, OrganizationError>> ListMembershipsAsync(
            OrganizationId organization, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result<InvitationToken, OrganizationError>> InviteAsync(
            OrganizationId organization, OrganizationRole role, DateTimeOffset expiresAt,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result<Membership, OrganizationError>> RedeemInvitationAsync(
            string token, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result<OrganizationError>> RevokeMembershipAsync(
            OrganizationId organization, PrincipalId principal, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class ScenarioLicenceState : ILicenceState
    {
        public LicenceClaims? Current => null;

        public LicenceTier Tier => LicenceTier.Community;

        public LicenceVerificationOutcome? LastOutcome => null;
    }

    private sealed class ScenarioAuditReadApi(TenantId tenant) : IAuditReadApi
    {
        public Task<Result<IReadOnlyList<AuditEvent>, AuditReadError>> ByTenantAsync(
            TenantId requested, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) =>
            Task.FromResult(Result<IReadOnlyList<AuditEvent>, AuditReadError>.Success(requested == tenant
                ? [new AuditEvent(
                    new AuditEventId(Guid.NewGuid()), DateTimeOffset.UtcNow, new PrincipalId("scenario-shell", "alice"),
                    PrincipalKind.Account, tenant, new AuditAction("scenario.shell.read"), null, AuditOutcome.Allowed,
                    new CorrelationId(Guid.NewGuid().ToString("N")), AuditClass.Recorded)]
                : []));

        public Task<Result<IReadOnlyList<AuditEvent>, AuditReadError>> ByCorrelationAsync(
            CorrelationId correlation, CancellationToken cancellationToken) =>
            Task.FromResult(Result<IReadOnlyList<AuditEvent>, AuditReadError>.Success(
                (IReadOnlyList<AuditEvent>)[]));

        public Task<Result<IReadOnlyList<AuditEvent>, AuditReadError>> ByActorAsync(
            PrincipalId actor, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) =>
            Task.FromResult(Result<IReadOnlyList<AuditEvent>, AuditReadError>.Success(
                (IReadOnlyList<AuditEvent>)[]));
    }

    private sealed class ScenarioToolInvoker(string producerName) : IToolInvoker
    {
        public int CallCount { get; private set; }

        public ToolProducerName Name { get; } = new(producerName);

        public ValueTask<ToolInvocationResult> InvokeAsync(
            ToolName tool, IDictionary<string, JsonElement> arguments, CancellationToken cancellationToken)
        {
            CallCount++;
            return ValueTask.FromResult(ToolInvocationResult.Success("scenario-ok"));
        }
    }

    private sealed class ScenarioMcpPrincipalAuthenticationProvider : IAuthenticationProvider
    {
        public string Name => "scenario-mcp-authentication";

        public Task<Result<Principal, AuthenticationError>> AuthenticateAsync(
            IAuthenticationRequest request, CancellationToken cancellationToken)
        {
            var name = request.Headers.TryGetValue("X-Scenario-Mcp-Principal", out var values)
                ? values.FirstOrDefault()
                : null;

            return Task.FromResult(Result<Principal, AuthenticationError>.Success(
                name is null
                    ? Principal.Anonymous
                    : new Principal(new PrincipalId("scenario-mcp", name), PrincipalKind.Account, name, null)));
        }
    }

    private sealed class ScenarioMcpHarness(WebApplication app, HttpClient httpClient) : IAsyncDisposable
    {
        internal static async Task<ScenarioMcpHarness> StartAsync(
            string connectionString, PersistenceProvider provider, IReadOnlyCollection<ToolName> exposed,
            PermissionName permission, bool grantsPermission, StubToolProducer producer, ScenarioToolInvoker invoker)
        {
            var (app, client) = await WebHostUnderTest.StartAsync(
                services =>
                {
                    services.AddSingleton<IPlatformModule, McpModule>();
                    services.AddSingleton(new McpOptions { ExposedTools = exposed.ToHashSet() });
                    services.AddSingleton<IPermissionCatalog>(new StubPermissionCatalog(permission));
                    services.AddSingleton<IPermissionProvider>(new StubPermissionProvider(
                        "scenario-mcp-permissions",
                        (_, _, _) => Result<IReadOnlySet<PermissionName>, AuthorizationError>.Success(
                            grantsPermission ? new HashSet<PermissionName> { permission } : new HashSet<PermissionName>())));
                    services.AddSingleton<IAuthenticationProvider, ScenarioMcpPrincipalAuthenticationProvider>();
                    services.AddSingleton<IToolProducer>(producer);
                    services.AddSingleton<IToolInvoker>(invoker);
                },
                settings: new Dictionary<string, string?>
                {
                    ["Platform:Persistence:Provider"] = provider.ToString(),
                    ["Platform:Persistence:ConnectionString"] = connectionString,
                },
                composeOperatedDefaults: false,
                postCompose: services => services.AddSingleton<IAuditSink>(
                    new RecordingAuditSink("scenario-mcp-default", isDurable: true)),
                mapEndpoints: application => application.MapPlatformMcp());

            return new ScenarioMcpHarness(app, client);
        }

        internal async Task<McpClient> ConnectAsync(string principal)
        {
            var transport = new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Endpoint = new Uri(httpClient.BaseAddress!, "/mcp"),
                    TransportMode = HttpTransportMode.StreamableHttp,
                    AdditionalHeaders = new Dictionary<string, string> { ["X-Scenario-Mcp-Principal"] = principal },
                },
                NewHttpClient(principal));
            return await McpClient.CreateAsync(transport, cancellationToken: CancellationToken.None);
        }

        private HttpClient NewHttpClient(string principal)
        {
            var client = new HttpClient { BaseAddress = httpClient.BaseAddress };
            client.DefaultRequestHeaders.Add("X-Scenario-Mcp-Principal", principal);
            return client;
        }

        public async ValueTask DisposeAsync()
        {
            httpClient.Dispose();
            await app.DisposeAsync();
        }
    }
}

public sealed class SqliteOperatedScenarioTests : OperatedScenarioTests
{
    protected override PersistenceProvider Provider => PersistenceProvider.Sqlite;

    protected override Task<string> AcquireConnectionStringAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"platform-scenario-{Guid.NewGuid():N}.db");
        return Task.FromResult($"Data Source={path}");
    }

    protected override Task ReleaseConnectionStringAsync(string connectionString)
    {
        var path = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString).DataSource;
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            try
            {
                File.Delete(path + suffix);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }

        return Task.CompletedTask;
    }
}

public sealed class PostgresOperatedScenarioTests(PostgresContainerFixture fixture)
    : OperatedScenarioTests, IClassFixture<PostgresContainerFixture>
{
    protected override PersistenceProvider Provider => PersistenceProvider.PostgreSql;

    protected override async Task<string> AcquireConnectionStringAsync()
    {
        var database = $"test_{Guid.NewGuid():N}";

        await using var admin = new Npgsql.NpgsqlConnection(fixture.AdminConnectionString);
        await admin.OpenAsync();
        await using (var create = admin.CreateCommand())
        {
            create.CommandText = $"CREATE DATABASE \"{database}\";";
            await create.ExecuteNonQueryAsync();
        }

        var builder = new Npgsql.NpgsqlConnectionStringBuilder(fixture.AdminConnectionString)
        {
            Database = database,
            MaxPoolSize = 200,
        };
        return builder.ConnectionString;
    }

    protected override async Task ReleaseConnectionStringAsync(string connectionString)
    {
        var database = new Npgsql.NpgsqlConnectionStringBuilder(connectionString).Database;
        if (string.IsNullOrEmpty(database))
        {
            return;
        }

        await using var admin = new Npgsql.NpgsqlConnection(fixture.AdminConnectionString);
        await admin.OpenAsync();
        await using var drop = admin.CreateCommand();
        drop.CommandText = $"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE);";
        await drop.ExecuteNonQueryAsync();
    }
}
