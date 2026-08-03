using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Persistence;
using SubZeroDev.Platform.Testing;

namespace SubZeroDev.Platform.Tests;

/// <summary>S3's store and migration are one implementation parameterised by
/// <see cref="IProviderCapability"/>, not one per provider — <c>HostRegistrationTests</c> exercises
/// it on SQLite; this proves the same SQL, including the <c>ON CONFLICT</c> upsert, is equally valid
/// against PostgreSQL.</summary>
public sealed class HostRegistrationPostgresTests(PostgresContainerFixture fixture) : IClassFixture<PostgresContainerFixture>
{
    [Fact]
    public async Task Starting_writes_exactly_one_row__a_later_heartbeat_updates_only_heartbeat_at()
    {
        var connectionString = await AcquireConnectionStringAsync();

        await using var host = await PlatformTestHost.CreateBuilder()
            .WithRole(HostRole.Web)
            .WithProvider(PersistenceProvider.PostgreSql)
            .WithSetting("Persistence:ConnectionString", connectionString)
            .StartAsync(CancellationToken.None);

        var applied = await host.Services.GetRequiredService<IMigrationRunner>().ApplyAsync(CancellationToken.None);
        Assert.True(applied.IsSuccess);

        await host.RunBackgroundWorkOnceAsync(PlatformBackgroundWork.HostRegistrationHeartbeat, CancellationToken.None);

        var store = host.Services.GetRequiredService<IHostRegistrationStore>();
        var instance = host.Services.GetRequiredService<InstanceId>();

        var firstListing = await store.ListLiveAsync(DateTimeOffset.MinValue, CancellationToken.None);
        Assert.True(firstListing.IsSuccess);
        var row = Assert.Single(firstListing.Value);
        Assert.Equal(HostRole.Web, row.Role);
        Assert.Equal(instance, row.Instance);

        var startedAt = row.StartedAt;
        host.Clock.Advance(TimeSpan.FromSeconds(30));
        await host.RunBackgroundWorkOnceAsync(PlatformBackgroundWork.HostRegistrationHeartbeat, CancellationToken.None);

        var secondListing = await store.ListLiveAsync(DateTimeOffset.MinValue, CancellationToken.None);
        Assert.True(secondListing.IsSuccess);
        var updated = Assert.Single(secondListing.Value);

        Assert.Equal(startedAt, updated.StartedAt);
        Assert.Equal(host.Clock.UtcNow, updated.HeartbeatAt);
    }

    private async Task<string> AcquireConnectionStringAsync()
    {
        var database = $"test_{Guid.NewGuid():N}";

        await using var admin = new NpgsqlConnection(fixture.AdminConnectionString);
        await admin.OpenAsync();
        await using var create = admin.CreateCommand();
        create.CommandText = $"CREATE DATABASE \"{database}\";";
        await create.ExecuteNonQueryAsync();

        var builder = new NpgsqlConnectionStringBuilder(fixture.AdminConnectionString) { Database = database };
        return builder.ConnectionString;
    }
}
