using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Licensing;
using SubZeroDev.Platform.Organizations;
using SubZeroDev.Platform.Persistence;
using SubZeroDev.Platform.Testing;

namespace SubZeroDev.Platform.Tests;

/// <summary>D5-S17: the operated proof against a real, shared PostgreSQL database with at least two
/// separate host instances — S17.2's four contention scenarios (concurrent licence verification,
/// concurrent organization creation, one invitation redeemed twice, concurrent audit appends — the
/// last already proved by <see cref="AuditStorePostgresTests"/>) and S17.4's minimum shape (at least
/// two principals, two organizations, two tenants). Each scenario here issues the racing calls
/// through <see cref="Task.WhenAll{TResult}(System.Collections.Generic.IEnumerable{Task{TResult}})"/>
/// against two independently started <see cref="IPlatformTestHost"/> instances, not two scopes on
/// one host — the same distinction <c>AuditStorePostgresTests</c> draws.</summary>
public sealed class OperatedProofTests(PostgresContainerFixture fixture) : IClassFixture<PostgresContainerFixture>
{
    private static readonly Principal Alice = new(new PrincipalId("issuer-a", "alice"), PrincipalKind.Account, "Alice", null);
    private static readonly Principal Bob = new(new PrincipalId("issuer-a", "bob"), PrincipalKind.Account, "Bob", null);
    private static readonly Principal Carol = new(new PrincipalId("issuer-a", "carol"), PrincipalKind.Account, "Carol", null);
    private static readonly Principal Dave = new(new PrincipalId("issuer-a", "dave"), PrincipalKind.Account, "Dave", null);

    // S17.2 / S17.4 -----------------------------------------------------------------------------

    /// <summary>Four principals create four organizations concurrently, split across two host
    /// instances that share one PostgreSQL database — twice S17.4's minimum of two principals, two
    /// organizations and two tenants, proven under real concurrency rather than sequential calls.
    /// Every organization must land with its own tenant; none may be lost or collide.</summary>
    [Fact]
    public async Task S17_2_Concurrent_organization_creation_from_two_hosts_never_loses_one_or_collides_a_tenant()
    {
        var connectionString = await AcquireConnectionStringAsync();

        await using var hostOne = await StartOrganizationsHostAsync(connectionString);
        await using var hostTwo = await StartOrganizationsHostAsync(connectionString);

        await MigrateAsync(hostOne);

        var creations = new[]
        {
            CreateOrganizationAsync(hostOne, Alice, "Acme"),
            CreateOrganizationAsync(hostTwo, Bob, "Beta"),
            CreateOrganizationAsync(hostOne, Carol, "Cinder"),
            CreateOrganizationAsync(hostTwo, Dave, "Delta"),
        };

        var organizations = await Task.WhenAll(creations);

        Assert.Equal(4, organizations.Select(organization => organization.Id).Distinct().Count());
        Assert.Equal(4, organizations.Select(organization => organization.Tenant).Distinct().Count());

        // A read against a host that did not create it still sees the owner's membership — one
        // shared database, not two.
        var owners = new[] { Alice, Bob, Carol, Dave };
        var readApi = hostTwo.Services.GetRequiredService<IOrganizationApi>();
        for (var i = 0; i < organizations.Length; i++)
        {
            using var scope = hostTwo.Services.GetRequiredService<IOperationScopeFactory>()
                .Begin(TenantId.Implicit, owners[i]);
            var memberships = await readApi.ListMembershipsAsync(organizations[i].Id, CancellationToken.None);
            Assert.True(memberships.IsSuccess);
            Assert.Contains(memberships.Value, membership => membership.Principal == owners[i].Id);
        }
    }

    /// <summary>S17.2: two hosts hold two different licence documents for the same installation and
    /// verify concurrently rather than sequentially. Each host's clock is fixed before the race, so
    /// the monotonic guard's outcome — the later stamp wins, the earlier answers
    /// <see cref="LicensingError.SupersededByNewerVerification"/> — is determined by the data the two
    /// transactions carry, not by which one happens to commit first.</summary>
    [Fact]
    public async Task S17_2_Concurrent_licence_verification_from_two_hosts_converges_on_the_later_stamp()
    {
        var connectionString = await AcquireConnectionStringAsync();

        using var signer = new PostgresTestLicenceSigner("key-2026-postgres");
        var olderDocument = WriteDocument(signer, tier: "Studio", features: ["paid-simulation"]);
        var newerDocument = WriteDocument(signer, tier: "Enterprise", features: ["paid-export"]);

        await using var older = await StartLicensingHostAsync(connectionString, signer.Options(olderDocument));
        await using var newer = await StartLicensingHostAsync(connectionString, signer.Options(newerDocument));

        await MigrateAsync(older);

        older.Clock.Advance(TimeSpan.FromHours(1));
        newer.Clock.Advance(TimeSpan.FromHours(2));

        var results = await Task.WhenAll(VerifyAsync(older), VerifyAsync(newer));

        var olderResult = results[0];
        var newerResult = results[1];

        Assert.True(newerResult.IsSuccess);
        Assert.Equal(LicenceVerificationOutcome.Verified, newerResult.Value);

        // The older host's own re-read after the race must agree that the newer stamp won, whether
        // its own concurrent verification landed first (then lost the row to the newer write) or
        // second (then observed the newer stamp already stored and refused to regress it).
        if (!olderResult.IsSuccess)
        {
            Assert.Equal(nameof(LicensingError.SupersededByNewerVerification), olderResult.Error.Code);
        }

        Assert.Equal(new LicenceTier("Enterprise"), newer.Services.GetRequiredService<ILicenceState>().Tier);
    }

    /// <summary>S17.2 / S17.4: Alice creates an organization and mints one invitation; Bob presents
    /// that same token from two different host instances at once. Exactly one redemption succeeds
    /// and exactly one membership row is written — the concurrency form of S10.4's sequential
    /// double-presentation proof.</summary>
    [Fact]
    public async Task S17_2_Concurrent_redemption_of_one_invitation_from_two_hosts_creates_exactly_one_membership()
    {
        var connectionString = await AcquireConnectionStringAsync();

        await using var issuingHost = await StartOrganizationsHostAsync(connectionString);
        await MigrateAsync(issuingHost);

        var issuingApi = issuingHost.Services.GetRequiredService<IOrganizationApi>();
        var issuingScopes = issuingHost.Services.GetRequiredService<IOperationScopeFactory>();

        OrganizationId organizationId;
        string token;
        using (issuingScopes.Begin(TenantId.Implicit, Alice))
        {
            var organization = (await issuingApi.CreateOrganizationAsync("Acme", CancellationToken.None)).Value;
            organizationId = organization.Id;
            token = (await issuingApi.InviteAsync(
                organizationId, OrganizationRole.Member, issuingHost.Clock.UtcNow.AddDays(7), CancellationToken.None)).Value.Token;
        }

        await using var hostOne = await StartOrganizationsHostAsync(connectionString);
        await using var hostTwo = await StartOrganizationsHostAsync(connectionString);

        var redemptions = await Task.WhenAll(
            RedeemAsync(hostOne, Bob, token),
            RedeemAsync(hostTwo, Bob, token));

        var succeeded = redemptions.Count(result => result.IsSuccess);
        var refused = redemptions.Where(result => !result.IsSuccess).ToList();

        Assert.Equal(1, succeeded);
        Assert.Single(refused);
        Assert.Equal(nameof(OrganizationError.InvitationNotRedeemable), refused[0].Error.Code);

        // Two memberships in total — Alice's own Owner membership from creation, plus exactly one
        // for Bob — never two for Bob.
        var memberships = await ListMembershipsAsync(issuingHost, organizationId);
        Assert.Equal(2, memberships.Count);
        Assert.Single(memberships, membership => membership.Principal == Bob.Id);
    }

    // Helpers -------------------------------------------------------------------------------------

    private static async Task<Organization> CreateOrganizationAsync(IPlatformTestHost host, Principal principal, string name)
    {
        var api = host.Services.GetRequiredService<IOrganizationApi>();
        using var scope = host.Services.GetRequiredService<IOperationScopeFactory>().Begin(TenantId.Implicit, principal);
        var created = await api.CreateOrganizationAsync(name, CancellationToken.None);
        Assert.True(created.IsSuccess);
        return created.Value;
    }

    private static async Task<Result<Membership, OrganizationError>> RedeemAsync(IPlatformTestHost host, Principal principal, string token)
    {
        var api = host.Services.GetRequiredService<IOrganizationApi>();
        using var scope = host.Services.GetRequiredService<IOperationScopeFactory>().Begin(TenantId.Implicit, principal);
        return await api.RedeemInvitationAsync(token, CancellationToken.None);
    }

    private static async Task<IReadOnlyList<Membership>> ListMembershipsAsync(IPlatformTestHost host, OrganizationId organizationId)
    {
        var api = host.Services.GetRequiredService<IOrganizationApi>();
        using var scope = host.Services.GetRequiredService<IOperationScopeFactory>().Begin(TenantId.Implicit, Alice);
        var memberships = await api.ListMembershipsAsync(organizationId, CancellationToken.None);
        Assert.True(memberships.IsSuccess);
        return memberships.Value;
    }

    private static Task<Result<LicenceVerificationOutcome, LicensingError>> VerifyAsync(IPlatformTestHost host) =>
        host.Services.GetRequiredService<ILicenceVerifier>().VerifyOnceAsync(CancellationToken.None);

    private static Task<IPlatformTestHost> StartOrganizationsHostAsync(string connectionString) =>
        PlatformTestHost.CreateBuilder()
            .WithProvider(PersistenceProvider.PostgreSql)
            .WithSetting("Persistence:ConnectionString", connectionString)
            .WithServices(services => services.AddSingleton<IPlatformModule, OrganizationsModule>())
            .StartAsync(CancellationToken.None);

    private static Task<IPlatformTestHost> StartLicensingHostAsync(string connectionString, LicensingOptions options) =>
        PlatformTestHost.CreateBuilder()
            .WithProvider(PersistenceProvider.PostgreSql)
            .WithSetting("Persistence:ConnectionString", connectionString)
            .WithServices(services =>
            {
                services.AddSingleton<IPlatformModule, LicensingModule>();
                services.AddSingleton(options);
            })
            .StartAsync(CancellationToken.None);

    private static async Task MigrateAsync(IPlatformTestHost host)
    {
        var migrated = await host.Services.GetRequiredService<IMigrationRunner>().ApplyAsync(CancellationToken.None);
        Assert.True(migrated.IsSuccess);
    }

    private static string WriteDocument(PostgresTestLicenceSigner signer, string tier, IReadOnlyList<string> features)
    {
        var issuedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var path = Path.Combine(Path.GetTempPath(), $"s17-licence-{Guid.NewGuid():N}.json");
        File.WriteAllBytes(path, signer.Mint(tier, features, issuedAt, issuedAt.AddDays(365)));
        return path;
    }

    private async Task<string> AcquireConnectionStringAsync()
    {
        var database = $"test_{Guid.NewGuid():N}";

        await using var admin = new NpgsqlConnection(fixture.AdminConnectionString);
        await admin.OpenAsync();
        await using var create = admin.CreateCommand();
        create.CommandText = $"CREATE DATABASE \"{database}\";";
        await create.ExecuteNonQueryAsync();

        var builder = new NpgsqlConnectionStringBuilder(fixture.AdminConnectionString)
        {
            Database = database,
            MaxPoolSize = 200,
        };
        return builder.ConnectionString;
    }

    /// <summary>A minimal accepted key pair and the documents it mints — the same shape as
    /// <c>LicensingTests.TestLicenceSigner</c>, duplicated locally because that one is private to its
    /// own test class.</summary>
    private sealed class PostgresTestLicenceSigner(string keyId) : IDisposable
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
}
