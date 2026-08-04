using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Persistence;
using SubZeroDev.Platform.Testing;

namespace SubZeroDev.Platform.Tests;

/// <summary>S6.5-S6.7's mapping from store outcome to <see cref="LeaseError"/>, isolated from real
/// SQL via <see cref="FakeLeaseStore"/> — the store's own atomic acquire-when-absent-or-expired SQL
/// is proven against a real database in <c>PersistenceIntegrationTests</c>.</summary>
public sealed class LeaseManagerTests
{
    [Fact]
    public async Task Acquiring_an_unheld_lease_succeeds()
    {
        var clock = new FakeClock();
        var manager = new LeaseManager(new FakeLeaseStore(clock), Options(), new InstanceId("host/1"), clock);

        var acquired = await manager.AcquireAsync(PlatformBackgroundWork.Prune, CancellationToken.None);

        Assert.True(acquired.IsSuccess);
        Assert.Equal(clock.UtcNow + TimeSpan.FromMinutes(5), acquired.Value.ExpiresAt);
    }

    [Fact]
    public async Task A_second_acquirer_is_told_the_lease_is_held()
    {
        var clock = new FakeClock();
        var store = new FakeLeaseStore(clock);
        var first = new LeaseManager(store, Options(), new InstanceId("host/1"), clock);
        var second = new LeaseManager(store, Options(), new InstanceId("host/2"), clock);

        Assert.True((await first.AcquireAsync(PlatformBackgroundWork.Prune, CancellationToken.None)).IsSuccess);
        var secondAttempt = await second.AcquireAsync(PlatformBackgroundWork.Prune, CancellationToken.None);

        Assert.False(secondAttempt.IsSuccess);
        Assert.Equal(nameof(LeaseError.Held), secondAttempt.Error.Code);
    }

    [Fact]
    public async Task An_expired_lease_is_acquired_by_a_second_holder()
    {
        var clock = new FakeClock();
        var store = new FakeLeaseStore(clock);
        var first = new LeaseManager(store, Options(), new InstanceId("host/1"), clock);
        var second = new LeaseManager(store, Options(), new InstanceId("host/2"), clock);

        Assert.True((await first.AcquireAsync(PlatformBackgroundWork.Prune, CancellationToken.None)).IsSuccess);
        clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromTicks(1));

        var secondAttempt = await second.AcquireAsync(PlatformBackgroundWork.Prune, CancellationToken.None);
        Assert.True(secondAttempt.IsSuccess);
    }

    [Fact]
    public async Task Renewing_after_the_lease_was_reclaimed_returns_lost_and_obliges_abort()
    {
        var clock = new FakeClock();
        var store = new FakeLeaseStore(clock);
        var first = new LeaseManager(store, Options(), new InstanceId("host/1"), clock);
        var second = new LeaseManager(store, Options(), new InstanceId("host/2"), clock);

        var firstHandle = (await first.AcquireAsync(PlatformBackgroundWork.Prune, CancellationToken.None)).Value;
        clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromTicks(1));
        Assert.True((await second.AcquireAsync(PlatformBackgroundWork.Prune, CancellationToken.None)).IsSuccess);

        var renewed = await firstHandle.RenewAsync(CancellationToken.None);

        Assert.False(renewed.IsSuccess);
        Assert.Equal(nameof(LeaseError.Lost), renewed.Error.Code);
    }

    [Fact]
    public async Task Renewing_a_still_held_lease_extends_its_expiry()
    {
        var clock = new FakeClock();
        var store = new FakeLeaseStore(clock);
        var manager = new LeaseManager(store, Options(), new InstanceId("host/1"), clock);
        var handle = (await manager.AcquireAsync(PlatformBackgroundWork.Prune, CancellationToken.None)).Value;

        clock.Advance(TimeSpan.FromMinutes(1));
        var renewed = await handle.RenewAsync(CancellationToken.None);

        Assert.True(renewed.IsSuccess);
        Assert.Equal(clock.UtcNow + TimeSpan.FromMinutes(5), handle.ExpiresAt);
    }

    [Fact]
    public async Task Disposing_a_handle_releases_it_for_the_next_acquirer()
    {
        var clock = new FakeClock();
        var store = new FakeLeaseStore(clock);
        var first = new LeaseManager(store, Options(), new InstanceId("host/1"), clock);
        var second = new LeaseManager(store, Options(), new InstanceId("host/2"), clock);

        var handle = (await first.AcquireAsync(PlatformBackgroundWork.Prune, CancellationToken.None)).Value;
        await handle.DisposeAsync();

        var secondAttempt = await second.AcquireAsync(PlatformBackgroundWork.Prune, CancellationToken.None);
        Assert.True(secondAttempt.IsSuccess);
    }

    [Fact]
    public async Task An_unreachable_store_reports_unavailable()
    {
        var clock = new FakeClock();
        var manager = new LeaseManager(new FakeLeaseStore(clock) { Unavailable = true }, Options(), new InstanceId("host/1"), clock);

        var acquired = await manager.AcquireAsync(PlatformBackgroundWork.Prune, CancellationToken.None);

        Assert.False(acquired.IsSuccess);
        Assert.Equal(nameof(LeaseError.Unavailable), acquired.Error.Code);
    }

    private static PlatformOptions Options() => new()
    {
        Persistence = new PersistenceOptions { Provider = PersistenceProvider.Sqlite, ConnectionString = "Data Source=:memory:" },
        Outbox = new OutboxOptions { ProcessedRetention = TimeSpan.FromDays(1), PoisonedRetention = TimeSpan.FromDays(7) },
        Lease = new LeaseOptions { Duration = TimeSpan.FromMinutes(5) },
    };
}
