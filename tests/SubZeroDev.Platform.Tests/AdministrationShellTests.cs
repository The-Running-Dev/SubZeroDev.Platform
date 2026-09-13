using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Audit;
using SubZeroDev.Platform.Hosting;
using SubZeroDev.Platform.Licensing;
using SubZeroDev.Platform.Organizations;
using SubZeroDev.Platform.Sample.Web;

namespace SubZeroDev.Platform.Tests;

/// <summary>D5-S16: the administration shell holds no privileged endpoint of its own (I-W1). Every
/// route the shell calls is <c>AdminEndpoints.MapAdminEndpoints</c>, mapped on the sample host the
/// same way <c>/orders</c> is — declared with a permission, reachable by any caller who holds it,
/// with the shell nowhere in the loop.</summary>
public sealed class AdministrationShellTests
{
    private static readonly OrganizationId MemberOrganization = OrganizationId.CreateNew();
    private static readonly OrganizationId ForeignOrganization = OrganizationId.CreateNew();
    private static readonly TenantId MemberTenant = new(Guid.NewGuid());

    /// <summary>S16.1 — the shell's identity view distinguishes an anonymous caller from an
    /// authenticated one, through the same <c>/api/me</c> a plain client reaches.</summary>
    [Fact]
    public async Task S16_1_The_identity_endpoint_distinguishes_anonymous_from_authenticated()
    {
        var (app, client) = await StartShellHostAsync();

        try
        {
            using var anonymous = await client.GetAsync("/api/me");
            Assert.Equal(HttpStatusCode.OK, anonymous.StatusCode);
            var anonymousView = await anonymous.Content.ReadFromJsonAsync<PrincipalView>();
            Assert.False(anonymousView!.IsAuthenticated);
            Assert.Equal(nameof(PrincipalKind.Anonymous), anonymousView.Kind);

            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/me");
            request.Headers.Add(AuthenticatingProvider.PrincipalHeader, "alice");
            using var authenticated = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, authenticated.StatusCode);
            var authenticatedView = await authenticated.Content.ReadFromJsonAsync<PrincipalView>();
            Assert.True(authenticatedView!.IsAuthenticated);
            Assert.Equal("Alice", authenticatedView.DisplayName);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    /// <summary>S16.2 — the shell switches the active organization through <c>/api/organizations/{id}/switch</c>,
    /// the same endpoint a bare <see cref="HttpClient"/> reaches with no UI. A member's switch
    /// succeeds; a non-member's answers not found, never forbidden (S10.6's rule, restated at the
    /// endpoint).</summary>
    [Fact]
    public async Task S16_2_A_plain_HTTP_client_performs_the_identical_switch_the_shell_would()
    {
        var (app, client) = await StartShellHostAsync();

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post, $"/api/organizations/{MemberOrganization}/switch");
            request.Headers.Add(AuthenticatingProvider.PrincipalHeader, "alice");
            using var switched = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, switched.StatusCode);
            var view = await switched.Content.ReadFromJsonAsync<OrganizationSwitchView>();
            Assert.Equal(MemberTenant.Value, view!.Tenant);

            using var foreignRequest = new HttpRequestMessage(
                HttpMethod.Post, $"/api/organizations/{ForeignOrganization}/switch");
            foreignRequest.Headers.Add(AuthenticatingProvider.PrincipalHeader, "alice");
            using var refused = await client.SendAsync(foreignRequest);

            Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    /// <summary>S16.4 — the shell's entitlement view reads the resolved entitlement, the licence
    /// tier and the grace state through <c>/api/entitlement</c>, the public API and nothing
    /// else.</summary>
    [Fact]
    public async Task S16_4_The_entitlement_endpoint_reads_the_decision_and_the_licence_state()
    {
        var (app, client) = await StartShellHostAsync();

        try
        {
            using var response = await client.GetAsync("/api/entitlement");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var view = await response.Content.ReadFromJsonAsync<EntitlementView>();
            Assert.Equal(AdminEndpoints.DemonstrationFeature.Value, view!.Feature);
            Assert.False(view.Granted);
            Assert.Empty(view.Sources);
            Assert.Equal(LicenceTier.Community.Value, view.LicenceTier);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    /// <summary>S16.5 — the shell's audit view reads records filtered by tenant and instant range
    /// through <c>/api/audit</c>, and the endpoint offers no update and no delete: it maps only a
    /// <c>GET</c>.</summary>
    [Fact]
    public async Task S16_5_The_audit_endpoint_reads_records_filtered_by_tenant_and_instant_range()
    {
        var (app, client) = await StartShellHostAsync();

        try
        {
            var from = DateTimeOffset.UtcNow.AddDays(-1);
            var to = DateTimeOffset.UtcNow.AddDays(1);
            using var response = await client.GetAsync(
                $"/api/audit?tenant={MemberTenant.Value}&from={Uri.EscapeDataString(from.ToString("O"))}"
                + $"&to={Uri.EscapeDataString(to.ToString("O"))}");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var view = await response.Content.ReadFromJsonAsync<AuditEventView[]>();
            Assert.Single(view!);
            Assert.Equal(MemberTenant.Value, view![0].Tenant);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    /// <summary>S16.3 / I-W1 — every route named in <see cref="AdminEndpoints.ShellCalls"/> (the
    /// shell's own call list) is mapped as an ordinary endpoint on the live data source, carrying
    /// either a permission requirement or a named exemption like every other endpoint — never a
    /// route reachable only because the shell knows about it.</summary>
    [Fact]
    public async Task S16_3_Every_route_the_shell_calls_is_an_ordinary_declared_endpoint()
    {
        var (app, _) = await StartShellHostAsync();

        try
        {
            var dataSources = app.Services.GetRequiredService<IEnumerable<EndpointDataSource>>();
            var endpoints = dataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().ToList();

            foreach (var (method, template) in AdminEndpoints.ShellCalls)
            {
                var match = endpoints.SingleOrDefault(endpoint =>
                    endpoint.RoutePattern.RawText == template
                    && endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(method) == true);

                Assert.NotNull(match);

                var hasRequirement = match!.Metadata.GetMetadata<EndpointRequirement>() is not null;
                var hasExemption = match.Metadata.GetMetadata<EndpointRequirementExemption>() is not null;
                Assert.True(
                    hasRequirement || hasExemption,
                    $"{method} {template} carries neither a requirement nor an exemption.");

                // I-W1's second half: the shell has no privileged endpoint of its own — a caller
                // holding the declared permission may reach the same route with no exemption
                // singling the shell out. An exemption here would be exactly that special case.
                Assert.False(hasExemption, $"{method} {template} is exempt — the shell would be privileged.");
            }
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    /// <summary>Starts a web host mapping <see cref="AdminEndpoints.MapAdminEndpoints"/> — the exact
    /// endpoints the shell calls — with a fake <see cref="IOrganizationApi"/>, <see cref="ILicenceState"/>
    /// and <see cref="IAuditReadApi"/> standing in for the real modules' persistence. S16 is about the
    /// shell's HTTP surface, not a re-test of Organizations', Licensing's or the audit store's own
    /// business logic, which S10, S12 and S13 already cover.</summary>
    private static Task<(WebApplication App, HttpClient Client)> StartShellHostAsync() =>
        WebHostUnderTest.StartAsync(
            services =>
            {
                services.AddSingleton<IAuthenticationProvider, AuthenticatingProvider>();
                services.AddSingleton<IPermissionCatalog, OperatedComposition.SamplePermissionCatalog>();
                services.AddSingleton<IPermissionProvider, OperatedComposition.NoPolicyPermissionProvider>();
                services.AddSingleton<IOrganizationApi>(
                    new FakeOrganizationApi(MemberOrganization, ForeignOrganization, MemberTenant));
                services.AddSingleton<ILicenceState>(new FakeLicenceState());
                services.AddSingleton<IAuditReadApi>(new FakeAuditReadApi(MemberTenant));
            },
            composeOperatedDefaults: false,
            postCompose: services => services.AddSingleton<IAuditSink>(
                new RecordingAuditSink("test-durable-default", isDurable: true)),
            mapEndpoints: application => application.MapAdminEndpoints());

    /// <summary>Answers <c>Principal.Anonymous</c> with no header, or a fixed "Alice" account when
    /// the test's own header names one — the smallest double that lets S16.1's two branches and
    /// S16.2's switch both authenticate as themselves.</summary>
    private sealed class AuthenticatingProvider : IAuthenticationProvider
    {
        internal const string PrincipalHeader = "X-Test-Principal";

        public string Name => "test-shell-authenticator";

        public Task<Result<Principal, AuthenticationError>> AuthenticateAsync(
            IAuthenticationRequest request, CancellationToken cancellationToken)
        {
            if (!request.Headers.TryGetValue(PrincipalHeader, out var values) || values.Count == 0)
            {
                return Task.FromResult(Result<Principal, AuthenticationError>.Success(Principal.Anonymous));
            }

            var principal = new Principal(
                new PrincipalId("test-issuer", values[0]), PrincipalKind.Account, "Alice", null);
            return Task.FromResult(Result<Principal, AuthenticationError>.Success(principal));
        }
    }

    /// <summary>Answers <see cref="IOrganizationApi.SwitchActiveOrganizationAsync"/> only: every
    /// other member throws, because no test here calls them.</summary>
    private sealed class FakeOrganizationApi(
        OrganizationId member, OrganizationId foreign, TenantId memberTenant) : IOrganizationApi
    {
        public Task<Result<Organization, OrganizationError>> CreateOrganizationAsync(
            string name, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result<InvitationToken, OrganizationError>> InviteAsync(
            OrganizationId organization, OrganizationRole role, DateTimeOffset expiresAt,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result<Membership, OrganizationError>> RedeemInvitationAsync(
            string token, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result<OrganizationError>> RevokeMembershipAsync(
            OrganizationId organization, PrincipalId principal, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Result<TenantId, OrganizationError>> SwitchActiveOrganizationAsync(
            OrganizationId organization, CancellationToken cancellationToken)
        {
            if (organization == member)
            {
                return Task.FromResult(Result<TenantId, OrganizationError>.Success(memberTenant));
            }

            if (organization == foreign)
            {
                return Task.FromResult(
                    Result<TenantId, OrganizationError>.Failure(OrganizationError.OrganizationNotFound()));
            }

            throw new InvalidOperationException("Unexpected organization id in test.");
        }

        public Task<Result<IReadOnlyList<Membership>, OrganizationError>> ListMembershipsAsync(
            OrganizationId organization, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    /// <summary>A fixed Community-tier state with no verified claims — the same shape a fresh
    /// installation with no licence document answers.</summary>
    private sealed class FakeLicenceState : ILicenceState
    {
        public LicenceClaims? Current => null;

        public LicenceTier Tier => LicenceTier.Community;

        public LicenceVerificationOutcome? LastOutcome => LicenceVerificationOutcome.Unavailable;
    }

    /// <summary>Answers <see cref="IAuditReadApi.ByTenantAsync"/> with one fixed record for
    /// <paramref name="tenant"/>; every other member throws, because no test here calls them.</summary>
    private sealed class FakeAuditReadApi(TenantId tenant) : IAuditReadApi
    {
        public Task<Result<IReadOnlyList<AuditEvent>, AuditReadError>> ByTenantAsync(
            TenantId requested, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
        {
            IReadOnlyList<AuditEvent> events = requested == tenant
                ?
                [
                    new AuditEvent(
                        AuditEventId.CreateNew(),
                        DateTimeOffset.UtcNow,
                        new PrincipalId("test-issuer", "alice"),
                        PrincipalKind.Account,
                        tenant,
                        new AuditAction("test.shell.viewed"),
                        null,
                        AuditOutcome.Allowed,
                        new CorrelationId("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
                        AuditClass.Recorded),
                ]
                : [];

            return Task.FromResult(Result<IReadOnlyList<AuditEvent>, AuditReadError>.Success(events));
        }

        public Task<Result<IReadOnlyList<AuditEvent>, AuditReadError>> ByCorrelationAsync(
            CorrelationId correlation, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result<IReadOnlyList<AuditEvent>, AuditReadError>> ByActorAsync(
            PrincipalId actor, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
