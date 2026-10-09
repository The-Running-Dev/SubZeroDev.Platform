using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Hosting;
using SubZeroDev.Platform.Identity;
using SubZeroDev.Platform.Persistence;
using SubZeroDev.Platform.SignIn;
using SubZeroDev.Platform.Testing;

namespace SubZeroDev.Platform.Tests;

/// <summary>D5-S46 and S47: the optional account store. Creating an account, the mapping from a linked
/// identity to its account's principal, a host that does not opt in behaving exactly as before, and
/// linking a second sign-in by completing it.</summary>
public sealed class AccountTests
{
    internal static readonly byte[] IssuerAKey = RandomNumberGenerator.GetBytes(32);
    internal static readonly byte[] IssuerBKey = RandomNumberGenerator.GetBytes(32);
    internal const string IssuerA = "https://issuer-a.test";
    internal const string IssuerB = "https://issuer-b.test";
    private const string SharedEmail = "a@example.test";

    // S46.1 -----------------------------------------------------------------------------------

    /// <summary>S46.1 — two issuers reporting the same email establish two different principals.</summary>
    [Fact]
    public async Task S46_1_Two_sign_ins_reporting_the_same_email_are_two_different_principals()
    {
        await using var host = await StartHostAsync();

        var fromA = await AuthenticateAsync(host, TokenA());
        var fromB = await AuthenticateAsync(host, TokenB());

        Assert.Equal(new PrincipalId(IssuerA, "alice"), fromA.Id);
        Assert.Equal(new PrincipalId(IssuerB, "alice-b"), fromB.Id);
        Assert.Equal(PrincipalKind.Account, fromA.Kind);
        Assert.Equal(PrincipalKind.Account, fromB.Kind);
        Assert.NotEqual(fromA.Id, fromB.Id);
    }

    // S46.2 -----------------------------------------------------------------------------------

    /// <summary>S46.2 — a created account has exactly the creating identity, and the next request with
    /// that identity is the account principal, carrying the token's display name.</summary>
    [Fact]
    public async Task S46_2_The_next_request_after_a_create_is_the_account_principal()
    {
        await using var host = await StartHostAsync();

        var created = await CreateAsync(host, await AuthenticateAsync(host, TokenA()));

        Assert.True(created.IsSuccess);
        var identity = Assert.Single(created.Value.Identities);
        Assert.Equal(new PrincipalId(IssuerA, "alice"), identity.Identity);

        var next = await AuthenticateAsync(host, TokenA());
        Assert.Equal(AccountId.PrincipalIssuer, next.Id.Issuer);
        Assert.Matches("^[0-9a-f]{32}$", next.Id.Subject);
        Assert.Equal(created.Value.Id.Value, next.Id.Subject);
        Assert.Equal(created.Value.Id.ToPrincipalId(), next.Id);
        Assert.Equal(PrincipalKind.Account, next.Kind);
        Assert.Equal("Alice A", next.DisplayName);

        Assert.Equal(new PrincipalId(IssuerB, "alice-b"), (await AuthenticateAsync(host, TokenB())).Id);
    }

    /// <summary>S46.2 — the account principal reads its own account; an identity principal is not
    /// an account.</summary>
    [Fact]
    public async Task S46_2_The_account_principal_reads_its_account_and_an_identity_principal_does_not()
    {
        await using var host = await StartHostAsync();
        var unlinked = await AuthenticateAsync(host, TokenA());

        var beforeCreate = await GetCurrentAsync(host, unlinked);
        var created = await CreateAsync(host, unlinked);
        var read = await GetCurrentAsync(host, await AuthenticateAsync(host, TokenA()));
        var unknown = await GetCurrentAsync(
            host, new Principal(new AccountId(new string('0', 32)).ToPrincipalId(), PrincipalKind.Account, null, null));

        // An identity whose subject happens to equal the account's id is still not that account.
        var lookalike = await GetCurrentAsync(
            host, await AuthenticateAsync(host, Mint(IssuerBKey, IssuerB, created.Value.Id.Value, SharedEmail, "Alice B")));

        Assert.Equal(nameof(AccountError.NotAnAccount), beforeCreate.Error.Code);
        Assert.True(read.IsSuccess);
        Assert.Equal(created.Value, read.Value with { Identities = created.Value.Identities });
        Assert.Equal(created.Value.Identities.Select(i => i.Identity), read.Value.Identities.Select(i => i.Identity));
        Assert.Equal(nameof(AccountError.NotAnAccount), unknown.Error.Code);
        Assert.Equal(nameof(AccountError.NotAnAccount), lookalike.Error.Code);
    }

    // S46.3 -----------------------------------------------------------------------------------

    /// <summary>S46.3 — an account principal cannot create a second account.</summary>
    [Fact]
    public async Task S46_3_A_second_create_under_the_same_sign_in_answers_NotEligible()
    {
        await using var host = await StartHostAsync();
        Assert.True((await CreateAsync(host, await AuthenticateAsync(host, TokenA()))).IsSuccess);

        var second = await CreateAsync(host, await AuthenticateAsync(host, TokenA()));

        Assert.Equal(nameof(AccountError.NotEligible), second.Error.Code);
        Assert.False(second.Error.IsRetryable);
        Assert.Equal(1, await AccountRowsAsync(host));
    }

    /// <summary>S46.3 — two concurrent creates under one unlinked sign-in leave one account; the loser
    /// learns the identity is already linked, not that the store failed.</summary>
    [Fact]
    public async Task S46_3_Two_concurrent_creates_leave_one_account_and_the_loser_answers_IdentityAlreadyLinked()
    {
        await using var host = await StartHostAsync();
        await ConcurrentCreatesLeaveOneAccountAsync(host);
    }

    internal static async Task ConcurrentCreatesLeaveOneAccountAsync(IPlatformTestHost host)
    {
        var unlinked = await AuthenticateAsync(host, TokenA());

        using var go = new ManualResetEventSlim();
        var creates = Enumerable.Range(0, 2)
            .Select(_ => Task.Run(async () =>
            {
                go.Wait();
                return await CreateAsync(host, unlinked);
            }))
            .ToArray();
        go.Set();
        var results = await Task.WhenAll(creates);

        Assert.Single(results, result => result.IsSuccess);
        var loser = Assert.Single(results, result => !result.IsSuccess);
        Assert.Equal(nameof(AccountError.IdentityAlreadyLinked), loser.Error.Code);
        Assert.Equal(1, await AccountRowsAsync(host));
        Assert.Equal(1, await LinkRowsAsync(host));
    }

    // S46.4 -----------------------------------------------------------------------------------

    /// <summary>S46.4 — a principal no Identity provider established as an account cannot create
    /// one, and nothing is written.</summary>
    [Theory]
    [InlineData("delegated")]
    [InlineData("anonymous")]
    [InlineData("local-system")]
    [InlineData("consumer-provider")]
    [InlineData("unknown-provider")]
    public async Task S46_4_A_principal_not_established_by_an_Identity_provider_answers_NotEligible(string kind)
    {
        await using var host = await StartHostAsync(extra: services =>
        {
            services.AddSingleton<IAuthenticationProvider>(
                new UpstreamProxyAuthenticationProvider("test-proxy", "https://proxy.test", "X-Forwarded-User"));
            services.AddSingleton<IAuthenticationProvider>(new ConsumerAccountProvider());
        });

        var principal = kind switch
        {
            "delegated" => await AuthenticateAsync(host, new StubAuthenticationRequest(("X-Forwarded-User", "alice"))),
            "anonymous" => Principal.Anonymous,
            "local-system" => Principal.LocalSystem,
            "consumer-provider" => await AuthenticateAsync(host, new StubAuthenticationRequest(("X-Consumer", "alice"))),
            _ => new Principal(
                new PrincipalId(IssuerA, "alice"),
                PrincipalKind.Account,
                null,
                new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([], "not-registered"))),
        };

        var created = await CreateAsync(host, principal);

        Assert.Equal(nameof(AccountError.NotEligible), created.Error.Code);
        Assert.Equal(0, await AccountRowsAsync(host));
        Assert.Equal(0, await LinkRowsAsync(host));
    }

    // S46.5 -----------------------------------------------------------------------------------

    /// <summary>S46.5 — a host that registers only <see cref="IdentityModule"/> has no account
    /// migration, no account table, and its principals are unchanged.</summary>
    [Fact]
    public async Task S46_5_A_host_without_the_store_has_no_account_table_and_unchanged_principals()
    {
        await using var host = await StartHostAsync(accounts: false);

        Assert.DoesNotContain(
            host.Services.GetServices<IModuleMigrationSource>(),
            source => source.Module == new ModuleName("IdentityAccounts"));
        Assert.Empty(host.Services.GetServices<IPrincipalMapping>());
        Assert.Equal(0, await RuntimeSettingsTests.ScalarAsync(
            host, "SELECT COUNT(*) FROM sqlite_master WHERE name LIKE 'identity%';"));
        Assert.Equal(new PrincipalId(IssuerA, "alice"), (await AuthenticateAsync(host, TokenA())).Id);
    }

    // S46.6 -----------------------------------------------------------------------------------

    /// <summary>S46.6 — with the store unreachable, a request carrying a linked identity's token is
    /// refused unauthenticated, and the endpoint never observes the unlinked identity.</summary>
    [Fact]
    public async Task S46_6_A_store_failure_at_authentication_answers_401_ProviderFailed_never_the_unlinked_pair()
    {
        var path = Path.Combine(Path.GetTempPath(), $"platform-accounts-{Guid.NewGuid():N}.db");
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            // The startup check refuses a SQLite file not in WAL mode; an operator's file already is.
            connection.Open();
            using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA journal_mode=wal;";
            pragma.ExecuteNonQuery();
        }

        var toggle = new FailureToggle();
        var observed = new ConcurrentQueue<PrincipalId>();
        var (app, client) = await WebHostUnderTest.StartAsync(
            services =>
            {
                services.AddPlatformPersistence();
                services.AddSingleton<IPlatformModule, IdentityModule>();
                services.AddSingleton<IPlatformModule, IdentityAccountsModule>();
                services.AddSingleton<IAuthenticationProvider>(ProviderA());
            },
            settings: new Dictionary<string, string?> { ["Platform:Persistence:ConnectionString"] = $"Data Source={path}" },
            postCompose: services => FailingCapability.Decorate(services, toggle),
            mapEndpoints: application => application.MapGet(
                    "/whoami",
                    (ICurrentPrincipal principal) =>
                    {
                        observed.Enqueue(principal.Current.Id);
                        return Results.Text(principal.Current.Id.ToString());
                    })
                .ExemptFromPlatformAuthorization("S46.6 test endpoint recording the principal it observes; not a product surface."));

        try
        {
            Assert.True((await app.Services.GetRequiredService<IMigrationRunner>().ApplyAsync(CancellationToken.None)).IsSuccess);
            var linked = await AuthenticateAsync(app.Services, new StubAuthenticationRequest(("Authorization", $"Bearer {TokenA()}")));
            var created = await CreateAsync(app.Services, linked);
            Assert.True(created.IsSuccess);

            using (var mapped = await client.SendAsync(WhoAmI(TokenA())))
            {
                Assert.Equal(HttpStatusCode.OK, mapped.StatusCode);
            }

            toggle.Failing = true;
            using var refused = await client.SendAsync(WhoAmI(TokenA()));
            var body = await refused.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
            Assert.Contains(nameof(AuthenticationError.ProviderFailed), body, StringComparison.Ordinal);
            Assert.Equal([created.Value.Id.ToPrincipalId()], observed);
        }
        finally
        {
            await app.DisposeAsync();
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                File.Delete(path + suffix);
            }
        }
    }

    // S46.7 -----------------------------------------------------------------------------------

    /// <summary>S46.7 — a create writes one Required record naming the account, with the creating
    /// identity as the actor.</summary>
    [Fact]
    public async Task S46_7_A_create_writes_one_Required_record_naming_the_account()
    {
        var sink = new RecordingAuditSink("accounts", isDurable: true);
        await using var host = await StartHostAsync(sink: sink);

        var created = await CreateAsync(host, await AuthenticateAsync(host, TokenA()));

        Assert.True(created.IsSuccess);
        var record = Assert.Single(sink.Received);
        Assert.Equal(IdentityAuditActions.AccountCreated, record.Action);
        Assert.Equal("platform.identity.account-created", record.Action.Value);
        Assert.Equal(AuditClass.Required, record.Class);
        Assert.Equal(AuditOutcome.Allowed, record.Outcome);
        Assert.Equal(new ResourceRef("Account", created.Value.Id.Value), record.Resource);
        Assert.Equal(new PrincipalId(IssuerA, "alice"), record.Actor);
    }

    /// <summary>S46.7 — a create whose record cannot be written writes nothing, and the sign-in stays
    /// unlinked.</summary>
    [Fact]
    public async Task S46_7_A_create_whose_record_fails_writes_no_account()
    {
        var sink = new RecordingAuditSink("accounts", isDurable: true);
        await using var host = await StartHostAsync(sink: sink);
        sink.FailNextWith(_ => Result<AuditError>.Failure(AuditError.SinkUnavailable(sink.Name)));

        var created = await CreateAsync(host, await AuthenticateAsync(host, TokenA()));

        Assert.False(created.IsSuccess);
        Assert.Equal(nameof(AccountError.StoreUnavailable), created.Error.Code);
        Assert.True(created.Error.IsRetryable);
        Assert.Equal(0, await AccountRowsAsync(host));
        Assert.Equal(0, await LinkRowsAsync(host));
        Assert.Equal(new PrincipalId(IssuerA, "alice"), (await AuthenticateAsync(host, TokenA())).Id);
    }

    // S46.8 -----------------------------------------------------------------------------------

    /// <summary>S46.8 — the store itself refuses a second link for a linked pair, beneath the API.</summary>
    [Fact]
    public async Task S46_8_The_store_refuses_a_second_link_for_a_linked_pair()
    {
        await using var host = await StartHostAsync();
        await SecondLinkIsRefusedAsync(host);
    }

    internal static async Task SecondLinkIsRefusedAsync(IPlatformTestHost host)
    {
        var created = await CreateAsync(host, await AuthenticateAsync(host, TokenA()));
        Assert.True(created.IsSuccess);
        const string other = "0123456789abcdef0123456789abcdef";
        Assert.True(await ExecuteAsync(host, $"INSERT INTO identity_account (id, created_at) VALUES ('{other}', '2026-01-01');"));

        var inserted = await ExecuteAsync(
            host,
            $"INSERT INTO identity_account_link (issuer, subject, account, linked_at) VALUES ('{IssuerA}', 'alice', '{other}', '2026-01-01');");

        Assert.False(inserted);
        Assert.Equal(created.Value.Id.ToPrincipalId(), (await AuthenticateAsync(host, TokenA())).Id);
    }

    // S46.9 -----------------------------------------------------------------------------------

    /// <summary>S46.9 — the two tables carry exactly their keys and timestamps: no email, profile,
    /// credential or claim; and Identity names no type a user or a directory.</summary>
    [Fact]
    public async Task S46_9_The_tables_carry_exactly_their_columns()
    {
        await using var host = await StartHostAsync();

        Assert.Equal(["id", "created_at"], await ColumnsAsync(host, "identity_account"));
        Assert.Equal(["issuer", "subject", "account", "linked_at"], await ColumnsAsync(host, "identity_account_link"));
        Assert.DoesNotContain(
            typeof(IdentityAccountsModule).Assembly.GetTypes(),
            type => type.Name.Contains("User", StringComparison.OrdinalIgnoreCase)
                || type.Name.Contains("Directory", StringComparison.OrdinalIgnoreCase));
    }

    // S46.10 ----------------------------------------------------------------------------------

    /// <summary>S46.10 — the store without <see cref="IdentityModule"/> fails startup through the
    /// module graph.</summary>
    [Fact]
    public async Task S46_10_The_store_without_IdentityModule_fails_startup_with_a_missing_dependency()
    {
        var thrown = await Assert.ThrowsAsync<PlatformStartupException>(() => PlatformTestHost.CreateBuilder()
            .WithProvider(PersistenceProvider.Sqlite)
            .WithServices(services => services.AddSingleton<IPlatformModule, IdentityAccountsModule>())
            .StartAsync(CancellationToken.None));

        var error = Assert.IsType<HostStartupError>(thrown.Error);
        Assert.Equal(nameof(HostStartupError.ModuleGraph), error.Code);
        Assert.Equal("MissingDependency", error.Inner!.Code);
    }

    // S46.11 ----------------------------------------------------------------------------------

    /// <summary>S46.11 — a provider configured with the account issuer cannot mint account
    /// principals: its tokens are rejected.</summary>
    [Fact]
    public async Task S46_11_A_provider_configured_with_the_account_issuer_has_its_tokens_rejected()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        await using var host = await StartHostAsync(extra: services => services.AddSingleton<IAuthenticationProvider>(
            new JwtBearerAuthenticationProvider("impostor", AccountId.PrincipalIssuer, key)));

        var chain = host.Services.GetRequiredService<AuthenticationChain>();
        var authenticated = await chain.AuthenticateAsync(
            Bearer(Mint(key, AccountId.PrincipalIssuer, new string('a', 32), SharedEmail, "Mallory")),
            CancellationToken.None);

        Assert.False(authenticated.IsSuccess);
        Assert.Equal(nameof(AuthenticationError.CredentialRejected), authenticated.Error.Code);
    }

    // Generic path ----------------------------------------------------------------------------

    /// <summary>S46 — the generic-path provider's principals are mapped too, not only the test-grade
    /// provider's.</summary>
    [Fact]
    public async Task S46_A_generic_path_sign_in_becomes_its_account_principal()
    {
        // Alone: the test-grade provider reads every claim as a string, so it rejects rather than
        // defers on the generic path's numeric exp.
        var settings = ConfiguredBearerTests.Valid("corp");
        await using var host = await StartHostAsync(testGrade: false, configure: builder =>
        {
            foreach (var (key, value) in settings)
            {
                builder.WithSetting(key["Platform:".Length..], value!);
            }
        });
        var token = ConfiguredBearerTests.Mint(ConfiguredBearerTests.Rsa, extra: ("name", "Corp Alice"));

        var created = await CreateAsync(host, await AuthenticateAsync(host, Bearer(token)));
        var next = await AuthenticateAsync(host, Bearer(token));

        Assert.True(created.IsSuccess);
        Assert.Equal(new PrincipalId(ConfiguredBearerTests.Issuer, "alice"), Assert.Single(created.Value.Identities).Identity);
        Assert.Equal(created.Value.Id.ToPrincipalId(), next.Id);
    }

    // S47.1 -----------------------------------------------------------------------------------

    /// <summary>S47.1 — a freshly issued second token links its pair to the account, and the next
    /// request with it is the account principal.</summary>
    [Fact]
    public async Task S47_1_A_fresh_second_token_links_and_the_next_request_with_it_is_the_account()
    {
        await using var host = await StartHostAsync();
        var created = await CreateAsync(host, await AuthenticateAsync(host, TokenA()));
        Assert.Equal(new PrincipalId(IssuerB, "alice-b"), (await AuthenticateAsync(host, TokenB())).Id);

        var linked = await LinkAsync(host, await AuthenticateAsync(host, TokenA()), TokenB(host.Clock.UtcNow));

        Assert.True(linked.IsSuccess, linked.IsSuccess ? null : linked.Error.Code);
        Assert.Equal(created.Value.Id, linked.Value.Id);
        Assert.Equal(
            [new PrincipalId(IssuerA, "alice"), new PrincipalId(IssuerB, "alice-b")],
            linked.Value.Identities.Select(i => i.Identity));
        Assert.Equal(host.Clock.UtcNow, Assert.Single(linked.Value.Identities, i => i.Identity.Issuer == IssuerB).LinkedAt);
        Assert.Equal(2, await LinkRowsAsync(host));

        var next = await AuthenticateAsync(host, TokenB());
        Assert.Equal(created.Value.Id.ToPrincipalId(), next.Id);
        Assert.Equal("Alice B", next.DisplayName);
    }

    /// <summary>S47.1 — an account's identities are listed in the order they were linked, not in key
    /// order.</summary>
    [Fact]
    public async Task S47_1_Identities_are_listed_in_link_order()
    {
        await using var host = await StartHostAsync();
        var created = await CreateAsync(host, await AuthenticateAsync(host, TokenB()));
        host.Clock.Advance(TimeSpan.FromMinutes(1));

        var linked = await LinkAsync(
            host,
            await AuthenticateAsync(host, TokenB()),
            Mint(IssuerAKey, IssuerA, "alice", SharedEmail, "Alice A", issuedAt: host.Clock.UtcNow));

        Assert.True(linked.IsSuccess, linked.IsSuccess ? null : linked.Error.Code);
        Assert.Equal(
            [new PrincipalId(IssuerB, "alice-b"), new PrincipalId(IssuerA, "alice")],
            linked.Value.Identities.Select(i => i.Identity));
        Assert.Equal(
            [created.Value.CreatedAt, host.Clock.UtcNow],
            linked.Value.Identities.Select(i => i.LinkedAt));
    }

    /// <summary>S47.1 — the order is the link time's, not the order rows happen to come back in: with
    /// the first-inserted row's link time moved after the second's, it is listed second.</summary>
    [Fact]
    public async Task S47_1_Identities_are_ordered_by_link_time_not_by_row_order()
    {
        await using var host = await StartHostAsync();
        Assert.True((await CreateAsync(host, await AuthenticateAsync(host, TokenA()))).IsSuccess);
        Assert.True((await LinkAsync(host, await AuthenticateAsync(host, TokenA()), TokenB(host.Clock.UtcNow))).IsSuccess);
        var later = host.Services.GetRequiredService<IProviderCapability>().FormatInstant(host.Clock.UtcNow.AddHours(1));
        Assert.True(await ExecuteAsync(host, $"UPDATE identity_account_link SET linked_at = '{later}' WHERE subject = 'alice';"));

        var read = await GetCurrentAsync(host, await AuthenticateAsync(host, TokenA()));

        Assert.Equal(
            [new PrincipalId(IssuerB, "alice-b"), new PrincipalId(IssuerA, "alice")],
            read.Value.Identities.Select(i => i.Identity));
    }

    /// <summary>S47.1 — the second credential is read as given, and a null one is a programming
    /// error.</summary>
    [Fact]
    public async Task S47_1_A_null_second_credential_throws()
    {
        await using var host = await StartHostAsync();
        var created = await CreateAsync(host, await AuthenticateAsync(host, TokenA()));
        Assert.True(created.IsSuccess);

        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await LinkAsync(host, await AuthenticateAsync(host, TokenA()), null!));
    }

    // S47.2 -----------------------------------------------------------------------------------

    /// <summary>S47.2 — on the test-grade provider (no clock tolerance) a second token issued more
    /// than five minutes before the call, after it, or with no <c>iat</c> at all is refused, and
    /// nothing is written.</summary>
    [Theory]
    [InlineData(-7 * 60, false)]
    [InlineData(-5 * 60 - 1, false)]
    [InlineData(-5 * 60, true)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(null, false)]
    public async Task S47_2_A_second_token_must_be_issued_within_five_minutes_of_the_call(int? secondsFromNow, bool links)
    {
        await using var host = await StartHostAsync();
        Assert.True((await CreateAsync(host, await AuthenticateAsync(host, TokenA()))).IsSuccess);
        var token = secondsFromNow is { } offset ? TokenB(host.Clock.UtcNow.AddSeconds(offset)) : TokenB();

        var linked = await LinkAsync(host, await AuthenticateAsync(host, TokenA()), token);

        if (links)
        {
            Assert.True(linked.IsSuccess, linked.IsSuccess ? null : linked.Error.Code);
            Assert.Equal(2, await LinkRowsAsync(host));
            return;
        }

        Assert.Equal(nameof(AccountError.SecondCredentialRejected), linked.Error.Code);
        Assert.False(linked.Error.IsRetryable);
        Assert.Equal(1, await LinkRowsAsync(host));
        Assert.Equal(new PrincipalId(IssuerB, "alice-b"), (await AuthenticateAsync(host, TokenB())).Id);
    }

    /// <summary>S47.2 — on the test-grade provider only <c>iat</c> is an issued-at: a token whose one
    /// numeric claim is a fresh <c>auth_time</c> is unreadable, so it is refused and writes nothing.</summary>
    [Fact]
    public async Task S47_2_A_numeric_claim_other_than_iat_is_not_an_issued_at()
    {
        await using var host = await StartHostAsync();
        Assert.True((await CreateAsync(host, await AuthenticateAsync(host, TokenA()))).IsSuccess);
        var token = Mint(IssuerBKey, IssuerB, "alice-b", SharedEmail, "Alice B", numericClaim: ("auth_time", host.Clock.UtcNow));

        var linked = await LinkAsync(host, await AuthenticateAsync(host, TokenA()), token);

        Assert.Equal(nameof(AccountError.SecondCredentialRejected), linked.Error.Code);
        Assert.Equal(1, await LinkRowsAsync(host));
    }

    /// <summary>S47.2 — on the generic path the provider's <c>ClockTolerance</c> (one minute) widens
    /// the window on either side: seven minutes old is refused, five and a half is not, and a token
    /// with no <c>iat</c> is refused.</summary>
    [Theory]
    [InlineData(-7 * 60, false)]
    [InlineData(-6 * 60 - 1, false)]
    [InlineData(-5 * 60 - 30, true)]
    [InlineData(30, true)]
    [InlineData(61, false)]
    [InlineData(null, false)]
    public async Task S47_2_On_the_generic_path_ClockTolerance_widens_the_window(int? secondsFromNow, bool links)
    {
        await using var host = await StartGenericHostAsync();
        var now = host.Clock.UtcNow;
        Assert.True((await CreateAsync(host, await AuthenticateAsync(host, Bearer(MintGeneric(GenericA, "alice"))))).IsSuccess);
        var token = MintGeneric(GenericB, "alice-b", secondsFromNow is { } offset ? now.AddSeconds(offset) : null);

        var linked = await LinkAsync(host, await AuthenticateAsync(host, Bearer(MintGeneric(GenericA, "alice"))), token);

        if (links)
        {
            Assert.True(linked.IsSuccess, linked.IsSuccess ? null : linked.Error.Code);
            Assert.Contains(new PrincipalId(GenericB, "alice-b"), linked.Value.Identities.Select(i => i.Identity));
            Assert.Equal(linked.Value.Id.ToPrincipalId(), (await AuthenticateAsync(host, Bearer(token))).Id);
            return;
        }

        Assert.Equal(nameof(AccountError.SecondCredentialRejected), linked.Error.Code);
        Assert.Equal(1, await LinkRowsAsync(host));
    }

    // S47.3 -----------------------------------------------------------------------------------

    /// <summary>S47.3 — a principal with no account cannot link: B's unlinked sign-in linking a C
    /// token answers <c>NotAnAccount</c>, and nothing is written.</summary>
    [Fact]
    public async Task S47_3_Linking_under_a_principal_with_no_account_answers_NotAnAccount()
    {
        await using var host = await StartHostAsync();
        var tokenC = Mint(IssuerAKey, IssuerA, "carol", "c@example.test", "Carol", issuedAt: host.Clock.UtcNow);

        var linked = await LinkAsync(host, await AuthenticateAsync(host, TokenB()), tokenC);
        var anonymous = await LinkAsync(host, Principal.Anonymous, tokenC);

        Assert.Equal(nameof(AccountError.NotAnAccount), linked.Error.Code);
        Assert.Equal(nameof(AccountError.NotAnAccount), anonymous.Error.Code);
        Assert.Equal(0, await AccountRowsAsync(host));
        Assert.Equal(0, await LinkRowsAsync(host));
    }

    /// <summary>S47.3 — a provider's principal whose subject happens to equal an account id is not
    /// that account's principal: linking under it answers <c>NotAnAccount</c> and writes nothing.</summary>
    [Fact]
    public async Task S47_3_A_principal_whose_subject_is_an_account_id_is_not_that_account()
    {
        await using var host = await StartHostAsync();
        var x = await CreateAsync(host, await AuthenticateAsync(host, TokenA()));
        Assert.True(x.IsSuccess);
        var lookalike = await AuthenticateAsync(
            host, Mint(IssuerBKey, IssuerB, x.Value.Id.Value, SharedEmail, "Mallory"));
        var tokenC = Mint(IssuerAKey, IssuerA, "carol", "c@example.test", "Carol", issuedAt: host.Clock.UtcNow);

        var linked = await LinkAsync(host, lookalike, tokenC);

        Assert.Equal(nameof(AccountError.NotAnAccount), linked.Error.Code);
        Assert.Equal(1, await LinkRowsAsync(host));
    }

    /// <summary>S47.3 — an account principal whose account row is gone by the time of the call has no
    /// account: linking answers <c>NotAnAccount</c>, not <c>IdentityAlreadyLinked</c>, and writes
    /// nothing.</summary>
    [Fact]
    public async Task S47_3_Linking_to_an_account_that_no_longer_exists_answers_NotAnAccount()
    {
        await using var host = await StartHostAsync();
        Assert.True((await CreateAsync(host, await AuthenticateAsync(host, TokenA()))).IsSuccess);
        var principal = await AuthenticateAsync(host, TokenA());
        Assert.True(await ExecuteAsync(host, "DELETE FROM identity_account_link;"));
        Assert.True(await ExecuteAsync(host, "DELETE FROM identity_account;"));

        var linked = await LinkAsync(host, principal, TokenB(host.Clock.UtcNow));

        Assert.Equal(nameof(AccountError.NotAnAccount), linked.Error.Code);
        Assert.Equal(0, await LinkRowsAsync(host));
    }

    // S47.4 -----------------------------------------------------------------------------------

    /// <summary>S47.4 — a pair already linked to another account is not moved: linking it answers
    /// <c>IdentityAlreadyLinked</c> and both accounts keep their identities.</summary>
    [Fact]
    public async Task S47_4_A_pair_linked_to_another_account_answers_IdentityAlreadyLinked()
    {
        await using var host = await StartHostAsync();
        await PairLinkedElsewhereIsRefusedAsync(host);
    }

    internal static async Task PairLinkedElsewhereIsRefusedAsync(IPlatformTestHost host)
    {
        var x = await CreateAsync(host, await AuthenticateAsync(host, TokenA()));
        var y = await CreateAsync(host, await AuthenticateAsync(host, TokenB()));
        Assert.True(x.IsSuccess);
        Assert.True(y.IsSuccess);

        var linked = await LinkAsync(host, await AuthenticateAsync(host, TokenA()), TokenB(host.Clock.UtcNow));

        Assert.Equal(nameof(AccountError.IdentityAlreadyLinked), linked.Error.Code);
        Assert.False(linked.Error.IsRetryable);
        Assert.Equal(2, await LinkRowsAsync(host));
        Assert.Equal(
            [new PrincipalId(IssuerA, "alice")],
            (await GetCurrentAsync(host, await AuthenticateAsync(host, TokenA()))).Value.Identities.Select(i => i.Identity));
        Assert.Equal(
            [new PrincipalId(IssuerB, "alice-b")],
            (await GetCurrentAsync(host, await AuthenticateAsync(host, TokenB()))).Value.Identities.Select(i => i.Identity));
        Assert.Equal(y.Value.Id.ToPrincipalId(), (await AuthenticateAsync(host, TokenB())).Id);
    }

    /// <summary>S47.4 — two concurrent links of one pair to the same account both succeed and leave
    /// one row: the loser of the insert learns the pair is already this account's.</summary>
    [Fact]
    public async Task S47_4_Two_concurrent_links_of_one_pair_to_one_account_both_succeed()
    {
        await using var host = await StartHostAsync();
        await ConcurrentLinksToOneAccountSucceedAsync(host);
    }

    internal static async Task ConcurrentLinksToOneAccountSucceedAsync(IPlatformTestHost host)
    {
        var created = await CreateAsync(host, await AuthenticateAsync(host, TokenA()));
        Assert.True(created.IsSuccess);
        var account = await AuthenticateAsync(host, TokenA());
        var second = TokenB(host.Clock.UtcNow);

        using var go = new ManualResetEventSlim();
        var links = Enumerable.Range(0, 4)
            .Select(_ => Task.Run(async () =>
            {
                go.Wait();
                return await LinkAsync(host, account, second);
            }))
            .ToArray();
        go.Set();
        var results = await Task.WhenAll(links);

        Assert.All(results, result => Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Code));
        Assert.All(results, result => Assert.Equal(2, result.Value.Identities.Count));
        Assert.Equal(2, await LinkRowsAsync(host));
    }

    // S47.5 -----------------------------------------------------------------------------------

    /// <summary>S47.5 — linking a pair the account already holds succeeds, adds no row and writes no
    /// record.</summary>
    [Fact]
    public async Task S47_5_Linking_a_pair_the_account_holds_succeeds_and_writes_nothing()
    {
        var sink = new RecordingAuditSink("accounts", isDurable: true);
        await using var host = await StartHostAsync(sink: sink);
        var created = await CreateAsync(host, await AuthenticateAsync(host, TokenA()));
        host.Clock.Advance(TimeSpan.FromMinutes(1));

        var linked = await LinkAsync(
            host,
            await AuthenticateAsync(host, TokenA()),
            Mint(IssuerAKey, IssuerA, "alice", SharedEmail, "Alice A", issuedAt: host.Clock.UtcNow));

        Assert.True(linked.IsSuccess, linked.IsSuccess ? null : linked.Error.Code);
        Assert.Equal(created.Value, linked.Value with { Identities = created.Value.Identities });
        Assert.Equal(created.Value.Identities, linked.Value.Identities);
        Assert.Equal(1, await LinkRowsAsync(host));
        Assert.Equal(IdentityAuditActions.AccountCreated, Assert.Single(sink.Received).Action);
    }

    // S47.6 -----------------------------------------------------------------------------------

    /// <summary>S47.6 — a second token Identity's providers do not accept is refused, whatever the
    /// cause: an issuer none trusts, a trusted issuer's token signed with the wrong key, a token
    /// that is not a token, and a token naming the account issuer. A consumer's provider is never
    /// asked.</summary>
    [Theory]
    [InlineData("untrusted issuer")]
    [InlineData("wrong key")]
    [InlineData("not a token")]
    [InlineData("account issuer")]
    public async Task S47_6_A_token_no_Identity_provider_accepts_answers_SecondCredentialRejected(string kind)
    {
        var impostorKey = RandomNumberGenerator.GetBytes(32);
        await using var host = await StartHostAsync(extra: services =>
        {
            services.AddSingleton<IAuthenticationProvider>(new ConsumerAccountProvider());
            services.AddSingleton<IAuthenticationProvider>(
                new JwtBearerAuthenticationProvider("impostor", AccountId.PrincipalIssuer, impostorKey));
        });
        var created = await CreateAsync(host, await AuthenticateAsync(host, TokenA()));
        Assert.True(created.IsSuccess);
        var now = host.Clock.UtcNow;

        var token = kind switch
        {
            "untrusted issuer" => Mint(RandomNumberGenerator.GetBytes(32), "https://unknown.test", "mallory", SharedEmail, "M", issuedAt: now),
            "wrong key" => Mint(RandomNumberGenerator.GetBytes(32), IssuerB, "alice-b", SharedEmail, "Alice B", issuedAt: now),
            "not a token" => "not-a-token",
            _ => Mint(impostorKey, AccountId.PrincipalIssuer, new string('b', 32), SharedEmail, "M", issuedAt: now),
        };

        var linked = await LinkAsync(host, await AuthenticateAsync(host, TokenA()), token);

        Assert.Equal(nameof(AccountError.SecondCredentialRejected), linked.Error.Code);
        Assert.Equal(1, await LinkRowsAsync(host));
        Assert.Equal(1, await AccountRowsAsync(host));
    }

    // S47.7 -----------------------------------------------------------------------------------

    /// <summary>S47.7 — a link writes one Required record naming the account, with the account
    /// principal as the actor.</summary>
    [Fact]
    public async Task S47_7_A_link_writes_one_Required_record_naming_the_account()
    {
        var sink = new RecordingAuditSink("accounts", isDurable: true);
        await using var host = await StartHostAsync(sink: sink);
        var created = await CreateAsync(host, await AuthenticateAsync(host, TokenA()));

        var linked = await LinkAsync(host, await AuthenticateAsync(host, TokenA()), TokenB(host.Clock.UtcNow));

        Assert.True(linked.IsSuccess);
        Assert.Equal(2, sink.Received.Count);
        var record = sink.Received[1];
        Assert.Equal(IdentityAuditActions.IdentityLinked, record.Action);
        Assert.Equal("platform.identity.identity-linked", record.Action.Value);
        Assert.Equal(AuditClass.Required, record.Class);
        Assert.Equal(AuditOutcome.Allowed, record.Outcome);
        Assert.Equal(new ResourceRef("Account", created.Value.Id.Value), record.Resource);
        Assert.Equal(created.Value.Id.ToPrincipalId(), record.Actor);
    }

    /// <summary>S47.7 — a link whose record cannot be written writes no link, and the second sign-in
    /// stays its own pair.</summary>
    [Fact]
    public async Task S47_7_A_link_whose_record_fails_writes_no_link()
    {
        var sink = new RecordingAuditSink("accounts", isDurable: true);
        await using var host = await StartHostAsync(sink: sink);
        Assert.True((await CreateAsync(host, await AuthenticateAsync(host, TokenA()))).IsSuccess);
        sink.FailNextWith(_ => Result<AuditError>.Failure(AuditError.SinkUnavailable(sink.Name)));

        var linked = await LinkAsync(host, await AuthenticateAsync(host, TokenA()), TokenB(host.Clock.UtcNow));

        Assert.Equal(nameof(AccountError.StoreUnavailable), linked.Error.Code);
        Assert.True(linked.Error.IsRetryable);
        Assert.Equal(1, await LinkRowsAsync(host));
        Assert.Equal(new PrincipalId(IssuerB, "alice-b"), (await AuthenticateAsync(host, TokenB())).Id);
    }

    // S47.8 -----------------------------------------------------------------------------------

    /// <summary>S47.8 — end to end through the sign-in module: sign in with method A and create the
    /// account, sign in with method B, link B's token under A's, and a request bearing B's token
    /// resolves to the account. Before the link it resolved to B's own pair.</summary>
    [Fact]
    public async Task S47_8_End_to_end_a_second_sign_in_linked_through_the_sign_in_module_resolves_to_the_account()
    {
        var path = Path.Combine(Path.GetTempPath(), $"platform-accounts-{Guid.NewGuid():N}.db");
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA journal_mode=wal;";
            pragma.ExecuteNonQuery();
        }

        var issuers = new TwoIssuers();
        var settings = new Dictionary<string, string?> { ["Platform:Persistence:ConnectionString"] = $"Data Source={path}" };
        foreach (var (method, issuer) in new[] { ("a", TwoIssuers.A), ("b", TwoIssuers.B) })
        {
            foreach (var (key, value) in ConfiguredBearerTests.Valid(method, issuer: issuer))
            {
                settings[key] = value;
            }

            settings[$"SubZeroDev:SignIn:{method}:Issuer"] = issuer;
            settings[$"SubZeroDev:SignIn:{method}:ClientId"] = TwoIssuers.ClientId;
            settings[$"SubZeroDev:SignIn:{method}:RedirectPath"] = $"/signin-callback-{method}";
        }

        var (app, plain) = await WebHostUnderTest.StartAsync(
            services =>
            {
                services.AddPlatformPersistence();
                services.AddSingleton<IPlatformModule, IdentityModule>();
                services.AddSingleton<IPlatformModule, IdentityAccountsModule>();
                services.AddSingleton(new SignInTransport(() => issuers));
                services.AddPlatformSignIn();
            },
            settings: settings,
            mapEndpoints: web =>
            {
                web.MapPlatformSignIn();
                web.MapGet("/whoami", (ICurrentPrincipal principal) => Results.Text(principal.Current.Id.ToString()))
                    .ExemptFromPlatformAuthorization("S47.8 test endpoint returning the principal it observes; not a product surface.");
            });
        plain.Dispose();

        try
        {
            Assert.True((await app.Services.GetRequiredService<IMigrationRunner>().ApplyAsync(CancellationToken.None)).IsSuccess);
            var tokenA = await SignInAsync(app, issuers, "a");
            var created = await CreateAsync(
                app.Services, await AuthenticateAsync(app.Services, Bearer(tokenA)));
            Assert.True(created.IsSuccess);

            var tokenB = await SignInAsync(app, issuers, "b");
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
            Assert.Equal(new PrincipalId(TwoIssuers.B, "alice-b").ToString(), await WhoAmIAsync(client, tokenB));

            var linked = await LinkAsync(app.Services, await AuthenticateAsync(app.Services, Bearer(tokenA)), tokenB);

            Assert.True(linked.IsSuccess, linked.IsSuccess ? null : linked.Error.Code);
            Assert.Equal(created.Value.Id.ToPrincipalId().ToString(), await WhoAmIAsync(client, tokenB));
            Assert.Equal(created.Value.Id.ToPrincipalId().ToString(), await WhoAmIAsync(client, tokenA));
        }
        finally
        {
            await app.DisposeAsync();
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                File.Delete(path + suffix);
            }
        }
    }

    // Helpers ---------------------------------------------------------------------------------

    // S48.1 -----------------------------------------------------------------------------------

    /// <summary>S48.1 — an account holding A, linked first, and B, linked second, answers both, in that
    /// order, under either token.</summary>
    [Fact]
    public async Task S48_1_Either_sign_in_reads_both_identities_in_link_order()
    {
        await using var host = await StartHostAsync();
        await LinkedPairAsync(host);

        var underA = await GetCurrentAsync(host, await AuthenticateAsync(host, TokenA()));
        var underB = await GetCurrentAsync(host, await AuthenticateAsync(host, TokenB()));

        PrincipalId[] expected = [new(IssuerA, "alice"), new(IssuerB, "alice-b")];
        Assert.Equal(expected, underA.Value.Identities.Select(i => i.Identity));
        Assert.Equal(expected, underB.Value.Identities.Select(i => i.Identity));
        Assert.Equal(underA.Value.Id, underB.Value.Id);
    }

    // S48.2 -----------------------------------------------------------------------------------

    /// <summary>S48.2 — unlinking B under A's token leaves the account with A only, B's token is its raw
    /// pair on the next request, and one Required record names the account.</summary>
    [Fact]
    public async Task S48_2_Unlinking_a_second_sign_in_leaves_the_first_and_frees_the_second()
    {
        var sink = new RecordingAuditSink("accounts", isDurable: true);
        await using var host = await StartHostAsync(sink: sink);
        var x = await LinkedPairAsync(host);

        var unlinked = await UnlinkAsync(host, await AuthenticateAsync(host, TokenA()), new PrincipalId(IssuerB, "alice-b"));

        Assert.True(unlinked.IsSuccess, unlinked.IsSuccess ? null : unlinked.Error.Code);
        Assert.Equal(x, unlinked.Value.Id);
        Assert.Equal([new PrincipalId(IssuerA, "alice")], unlinked.Value.Identities.Select(i => i.Identity));
        Assert.Equal(1, await LinkRowsAsync(host));
        Assert.Equal(new PrincipalId(IssuerB, "alice-b"), (await AuthenticateAsync(host, TokenB())).Id);
        Assert.Equal(x.ToPrincipalId(), (await AuthenticateAsync(host, TokenA())).Id);

        Assert.Equal(3, sink.Received.Count);
        var record = sink.Received[2];
        Assert.Equal(IdentityAuditActions.IdentityUnlinked, record.Action);
        Assert.Equal("platform.identity.identity-unlinked", record.Action.Value);
        Assert.Equal(AuditClass.Required, record.Class);
        Assert.Equal(AuditOutcome.Allowed, record.Outcome);
        Assert.Equal(new ResourceRef("Account", x.Value), record.Resource);
        Assert.Equal(x.ToPrincipalId(), record.Actor);
    }

    /// <summary>S48.2 — an unlink whose record cannot be written removes nothing.</summary>
    [Fact]
    public async Task S48_2_An_unlink_whose_record_fails_removes_nothing()
    {
        var sink = new RecordingAuditSink("accounts", isDurable: true);
        await using var host = await StartHostAsync(sink: sink);
        var x = await LinkedPairAsync(host);
        sink.FailNextWith(_ => Result<AuditError>.Failure(AuditError.SinkUnavailable(sink.Name)));

        var unlinked = await UnlinkAsync(host, await AuthenticateAsync(host, TokenA()), new PrincipalId(IssuerB, "alice-b"));

        Assert.Equal(nameof(AccountError.StoreUnavailable), unlinked.Error.Code);
        Assert.True(unlinked.Error.IsRetryable);
        Assert.Equal(2, await LinkRowsAsync(host));
        Assert.Equal(x.ToPrincipalId(), (await AuthenticateAsync(host, TokenB())).Id);
    }

    // S48.3 -----------------------------------------------------------------------------------

    /// <summary>S48.3 — the account's only identity cannot be unlinked, and the refusal writes
    /// nothing.</summary>
    [Fact]
    public async Task S48_3_The_last_identity_cannot_be_unlinked()
    {
        var sink = new RecordingAuditSink("accounts", isDurable: true);
        await using var host = await StartHostAsync(sink: sink);
        var x = await CreateAsync(host, await AuthenticateAsync(host, TokenA()));

        var unlinked = await UnlinkAsync(host, await AuthenticateAsync(host, TokenA()), new PrincipalId(IssuerA, "alice"));

        Assert.Equal(nameof(AccountError.LastIdentity), unlinked.Error.Code);
        Assert.False(unlinked.Error.IsRetryable);
        Assert.Equal(1, await LinkRowsAsync(host));
        Assert.Single(sink.Received);
        Assert.Equal(x.Value.Id.ToPrincipalId(), (await AuthenticateAsync(host, TokenA())).Id);
    }

    // S48.4 -----------------------------------------------------------------------------------

    /// <summary>S48.4 — a pair another account holds and a pair no account holds answer alike, so an
    /// unlink is no way to learn which pairs other accounts hold; neither writes anything.</summary>
    [Fact]
    public async Task S48_4_A_pair_held_elsewhere_and_a_pair_held_nowhere_answer_alike()
    {
        var sink = new RecordingAuditSink("accounts", isDurable: true);
        await using var host = await StartHostAsync(sink: sink);
        await LinkedPairAsync(host);
        var carol = Mint(IssuerAKey, IssuerA, "carol", "c@example.test", "Carol");
        var y = await CreateAsync(host, await AuthenticateAsync(host, carol));
        Assert.True(y.IsSuccess);
        var account = await AuthenticateAsync(host, TokenA());

        var elsewhere = await UnlinkAsync(host, account, new PrincipalId(IssuerA, "carol"));
        var nowhere = await UnlinkAsync(host, account, new PrincipalId(IssuerB, "nobody"));

        Assert.Equal(nameof(AccountError.IdentityNotLinked), elsewhere.Error.Code);
        Assert.Equal(elsewhere.Error.Code, nowhere.Error.Code);
        Assert.Equal(elsewhere.Error.Detail, nowhere.Error.Detail);
        Assert.Equal(elsewhere.Error.IsRetryable, nowhere.Error.IsRetryable);
        Assert.False(elsewhere.Error.IsRetryable);
        Assert.Equal(3, await LinkRowsAsync(host));
        Assert.Equal(y.Value.Id.ToPrincipalId(), (await AuthenticateAsync(host, carol)).Id);
        Assert.Equal(3, sink.Received.Count);
    }

    /// <summary>S48.4 — pairs match ordinally on both halves: a pair differing only in case is not
    /// the account's.</summary>
    [Fact]
    public async Task S48_4_A_pair_differing_only_in_case_is_not_linked()
    {
        await using var host = await StartHostAsync();
        await LinkedPairAsync(host);

        var unlinked = await UnlinkAsync(host, await AuthenticateAsync(host, TokenA()), new PrincipalId(IssuerB, "Alice-B"));

        Assert.Equal(nameof(AccountError.IdentityNotLinked), unlinked.Error.Code);
        Assert.Equal(2, await LinkRowsAsync(host));
    }

    /// <summary>S48.4 — unlinking under a principal with no account answers <c>NotAnAccount</c>.</summary>
    [Fact]
    public async Task S48_4_Unlinking_under_a_principal_with_no_account_answers_NotAnAccount()
    {
        await using var host = await StartHostAsync();
        await LinkedPairAsync(host);
        var carol = await AuthenticateAsync(host, Mint(IssuerAKey, IssuerA, "carol", "c@example.test", "Carol"));

        var unlinked = await UnlinkAsync(host, carol, new PrincipalId(IssuerB, "alice-b"));
        var anonymous = await UnlinkAsync(host, Principal.Anonymous, new PrincipalId(IssuerB, "alice-b"));

        Assert.Equal(nameof(AccountError.NotAnAccount), unlinked.Error.Code);
        Assert.Equal(nameof(AccountError.NotAnAccount), anonymous.Error.Code);
        Assert.Equal(2, await LinkRowsAsync(host));
    }

    /// <summary>S48.4 — a provider principal whose subject equals an account id is not that account:
    /// unlinking answers <c>NotAnAccount</c> and removes nothing.</summary>
    [Fact]
    public async Task S48_4_A_principal_whose_subject_is_an_account_id_is_not_that_account()
    {
        await using var host = await StartHostAsync();
        var x = await LinkedPairAsync(host);
        var lookalike = await AuthenticateAsync(
            host, Mint(IssuerBKey, IssuerB, x.Value, "m@example.test", "Mallory"));

        var unlinked = await UnlinkAsync(host, lookalike, new PrincipalId(IssuerB, "alice-b"));

        Assert.Equal(nameof(AccountError.NotAnAccount), unlinked.Error.Code);
        Assert.Equal(2, await LinkRowsAsync(host));
    }

    /// <summary>S48.4 — an account principal whose account row is gone answers <c>NotAnAccount</c>.</summary>
    [Fact]
    public async Task S48_4_Unlinking_from_an_account_that_no_longer_exists_answers_NotAnAccount()
    {
        await using var host = await StartHostAsync();
        await LinkedPairAsync(host);
        var principal = await AuthenticateAsync(host, TokenA());
        Assert.True(await ExecuteAsync(host, "DELETE FROM identity_account_link;"));
        Assert.True(await ExecuteAsync(host, "DELETE FROM identity_account;"));

        var unlinked = await UnlinkAsync(host, principal, new PrincipalId(IssuerB, "alice-b"));

        Assert.Equal(nameof(AccountError.NotAnAccount), unlinked.Error.Code);
    }

    /// <summary>S48.2 — the pair is both halves: unlinking (B, <c>alice</c>) leaves (A, <c>alice</c>),
    /// which shares its subject.</summary>
    [Fact]
    public async Task S48_2_Unlinking_one_pair_leaves_another_with_the_same_subject()
    {
        await using var host = await StartHostAsync();
        var x = await CreateAsync(host, await AuthenticateAsync(host, TokenA()));
        Assert.True(x.IsSuccess);
        var account = await AuthenticateAsync(host, TokenA());
        var sameSubject = Mint(IssuerBKey, IssuerB, "alice", SharedEmail, "Alice B", issuedAt: host.Clock.UtcNow);
        Assert.True((await LinkAsync(host, account, sameSubject)).IsSuccess);

        var unlinked = await UnlinkAsync(host, account, new PrincipalId(IssuerB, "alice"));

        Assert.True(unlinked.IsSuccess, unlinked.IsSuccess ? null : unlinked.Error.Code);
        Assert.Equal([new PrincipalId(IssuerA, "alice")], unlinked.Value.Identities.Select(i => i.Identity));
        Assert.Equal(1, await LinkRowsAsync(host));
    }

    // S48.5 -----------------------------------------------------------------------------------

    /// <summary>S48.5 — the pair the caller is presenting may be unlinked while another remains, and
    /// that token is its raw pair from the next request on.</summary>
    [Fact]
    public async Task S48_5_Unlinking_the_presented_sign_in_frees_it_from_the_next_request()
    {
        await using var host = await StartHostAsync();
        var x = await LinkedPairAsync(host);
        var presenting = await AuthenticateAsync(host, TokenB());
        Assert.Equal(x.ToPrincipalId(), presenting.Id);

        var unlinked = await UnlinkAsync(host, presenting, new PrincipalId(IssuerB, "alice-b"));

        Assert.True(unlinked.IsSuccess, unlinked.IsSuccess ? null : unlinked.Error.Code);
        Assert.Equal([new PrincipalId(IssuerA, "alice")], unlinked.Value.Identities.Select(i => i.Identity));
        Assert.Equal(new PrincipalId(IssuerB, "alice-b"), (await AuthenticateAsync(host, TokenB())).Id);
        Assert.Equal(x.ToPrincipalId(), (await AuthenticateAsync(host, TokenA())).Id);
    }

    // S48.6 -----------------------------------------------------------------------------------

    /// <summary>S48.6 — two concurrent unlinks of an account's two identities: exactly one succeeds,
    /// the other answers <c>LastIdentity</c>, and the account keeps one identity.</summary>
    [Fact]
    public async Task S48_6_Two_concurrent_unlinks_leave_one_identity()
    {
        await using var host = await StartHostAsync();
        await ConcurrentUnlinksLeaveOneIdentityAsync(host);
    }

    internal static async Task ConcurrentUnlinksLeaveOneIdentityAsync(IPlatformTestHost host)
    {
        var x = await LinkedPairAsync(host);
        var account = await AuthenticateAsync(host, TokenA());

        using var go = new ManualResetEventSlim();
        var unlinks = new[] { new PrincipalId(IssuerA, "alice"), new PrincipalId(IssuerB, "alice-b") }
            .Select(identity => Task.Run(async () =>
            {
                go.Wait();
                return await UnlinkAsync(host, account, identity);
            }))
            .ToArray();
        go.Set();
        var results = await Task.WhenAll(unlinks);

        Assert.Single(results, result => result.IsSuccess);
        Assert.Single(results, result => !result.IsSuccess && result.Error.Code == nameof(AccountError.LastIdentity));
        Assert.Equal(1, await LinkRowsAsync(host));
        Assert.Single((await GetCurrentAsync(host, account)).Value.Identities);
        Assert.Equal(x, (await GetCurrentAsync(host, account)).Value.Id);
    }

    /// <summary>X created from A, then B linked: the account both tokens reach.</summary>
    internal static async Task<AccountId> LinkedPairAsync(IPlatformTestHost host)
    {
        var created = await CreateAsync(host, await AuthenticateAsync(host, TokenA()));
        Assert.True(created.IsSuccess);
        var linked = await LinkAsync(host, await AuthenticateAsync(host, TokenA()), TokenB(host.Clock.UtcNow));
        Assert.True(linked.IsSuccess, linked.IsSuccess ? null : linked.Error.Code);
        return created.Value.Id;
    }

    internal static async Task<Result<Account, AccountError>> UnlinkAsync(
        IPlatformTestHost host, Principal principal, PrincipalId identity)
    {
        var api = host.Services.GetRequiredService<IAccountApi>();
        using (host.Services.GetRequiredService<IOperationScopeFactory>().Begin(TenantId.Implicit, principal))
        {
            return await api.UnlinkAsync(identity, CancellationToken.None);
        }
    }

    internal static string TokenA() => Mint(IssuerAKey, IssuerA, "alice", SharedEmail, "Alice A");

    internal static string TokenB() => Mint(IssuerBKey, IssuerB, "alice-b", SharedEmail, "Alice B");

    /// <summary>B's token carrying an <c>iat</c>: a second credential.</summary>
    internal static string TokenB(DateTimeOffset issuedAt) =>
        Mint(IssuerBKey, IssuerB, "alice-b", SharedEmail, "Alice B", issuedAt: issuedAt);

    internal static JwtBearerAuthenticationProvider ProviderA() => new("test-issuer-a", IssuerA, IssuerAKey);

    internal static JwtBearerAuthenticationProvider ProviderB() => new("test-issuer-b", IssuerB, IssuerBKey);

    /// <summary>A test-grade HS256 token carrying <c>sub</c>, <c>email</c>, <c>name</c> and, when given,
    /// <c>profile</c>, a numeric <c>iat</c> and one other numeric claim.</summary>
    internal static string Mint(
        byte[] key,
        string issuer,
        string subject,
        string email,
        string name,
        string? profile = null,
        DateTimeOffset? issuedAt = null,
        (string Name, DateTimeOffset Value)? numericClaim = null)
    {
        var claims = new Dictionary<string, object> { ["iss"] = issuer, ["sub"] = subject, ["email"] = email, ["name"] = name };
        if (profile is not null)
        {
            claims["profile"] = profile;
        }

        if (numericClaim is { } numeric)
        {
            claims[numeric.Name] = numeric.Value.ToUnixTimeSeconds();
        }

        if (issuedAt is { } iat)
        {
            claims["iat"] = iat.ToUnixTimeSeconds();
        }

        var header = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "HS256", typ = "JWT" }));
        var payload = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(claims));
        var signingInput = $"{header}.{payload}";
        var signature = Base64UrlEncode(HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(signingInput)));
        return $"{signingInput}.{signature}";
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static StubAuthenticationRequest Bearer(string token) => new(("Authorization", $"Bearer {token}"));

    private static HttpRequestMessage WhoAmI(string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/whoami");
        request.Headers.Add("Authorization", $"Bearer {token}");
        return request;
    }

    private const string GenericA = "https://generic-a.test";
    private const string GenericB = "https://generic-b.test";

    /// <summary>A host with two generic-path providers, <c>a</c> and <c>b</c>, each with a one-minute
    /// <c>ClockTolerance</c>, and no test-grade provider.</summary>
    private static Task<IPlatformTestHost> StartGenericHostAsync() =>
        StartHostAsync(testGrade: false, configure: builder =>
        {
            foreach (var (name, issuer) in new[] { ("a", GenericA), ("b", GenericB) })
            {
                foreach (var (key, value) in ConfiguredBearerTests.Valid(name, issuer: issuer))
                {
                    builder.WithSetting(key["Platform:".Length..], value!);
                }

                builder.WithSetting($"Identity:Bearer:{name}:ClockTolerance", "00:01:00");
            }
        });

    /// <summary>A generic-path RS256 token, valid by wall-clock time, carrying a numeric <c>iat</c>
    /// when given.</summary>
    private static string MintGeneric(string issuer, string subject, DateTimeOffset? issuedAt = null)
    {
        var payload = new Dictionary<string, object>
        {
            ["iss"] = issuer,
            ["aud"] = ConfiguredBearerTests.Audience,
            ["sub"] = subject,
            ["exp"] = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds(),
        };
        if (issuedAt is { } iat)
        {
            payload["iat"] = iat.ToUnixTimeSeconds();
        }

        return new JsonWebTokenHandler().CreateToken(
            JsonSerializer.Serialize(payload),
            new SigningCredentials(new RsaSecurityKey(ConfiguredBearerTests.Rsa) { KeyId = "key-1" }, SecurityAlgorithms.RsaSha256));
    }

    internal static Task<Result<Account, AccountError>> LinkAsync(IPlatformTestHost host, Principal principal, string token) =>
        LinkAsync(host.Services, principal, token);

    private static async Task<Result<Account, AccountError>> LinkAsync(
        IServiceProvider services, Principal principal, string token)
    {
        var api = services.GetRequiredService<IAccountApi>();
        using (services.GetRequiredService<IOperationScopeFactory>().Begin(TenantId.Implicit, principal))
        {
            return await api.LinkAsync(token, CancellationToken.None);
        }
    }

    /// <summary>Completes one sign-in method end to end and reads the access token it hands the
    /// browser.</summary>
    private static async Task<string> SignInAsync(WebApplication app, TwoIssuers issuers, string method)
    {
        var cookies = new CookieContainer();
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = cookies, UseCookies = true })
        {
            BaseAddress = new Uri(app.Urls.First()),
        };

        var begin = await client.GetAsync($"/signin/{method}/begin");
        Assert.Equal(HttpStatusCode.Found, begin.StatusCode);
        var query = System.Web.HttpUtility.ParseQueryString(begin.Headers.Location!.Query);
        issuers.Nonce = query["nonce"]!;

        var callback = await client.GetAsync($"/signin-callback-{method}?code=abc&state={Uri.EscapeDataString(query["state"]!)}");
        Assert.Equal(HttpStatusCode.Found, callback.StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/signin/{method}/token");
        request.Headers.Add("X-Platform-SignIn", "1");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("accessToken").GetString()!;
    }

    private static async Task<string> WhoAmIAsync(HttpClient client, string token)
    {
        using var response = await client.SendAsync(WhoAmI(token));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>A host composing <see cref="IdentityModule"/>, the store unless
    /// <paramref name="accounts"/> is false, and providers A and B unless <paramref name="testGrade"/>
    /// is false; migrated.</summary>
    internal static async Task<IPlatformTestHost> StartHostAsync(
        bool accounts = true,
        bool testGrade = true,
        RecordingAuditSink? sink = null,
        Action<IServiceCollection>? extra = null,
        Action<IPlatformTestHostBuilder>? configure = null,
        PersistenceProvider provider = PersistenceProvider.Sqlite)
    {
        var builder = PlatformTestHost.CreateBuilder().WithProvider(provider);
        configure?.Invoke(builder);

        var host = await builder
            .WithServices(services =>
            {
                services.AddSingleton<IPlatformModule, IdentityModule>();
                if (accounts)
                {
                    services.AddSingleton<IPlatformModule, IdentityAccountsModule>();
                }

                if (testGrade)
                {
                    services.AddSingleton<IAuthenticationProvider>(ProviderA());
                    services.AddSingleton<IAuthenticationProvider>(ProviderB());
                }

                if (sink is not null)
                {
                    services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditSink>(sink));
                }

                extra?.Invoke(services);
            })
            .StartAsync(CancellationToken.None);

        Assert.True((await host.Services.GetRequiredService<IMigrationRunner>().ApplyAsync(CancellationToken.None)).IsSuccess);
        return host;
    }

    internal static Task<Principal> AuthenticateAsync(IPlatformTestHost host, string token) =>
        AuthenticateAsync(host.Services, Bearer(token));

    private static Task<Principal> AuthenticateAsync(IPlatformTestHost host, IAuthenticationRequest request) =>
        AuthenticateAsync(host.Services, request);

    private static async Task<Principal> AuthenticateAsync(IServiceProvider services, IAuthenticationRequest request)
    {
        var authenticated = await services.GetRequiredService<AuthenticationChain>()
            .AuthenticateAsync(request, CancellationToken.None);
        Assert.True(authenticated.IsSuccess, authenticated.IsSuccess ? null : authenticated.Error.Code);
        return authenticated.Value;
    }

    internal static Task<Result<Account, AccountError>> CreateAsync(IPlatformTestHost host, Principal principal) =>
        CreateAsync(host.Services, principal);

    private static async Task<Result<Account, AccountError>> CreateAsync(IServiceProvider services, Principal principal)
    {
        var api = services.GetRequiredService<IAccountApi>();
        using (services.GetRequiredService<IOperationScopeFactory>().Begin(TenantId.Implicit, principal))
        {
            return await api.CreateAccountAsync(CancellationToken.None);
        }
    }

    private static async Task<Result<Account, AccountError>> GetCurrentAsync(IPlatformTestHost host, Principal principal)
    {
        var api = host.Services.GetRequiredService<IAccountApi>();
        using (host.Services.GetRequiredService<IOperationScopeFactory>().Begin(TenantId.Implicit, principal))
        {
            return await api.GetCurrentAccountAsync(CancellationToken.None);
        }
    }

    internal static Task<long> AccountRowsAsync(IPlatformTestHost host) =>
        RuntimeSettingsTests.ScalarAsync(host, "SELECT COUNT(*) FROM identity_account;");

    internal static Task<long> LinkRowsAsync(IPlatformTestHost host) =>
        RuntimeSettingsTests.ScalarAsync(host, "SELECT COUNT(*) FROM identity_account_link;");

    /// <summary>Runs one statement by raw SQL, beneath the module; whether it committed.</summary>
    private static async Task<bool> ExecuteAsync(IPlatformTestHost host, string sql)
    {
        var unitOfWork = host.Services.GetRequiredService<IUnitOfWork>();
        var ambient = host.Services.GetRequiredService<IAmbientTransactionAccessor>();
        var result = await unitOfWork.ExecuteAsync(
            TransactionIntent.Write,
            async ct =>
            {
                await using var command = ambient.Current!.Connection.CreateCommand();
                command.Transaction = ambient.Current!.Transaction;
                command.CommandText = sql;
                return await command.ExecuteNonQueryAsync(ct);
            },
            CancellationToken.None);

        return result.IsSuccess;
    }

    private static async Task<string[]> ColumnsAsync(IPlatformTestHost host, string table)
    {
        var unitOfWork = host.Services.GetRequiredService<IUnitOfWork>();
        var ambient = host.Services.GetRequiredService<IAmbientTransactionAccessor>();
        var result = await unitOfWork.ExecuteAsync(
            TransactionIntent.ReadOnly,
            async ct =>
            {
                await using var command = ambient.Current!.Connection.CreateCommand();
                command.Transaction = ambient.Current!.Transaction;
                command.CommandText = $"SELECT name FROM pragma_table_info('{table}') ORDER BY cid;";
                List<string> names = [];
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    names.Add(reader.GetString(0));
                }

                return names.ToArray();
            },
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        return result.Value;
    }

    /// <summary>A consumer's own provider establishing an <see cref="PrincipalKind.Account"/>
    /// principal: Identity neither maps it nor makes an account from it.</summary>
    private sealed class ConsumerAccountProvider : IAuthenticationProvider
    {
        public string Name => "consumer";

        public Task<Result<Principal, AuthenticationError>> AuthenticateAsync(
            IAuthenticationRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(Result<Principal, AuthenticationError>.Success(
                request.Headers.TryGetValue("X-Consumer", out var subject)
                    ? new Principal(
                        new PrincipalId("https://consumer.test", subject[0]),
                        PrincipalKind.Account,
                        null,
                        new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([], Name)))
                    : Principal.Anonymous));
    }

    /// <summary>Two sign-in issuers behind one transport, routed by host. Each answers discovery and
    /// a token exchange whose access token is a generic-path RS256 token issued now, for
    /// <c>alice</c> at A and <c>alice-b</c> at B. The SignIn tests' own fixture issuer is private and
    /// answers for a single issuer with an opaque access token, so it cannot stand in here.</summary>
    private sealed class TwoIssuers : HttpMessageHandler
    {
        internal const string A = "https://signin-a.test";
        internal const string B = "https://signin-b.test";
        internal const string ClientId = "web-client";

        internal string Nonce { get; set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var issuer = request.RequestUri!.GetLeftPart(UriPartial.Authority);
            var subject = issuer switch
            {
                A => "alice",
                B => "alice-b",
                _ => null,
            };
            if (subject is null)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath == "/.well-known/openid-configuration")
            {
                return Task.FromResult(Json(new Dictionary<string, string>
                {
                    ["issuer"] = issuer,
                    ["authorization_endpoint"] = issuer + "/authorize",
                    ["token_endpoint"] = issuer + "/token",
                }));
            }

            if (request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath == "/token")
            {
                var now = DateTimeOffset.UtcNow;
                var accessToken = new JsonWebTokenHandler().CreateToken(
                    JsonSerializer.Serialize(new Dictionary<string, object>
                    {
                        ["iss"] = issuer,
                        ["aud"] = ConfiguredBearerTests.Audience,
                        ["sub"] = subject,
                        ["iat"] = now.ToUnixTimeSeconds(),
                        ["exp"] = now.AddMinutes(10).ToUnixTimeSeconds(),
                    }),
                    new SigningCredentials(new RsaSecurityKey(ConfiguredBearerTests.Rsa) { KeyId = "key-1" }, SecurityAlgorithms.RsaSha256));
                var idToken = Part(new { alg = "none" }) + "."
                    + Part(new { iss = issuer, aud = ClientId, sub = subject, nonce = Nonce, exp = now.AddMinutes(5).ToUnixTimeSeconds() })
                    + ".sig";
                return Task.FromResult(Json(new { access_token = accessToken, id_token = idToken, expires_in = 600, token_type = "Bearer" }));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
        }

        private static string Part(object value) => Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(value));

        private static HttpResponseMessage Json(object value) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
            };
    }

    private sealed class FailureToggle
    {
        public volatile bool Failing;
    }

    /// <summary>Delegates every provider operation, but fails every transaction it is asked to open
    /// while the toggle is set: the store's connection failing.</summary>
    private sealed class FailingCapability(IProviderCapability inner, FailureToggle toggle) : IProviderCapability
    {
        internal static void Decorate(IServiceCollection services, FailureToggle toggle)
        {
            var original = services.Last(descriptor => descriptor.ServiceType == typeof(IProviderCapability));
            services.Remove(original);
            services.AddSingleton<IProviderCapability>(provider => new FailingCapability(
                (IProviderCapability)(original.ImplementationInstance
                    ?? original.ImplementationFactory?.Invoke(provider)
                    ?? ActivatorUtilities.CreateInstance(provider, original.ImplementationType!)),
                toggle));
        }

        public PersistenceProvider Provider => inner.Provider;

        public string FormatInstant(DateTimeOffset instant) => inner.FormatInstant(instant);

        public bool TryParseInstant(string stored, out DateTimeOffset instant) =>
            inner.TryParseInstant(stored, out instant);

        public byte[] EncodeIdentifier(Guid value) => inner.EncodeIdentifier(value);

        public bool TryDecodeIdentifier(ReadOnlySpan<byte> encoded, out Guid value) =>
            inner.TryDecodeIdentifier(encoded, out value);

        public string MigrationHistoryTable(ModuleName module) => inner.MigrationHistoryTable(module);

        public Task<Result<IAmbientTransaction, TransactionError>> BeginAsync(
            TransactionIntent intent,
            CancellationToken cancellationToken) =>
            toggle.Failing
                ? Task.FromResult(Result<IAmbientTransaction, TransactionError>.Failure(TransactionError.Unavailable()))
                : inner.BeginAsync(intent, cancellationToken);

        public TransactionError Classify(Exception exception) => inner.Classify(exception);

        public Task<Result<OutboxMessageId?, TransactionError>> StampClaimAsync(
            InstanceId holder,
            DateTimeOffset now,
            TimeSpan claimWindow,
            CancellationToken cancellationToken) =>
            inner.StampClaimAsync(holder, now, claimWindow, cancellationToken);

        public Task<Result<IMigrationLock, MigrationError>> AcquireMigrationLockAsync(
            CancellationToken cancellationToken) => inner.AcquireMigrationLockAsync(cancellationToken);

        public Task<Result<ConfigurationError>> AssertStartupPreconditionsAsync(
            CancellationToken cancellationToken) => inner.AssertStartupPreconditionsAsync(cancellationToken);

        public Task<Result<int, TransactionError>> DeleteBoundedAsync(
            PruneTarget target,
            DateTimeOffset olderThan,
            int batchSize,
            CancellationToken cancellationToken) =>
            inner.DeleteBoundedAsync(target, olderThan, batchSize, cancellationToken);
    }
}

/// <summary>S46.3, S46.8, S47.4 on PostgreSQL: the link's primary key, not a check before the insert,
/// is what keeps one identity on one account under concurrency.</summary>
public sealed class AccountPostgresTests(PostgresContainerFixture fixture) : IClassFixture<PostgresContainerFixture>
{
    [Fact]
    public async Task S46_3_Two_concurrent_creates_leave_one_account_and_the_loser_answers_IdentityAlreadyLinked()
    {
        await using var host = await StartAsync();
        await AccountTests.ConcurrentCreatesLeaveOneAccountAsync(host);
    }

    [Fact]
    public async Task S46_8_The_store_refuses_a_second_link_for_a_linked_pair()
    {
        await using var host = await StartAsync();
        await AccountTests.SecondLinkIsRefusedAsync(host);
    }

    [Fact]
    public async Task S47_4_A_pair_linked_to_another_account_answers_IdentityAlreadyLinked()
    {
        await using var host = await StartAsync();
        await AccountTests.PairLinkedElsewhereIsRefusedAsync(host);
    }

    [Fact]
    public async Task S47_4_Two_concurrent_links_of_one_pair_to_one_account_both_succeed()
    {
        await using var host = await StartAsync();
        await AccountTests.ConcurrentLinksToOneAccountSucceedAsync(host);
    }

    /// <summary>S47.4 — the race, forced: a concurrent transaction holds an uncommitted link of the
    /// same pair while <c>LinkAsync</c> runs, so its check sees no holder and its insert waits on the
    /// primary key. When the other transaction commits a link to the same account, the call succeeds
    /// and writes nothing of its own; when it commits a link to another account, the call answers
    /// <c>IdentityAlreadyLinked</c>.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task S47_4_A_link_that_loses_the_race_reads_which_account_won(bool sameAccount)
    {
        var (host, connectionString) = await StartWithConnectionStringAsync();
        await using var _ = host;
        var x = await AccountTests.CreateAsync(host, await AccountTests.AuthenticateAsync(host, AccountTests.TokenA()));
        var y = await AccountTests.CreateAsync(
            host,
            await AccountTests.AuthenticateAsync(
                host, AccountTests.Mint(AccountTests.IssuerAKey, AccountTests.IssuerA, "carol", "c@example.test", "Carol")));
        Assert.True(x.IsSuccess);
        Assert.True(y.IsSuccess);
        var winner = sameAccount ? x.Value.Id.Value : y.Value.Id.Value;
        var principal = await AccountTests.AuthenticateAsync(host, AccountTests.TokenA());
        var second = AccountTests.TokenB(host.Clock.UtcNow);

        await using var other = new Npgsql.NpgsqlConnection(connectionString);
        await other.OpenAsync();
        await using var transaction = await other.BeginTransactionAsync();
        await using (var insert = other.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO identity_account_link (issuer, subject, account, linked_at) VALUES (@i, 'alice-b', @a, '2026-01-01T00:00:00.0000000+00:00');";
            insert.Parameters.AddWithValue("i", AccountTests.IssuerB);
            insert.Parameters.AddWithValue("a", winner);
            await insert.ExecuteNonQueryAsync();
        }

        var link = AccountTests.LinkAsync(host, principal, second);
        await WaitForLockWaitAsync(connectionString);
        await transaction.CommitAsync();
        var linked = await link;

        if (sameAccount)
        {
            Assert.True(linked.IsSuccess, linked.IsSuccess ? null : linked.Error.Code);
            Assert.Equal(2, linked.Value.Identities.Count);
        }
        else
        {
            Assert.Equal(nameof(AccountError.IdentityAlreadyLinked), linked.Error.Code);
        }

        Assert.Equal(3, await AccountTests.LinkRowsAsync(host));
    }

    [Fact]
    public async Task S48_6_Two_concurrent_unlinks_leave_one_identity()
    {
        await using var host = await StartAsync();
        await AccountTests.ConcurrentUnlinksLeaveOneIdentityAsync(host);
    }

    /// <summary>S48.6 — the race, forced: another transaction has locked the account row and removed
    /// A, uncommitted, as a concurrent unlink would. An unlink of B waits on the account row, then sees
    /// the account as it stands after that commit and answers <c>LastIdentity</c>.</summary>
    [Fact]
    public async Task S48_6_An_unlink_waits_for_a_concurrent_unlink_and_then_sees_the_last_identity()
    {
        var (host, connectionString) = await StartWithConnectionStringAsync();
        await using var _ = host;
        var x = await AccountTests.LinkedPairAsync(host);
        var principal = await AccountTests.AuthenticateAsync(host, AccountTests.TokenA());

        await using var other = new Npgsql.NpgsqlConnection(connectionString);
        await other.OpenAsync();
        await using var transaction = await other.BeginTransactionAsync();
        await using (var unlink = other.CreateCommand())
        {
            unlink.Transaction = transaction;
            unlink.CommandText = "SELECT id FROM identity_account WHERE id = @a FOR UPDATE; DELETE FROM identity_account_link WHERE account = @a AND subject = 'alice';";
            unlink.Parameters.AddWithValue("a", x.Value);
            await unlink.ExecuteNonQueryAsync();
        }

        var unlinked = AccountTests.UnlinkAsync(host, principal, new PrincipalId(AccountTests.IssuerB, "alice-b"));
        await WaitForLockWaitAsync(connectionString, unlinked);
        await transaction.CommitAsync();
        var result = await unlinked;

        Assert.Equal(nameof(AccountError.LastIdentity), result.IsSuccess ? "success" : result.Error.Code);
        Assert.Equal(1, await AccountTests.LinkRowsAsync(host));
    }

    /// <summary>Waits until some session in the database is blocked on a lock, or until
    /// <paramref name="call"/> finishes without having waited.</summary>
    private static async Task WaitForLockWaitAsync(string connectionString, Task? call = null)
    {
        await using var probe = new Npgsql.NpgsqlConnection(connectionString);
        await probe.OpenAsync();
        await using var command = probe.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock';";
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while ((long)(await command.ExecuteScalarAsync())! == 0 && call is not { IsCompleted: true })
        {
            Assert.True(DateTime.UtcNow < deadline, "LinkAsync never waited on the other transaction's link.");
            await Task.Delay(20);
        }
    }

    private async Task<IPlatformTestHost> StartAsync() => (await StartWithConnectionStringAsync()).Host;

    private async Task<(IPlatformTestHost Host, string ConnectionString)> StartWithConnectionStringAsync()
    {
        var database = $"test_{Guid.NewGuid():N}";
        await using (var admin = new Npgsql.NpgsqlConnection(fixture.AdminConnectionString))
        {
            await admin.OpenAsync();
            await using var create = admin.CreateCommand();
            create.CommandText = $"CREATE DATABASE \"{database}\";";
            await create.ExecuteNonQueryAsync();
        }

        var connectionString = new Npgsql.NpgsqlConnectionStringBuilder(fixture.AdminConnectionString)
        {
            Database = database,
        }.ConnectionString;

        var host = await AccountTests.StartHostAsync(
            provider: PersistenceProvider.PostgreSql,
            configure: builder => builder.WithSetting("Persistence:ConnectionString", connectionString));
        return (host, connectionString);
    }
}
