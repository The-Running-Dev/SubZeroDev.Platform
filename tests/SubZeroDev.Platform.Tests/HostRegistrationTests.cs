using Microsoft.Extensions.DependencyInjection;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Persistence;
using SubZeroDev.Platform.Testing;

namespace SubZeroDev.Platform.Tests;

/// <summary>S3, proven end to end: every host records itself in the store it is actually using, and
/// the wiring — real SQL, real DI registration, a real minted <c>InstanceId</c>, and the real
/// graceful-shutdown hook — holds together. The finer-grained policy these lean on (the peer-absence
/// grace, which settings participate in the fingerprint comparison, the Development carve-out) is
/// proven in isolation in <c>HostRegistrationHealthCheckTests</c> and
/// <c>HostRegistrationHeartbeatTests</c>, where a single clock and an in-memory store make the
/// timing unambiguous instead of racing two independent <see cref="FakeClock"/> instances.</summary>
public sealed class HostRegistrationTests
{
    [Fact]
    public async Task Starting_writes_exactly_one_row__a_later_heartbeat_updates_only_heartbeat_at()
    {
        await using var host = await StartAsync(HostRole.Web);
        await ApplyMigrationsAsync(host);

        await Tick(host);

        var store = host.Services.GetRequiredService<IHostRegistrationStore>();
        var instance = host.Services.GetRequiredService<InstanceId>();

        var firstListing = await store.ListLiveAsync(DateTimeOffset.MinValue, CancellationToken.None);
        Assert.True(firstListing.IsSuccess);
        var row = Assert.Single(firstListing.Value);

        Assert.Equal(HostRole.Web, row.Role);
        Assert.Equal(instance, row.Instance);
        Assert.Equal(host.Clock.UtcNow, row.StartedAt);
        Assert.Equal(host.Clock.UtcNow, row.HeartbeatAt);
        Assert.False(string.IsNullOrEmpty(row.SettingsFingerprint));

        var startedAt = row.StartedAt;
        host.Clock.Advance(TimeSpan.FromSeconds(30));
        await Tick(host);

        var secondListing = await store.ListLiveAsync(DateTimeOffset.MinValue, CancellationToken.None);
        Assert.True(secondListing.IsSuccess);
        var updated = Assert.Single(secondListing.Value);

        Assert.Equal(startedAt, updated.StartedAt);
        Assert.Equal(host.Clock.UtcNow, updated.HeartbeatAt);
        Assert.NotEqual(updated.StartedAt, updated.HeartbeatAt);
    }

    [Fact]
    public async Task A_store_with_no_schema_does_not_fail_the_heartbeat__the_row_appears_once_migrated()
    {
        await using var host = await StartAsync(HostRole.Web);
        var store = host.Services.GetRequiredService<IHostRegistrationStore>();

        // No schema yet: the tick itself must not throw, and the underlying write reports the
        // ordinary retryable outage rather than anything an operator would page on.
        var exception = await Record.ExceptionAsync(() => Tick(host));
        Assert.Null(exception);

        var beforeMigration = await store.ListLiveAsync(DateTimeOffset.MinValue, CancellationToken.None);
        Assert.False(beforeMigration.IsSuccess);
        Assert.Equal(nameof(TransactionError.Unavailable), beforeMigration.Error.Code);

        // Migrate mode runs; no bespoke startup retry exists — the next ordinary tick is the retry.
        await ApplyMigrationsAsync(host);
        await Tick(host);

        var afterMigration = await store.ListLiveAsync(DateTimeOffset.MinValue, CancellationToken.None);
        Assert.True(afterMigration.IsSuccess);
        Assert.Single(afterMigration.Value);
    }

    [Fact]
    public async Task Two_hosts_pointed_at_different_files_each_degrade_on_peer_host_while_each_serves_individually()
    {
        await using var web = await StartAsync(HostRole.Web);
        await using var worker = await StartAsync(HostRole.Worker);

        await ApplyMigrationsAsync(web);
        await ApplyMigrationsAsync(worker);
        await Tick(web);
        await Tick(worker);

        var threshold = web.Services.GetRequiredService<PlatformOptions>().HostRegistration.PeerLivenessThreshold;
        var grace = web.Services.GetRequiredService<PlatformOptions>().HostRegistration.PeerAbsenceGrace;

        // Two probes: the first starts the absence timer, the second is past the grace.
        await web.ProbeAsync(HealthCheckKind.Readiness, CancellationToken.None);
        web.Clock.Advance(threshold + grace + TimeSpan.FromSeconds(1));
        var report = await web.ProbeAsync(HealthCheckKind.Readiness, CancellationToken.None);

        AssertCheck(report, PlatformHealthChecks.PeerHost, HealthStatus.Degraded);
        AssertCheck(report, PlatformHealthChecks.Database, HealthStatus.Healthy);
        AssertCheck(report, PlatformHealthChecks.PendingMigrations, HealthStatus.Healthy);
        Assert.NotEqual(HealthStatus.Unhealthy, report.Aggregate);
    }

    [Fact]
    public async Task Two_hosts_differing_on_a_fingerprinted_setting_both_degrade_on_settings_fingerprint_naming_the_peer()
    {
        var sharedFile = SharedSqliteFile();
        try
        {
            await using var web = await StartAsync(HostRole.Web, sharedFile, processedRetention: "1.00:00:00");
            await using var worker = await StartAsync(HostRole.Worker, sharedFile, processedRetention: "2.00:00:00");

            await ApplyMigrationsAsync(web);
            await Tick(web);
            await Tick(worker);

            var report = await web.ProbeAsync(HealthCheckKind.Readiness, CancellationToken.None);
            var entry = Assert.Single(report.Entries, e => e.Name == PlatformHealthChecks.SettingsFingerprint);

            Assert.Equal(HealthStatus.Degraded, entry.Status);
            Assert.NotNull(entry.Data);
            Assert.Contains(entry.Data!, pair => pair.Value.Contains("Worker", StringComparison.Ordinal));
        }
        finally
        {
            DeleteSqliteFiles(sharedFile);
        }
    }

    [Fact]
    public async Task Two_hosts_differing_only_on_an_unfingerprinted_setting_do_not_degrade_on_settings_fingerprint()
    {
        var sharedFile = SharedSqliteFile();
        try
        {
            await using var web = await StartAsync(HostRole.Web, sharedFile, dispatchInterval: "00:00:05");
            await using var worker = await StartAsync(HostRole.Worker, sharedFile, dispatchInterval: "00:00:09");

            await ApplyMigrationsAsync(web);
            await Tick(web);
            await Tick(worker);

            var report = await web.ProbeAsync(HealthCheckKind.Readiness, CancellationToken.None);
            AssertCheck(report, PlatformHealthChecks.SettingsFingerprint, HealthStatus.Healthy);
        }
        finally
        {
            DeleteSqliteFiles(sharedFile);
        }
    }

    [Fact]
    public async Task Graceful_shutdown_deletes_the_hosts_own_row_at_once()
    {
        var sharedFile = SharedSqliteFile();
        try
        {
            var web = await StartAsync(HostRole.Web, sharedFile);
            await using var worker = await StartAsync(HostRole.Worker, sharedFile);

            await ApplyMigrationsAsync(web);
            await Tick(web);
            await Tick(worker);

            var store = worker.Services.GetRequiredService<IHostRegistrationStore>();
            var webInstance = web.Services.GetRequiredService<InstanceId>();

            var beforeShutdown = await store.ListLiveAsync(DateTimeOffset.MinValue, CancellationToken.None);
            Assert.True(beforeShutdown.IsSuccess);
            Assert.Contains(beforeShutdown.Value, row => row.Role == HostRole.Web && row.Instance == webInstance);

            await web.DisposeAsync();

            // Visible from the surviving peer's own store read at once — not merely stale.
            var afterShutdown = await store.ListLiveAsync(DateTimeOffset.MinValue, CancellationToken.None);
            Assert.True(afterShutdown.IsSuccess);
            Assert.DoesNotContain(afterShutdown.Value, row => row.Role == HostRole.Web && row.Instance == webInstance);
        }
        finally
        {
            DeleteSqliteFiles(sharedFile);
        }
    }

    [Fact]
    public async Task Two_hosts_of_one_role_hold_different_instance_ids()
    {
        await using var first = await StartAsync(HostRole.Web);
        await using var second = await StartAsync(HostRole.Web);

        Assert.NotEqual(
            first.Services.GetRequiredService<InstanceId>(),
            second.Services.GetRequiredService<InstanceId>());
    }

    [Fact]
    public async Task A_restarted_host_holds_a_different_instance_id_from_the_row_it_replaced()
    {
        var sharedFile = SharedSqliteFile();
        try
        {
            var first = await StartAsync(HostRole.Web, sharedFile);
            await ApplyMigrationsAsync(first);
            await Tick(first);
            var firstInstance = first.Services.GetRequiredService<InstanceId>();
            await first.DisposeAsync();

            await using var second = await StartAsync(HostRole.Web, sharedFile);
            await Tick(second);
            var secondInstance = second.Services.GetRequiredService<InstanceId>();

            Assert.NotEqual(firstInstance, secondInstance);

            var store = second.Services.GetRequiredService<IHostRegistrationStore>();
            var listing = await store.ListLiveAsync(DateTimeOffset.MinValue, CancellationToken.None);
            Assert.True(listing.IsSuccess);
            var row = Assert.Single(listing.Value, r => r.Role == HostRole.Web);
            Assert.Equal(secondInstance, row.Instance);
        }
        finally
        {
            DeleteSqliteFiles(sharedFile);
        }
    }

    private static Task Tick(IPlatformTestHost host) =>
        host.RunBackgroundWorkOnceAsync(PlatformBackgroundWork.HostRegistrationHeartbeat, CancellationToken.None);

    private static async Task ApplyMigrationsAsync(IPlatformTestHost host)
    {
        var applied = await host.Services.GetRequiredService<IMigrationRunner>().ApplyAsync(CancellationToken.None);
        Assert.True(applied.IsSuccess);
    }

    private static void AssertCheck(HealthReport report, HealthCheckName name, HealthStatus expected)
    {
        var entry = Assert.Single(report.Entries, e => e.Name == name);
        Assert.Equal(expected, entry.Status);
    }

    private static string SharedSqliteFile() =>
        Path.Combine(Path.GetTempPath(), $"platform-s3-test-{Guid.NewGuid():N}.db");

    private static void DeleteSqliteFiles(string path)
    {
        try
        {
            File.Delete(path);
            File.Delete(path + "-wal");
            File.Delete(path + "-shm");
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    private static Task<IPlatformTestHost> StartAsync(
        HostRole role,
        string? sharedSqliteFile = null,
        string processedRetention = "1.00:00:00",
        string dispatchInterval = "00:00:05")
    {
        var builder = PlatformTestHost.CreateBuilder()
            .WithRole(role)
            .WithProvider(PersistenceProvider.Sqlite)
            .WithSetting("Outbox:ProcessedRetention", processedRetention)
            .WithSetting("Outbox:DispatchInterval", dispatchInterval);

        if (sharedSqliteFile is not null)
        {
            builder = builder.WithSetting("Persistence:ConnectionString", $"Data Source={sharedSqliteFile}");
        }

        return builder.StartAsync(CancellationToken.None);
    }
}
