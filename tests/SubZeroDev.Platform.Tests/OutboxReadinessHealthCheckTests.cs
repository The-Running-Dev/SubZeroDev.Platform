using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Persistence;
using SubZeroDev.Platform.Testing;

namespace SubZeroDev.Platform.Tests;

/// <summary>S6.8: <c>OutboxBacklogAge</c> measures time past due, isolated from real SQL — the
/// underlying query is proven against a real database in <c>PersistenceIntegrationTests</c>.</summary>
public sealed class OutboxBacklogAgeHealthCheckTests
{
    [Fact]
    public async Task No_pending_rows_reports_healthy()
    {
        var clock = new FakeClock();
        var check = new OutboxBacklogAgeHealthCheck(new FakeOutboxStore(), Options(), clock);

        Assert.Equal(HealthStatus.Healthy, (await check.CheckAsync(CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Ten_rows_deferred_one_minute_ahead_stay_healthy()
    {
        var clock = new FakeClock();
        var store = new FakeOutboxStore { OldestPendingDue = clock.UtcNow + TimeSpan.FromMinutes(1) };
        var check = new OutboxBacklogAgeHealthCheck(store, Options(), clock);

        Assert.Equal(HealthStatus.Healthy, (await check.CheckAsync(CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Ten_rows_backing_off_six_hours_ahead_stay_healthy()
    {
        var clock = new FakeClock();
        var store = new FakeOutboxStore { OldestPendingDue = clock.UtcNow + TimeSpan.FromHours(6) };
        var check = new OutboxBacklogAgeHealthCheck(store, Options(), clock);

        Assert.Equal(HealthStatus.Healthy, (await check.CheckAsync(CancellationToken.None)).Status);
    }

    [Fact]
    public async Task A_row_occurred_three_days_ago_but_due_now_stays_healthy()
    {
        var clock = new FakeClock();
        var store = new FakeOutboxStore { OldestPendingDue = clock.UtcNow };
        var check = new OutboxBacklogAgeHealthCheck(store, Options(), clock);

        Assert.Equal(HealthStatus.Healthy, (await check.CheckAsync(CancellationToken.None)).Status);
    }

    [Fact]
    public async Task A_worker_stopped_ten_minutes_with_due_rows_degrades()
    {
        var clock = new FakeClock();
        var store = new FakeOutboxStore { OldestPendingDue = clock.UtcNow - TimeSpan.FromMinutes(10) };
        var check = new OutboxBacklogAgeHealthCheck(store, Options(), clock);

        Assert.Equal(HealthStatus.Degraded, (await check.CheckAsync(CancellationToken.None)).Status);
    }

    [Fact]
    public async Task An_unreachable_store_degrades_rather_than_throwing()
    {
        var check = new OutboxBacklogAgeHealthCheck(new FakeOutboxStore { Unavailable = true }, Options(), new FakeClock());

        Assert.Equal(HealthStatus.Degraded, (await check.CheckAsync(CancellationToken.None)).Status);
    }

    internal static PlatformOptions Options(long pendingCountThreshold = 100_000) => new()
    {
        Persistence = new PersistenceOptions { Provider = PersistenceProvider.Sqlite, ConnectionString = "Data Source=:memory:" },
        Outbox = new OutboxOptions { ProcessedRetention = TimeSpan.FromDays(1), PoisonedRetention = TimeSpan.FromDays(7) },
        Health = new HealthOptions { BacklogAgeThreshold = TimeSpan.FromMinutes(5), PendingCountThreshold = pendingCountThreshold },
    };
}

/// <summary>S6.10: <c>OutboxPendingCount</c> degrades over its threshold.</summary>
public sealed class OutboxPendingCountHealthCheckTests
{
    [Fact]
    public async Task One_over_the_threshold_degrades()
    {
        var store = new FakeOutboxStore { PendingCount = 101 };
        var check = new OutboxPendingCountHealthCheck(store, OutboxBacklogAgeHealthCheckTests.Options(pendingCountThreshold: 100));

        Assert.Equal(HealthStatus.Degraded, (await check.CheckAsync(CancellationToken.None)).Status);
    }

    [Fact]
    public async Task One_under_the_threshold_stays_healthy()
    {
        var store = new FakeOutboxStore { PendingCount = 99 };
        var check = new OutboxPendingCountHealthCheck(store, OutboxBacklogAgeHealthCheckTests.Options(pendingCountThreshold: 100));

        Assert.Equal(HealthStatus.Healthy, (await check.CheckAsync(CancellationToken.None)).Status);
    }

    [Fact]
    public async Task An_unreachable_store_degrades_rather_than_throwing()
    {
        var check = new OutboxPendingCountHealthCheck(
            new FakeOutboxStore { Unavailable = true }, OutboxBacklogAgeHealthCheckTests.Options());

        Assert.Equal(HealthStatus.Degraded, (await check.CheckAsync(CancellationToken.None)).Status);
    }
}

/// <summary>S6.9: <c>OutboxPoisonCount</c> counts poisoned rows only — a discarded row has already
/// had its operator disposition made, and <see cref="IOutboxStore.PoisonedCountAsync"/> is what
/// excludes it, proven for real against SQL in <c>PersistenceIntegrationTests</c>. Here only the
/// check's own threshold logic — any count above zero degrades, never unhealthy — is under test.</summary>
public sealed class OutboxPoisonCountHealthCheckTests
{
    [Fact]
    public async Task Any_poisoned_count_above_zero_degrades_never_unhealthy()
    {
        var store = new FakeOutboxStore { PoisonedCount = 1 };
        var check = new OutboxPoisonCountHealthCheck(store);

        Assert.Equal(HealthStatus.Degraded, (await check.CheckAsync(CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Zero_poisoned_reports_healthy()
    {
        var store = new FakeOutboxStore { PoisonedCount = 0 };
        var check = new OutboxPoisonCountHealthCheck(store);

        Assert.Equal(HealthStatus.Healthy, (await check.CheckAsync(CancellationToken.None)).Status);
    }

    [Fact]
    public async Task An_unreachable_store_degrades_rather_than_throwing()
    {
        var check = new OutboxPoisonCountHealthCheck(new FakeOutboxStore { Unavailable = true });

        Assert.Equal(HealthStatus.Degraded, (await check.CheckAsync(CancellationToken.None)).Status);
    }
}
