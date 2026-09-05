using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Identity;
using SubZeroDev.Platform.Persistence;
using SubZeroDev.Platform.Testing;

namespace SubZeroDev.Platform.Tests;

/// <summary>D5-S9: the Identity module's authentication providers, and the principal they
/// establish.</summary>
public sealed class IdentityTests
{
    private static readonly byte[] IssuerAKey = RandomNumberGenerator.GetBytes(32);
    private static readonly byte[] IssuerBKey = RandomNumberGenerator.GetBytes(32);
    private const string IssuerA = "https://issuer-a.test";
    private const string IssuerB = "https://issuer-b.test";

    /// <summary>S9.1 — a bearer credential authenticates at the transport, and the request observes
    /// an <see cref="PrincipalKind.Account"/> principal whose id carries the issuer and the subject
    /// as two opaque halves.</summary>
    [Fact]
    public async Task S9_1_A_bearer_credential_establishes_an_account_principal_carrying_issuer_and_subject()
    {
        var provider = new JwtBearerAuthenticationProvider("test-issuer-a", IssuerA, IssuerAKey);
        var token = MintToken(IssuerAKey, IssuerA, "alice");

        var authenticated = await provider.AuthenticateAsync(
            new StubAuthenticationRequest(("Authorization", $"Bearer {token}")), CancellationToken.None);

        Assert.True(authenticated.IsSuccess);
        Assert.Equal(PrincipalKind.Account, authenticated.Value.Kind);
        Assert.Equal(new PrincipalId(IssuerA, "alice"), authenticated.Value.Id);
    }

    /// <summary>S9.2 — a credential from a second issuer carrying the same subject produces a
    /// different principal id, never treated as the same principal.</summary>
    [Fact]
    public async Task S9_2_A_credential_from_a_second_issuer_with_the_same_subject_differs()
    {
        var providerA = new JwtBearerAuthenticationProvider("test-issuer-a", IssuerA, IssuerAKey);
        var providerB = new JwtBearerAuthenticationProvider("test-issuer-b", IssuerB, IssuerBKey);

        var fromA = await providerA.AuthenticateAsync(
            new StubAuthenticationRequest(("Authorization", $"Bearer {MintToken(IssuerAKey, IssuerA, "alice")}")),
            CancellationToken.None);
        var fromB = await providerB.AuthenticateAsync(
            new StubAuthenticationRequest(("Authorization", $"Bearer {MintToken(IssuerBKey, IssuerB, "alice")}")),
            CancellationToken.None);

        Assert.True(fromA.IsSuccess);
        Assert.True(fromB.IsSuccess);
        Assert.NotEqual(fromA.Value.Id, fromB.Value.Id);
    }

    /// <summary>S9.2, chained — a deployment trusting two issuers registers one provider per
    /// issuer, and a token from the second issuer still authenticates: the first provider defers
    /// rather than rejecting a well-formed token asserting an issuer it does not trust.</summary>
    [Fact]
    public async Task S9_2_Two_providers_for_two_issuers_both_authenticate_through_the_chain()
    {
        var registry = new AuthenticationProviderRegistry();
        Assert.True(registry.Register(new JwtBearerAuthenticationProvider("test-issuer-a", IssuerA, IssuerAKey)).IsSuccess);
        Assert.True(registry.Register(new JwtBearerAuthenticationProvider("test-issuer-b", IssuerB, IssuerBKey)).IsSuccess);
        registry.Freeze();
        var chain = new AuthenticationChain(registry);

        var authenticated = await chain.AuthenticateAsync(
            new StubAuthenticationRequest(("Authorization", $"Bearer {MintToken(IssuerBKey, IssuerB, "alice")}")),
            CancellationToken.None);

        Assert.True(authenticated.IsSuccess);
        Assert.Equal(new PrincipalId(IssuerB, "alice"), authenticated.Value.Id);
    }

    /// <summary>S9.3 — an invalid credential is refused with <c>CredentialRejected</c> and the
    /// request does not proceed as anonymous.</summary>
    [Fact]
    public async Task S9_3_An_invalid_credential_is_refused_and_does_not_proceed_as_anonymous()
    {
        var provider = new JwtBearerAuthenticationProvider("test-issuer-a", IssuerA, IssuerAKey);
        var forged = MintToken(RandomNumberGenerator.GetBytes(32), IssuerA, "alice");

        var authenticated = await provider.AuthenticateAsync(
            new StubAuthenticationRequest(("Authorization", $"Bearer {forged}")), CancellationToken.None);

        Assert.False(authenticated.IsSuccess);
        Assert.Equal(nameof(AuthenticationError.CredentialRejected), authenticated.Error.Code);
    }

    /// <summary>S8.10/S9.3 — with no signing key cached, the provider fails as unauthenticated
    /// rather than fetching one on the request path.</summary>
    [Fact]
    public async Task No_cached_signing_key_fails_as_key_material_unavailable()
    {
        var provider = new JwtBearerAuthenticationProvider("test-issuer-a", IssuerA, signingKey: null);
        var token = MintToken(IssuerAKey, IssuerA, "alice");

        var authenticated = await provider.AuthenticateAsync(
            new StubAuthenticationRequest(("Authorization", $"Bearer {token}")), CancellationToken.None);

        Assert.False(authenticated.IsSuccess);
        Assert.Equal(nameof(AuthenticationError.KeyMaterialUnavailable), authenticated.Error.Code);
    }

    /// <summary>No credential of this provider's kind (no header, and a differently-issued, still
    /// well-formed token) defers to <see cref="Principal.Anonymous"/> rather than failing.</summary>
    [Fact]
    public async Task No_authorization_header_defers_to_anonymous()
    {
        var provider = new JwtBearerAuthenticationProvider("test-issuer-a", IssuerA, IssuerAKey);

        var authenticated = await provider.AuthenticateAsync(new StubAuthenticationRequest(), CancellationToken.None);

        Assert.True(authenticated.IsSuccess);
        Assert.Equal(Principal.Anonymous, authenticated.Value);
    }

    /// <summary>S9.4 — a principal established from an upstream-proxy assertion observes
    /// <see cref="PrincipalKind.Delegated"/> with no claims, and carries a membership and an audit
    /// actor identically to an <see cref="PrincipalKind.Account"/> one: neither Membership (a
    /// PrincipalId) nor the audit writer special-cases the principal's kind.</summary>
    [Fact]
    public async Task S9_4_A_delegated_principal_carries_an_audit_actor_identically_to_an_account_one()
    {
        var provider = new UpstreamProxyAuthenticationProvider("test-proxy", "https://proxy.test", "X-Forwarded-User");

        var authenticated = await provider.AuthenticateAsync(
            new StubAuthenticationRequest(("X-Forwarded-User", "bob")), CancellationToken.None);

        Assert.True(authenticated.IsSuccess);
        var delegatedPrincipal = authenticated.Value;
        Assert.Equal(PrincipalKind.Delegated, delegatedPrincipal.Kind);
        Assert.Null(delegatedPrincipal.Claims);
        Assert.Equal(new PrincipalId("https://proxy.test", "bob"), delegatedPrincipal.Id);

        var accountPrincipal = new Principal(
            new PrincipalId(IssuerA, "alice"), PrincipalKind.Account, "Alice", null);

        await using var host = await PlatformTestHost.CreateBuilder()
            .WithServices(services => services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IAuditSink>(new RecordingAuditSink())))
            .StartAsync(CancellationToken.None);

        var sink = (RecordingAuditSink)host.Services.GetServices<IAuditSink>().Single(s => s is RecordingAuditSink);
        var scopeFactory = host.Services.GetRequiredService<IOperationScopeFactory>();
        var writer = host.Services.GetRequiredService<IAuditWriter>();
        var tenant = new TenantId(Guid.Parse("22222222-2222-2222-2222-222222222222"));

        foreach (var principal in new[] { delegatedPrincipal, accountPrincipal })
        {
            using var scope = scopeFactory.Begin(tenant, principal);
            var written = await writer.WriteAsync(
                new AuditAction("test.identity.action"), null, AuditOutcome.Allowed, AuditClass.Recorded,
                CancellationToken.None);
            Assert.True(written.IsSuccess);
        }

        Assert.Equal(2, sink.Received.Count);
        Assert.Equal(delegatedPrincipal.Id, sink.Received[0].Actor);
        Assert.Equal(delegatedPrincipal.Kind, sink.Received[0].ActorKind);
        Assert.Equal(accountPrincipal.Id, sink.Received[1].Actor);
        Assert.Equal(accountPrincipal.Kind, sink.Received[1].ActorKind);
    }

    /// <summary>A partial proxy assertion — the header present but empty — is a rejected credential
    /// rather than a silent anonymous fallback.</summary>
    [Fact]
    public async Task An_empty_proxy_assertion_is_rejected()
    {
        var provider = new UpstreamProxyAuthenticationProvider("test-proxy", "https://proxy.test", "X-Forwarded-User");

        var authenticated = await provider.AuthenticateAsync(
            new StubAuthenticationRequest(("X-Forwarded-User", "")), CancellationToken.None);

        Assert.False(authenticated.IsSuccess);
        Assert.Equal(nameof(AuthenticationError.CredentialRejected), authenticated.Error.Code);
    }

    /// <summary>S9.5 — an architecture test: the Identity module declares no entity type, no
    /// <c>DbContext</c> and no migration. Structurally this holds because the assembly carries no
    /// reference at all to Persistence, where every one of those concepts is declared or
    /// implemented — a later addition of any of them would need that reference and trip this test
    /// immediately.</summary>
    [Fact]
    public void S9_5_The_module_declares_no_entity_type_no_DbContext_and_no_migration()
    {
        var identityAssembly = typeof(IdentityModule).Assembly;
        var persistenceAssemblyName = typeof(ITenantOwned).Assembly.GetName().Name;

        var referenced = identityAssembly.GetReferencedAssemblies().Select(a => a.Name).ToArray();
        Assert.DoesNotContain(persistenceAssemblyName, referenced);

        Assert.DoesNotContain(
            identityAssembly.GetTypes(),
            type => typeof(IModuleMigrationSource).IsAssignableFrom(type));
        Assert.DoesNotContain(
            identityAssembly.GetTypes(),
            type => type.Name.Contains("DbContext", StringComparison.Ordinal));
    }

    /// <summary>S9.7 — two credentials differing only in the case of the subject produce two
    /// different principals: nothing is trimmed, folded or normalised.</summary>
    [Fact]
    public async Task S9_7_Subjects_differing_only_in_case_produce_different_principals()
    {
        var provider = new JwtBearerAuthenticationProvider("test-issuer-a", IssuerA, IssuerAKey);

        var lower = await provider.AuthenticateAsync(
            new StubAuthenticationRequest(("Authorization", $"Bearer {MintToken(IssuerAKey, IssuerA, "alice")}")),
            CancellationToken.None);
        var upper = await provider.AuthenticateAsync(
            new StubAuthenticationRequest(("Authorization", $"Bearer {MintToken(IssuerAKey, IssuerA, "ALICE")}")),
            CancellationToken.None);

        Assert.True(lower.IsSuccess);
        Assert.True(upper.IsSuccess);
        Assert.NotEqual(lower.Value.Id, upper.Value.Id);
    }

    /// <summary>S9.7, the proxy provider's half — an upstream-asserted subject is never
    /// case-folded either.</summary>
    [Fact]
    public async Task S9_7_Proxy_asserted_subjects_differing_only_in_case_produce_different_principals()
    {
        var provider = new UpstreamProxyAuthenticationProvider("test-proxy", "https://proxy.test", "X-Forwarded-User");

        var lower = await provider.AuthenticateAsync(
            new StubAuthenticationRequest(("X-Forwarded-User", "bob")), CancellationToken.None);
        var upper = await provider.AuthenticateAsync(
            new StubAuthenticationRequest(("X-Forwarded-User", "BOB")), CancellationToken.None);

        Assert.True(lower.IsSuccess);
        Assert.True(upper.IsSuccess);
        Assert.NotEqual(lower.Value.Id, upper.Value.Id);
    }

    /// <summary>Mints a compact HMAC-SHA256-signed test token — this test suite acting as the test
    /// issuer, over the wire (headers only), exactly as a real one would present a credential. Does
    /// not touch <c>BearerCredential</c>, which is internal to the Identity module: this is the
    /// suite proving the provider's public surface, not its implementation detail.</summary>
    private static string MintToken(byte[] key, string issuer, string subject)
    {
        var header = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "HS256", typ = "JWT" }));
        var payload = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new { iss = issuer, sub = subject }));
        var signingInput = $"{header}.{payload}";
        var signature = Base64UrlEncode(HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(signingInput)));
        return $"{signingInput}.{signature}";
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
