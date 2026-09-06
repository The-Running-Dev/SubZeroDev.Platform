using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Organizations;
using SubZeroDev.Platform.Persistence;
using SubZeroDev.Platform.Testing;

namespace SubZeroDev.Platform.Tests;

/// <summary>D5-S10: the Organizations module — create, invite, redeem, revoke, switch active
/// organization, list memberships, and its own <see cref="ITenantResolver"/> and
/// <see cref="IPermissionProvider"/>.</summary>
public sealed class OrganizationsTests
{
    private static readonly Principal Alice = new(new PrincipalId("issuer-a", "alice"), PrincipalKind.Account, "Alice", null);
    private static readonly Principal Bob = new(new PrincipalId("issuer-a", "bob"), PrincipalKind.Account, "Bob", null);
    private static readonly Principal Carol = new(new PrincipalId("issuer-a", "carol"), PrincipalKind.Account, "Carol", null);

    /// <summary>A <see cref="Delegated"/> principal — an upstream-proxy assertion with no account
    /// behind it — used to prove S10.10.</summary>
    private static readonly Principal DelegatedDan = new(new PrincipalId("proxy", "dan"), PrincipalKind.Delegated, "Dan", null);

    private static async Task<IPlatformTestHost> StartHostAsync(RecordingAuditSink sink, Action<IServiceCollection>? extra = null)
    {
        var host = await PlatformTestHost.CreateBuilder()
            .WithProvider(PersistenceProvider.Sqlite)
            .WithServices(services =>
            {
                services.AddSingleton<IPlatformModule, OrganizationsModule>();
                services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditSink>(sink));
                extra?.Invoke(services);
            })
            .StartAsync(CancellationToken.None);

        var migrated = await host.Services.GetRequiredService<IMigrationRunner>().ApplyAsync(CancellationToken.None);
        Assert.True(migrated.IsSuccess);
        return host;
    }

    private static async Task<int> CountRowsAsync(IPlatformTestHost host, string table)
    {
        var unitOfWork = host.Services.GetRequiredService<IUnitOfWork>();
        var result = await unitOfWork.ExecuteAsync(
            TransactionIntent.ReadOnly,
            async ct =>
            {
                var ambient = host.Services.GetRequiredService<IAmbientTransactionAccessor>();
                await using var command = ambient.Current!.Connection.CreateCommand();
                command.Transaction = ambient.Current!.Transaction;
                command.CommandText = $"SELECT COUNT(*) FROM {table};";
                return Convert.ToInt32(await command.ExecuteScalarAsync(ct));
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        return result.Value;
    }

    // S10.1 -----------------------------------------------------------------------------------

    [Fact]
    public async Task S10_1_Create_writes_the_tenant_organization_owner_membership_and_a_Required_audit_row_in_one_transaction()
    {
        var sink = new RecordingAuditSink(isDurable: true);
        await using var host = await StartHostAsync(sink);
        var api = host.Services.GetRequiredService<IOrganizationApi>();
        var scopeFactory = host.Services.GetRequiredService<IOperationScopeFactory>();

        Organization organization;
        using (scopeFactory.Begin(TenantId.Implicit, Alice))
        {
            var created = await api.CreateOrganizationAsync("Acme", CancellationToken.None);
            Assert.True(created.IsSuccess);
            organization = created.Value;
        }

        Assert.Equal(Alice.Id, organization.Owner);
        Assert.Equal(1, await CountRowsAsync(host, "organization"));
        Assert.Equal(1, await CountRowsAsync(host, "organization_membership"));

        var recorded = Assert.Single(sink.Received);
        Assert.Equal(OrganizationsAuditActions.OrganizationCreated.Value, recorded.Action.Value);
        Assert.Equal(AuditClass.Required, recorded.Class);
    }

    [Fact]
    public async Task S10_1_Forcing_the_audit_write_to_fail_leaves_no_organization_and_no_membership()
    {
        var sink = new RecordingAuditSink(isDurable: true);
        await using var host = await StartHostAsync(sink);
        var api = host.Services.GetRequiredService<IOrganizationApi>();
        var scopeFactory = host.Services.GetRequiredService<IOperationScopeFactory>();

        sink.FailNextWith(_ => Result<AuditError>.Failure(AuditError.SinkUnavailable(sink.Name)));

        using (scopeFactory.Begin(TenantId.Implicit, Alice))
        {
            var created = await api.CreateOrganizationAsync("Acme", CancellationToken.None);
            Assert.False(created.IsSuccess);
        }

        Assert.Equal(0, await CountRowsAsync(host, "organization"));
        Assert.Equal(0, await CountRowsAsync(host, "organization_membership"));
    }

    // S10.2 -----------------------------------------------------------------------------------

    [Fact]
    public async Task S10_2_Two_creates_racing_for_the_same_minted_tenant__the_loser_answers_TenantAlreadyAssigned_and_a_retry_mints_fresh()
    {
        var sink = new RecordingAuditSink(isDurable: true);
        var minter = new SequenceTenantMinter();
        await using var host = await StartHostAsync(sink, s => s.AddSingleton<IOrganizationTenantMinter>(minter));
        var api = host.Services.GetRequiredService<IOrganizationApi>();
        var scopeFactory = host.Services.GetRequiredService<IOperationScopeFactory>();

        var collidingTenant = new TenantId(Guid.NewGuid());
        var freshTenant = new TenantId(Guid.NewGuid());
        minter.Enqueue(collidingTenant, collidingTenant, freshTenant);

        using (scopeFactory.Begin(TenantId.Implicit, Alice))
        {
            var first = await api.CreateOrganizationAsync("Acme", CancellationToken.None);
            Assert.True(first.IsSuccess);
            Assert.Equal(collidingTenant, first.Value.Tenant);

            var second = await api.CreateOrganizationAsync("Beta", CancellationToken.None);
            Assert.False(second.IsSuccess);
            Assert.Equal(nameof(OrganizationError.TenantAlreadyAssigned), second.Error.Code);
            Assert.True(second.Error.IsRetryable);

            var retried = await api.CreateOrganizationAsync("Beta", CancellationToken.None);
            Assert.True(retried.IsSuccess);
            Assert.Equal(freshTenant, retried.Value.Tenant);
        }

        Assert.Equal(2, await CountRowsAsync(host, "organization"));
    }

    // S10.3 -----------------------------------------------------------------------------------

    [Fact]
    public async Task S10_3_Minting_an_invitation_returns_the_token_once_and_the_stored_row_holds_only_its_hash()
    {
        var sink = new RecordingAuditSink(isDurable: true);
        await using var host = await StartHostAsync(sink);
        var api = host.Services.GetRequiredService<IOrganizationApi>();
        var scopeFactory = host.Services.GetRequiredService<IOperationScopeFactory>();

        using var scope = scopeFactory.Begin(TenantId.Implicit, Alice);
        var organization = (await api.CreateOrganizationAsync("Acme", CancellationToken.None)).Value;

        var invited = await api.InviteAsync(
            organization.Id, OrganizationRole.Member, host.Clock.UtcNow.AddDays(7), CancellationToken.None);
        Assert.True(invited.IsSuccess);
        Assert.False(string.IsNullOrWhiteSpace(invited.Value.Token));

        var storedHash = await ReadScalarAsync(host, "SELECT token_hash FROM organization_invitation;");
        Assert.NotEqual(invited.Value.Token, storedHash);

        // A second mint returns a different token — nothing is replayed.
        var invitedAgain = await api.InviteAsync(
            organization.Id, OrganizationRole.Member, host.Clock.UtcNow.AddDays(7), CancellationToken.None);
        Assert.True(invitedAgain.IsSuccess);
        Assert.NotEqual(invited.Value.Token, invitedAgain.Value.Token);
    }

    // S10.4 -----------------------------------------------------------------------------------

    [Fact]
    public async Task S10_4_Redeeming_a_valid_token_creates_exactly_one_membership__a_second_presentation_creates_no_second_and_is_refused()
    {
        var sink = new RecordingAuditSink(isDurable: true);
        await using var host = await StartHostAsync(sink);
        var api = host.Services.GetRequiredService<IOrganizationApi>();
        var scopeFactory = host.Services.GetRequiredService<IOperationScopeFactory>();

        OrganizationId organizationId;
        string token;
        using (scopeFactory.Begin(TenantId.Implicit, Alice))
        {
            var organization = (await api.CreateOrganizationAsync("Acme", CancellationToken.None)).Value;
            organizationId = organization.Id;
            token = (await api.InviteAsync(
                organizationId, OrganizationRole.Member, host.Clock.UtcNow.AddDays(7), CancellationToken.None)).Value.Token;
        }

        using (scopeFactory.Begin(TenantId.Implicit, Bob))
        {
            var redeemed = await api.RedeemInvitationAsync(token, CancellationToken.None);
            Assert.True(redeemed.IsSuccess);
            Assert.Equal(MembershipState.Active, redeemed.Value.State);
            Assert.Equal(Bob.Id, redeemed.Value.Principal);

            var redeemedAgain = await api.RedeemInvitationAsync(token, CancellationToken.None);
            Assert.False(redeemedAgain.IsSuccess);
            Assert.Equal(nameof(OrganizationError.InvitationNotRedeemable), redeemedAgain.Error.Code);
        }

        // Owner + Bob, never two Bob rows.
        Assert.Equal(2, await CountRowsAsync(host, "organization_membership"));
    }

    // S10.5 -----------------------------------------------------------------------------------

    [Fact]
    public async Task S10_5_An_expired_an_already_redeemed_and_a_never_existed_token_all_answer_identically()
    {
        var sink = new RecordingAuditSink(isDurable: true);
        await using var host = await StartHostAsync(sink);
        var api = host.Services.GetRequiredService<IOrganizationApi>();
        var scopeFactory = host.Services.GetRequiredService<IOperationScopeFactory>();

        string expiredToken, redeemedToken;
        using (scopeFactory.Begin(TenantId.Implicit, Alice))
        {
            var organization = (await api.CreateOrganizationAsync("Acme", CancellationToken.None)).Value;
            expiredToken = (await api.InviteAsync(
                organization.Id, OrganizationRole.Member, host.Clock.UtcNow.AddSeconds(1), CancellationToken.None)).Value.Token;
            redeemedToken = (await api.InviteAsync(
                organization.Id, OrganizationRole.Member, host.Clock.UtcNow.AddDays(7), CancellationToken.None)).Value.Token;
        }

        using (scopeFactory.Begin(TenantId.Implicit, Bob))
        {
            Assert.True((await api.RedeemInvitationAsync(redeemedToken, CancellationToken.None)).IsSuccess);
        }

        host.Clock.Advance(TimeSpan.FromMinutes(1));

        Result<Membership, OrganizationError> expired, alreadyRedeemed, neverExisted;
        using (scopeFactory.Begin(TenantId.Implicit, Carol))
        {
            expired = await api.RedeemInvitationAsync(expiredToken, CancellationToken.None);
            alreadyRedeemed = await api.RedeemInvitationAsync(redeemedToken, CancellationToken.None);
            neverExisted = await api.RedeemInvitationAsync("this-token-was-never-minted", CancellationToken.None);
        }

        Assert.False(expired.IsSuccess);
        Assert.False(alreadyRedeemed.IsSuccess);
        Assert.False(neverExisted.IsSuccess);
        Assert.Equal(nameof(OrganizationError.InvitationNotRedeemable), expired.Error.Code);
        Assert.Equal(expired.Error.Code, alreadyRedeemed.Error.Code);
        Assert.Equal(expired.Error.Code, neverExisted.Error.Code);
        Assert.Equal(expired.Error.Detail, alreadyRedeemed.Error.Detail);
        Assert.Equal(expired.Error.Detail, neverExisted.Error.Detail);
    }

    // S10.6 -----------------------------------------------------------------------------------

    [Fact]
    public async Task S10_6_Switching_to_a_member_organization_yields_its_tenant__switching_to_one_not_belonged_to_is_not_found()
    {
        var sink = new RecordingAuditSink(isDurable: true);
        await using var host = await StartHostAsync(sink);
        var api = host.Services.GetRequiredService<IOrganizationApi>();
        var activeOrganization = host.Services.GetRequiredService<ActiveOrganizationState>();
        var scopeFactory = host.Services.GetRequiredService<IOperationScopeFactory>();

        Organization organization;
        using (scopeFactory.Begin(TenantId.Implicit, Alice))
        {
            organization = (await api.CreateOrganizationAsync("Acme", CancellationToken.None)).Value;
        }

        using (scopeFactory.Begin(TenantId.Implicit, Alice))
        {
            var switched = await api.SwitchActiveOrganizationAsync(organization.Id, CancellationToken.None);
            Assert.True(switched.IsSuccess);
            Assert.Equal(organization.Tenant, switched.Value);

            // The caller applies the result itself, synchronously — see SwitchActiveOrganizationAsync's
            // doc comment for why the method cannot do this on the caller's behalf.
            activeOrganization.Current = switched.Value;
            Assert.Equal(organization.Tenant, activeOrganization.Current);
        }

        using (scopeFactory.Begin(TenantId.Implicit, Carol))
        {
            var switched = await api.SwitchActiveOrganizationAsync(organization.Id, CancellationToken.None);
            Assert.False(switched.IsSuccess);
            Assert.Equal(nameof(OrganizationError.OrganizationNotFound), switched.Error.Code);
        }
    }

    // S10.7 -----------------------------------------------------------------------------------

    [Fact]
    public async Task S10_7_A_non_member_attempting_to_administer_the_organization_is_told_not_found()
    {
        var sink = new RecordingAuditSink(isDurable: true);
        await using var host = await StartHostAsync(sink);
        var api = host.Services.GetRequiredService<IOrganizationApi>();
        var scopeFactory = host.Services.GetRequiredService<IOperationScopeFactory>();

        Organization organization;
        using (scopeFactory.Begin(TenantId.Implicit, Alice))
        {
            organization = (await api.CreateOrganizationAsync("Acme", CancellationToken.None)).Value;
        }

        using (scopeFactory.Begin(TenantId.Implicit, Carol))
        {
            var revoked = await api.RevokeMembershipAsync(organization.Id, Alice.Id, CancellationToken.None);
            Assert.False(revoked.IsSuccess);
            Assert.Equal(nameof(OrganizationError.OrganizationNotFound), revoked.Error.Code);

            var listed = await api.ListMembershipsAsync(organization.Id, CancellationToken.None);
            Assert.False(listed.IsSuccess);
            Assert.Equal(nameof(OrganizationError.OrganizationNotFound), listed.Error.Code);
        }
    }

    // S10.8 -----------------------------------------------------------------------------------

    [Fact]
    public async Task S10_8_With_no_active_organization_selected_the_resolver_defers()
    {
        var sink = new RecordingAuditSink(isDurable: true);
        await using var host = await StartHostAsync(sink);
        var resolver = new OrganizationsTenantResolverTestAccess(host.Services.GetRequiredService<ActiveOrganizationState>());

        Assert.Null(await resolver.ResolveAsync(CancellationToken.None));

        var chain = new TenantResolutionChainTestAccess(host.Services);
        Assert.Equal(TenantId.Implicit, await chain.ResolveAsync(CancellationToken.None));
    }

    // S10.9 -----------------------------------------------------------------------------------

    [Fact]
    public async Task S10_9_The_permission_provider_grants_AdministerOrganization_to_Owner_and_Administrator_and_not_Member_and_names_its_source()
    {
        var sink = new RecordingAuditSink(isDurable: true);
        await using var host = await StartHostAsync(sink);
        var api = host.Services.GetRequiredService<IOrganizationApi>();
        var scopeFactory = host.Services.GetRequiredService<IOperationScopeFactory>();

        Organization organization;
        string adminToken, memberToken;
        using (scopeFactory.Begin(TenantId.Implicit, Alice))
        {
            organization = (await api.CreateOrganizationAsync("Acme", CancellationToken.None)).Value;
            adminToken = (await api.InviteAsync(
                organization.Id, OrganizationRole.Administrator, host.Clock.UtcNow.AddDays(1), CancellationToken.None)).Value.Token;
            memberToken = (await api.InviteAsync(
                organization.Id, OrganizationRole.Member, host.Clock.UtcNow.AddDays(1), CancellationToken.None)).Value.Token;
        }

        using (scopeFactory.Begin(TenantId.Implicit, Bob))
        {
            Assert.True((await api.RedeemInvitationAsync(adminToken, CancellationToken.None)).IsSuccess);
        }

        using (scopeFactory.Begin(TenantId.Implicit, Carol))
        {
            Assert.True((await api.RedeemInvitationAsync(memberToken, CancellationToken.None)).IsSuccess);
        }

        var providers = host.Services.GetServices<IPermissionProvider>();
        var provider = Assert.Single(providers, p => p.Name.Value == "Platform.Organizations");
        var resource = new ResourceRef("Organization", organization.Id.ToString());

        var ownerGrants = await provider.GrantsAsync(Alice, organization.Tenant, resource, CancellationToken.None);
        var adminGrants = await provider.GrantsAsync(Bob, organization.Tenant, resource, CancellationToken.None);
        var memberGrants = await provider.GrantsAsync(Carol, organization.Tenant, resource, CancellationToken.None);

        Assert.Contains(PlatformPermissions.AdministerOrganization, ownerGrants.Value);
        Assert.Contains(PlatformPermissions.AdministerOrganization, adminGrants.Value);
        Assert.DoesNotContain(PlatformPermissions.AdministerOrganization, memberGrants.Value);

        var evaluator = host.Services.GetRequiredService<IAuthorizationEvaluator>();
        using (scopeFactory.Begin(organization.Tenant, Alice))
        {
            var decision = await evaluator.EvaluateAsync(PlatformPermissions.AdministerOrganization, resource, CancellationToken.None);
            Assert.Equal(AuthorizationOutcome.Allowed, decision.Outcome);
            Assert.Contains(provider.Name, decision.Sources);
        }
    }

    // S10.10 ----------------------------------------------------------------------------------

    [Fact]
    public async Task S10_10_Membership_is_keyed_by_issuer_and_subject_and_a_Delegated_principal_behaves_identically_to_an_Account_one()
    {
        var sink = new RecordingAuditSink(isDurable: true);
        await using var host = await StartHostAsync(sink);
        var api = host.Services.GetRequiredService<IOrganizationApi>();
        var scopeFactory = host.Services.GetRequiredService<IOperationScopeFactory>();

        Organization organization;
        string accountToken, delegatedToken;
        using (scopeFactory.Begin(TenantId.Implicit, Alice))
        {
            organization = (await api.CreateOrganizationAsync("Acme", CancellationToken.None)).Value;
            accountToken = (await api.InviteAsync(
                organization.Id, OrganizationRole.Member, host.Clock.UtcNow.AddDays(1), CancellationToken.None)).Value.Token;
            delegatedToken = (await api.InviteAsync(
                organization.Id, OrganizationRole.Member, host.Clock.UtcNow.AddDays(1), CancellationToken.None)).Value.Token;
        }

        Membership accountMembership, delegatedMembership;
        using (scopeFactory.Begin(TenantId.Implicit, Bob))
        {
            accountMembership = (await api.RedeemInvitationAsync(accountToken, CancellationToken.None)).Value;
        }

        using (scopeFactory.Begin(TenantId.Implicit, DelegatedDan))
        {
            delegatedMembership = (await api.RedeemInvitationAsync(delegatedToken, CancellationToken.None)).Value;
        }

        Assert.Equal(accountMembership.Role, delegatedMembership.Role);
        Assert.Equal(accountMembership.State, delegatedMembership.State);

        using (scopeFactory.Begin(TenantId.Implicit, Alice))
        {
            var listed = (await api.ListMembershipsAsync(organization.Id, CancellationToken.None)).Value;
            Assert.Contains(listed, m => m.Principal == Bob.Id);
            Assert.Contains(listed, m => m.Principal == DelegatedDan.Id);

            var revokedAccount = await api.RevokeMembershipAsync(organization.Id, Bob.Id, CancellationToken.None);
            var revokedDelegated = await api.RevokeMembershipAsync(organization.Id, DelegatedDan.Id, CancellationToken.None);
            Assert.True(revokedAccount.IsSuccess);
            Assert.True(revokedDelegated.IsSuccess);
        }

        // I-I3/I-O6: the membership table has no column referencing any user row — only the two
        // opaque halves of PrincipalId.
        var columns = await ReadColumnNamesAsync(host, "organization_membership");
        Assert.Contains("principal_issuer", columns);
        Assert.Contains("principal_subject", columns);
        Assert.DoesNotContain(columns, c => c.Contains("user", StringComparison.OrdinalIgnoreCase));
    }

    // S10.11 ----------------------------------------------------------------------------------

    [Fact]
    public async Task S10_11_The_schema_contains_no_role_assignment_table()
    {
        var sink = new RecordingAuditSink(isDurable: true);
        await using var host = await StartHostAsync(sink);

        var tables = await ReadOrganizationTableNamesAsync(host);

        Assert.Equal(
            new[] { "organization", "organization_invitation", "organization_membership" },
            tables.OrderBy(t => t, StringComparer.Ordinal));

        var membershipColumns = await ReadColumnNamesAsync(host, "organization_membership");
        Assert.Contains("role", membershipColumns);
    }

    // S10.12 ----------------------------------------------------------------------------------

    [Fact]
    public async Task S10_12_Membership_and_ownership_changes_write_Required_audit_records()
    {
        var sink = new RecordingAuditSink(isDurable: true);
        await using var host = await StartHostAsync(sink);
        var api = host.Services.GetRequiredService<IOrganizationApi>();
        var scopeFactory = host.Services.GetRequiredService<IOperationScopeFactory>();

        Organization organization;
        string token;
        using (scopeFactory.Begin(TenantId.Implicit, Alice))
        {
            organization = (await api.CreateOrganizationAsync("Acme", CancellationToken.None)).Value;
            token = (await api.InviteAsync(
                organization.Id, OrganizationRole.Member, host.Clock.UtcNow.AddDays(1), CancellationToken.None)).Value.Token;
        }

        using (scopeFactory.Begin(TenantId.Implicit, Bob))
        {
            Assert.True((await api.RedeemInvitationAsync(token, CancellationToken.None)).IsSuccess);
        }

        using (scopeFactory.Begin(TenantId.Implicit, Alice))
        {
            Assert.True((await api.RevokeMembershipAsync(organization.Id, Bob.Id, CancellationToken.None)).IsSuccess);
        }

        Assert.Equal(3, sink.Received.Count);
        Assert.All(sink.Received, e => Assert.Equal(AuditClass.Required, e.Class));
        Assert.Contains(sink.Received, e => e.Action.Value == OrganizationsAuditActions.OrganizationCreated.Value);
        Assert.Contains(sink.Received, e => e.Action.Value == OrganizationsAuditActions.MembershipCreated.Value);
        Assert.Contains(sink.Received, e => e.Action.Value == OrganizationsAuditActions.MembershipRevoked.Value);
    }

    // Helpers -----------------------------------------------------------------------------------

    private static async Task<string> ReadScalarAsync(IPlatformTestHost host, string sql)
    {
        var unitOfWork = host.Services.GetRequiredService<IUnitOfWork>();
        var ambient = host.Services.GetRequiredService<IAmbientTransactionAccessor>();
        var result = await unitOfWork.ExecuteAsync(
            TransactionIntent.ReadOnly,
            async ct =>
            {
                await using var command = ambient.Current!.Connection.CreateCommand();
                command.Transaction = ambient.Current!.Transaction;
                command.CommandText = sql;
                return (string)(await command.ExecuteScalarAsync(ct))!;
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        return result.Value;
    }

    private static async Task<IReadOnlyList<string>> ReadColumnNamesAsync(IPlatformTestHost host, string table)
    {
        var unitOfWork = host.Services.GetRequiredService<IUnitOfWork>();
        var ambient = host.Services.GetRequiredService<IAmbientTransactionAccessor>();
        var result = await unitOfWork.ExecuteAsync(
            TransactionIntent.ReadOnly,
            async ct =>
            {
                await using var command = ambient.Current!.Connection.CreateCommand();
                command.Transaction = ambient.Current!.Transaction;
                command.CommandText = $"PRAGMA table_info({table});";
                List<string> names = [];
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    names.Add(reader.GetString(1));
                }

                return (IReadOnlyList<string>)names;
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        return result.Value;
    }

    private static async Task<IReadOnlyList<string>> ReadOrganizationTableNamesAsync(IPlatformTestHost host)
    {
        var unitOfWork = host.Services.GetRequiredService<IUnitOfWork>();
        var ambient = host.Services.GetRequiredService<IAmbientTransactionAccessor>();
        var result = await unitOfWork.ExecuteAsync(
            TransactionIntent.ReadOnly,
            async ct =>
            {
                await using var command = ambient.Current!.Connection.CreateCommand();
                command.Transaction = ambient.Current!.Transaction;
                command.CommandText =
                    "SELECT name FROM sqlite_master WHERE type = 'table' AND name LIKE 'organization%';";
                List<string> names = [];
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    names.Add(reader.GetString(0));
                }

                return (IReadOnlyList<string>)names;
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        return result.Value;
    }
}

/// <summary>Mints a pre-queued sequence of tenants rather than fresh random ones, so S10.2 can force
/// a deterministic collision instead of depending on a real GUID collision.</summary>
internal sealed class SequenceTenantMinter : IOrganizationTenantMinter
{
    private readonly Queue<TenantId> _queue = new();

    internal void Enqueue(params TenantId[] tenants)
    {
        foreach (var tenant in tenants)
        {
            _queue.Enqueue(tenant);
        }
    }

    public TenantId Mint() => _queue.Count > 0 ? _queue.Dequeue() : new TenantId(Guid.NewGuid());
}

/// <summary>Exercises <see cref="OrganizationsTenantResolver"/> without needing internals access —
/// the resolver's whole behaviour is a function of <see cref="ActiveOrganizationState"/>, which is
/// public, so this wrapper reproduces its one line rather than reaching for
/// <c>InternalsVisibleTo</c>.</summary>
internal sealed class OrganizationsTenantResolverTestAccess(ActiveOrganizationState state)
{
    internal Task<TenantId?> ResolveAsync(CancellationToken cancellationToken) => Task.FromResult(state.Current);
}

/// <summary>Reproduces <c>TenantResolutionChain</c>'s "first non-null answer, else Implicit" rule
/// (I-T6) over whatever <see cref="ITenantResolver"/>s are registered in the host, without needing
/// access to Core's internal chain type.</summary>
internal sealed class TenantResolutionChainTestAccess(IServiceProvider services)
{
    internal async Task<TenantId> ResolveAsync(CancellationToken cancellationToken)
    {
        foreach (var resolver in services.GetServices<ITenantResolver>())
        {
            var answer = await resolver.ResolveAsync(cancellationToken);
            if (answer is { } tenant)
            {
                return tenant;
            }
        }

        return TenantId.Implicit;
    }
}
