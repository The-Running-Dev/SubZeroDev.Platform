using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Audit;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Persistence;
using SubZeroDev.Platform.Testing;

namespace SubZeroDev.Platform.Tests;

/// <summary>S13.7: a thousand concurrent appends from two hosts against PostgreSQL all land, proving
/// no unique constraint spans hosts — the same "prove it on the real second provider" pattern
/// <c>HostRegistrationPostgresTests</c> uses for S3's store.</summary>
public sealed class AuditStorePostgresTests(PostgresContainerFixture fixture) : IClassFixture<PostgresContainerFixture>
{
    [Fact]
    public async Task S13_7_A_thousand_concurrent_appends_from_two_hosts_all_land()
    {
        var connectionString = await AcquireConnectionStringAsync();

        await using var hostOne = await StartHostAsync(connectionString);
        await using var hostTwo = await StartHostAsync(connectionString);

        var migrated = await hostOne.Services.GetRequiredService<IMigrationRunner>().ApplyAsync(CancellationToken.None);
        Assert.True(migrated.IsSuccess);

        var tenant = new TenantId(Guid.NewGuid());
        var actor = new Principal(new PrincipalId("postgres-contention", "subject"), PrincipalKind.Account, "A", null);

        const int perHost = 500;
        var writes = new List<Task>(perHost * 2);
        for (var i = 0; i < perHost; i++)
        {
            writes.Add(WriteOneAsync(hostOne, tenant, actor, $"one-{i}"));
            writes.Add(WriteOneAsync(hostTwo, tenant, actor, $"two-{i}"));
        }

        await Task.WhenAll(writes);

        var readApi = hostOne.Services.GetRequiredService<IAuditReadApi>();
        var result = await readApi.ByTenantAsync(
            tenant, DateTimeOffset.MinValue, DateTimeOffset.MaxValue, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(perHost * 2, result.Value.Count);
    }

    /// <summary><see cref="AuditClass.Required"/>, not <see cref="AuditClass.Recorded"/>: a
    /// contention run pushes a shared Postgres connection pool hard enough that an individual write
    /// can meet a transient, retryable failure, and <c>Required</c> is what makes that failure
    /// visible to the caller instead of silently dropping the row — exactly the retry the contract's
    /// Error semantics §4 describes for a retryable <see cref="AuditError"/>.</summary>
    private static async Task WriteOneAsync(IPlatformTestHost host, TenantId tenant, Principal actor, string label)
    {
        var scopeFactory = host.Services.GetRequiredService<IOperationScopeFactory>();
        var writer = host.Services.GetRequiredService<IAuditWriter>();

        for (var attempt = 1; ; attempt++)
        {
            using var scope = scopeFactory.Begin(tenant, actor);
            var written = await writer.WriteAsync(
                new AuditAction($"test.append.{label}"), null, AuditOutcome.Allowed, AuditClass.Required,
                CancellationToken.None);

            if (written.IsSuccess)
            {
                return;
            }

            if (!written.Error.IsRetryable || attempt >= 5)
            {
                Assert.Fail($"Write '{label}' did not land: {written.Error.Code}.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), CancellationToken.None);
        }
    }

    private static Task<IPlatformTestHost> StartHostAsync(string connectionString) =>
        PlatformTestHost.CreateBuilder()
            .WithProvider(PersistenceProvider.PostgreSql)
            .WithSetting("Persistence:ConnectionString", connectionString)
            .WithServices(services => services.AddSingleton<IPlatformModule, AuditModule>())
            .StartAsync(CancellationToken.None);

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
}
