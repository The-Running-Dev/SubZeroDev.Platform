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
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Hosting;
using SubZeroDev.Platform.Identity;
using SubZeroDev.Platform.Persistence;
using SubZeroDev.Platform.Testing;

namespace SubZeroDev.Platform.Tests;

/// <summary>D5-S46: the optional account store. Creating an account, the mapping from a linked
/// identity to its account's principal, and a host that does not opt in behaving exactly as before.</summary>
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

    // Helpers ---------------------------------------------------------------------------------

    internal static string TokenA() => Mint(IssuerAKey, IssuerA, "alice", SharedEmail, "Alice A");

    internal static string TokenB() => Mint(IssuerBKey, IssuerB, "alice-b", SharedEmail, "Alice B");

    internal static JwtBearerAuthenticationProvider ProviderA() => new("test-issuer-a", IssuerA, IssuerAKey);

    internal static JwtBearerAuthenticationProvider ProviderB() => new("test-issuer-b", IssuerB, IssuerBKey);

    /// <summary>A test-grade HS256 token carrying <c>sub</c>, <c>email</c>, <c>name</c> and, when given,
    /// <c>profile</c>.</summary>
    internal static string Mint(
        byte[] key, string issuer, string subject, string email, string name, string? profile = null)
    {
        var claims = new Dictionary<string, string> { ["iss"] = issuer, ["sub"] = subject, ["email"] = email, ["name"] = name };
        if (profile is not null)
        {
            claims["profile"] = profile;
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

/// <summary>S46.3 and S46.8 on PostgreSQL: the link's primary key, not a check before the insert,
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

    private async Task<IPlatformTestHost> StartAsync()
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

        return await AccountTests.StartHostAsync(
            provider: PersistenceProvider.PostgreSql,
            configure: builder => builder.WithSetting("Persistence:ConnectionString", connectionString));
    }
}
