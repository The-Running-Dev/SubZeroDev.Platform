using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Persistence;
using SubZeroDev.Platform.Testing;

namespace SubZeroDev.Platform.Tests;

/// <summary>S5: tenant resolution at the request boundary.</summary>
public sealed class TenancyTests
{
    private static readonly TenantId FirstTenant = new(Guid.Parse("33333333-3333-3333-3333-333333333333"));
    private static readonly TenantId SecondTenant = new(Guid.Parse("44444444-4444-4444-4444-444444444444"));

    [Fact]
    public async Task S5_1_With_no_resolver_registered_the_chain_answers_the_implicit_tenant()
    {
        var registry = new TenantResolverRegistry();
        var chain = new TenantResolutionChain(registry);

        var resolved = await chain.ResolveAsync(CancellationToken.None);

        Assert.Equal(TenantId.Implicit, resolved);
    }

    [Fact]
    public async Task S5_1_A_request_through_the_real_pipeline_observes_the_implicit_tenant_with_no_configuration()
    {
        var (app, client) = await WebHostUnderTest.StartAsync();

        try
        {
            var body = await client.GetFromJsonAsync<JsonElement>("/", CancellationToken.None);

            Assert.Equal(TenantId.Implicit.Value, body.GetProperty("tenant").GetGuid());
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task S5_2_Resolvers_run_in_registration_order_and_the_first_non_null_answer_wins()
    {
        var registry = new TenantResolverRegistry();
        var first = new StubTenantResolver("first", () => FirstTenant);
        var second = new StubTenantResolver("second", () => SecondTenant);

        Assert.True(registry.Register(first).IsSuccess);
        Assert.True(registry.Register(second).IsSuccess);

        var chain = new TenantResolutionChain(registry);
        var resolved = await chain.ResolveAsync(CancellationToken.None);

        Assert.Equal(FirstTenant, resolved);
        Assert.Equal(1, first.CallCount);
        Assert.Equal(0, second.CallCount);
    }

    [Fact]
    public async Task S5_3_Every_resolver_deferring_leaves_the_request_in_the_implicit_tenant()
    {
        var registry = new TenantResolverRegistry();
        Assert.True(registry.Register(new StubTenantResolver("first", () => null)).IsSuccess);
        Assert.True(registry.Register(new StubTenantResolver("second", () => null)).IsSuccess);

        var chain = new TenantResolutionChain(registry);
        var resolved = await chain.ResolveAsync(CancellationToken.None);

        Assert.Equal(TenantId.Implicit, resolved);
    }

    [Fact]
    public void S5_4_ITenantResolver_carries_no_decision_or_denial_type()
    {
        var method = typeof(ITenantResolver).GetMethod(nameof(ITenantResolver.ResolveAsync))!;

        Assert.Equal(typeof(Task<TenantId?>), method.ReturnType);
    }

    [Fact]
    public async Task S5_5_The_scopes_tenant_is_fixed_for_the_requests_lifetime()
    {
        var currentAnswer = FirstTenant;
        var registry = new TenantResolverRegistry();
        Assert.True(registry.Register(new StubTenantResolver("resolver", () => currentAnswer)).IsSuccess);
        var chain = new TenantResolutionChain(registry);

        await using var host = await PlatformTestHost.CreateBuilder().StartAsync(CancellationToken.None);
        var factory = host.Services.GetRequiredService<IOperationScopeFactory>();

        var resolved = await chain.ResolveAsync(CancellationToken.None);
        using var scope = factory.Begin(resolved, FakePrincipals.System);

        // The resolver would answer differently now, but nothing re-resolves mid-scope: the scope
        // carries the tenant it was opened with for its entire lifetime.
        currentAnswer = SecondTenant;

        Assert.Equal(FirstTenant, scope.Tenant);
    }

    [Fact]
    public void S5_6_Two_resolvers_registered_under_the_same_name_are_rejected()
    {
        var registry = new TenantResolverRegistry();
        Assert.True(registry.Register(new StubTenantResolver("duplicate", () => FirstTenant)).IsSuccess);

        var second = registry.Register(new StubTenantResolver("duplicate", () => SecondTenant));

        Assert.False(second.IsSuccess);
        Assert.Equal("DuplicateProviderName", second.Error.Code);
        Assert.Contains("duplicate", second.Error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task S5_6_Two_registered_resolvers_sharing_a_name_fail_startup()
    {
        var thrown = await Assert.ThrowsAsync<Hosting.PlatformStartupException>(() => PlatformTestHost.CreateBuilder()
            .WithServices(services =>
            {
                services.TryAddEnumerable(
                    ServiceDescriptor.Singleton<ITenantResolver>(new StubTenantResolver("duplicate", () => FirstTenant)));
                services.AddSingleton<ITenantResolver>(new StubTenantResolver("duplicate", () => SecondTenant));
            })
            .StartAsync(CancellationToken.None));

        var error = Assert.IsType<Hosting.HostStartupError>(thrown.Error);
        Assert.Equal("Registration", error.Code);
        Assert.Equal("DuplicateProviderName", error.Inner?.Code);
    }

    [Fact]
    public async Task S5_7_The_tenant_column_the_primary_key_and_the_implicit_representation_are_unchanged()
    {
        // The implicit tenant is still the all-zero guid rendered as a standard 36-character form —
        // the representation D3 and G2 fixed.
        Assert.Equal(Guid.Empty, TenantId.Implicit.Value);
        Assert.Equal("00000000-0000-0000-0000-000000000000", TenantId.Implicit.ToString());

        var path = Path.Combine(Path.GetTempPath(), $"platform-tenancy-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={path}";

        try
        {
            await using (var host = await PlatformTestHost.CreateBuilder()
                .WithProvider(PersistenceProvider.Sqlite)
                .WithSetting("Persistence:ConnectionString", connectionString)
                .StartAsync(CancellationToken.None))
            {
                var applied = await host.Services.GetRequiredService<IMigrationRunner>()
                    .ApplyAsync(CancellationToken.None);
                Assert.True(applied.IsSuccess);
            }

            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(CancellationToken.None);

            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA table_info(platform_outbox);";
            await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);

            var byName = new Dictionary<string, (string Type, bool NotNull, bool PrimaryKey)>();
            while (await reader.ReadAsync(CancellationToken.None))
            {
                var name = reader.GetString(1);
                byName[name] = (reader.GetString(2), reader.GetInt64(3) != 0, reader.GetInt64(5) != 0);
            }

            Assert.True(byName.TryGetValue("tenant", out var tenantColumn));
            Assert.Equal("TEXT", tenantColumn.Type);
            Assert.True(tenantColumn.NotNull);
            Assert.False(tenantColumn.PrimaryKey);

            Assert.True(byName.TryGetValue("id", out var idColumn));
            Assert.True(idColumn.PrimaryKey);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDelete(path);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            File.Delete(path + "-wal");
            File.Delete(path + "-shm");
        }
        catch (IOException)
        {
            // Best-effort cleanup: a lingering handle leaves an orphaned temp file, not a test failure.
        }
    }
}
