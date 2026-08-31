using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Persistence;
using SubZeroDev.Platform.Testing;

namespace SubZeroDev.Platform.Tests;

/// <summary>S3's peer-absence grace and the Development carve-out, isolated from real SQL and from
/// a second host's independent clock — one <see cref="FakeClock"/> and an in-memory store make the
/// timing unambiguous. Wiring these into a real host is proven separately in
/// <c>HostRegistrationTests</c>.</summary>
public sealed class PeerHostHealthCheckTests
{
    [Fact]
    public async Task A_live_peer_reports_healthy()
    {
        var clock = new FakeClock();
        var store = new FakeHostRegistrationStore();
        store.Seed(Registration(HostRole.Worker, clock.UtcNow));

        var check = new PeerHostHealthCheck(store, Options(), clock);

        Assert.Equal(HealthStatus.Healthy, (await check.CheckAsync(CancellationToken.None)).Status);
    }

    [Fact]
    public async Task A_peer_still_within_the_liveness_threshold_counts_as_live()
    {
        var clock = new FakeClock();
        var store = new FakeHostRegistrationStore();
        store.Seed(Registration(HostRole.Worker, clock.UtcNow));

        var check = new PeerHostHealthCheck(store, Options(heartbeatInterval: TimeSpan.FromSeconds(15)), clock);

        // 2x the heartbeat interval — inside the 3x liveness threshold.
        clock.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(HealthStatus.Healthy, (await check.CheckAsync(CancellationToken.None)).Status);
    }

    [Fact]
    public async Task A_peer_that_stops_beating_stays_live_at_2x_the_interval_and_degrades_at_4x_plus_the_grace()
    {
        var clock = new FakeClock();
        var heartbeatInterval = TimeSpan.FromSeconds(15);
        var grace = TimeSpan.FromSeconds(60);
        var options = Options(heartbeatInterval: heartbeatInterval, peerAbsenceGrace: grace);

        var store = new FakeHostRegistrationStore();
        store.Seed(Registration(HostRole.Worker, clock.UtcNow)); // the peer's last real heartbeat

        var check = new PeerHostHealthCheck(store, options, clock);

        // 2x the interval, no further beat: still inside the 3x liveness threshold.
        clock.Advance(TimeSpan.FromTicks(heartbeatInterval.Ticks * 2));
        Assert.Equal(HealthStatus.Healthy, (await check.CheckAsync(CancellationToken.None)).Status);

        // 4x total: past the threshold, first observed absent here — not yet degraded.
        clock.Advance(TimeSpan.FromTicks(heartbeatInterval.Ticks * 2));
        Assert.Equal(HealthStatus.Healthy, (await check.CheckAsync(CancellationToken.None)).Status);

        // The grace has now elapsed since that first observation.
        clock.Advance(grace);
        Assert.Equal(HealthStatus.Degraded, (await check.CheckAsync(CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Newly_observed_absence_stays_healthy_within_the_grace()
    {
        var clock = new FakeClock();
        var check = new PeerHostHealthCheck(new FakeHostRegistrationStore(), Options(peerAbsenceGrace: TimeSpan.FromSeconds(60)), clock);

        Assert.Equal(HealthStatus.Healthy, (await check.CheckAsync(CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Absence_persisting_past_the_grace_degrades_naming_the_missing_role()
    {
        var clock = new FakeClock();
        var grace = TimeSpan.FromSeconds(60);
        var check = new PeerHostHealthCheck(
            new FakeHostRegistrationStore(), Options(role: HostRole.Web, peerAbsenceGrace: grace), clock);

        await check.CheckAsync(CancellationToken.None); // first observation, starts the timer
        clock.Advance(grace);
        var result = await check.CheckAsync(CancellationToken.None);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Equal(nameof(HostRole.Worker), result.Data["peer"]);
    }

    [Fact]
    public async Task A_peer_that_returns_inside_the_grace_resets_the_absence_timer()
    {
        var clock = new FakeClock();
        var grace = TimeSpan.FromSeconds(60);

        // A heartbeat interval large enough that its own 3x liveness threshold never itself
        // intervenes — this test is about the absence-observation timer resetting, not staleness.
        var options = Options(role: HostRole.Web, heartbeatInterval: TimeSpan.FromHours(1), peerAbsenceGrace: grace);
        var store = new FakeHostRegistrationStore();
        var check = new PeerHostHealthCheck(store, options, clock);

        await check.CheckAsync(CancellationToken.None); // absent, timer starts at t=0

        clock.Advance(TimeSpan.FromSeconds(30));
        store.Seed(Registration(HostRole.Worker, clock.UtcNow));
        Assert.Equal(HealthStatus.Healthy, (await check.CheckAsync(CancellationToken.None)).Status); // present -> resets

        // A second outage — the row goes stale rather than being removed, which is equally absent
        // from the check's perspective.
        store.Seed(Registration(HostRole.Worker, DateTimeOffset.MinValue));
        Assert.Equal(HealthStatus.Healthy, (await check.CheckAsync(CancellationToken.None)).Status); // fresh timer at t=30s

        // If the timer had not reset, t=30s + (grace-1s) would already be past the original t=0s
        // grace window and this would already be degraded.
        clock.Advance(grace - TimeSpan.FromSeconds(1));
        Assert.Equal(HealthStatus.Healthy, (await check.CheckAsync(CancellationToken.None)).Status);

        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(HealthStatus.Degraded, (await check.CheckAsync(CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Development_never_degrades_and_reads_as_informational()
    {
        var clock = new FakeClock();
        var check = new PeerHostHealthCheck(
            new FakeHostRegistrationStore(),
            Options(environment: "Development", peerAbsenceGrace: TimeSpan.FromSeconds(60)),
            clock);

        await check.CheckAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromDays(365));
        var result = await check.CheckAsync(CancellationToken.None);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.NotNull(result.Detail);
    }

    [Fact]
    public async Task An_unreachable_store_degrades_rather_than_throwing()
    {
        var check = new PeerHostHealthCheck(new FakeHostRegistrationStore { Unavailable = true }, Options(), new FakeClock());

        Assert.Equal(HealthStatus.Degraded, (await check.CheckAsync(CancellationToken.None)).Status);
    }

    private static HostRegistration Registration(HostRole role, DateTimeOffset heartbeatAt) => new()
    {
        Role = role,
        Instance = new InstanceId("peer/1"),
        StartedAt = heartbeatAt,
        HeartbeatAt = heartbeatAt,
        SettingsFingerprint = "fingerprint",
    };

    internal static PlatformOptions Options(
        HostRole role = HostRole.Web,
        string environment = "Production",
        TimeSpan? heartbeatInterval = null,
        TimeSpan? peerAbsenceGrace = null,
        TimeSpan? processedRetention = null) => new()
    {
        Environment = environment,
        Role = role,
        CompositionProfile = CompositionProfile.Operated,
        Persistence = new PersistenceOptions { Provider = PersistenceProvider.Sqlite, ConnectionString = "Data Source=:memory:" },
        Outbox = new OutboxOptions
        {
            ProcessedRetention = processedRetention ?? TimeSpan.FromDays(1),
            PoisonedRetention = TimeSpan.FromDays(7),
        },
        HostRegistration = new HostRegistrationOptions
        {
            HeartbeatInterval = heartbeatInterval ?? TimeSpan.FromSeconds(15),
            PeerAbsenceGrace = peerAbsenceGrace ?? TimeSpan.FromSeconds(60),
        },
    };
}

/// <summary>Whether a live peer's fingerprinted settings agree, isolated the same way — one clock,
/// an in-memory store, and a stored fingerprint value the test controls directly rather than one
/// computed through real configuration binding.</summary>
public sealed class SettingsFingerprintHealthCheckTests
{
    [Fact]
    public async Task A_live_peer_with_a_matching_fingerprint_reports_healthy()
    {
        var clock = new FakeClock();
        var options = PeerHostHealthCheckTests.Options();
        var fingerprint = new SettingsFingerprint();
        var mine = fingerprint.Compute(options);

        var store = new FakeHostRegistrationStore();
        store.Seed(new HostRegistration
        {
            Role = HostRole.Worker,
            Instance = new InstanceId("peer/1"),
            StartedAt = clock.UtcNow,
            HeartbeatAt = clock.UtcNow,
            SettingsFingerprint = mine,
        });

        var check = new SettingsFingerprintHealthCheck(store, fingerprint, options, clock);

        Assert.Equal(HealthStatus.Healthy, (await check.CheckAsync(CancellationToken.None)).Status);
    }

    [Fact]
    public async Task A_live_peer_with_a_disagreeing_fingerprint_degrades_naming_the_peer_instance()
    {
        var clock = new FakeClock();
        var options = PeerHostHealthCheckTests.Options();
        var fingerprint = new SettingsFingerprint();
        var peerInstance = new InstanceId("peer/1");

        var store = new FakeHostRegistrationStore();
        store.Seed(new HostRegistration
        {
            Role = HostRole.Worker,
            Instance = peerInstance,
            StartedAt = clock.UtcNow,
            HeartbeatAt = clock.UtcNow,
            SettingsFingerprint = "not-a-real-fingerprint",
        });

        var check = new SettingsFingerprintHealthCheck(store, fingerprint, options, clock);
        var result = await check.CheckAsync(CancellationToken.None);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains(result.Data.Values, value => value.Contains(peerInstance.Value, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_stale_peer_with_a_disagreeing_fingerprint_never_contradicts_a_live_one()
    {
        var clock = new FakeClock();
        var options = PeerHostHealthCheckTests.Options();
        var fingerprint = new SettingsFingerprint();

        var store = new FakeHostRegistrationStore();
        store.Seed(new HostRegistration
        {
            Role = HostRole.Worker,
            Instance = new InstanceId("peer/1"),
            StartedAt = DateTimeOffset.MinValue,
            HeartbeatAt = DateTimeOffset.MinValue, // outside the liveness threshold no matter "now"
            SettingsFingerprint = "not-a-real-fingerprint",
        });

        var check = new SettingsFingerprintHealthCheck(store, fingerprint, options, clock);

        Assert.Equal(HealthStatus.Healthy, (await check.CheckAsync(CancellationToken.None)).Status);
    }

    [Fact]
    public async Task An_unreachable_store_degrades_rather_than_throwing()
    {
        var check = new SettingsFingerprintHealthCheck(
            new FakeHostRegistrationStore { Unavailable = true },
            new SettingsFingerprint(),
            PeerHostHealthCheckTests.Options(),
            new FakeClock());

        Assert.Equal(HealthStatus.Degraded, (await check.CheckAsync(CancellationToken.None)).Status);
    }
}

/// <summary>The heartbeat's own tick logic, isolated from real SQL — the store's upsert semantics
/// are proven against a real database in <c>HostRegistrationTests</c>.</summary>
public sealed class HostRegistrationHeartbeatTests
{
    [Fact]
    public async Task Ticking_upserts_the_current_role_instance_and_computed_fingerprint()
    {
        var clock = new FakeClock();
        var options = PeerHostHealthCheckTests.Options(role: HostRole.Web);
        var fingerprint = new SettingsFingerprint();
        var instance = new InstanceId("this-host/1");
        var store = new FakeHostRegistrationStore();

        var heartbeat = new HostRegistrationHeartbeat(store, fingerprint, options, instance, clock, NullLogger<HostRegistrationHeartbeat>.Instance);
        await heartbeat.TickAsync(CancellationToken.None);

        var written = Assert.Single(store.Upserted);
        Assert.Equal(HostRole.Web, written.Role);
        Assert.Equal(instance, written.Instance);
        Assert.Equal(clock.UtcNow, written.StartedAt);
        Assert.Equal(clock.UtcNow, written.HeartbeatAt);
        Assert.Equal(fingerprint.Compute(options), written.SettingsFingerprint);
    }

    [Fact]
    public async Task Started_at_is_captured_once_and_stays_stable_while_heartbeat_at_advances()
    {
        var clock = new FakeClock();
        var store = new FakeHostRegistrationStore();
        var heartbeat = new HostRegistrationHeartbeat(
            store,
            new SettingsFingerprint(),
            PeerHostHealthCheckTests.Options(),
            new InstanceId("host/1"),
            clock,
            NullLogger<HostRegistrationHeartbeat>.Instance);

        await heartbeat.TickAsync(CancellationToken.None);
        var firstStartedAt = store.Upserted[0].StartedAt;

        clock.Advance(TimeSpan.FromMinutes(5));
        await heartbeat.TickAsync(CancellationToken.None);

        Assert.Equal(2, store.Upserted.Count);
        Assert.Equal(firstStartedAt, store.Upserted[1].StartedAt);
        Assert.Equal(clock.UtcNow, store.Upserted[1].HeartbeatAt);
        Assert.NotEqual(store.Upserted[1].StartedAt, store.Upserted[1].HeartbeatAt);
    }

    [Fact]
    public async Task An_unavailable_store_does_not_throw_and_logs_at_debug_rather_than_warning()
    {
        var store = new FakeHostRegistrationStore { Unavailable = true };
        var logger = new CapturingLogger<HostRegistrationHeartbeat>();
        var heartbeat = new HostRegistrationHeartbeat(
            store, new SettingsFingerprint(), PeerHostHealthCheckTests.Options(), new InstanceId("host/1"), new FakeClock(), logger);

        var exception = await Record.ExceptionAsync(() => heartbeat.TickAsync(CancellationToken.None));

        Assert.Null(exception);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Debug);
        Assert.DoesNotContain(logger.Entries, entry => entry.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task A_write_failure_other_than_unavailable_logs_a_warning_naming_the_error()
    {
        var store = new FakeHostRegistrationStore { UpsertFailure = TransactionError.Faulted() };
        var logger = new CapturingLogger<HostRegistrationHeartbeat>();
        var heartbeat = new HostRegistrationHeartbeat(
            store, new SettingsFingerprint(), PeerHostHealthCheckTests.Options(), new InstanceId("host/1"), new FakeClock(), logger);

        await heartbeat.TickAsync(CancellationToken.None);

        Assert.Contains(
            logger.Entries,
            entry => entry.Level == LogLevel.Warning && entry.Message.Contains(nameof(TransactionError.Faulted), StringComparison.Ordinal));
    }
}
