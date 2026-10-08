using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Persistence;
using SubZeroDev.Platform.Testing;

namespace SubZeroDev.Platform.Tests;

/// <summary>S40: <see cref="PermissionProviderHarness.AssertIssuerRevocationDeniesWithinBoundAsync"/>
/// revokes a mirrored role where it lives — at the issuer — and fails a mirror whose grant outlives
/// <c>Platform:Authorization:MirrorMaximumAge</c> or whose sync does not carry the revocation (#264).
/// The mirror here keeps its rows and stamp in the test host's own database; the issuer is a set in
/// memory.</summary>
public sealed class PermissionProviderHarnessTests
{
    private static readonly TenantId TestTenant = new(Guid.Parse("44444444-4444-4444-4444-444444444444"));
    private static readonly PermissionName Permission = new("Test.Issuer.Role");
    private static readonly Principal Alice = FakePrincipals.Account("issuer-a", "alice");

    [Fact]
    public void S40_1_The_harness_method_exists_as_the_contract_declares_it()
    {
        var method = typeof(PermissionProviderHarness).GetMethod(
            nameof(PermissionProviderHarness.AssertIssuerRevocationDeniesWithinBoundAsync))!;

        Assert.True(method.IsStatic && method.IsPublic);
        Assert.Equal(typeof(Task), method.ReturnType);
        Assert.Equal(
            new[]
            {
                typeof(IPlatformTestHost), typeof(IMirroredPermissionProvider), typeof(Principal), typeof(TenantId),
                typeof(ResourceRef?), typeof(PermissionName), typeof(Func<Task>), typeof(Func<Task>), typeof(Func<Task>),
                typeof(CancellationToken),
            },
            method.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.Equal(
            ["host", "provider", "principal", "tenant", "resource", "permission", "grantAtIssuer", "revokeAtIssuer", "sync", "cancellationToken"],
            method.GetParameters().Select(parameter => parameter.Name));
        Assert.True(method.GetParameters()[^1].HasDefaultValue);
    }

    [Fact]
    public void S40_1_The_contract_points_at_the_tree_instead_of_scaffolding_the_method()
    {
        var contract = File.ReadAllText(Path.Combine(
            S17MutationFixtures.FindRepositoryRoot(AppContext.BaseDirectory), "design", "20-contract.md"));

        Assert.DoesNotContain("public static Task AssertIssuerRevocationDeniesWithinBoundAsync(", contract, StringComparison.Ordinal);
        Assert.DoesNotContain("scaffolded here until the slice that materialises it", contract, StringComparison.Ordinal);
    }

    [Fact]
    public async Task S40_2_A_mirror_whose_sync_reads_the_issuer_and_stamps_the_read_start_passes()
    {
        var issuer = new FakeIssuer();
        await using var host = await StartAsync(issuer, MirrorFault.None);
        var mirror = host.Services.GetRequiredService<SqlMirror>();

        await RunHarnessAsync(host, issuer, mirror);

        Assert.Equal(2, issuer.Reads);
        Assert.Equal(host.Clock.UtcNow, await mirror.StampAsync());
    }

    [Fact]
    public async Task S40_2_The_correct_mirror_also_passes_the_next_request_check_on_its_own_rows()
    {
        var issuer = new FakeIssuer();
        await using var host = await StartAsync(issuer, MirrorFault.None);
        var mirror = host.Services.GetRequiredService<SqlMirror>();

        await PermissionProviderHarness.AssertRevokedGrantDeniesNextRequestAsync(
            mirror, Alice, TestTenant, null, Permission,
            grant: async () => { issuer.Grant(Alice); await mirror.SyncAsync(TestTenant); },
            revoke: async () => { issuer.Revoke(Alice); await mirror.SyncAsync(TestTenant); });
    }

    [Fact]
    public async Task S40_3_A_mirror_whose_sync_stamps_without_reading_the_issuer_fails_naming_it()
    {
        var issuer = new FakeIssuer();
        await using var host = await StartAsync(issuer, MirrorFault.LaterSyncsStampWithoutReading);
        var mirror = host.Services.GetRequiredService<SqlMirror>();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => RunHarnessAsync(host, issuer, mirror));

        Assert.Contains("'mirror'", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("did not carry the revocation", thrown.Message, StringComparison.Ordinal);
        Assert.Equal(1, issuer.Reads);
    }

    [Fact]
    public async Task S40_3_A_mirror_whose_later_syncs_leave_the_stamp_where_it_was_fails_naming_it()
    {
        var issuer = new FakeIssuer();
        await using var host = await StartAsync(issuer, MirrorFault.LaterSyncsLeaveTheStamp);
        var mirror = host.Services.GetRequiredService<SqlMirror>();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => RunHarnessAsync(host, issuer, mirror));

        Assert.Contains("'mirror'", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("did not advance", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task S40_4_A_mirror_whose_stamp_is_always_now_fails_because_the_grant_survived_past_the_maximum()
    {
        var issuer = new FakeIssuer();
        await using var host = await StartAsync(issuer, MirrorFault.StampIsAlwaysNow);
        var mirror = host.Services.GetRequiredService<SqlMirror>();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => RunHarnessAsync(host, issuer, mirror));

        Assert.Contains("'mirror'", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("survived past MirrorMaximumAge with no sync", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task S40_4_A_provider_that_denies_after_the_revoke_but_is_never_stale_fails_too()
    {
        // Reads the issuer live and reports a fresh stamp: the role stops granting, but not because the
        // maximum held, so the stamp advanced with no sync and the harness says so.
        var issuer = new FakeIssuer();
        await using var host = await StartAsync(issuer, MirrorFault.StampIsAlwaysNow | MirrorFault.GrantsReadTheIssuerLive);
        var mirror = host.Services.GetRequiredService<SqlMirror>();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => RunHarnessAsync(host, issuer, mirror));

        Assert.Contains("'mirror'", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("advanced without a sync", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task S40_5_A_second_provider_that_also_grants_the_permission_is_named()
    {
        var issuer = new FakeIssuer();
        var other = new FakePermissionProvider
        {
            Name = new PermissionProviderName("elsewhere"),
            Response = Result<IReadOnlySet<PermissionName>, AuthorizationError>.Success(
                new HashSet<PermissionName> { Permission }),
        };
        await using var host = await StartAsync(issuer, MirrorFault.None, services => services.AddSingleton<IPermissionProvider>(other));
        var mirror = host.Services.GetRequiredService<SqlMirror>();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => RunHarnessAsync(host, issuer, mirror));

        Assert.Contains("'elsewhere'", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("also granted", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task S40_5_A_provider_not_among_the_sources_after_grant_and_sync_fails()
    {
        var issuer = new FakeIssuer();
        await using var host = await StartAsync(issuer, MirrorFault.EverySyncStampsWithoutReading);
        var mirror = host.Services.GetRequiredService<SqlMirror>();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => RunHarnessAsync(host, issuer, mirror));

        Assert.Contains("'mirror'", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("did not grant", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task S40_5_A_provider_instance_the_host_does_not_ask_is_refused()
    {
        var issuer = new FakeIssuer();
        await using var host = await StartAsync(issuer, MirrorFault.None);
        var registered = host.Services.GetRequiredService<SqlMirror>();
        var stray = new SqlMirror(
            issuer,
            host.Services.GetRequiredService<IUnitOfWork>(),
            host.Services.GetRequiredService<IAmbientTransactionAccessor>(),
            host.Clock,
            MirrorFault.None);

        await Assert.ThrowsAsync<ArgumentException>(() => PermissionProviderHarness.AssertIssuerRevocationDeniesWithinBoundAsync(
            host, stray, Alice, TestTenant, null, Permission,
            grantAtIssuer: () => { issuer.Grant(Alice); return Task.CompletedTask; },
            revokeAtIssuer: () => { issuer.Revoke(Alice); return Task.CompletedTask; },
            sync: () => registered.SyncAsync(TestTenant)));
        Assert.Equal(0, issuer.Reads);
    }

    [Fact]
    public async Task S40_6_The_harness_moves_the_clock_by_exactly_the_configured_maximum_plus_one_tick()
    {
        var issuer = new FakeIssuer();
        await using var host = await StartAsync(issuer, MirrorFault.None, maximumAge: "00:05:00");
        var mirror = host.Services.GetRequiredService<SqlMirror>();
        var syncs = new List<DateTimeOffset>();
        DateTimeOffset? revokedAt = null;

        await PermissionProviderHarness.AssertIssuerRevocationDeniesWithinBoundAsync(
            host, mirror, Alice, TestTenant, null, Permission,
            grantAtIssuer: () => { issuer.Grant(Alice); return Task.CompletedTask; },
            revokeAtIssuer: () => { issuer.Revoke(Alice); revokedAt = host.Clock.UtcNow; return Task.CompletedTask; },
            sync: async () => { syncs.Add(host.Clock.UtcNow); await mirror.SyncAsync(TestTenant); });

        Assert.Equal(2, syncs.Count);
        Assert.Equal(syncs[0], revokedAt);
        Assert.Equal(TimeSpan.FromMinutes(5) + TimeSpan.FromTicks(1), syncs[1] - revokedAt!.Value);
    }

    private static Task RunHarnessAsync(IPlatformTestHost host, FakeIssuer issuer, SqlMirror mirror) =>
        PermissionProviderHarness.AssertIssuerRevocationDeniesWithinBoundAsync(
            host, mirror, Alice, TestTenant, null, Permission,
            grantAtIssuer: () => { issuer.Grant(Alice); return Task.CompletedTask; },
            revokeAtIssuer: () => { issuer.Revoke(Alice); return Task.CompletedTask; },
            sync: () => mirror.SyncAsync(TestTenant));

    private static async Task<IPlatformTestHost> StartAsync(
        FakeIssuer issuer, MirrorFault fault, Action<IServiceCollection>? configure = null, string? maximumAge = null)
    {
        var builder = PlatformTestHost.CreateBuilder()
            .WithProvider(PersistenceProvider.Sqlite)
            .WithServices(services =>
            {
                services.AddSingleton<IPermissionCatalog>(new StubPermissionCatalog(Permission));
                services.AddSingleton<IModuleMigrationSource>(new TestMigrationSource(
                    "IssuerMirror",
                    TestMigration.Sql(
                        "0001_roles",
                        "CREATE TABLE t_mirror_role (tenant TEXT NOT NULL, principal TEXT NOT NULL, permission TEXT NOT NULL);"),
                    TestMigration.Sql(
                        "0002_synced",
                        "CREATE TABLE t_mirror_synced (tenant TEXT PRIMARY KEY, synced_at TEXT NOT NULL);")));
                services.AddSingleton(provider => new SqlMirror(
                    issuer,
                    provider.GetRequiredService<IUnitOfWork>(),
                    provider.GetRequiredService<IAmbientTransactionAccessor>(),
                    provider.GetRequiredService<IClock>(),
                    fault));
                services.AddSingleton<IPermissionProvider>(provider => provider.GetRequiredService<SqlMirror>());
                configure?.Invoke(services);
            });

        if (maximumAge is not null)
        {
            builder = builder.WithSetting("Authorization:MirrorMaximumAge", maximumAge);
        }

        var host = await builder.StartAsync(CancellationToken.None);
        Assert.True((await host.Services.GetRequiredService<IMigrationRunner>().ApplyAsync(CancellationToken.None)).IsSuccess);
        return host;
    }

    /// <summary>Where the role really lives: the identity provider's own record, which the mirror's sync
    /// reads.</summary>
    private sealed class FakeIssuer
    {
        private readonly HashSet<string> _holders = [];

        public int Reads { get; private set; }

        public void Grant(Principal principal) => _holders.Add(principal.Id.ToString());

        public void Revoke(Principal principal) => _holders.Remove(principal.Id.ToString());

        public IReadOnlyCollection<string> Read()
        {
            Reads++;
            return [.. _holders];
        }
    }

    [Flags]
    private enum MirrorFault
    {
        None = 0,

        /// <summary>Only the first sync reads the issuer; every later one advances the stamp alone.</summary>
        LaterSyncsStampWithoutReading = 1,

        /// <summary>No sync reads the issuer; each advances the stamp alone.</summary>
        EverySyncStampsWithoutReading = 2,

        /// <summary>Later syncs carry the issuer's rows but leave the stamp at the first sync's.</summary>
        LaterSyncsLeaveTheStamp = 4,

        /// <summary><see cref="IMirroredPermissionProvider.LastSyncedAsync"/> answers the host clock's
        /// current instant, whatever was stored.</summary>
        StampIsAlwaysNow = 8,

        /// <summary>Grants come from the issuer on every request instead of from the rows.</summary>
        GrantsReadTheIssuerLive = 16,
    }

    /// <summary>A consumer's mirror: a sync copies the issuer's role holders into the host's database
    /// and, in the same transaction, stamps the instant it began reading the issuer (I-A14). Grants come
    /// from the rows; <see cref="MirrorFault"/> selects one way of getting that wrong.</summary>
    private sealed class SqlMirror(
        FakeIssuer issuer,
        IUnitOfWork unitOfWork,
        IAmbientTransactionAccessor ambient,
        IClock clock,
        MirrorFault fault) : IMirroredPermissionProvider
    {
        private int _syncs;

        public PermissionProviderName Name { get; } = new("mirror");

        public async Task SyncAsync(TenantId tenant)
        {
            _syncs++;
            var startedAt = clock.UtcNow;
            var reads = !fault.HasFlag(MirrorFault.EverySyncStampsWithoutReading)
                && (_syncs == 1 || !fault.HasFlag(MirrorFault.LaterSyncsStampWithoutReading));
            var stamps = _syncs == 1 || !fault.HasFlag(MirrorFault.LaterSyncsLeaveTheStamp);
            var holders = reads ? issuer.Read() : null;

            var written = await unitOfWork.ExecuteAsync(
                TransactionIntent.Write,
                async token =>
                {
                    if (holders is not null)
                    {
                        await ExecuteAsync("DELETE FROM t_mirror_role WHERE tenant = @tenant;", token, ("@tenant", tenant.ToString()));
                        foreach (var holder in holders)
                        {
                            await ExecuteAsync(
                                "INSERT INTO t_mirror_role (tenant, principal, permission) VALUES (@tenant, @principal, @permission);",
                                token,
                                ("@tenant", tenant.ToString()),
                                ("@principal", holder),
                                ("@permission", Permission.Value));
                        }
                    }

                    if (stamps)
                    {
                        await ExecuteAsync(
                            "INSERT INTO t_mirror_synced (tenant, synced_at) VALUES (@tenant, @at) "
                            + "ON CONFLICT (tenant) DO UPDATE SET synced_at = excluded.synced_at;",
                            token,
                            ("@tenant", tenant.ToString()),
                            ("@at", startedAt.ToString("O", CultureInfo.InvariantCulture)));
                    }
                },
                CancellationToken.None);
            Assert.True(written.IsSuccess);
        }

        public async Task<DateTimeOffset?> StampAsync()
        {
            var read = await LastSyncedAsync(TestTenant, CancellationToken.None);
            return read.Value;
        }

        public async Task<Result<DateTimeOffset?, AuthorizationError>> LastSyncedAsync(
            TenantId tenant, CancellationToken cancellationToken)
        {
            if (fault.HasFlag(MirrorFault.StampIsAlwaysNow))
            {
                return Result<DateTimeOffset?, AuthorizationError>.Success(clock.UtcNow);
            }

            var read = await unitOfWork.ExecuteAsync(
                TransactionIntent.ReadOnly,
                async token =>
                {
                    var value = await ScalarAsync(
                        "SELECT synced_at FROM t_mirror_synced WHERE tenant = @tenant;", token, ("@tenant", tenant.ToString()));
                    return value is string text
                        ? DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                        : (DateTimeOffset?)null;
                },
                cancellationToken);

            return read.IsSuccess
                ? Result<DateTimeOffset?, AuthorizationError>.Success(read.Value)
                : Result<DateTimeOffset?, AuthorizationError>.Failure(AuthorizationError.ProviderUnavailable(Name));
        }

        public async Task<Result<IReadOnlySet<PermissionName>, AuthorizationError>> GrantsAsync(
            Principal principal, TenantId tenant, ResourceRef? resource, CancellationToken cancellationToken)
        {
            if (fault.HasFlag(MirrorFault.GrantsReadTheIssuerLive))
            {
                return Result<IReadOnlySet<PermissionName>, AuthorizationError>.Success(
                    issuer.Read().Contains(principal.Id.ToString())
                        ? new HashSet<PermissionName> { Permission }
                        : new HashSet<PermissionName>());
            }

            var read = await unitOfWork.ExecuteAsync(
                TransactionIntent.ReadOnly,
                async token => await ScalarAsync(
                    "SELECT permission FROM t_mirror_role WHERE tenant = @tenant AND principal = @principal;",
                    token,
                    ("@tenant", tenant.ToString()),
                    ("@principal", principal.Id.ToString())),
                cancellationToken);

            return read.IsSuccess
                ? Result<IReadOnlySet<PermissionName>, AuthorizationError>.Success(
                    read.Value is string permission
                        ? new HashSet<PermissionName> { new(permission) }
                        : new HashSet<PermissionName>())
                : Result<IReadOnlySet<PermissionName>, AuthorizationError>.Failure(AuthorizationError.ProviderUnavailable(Name));
        }

        private async Task ExecuteAsync(string sql, CancellationToken cancellationToken, params (string Name, string Value)[] parameters)
        {
            await using var command = Command(sql, parameters);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private async Task<object?> ScalarAsync(string sql, CancellationToken cancellationToken, params (string Name, string Value)[] parameters)
        {
            await using var command = Command(sql, parameters);
            return await command.ExecuteScalarAsync(cancellationToken);
        }

        private System.Data.Common.DbCommand Command(string sql, (string Name, string Value)[] parameters)
        {
            var current = ambient.Current!;
            var command = current.Connection.CreateCommand();
            command.Transaction = current.Transaction;
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = name;
                parameter.Value = value;
                command.Parameters.Add(parameter);
            }

            return command;
        }
    }
}
