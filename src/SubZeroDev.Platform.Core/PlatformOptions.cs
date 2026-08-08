using SubZeroDev.Platform.Abstractions;

namespace SubZeroDev.Platform.Core;

/// <summary>Which persistence provider a host is configured for. A setting rather than part of the
/// provider abstraction, which is why it lives beside <see cref="PersistenceOptions"/>.</summary>
public enum PersistenceProvider
{
    /// <summary>PostgreSQL, which serves everything but the single-file installation.</summary>
    PostgreSql,

    /// <summary>SQLite, which serves local developer execution and single-file homelab
    /// installations. A production path, not a test double.</summary>
    Sqlite,
}

/// <summary>Every setting one host is configured by. Bound once, validated at startup, immutable
/// after, and never persisted.</summary>
public sealed record PlatformOptions
{
    /// <summary>Derived from the entry assembly when unset, which is why it is not required.</summary>
    public string? ServiceName { get; init; }

    /// <summary>Derived from the entry assembly when unset, which is why it is not required.</summary>
    public string? ServiceVersion { get; init; }

    /// <summary>Derived from the host and not bindable — a service must not declare itself
    /// production in a file that shipped from a developer's machine.</summary>
    public string Environment { get; internal init; } = string.Empty;

    /// <summary>Fixed by which form of the registration call the host made, and not bindable.</summary>
    public HostRole Role { get; internal init; }

    /// <summary>Provider selection and connection.</summary>
    public required PersistenceOptions Persistence { get; init; }

    /// <summary>Outbox retention, retry and dispatch settings.</summary>
    public required OutboxOptions Outbox { get; init; }

    /// <summary>Background work lease settings.</summary>
    public LeaseOptions Lease { get; init; } = new();

    /// <summary>Host registration and peer-detection settings.</summary>
    public HostRegistrationOptions HostRegistration { get; init; } = new();

    /// <summary>Readiness thresholds.</summary>
    public HealthOptions Health { get; init; } = new();

    /// <summary>Shutdown and probe-surface settings.</summary>
    public HostingOptions Hosting { get; init; } = new();

    /// <summary>Log-file location and optional OTLP export target.</summary>
    public TelemetryOptions Telemetry { get; init; } = new();
}

/// <summary>Log-file location and optional OTLP export target. Everything else about telemetry
/// (rolling, retention, buffering, redaction, sampling) is fixed policy, not a setting — see
/// <c>design/d3/90-decisions.md</c>, "S8 telemetry policy is fixed, typed and non-blocking".</summary>
public sealed record TelemetryOptions
{
    /// <summary>Where role-specific JSON Lines log files are written.</summary>
    public string LogDirectory { get; init; } = "logs";

    /// <summary>The OTLP HTTP/protobuf collector endpoint. Absent by default: with no endpoint
    /// configured, no exporter starts and no outbound connection is attempted.</summary>
    public Uri? OtlpEndpoint { get; init; }
}

/// <summary>Provider selection and connection.</summary>
public sealed record PersistenceOptions
{
    /// <summary>Which provider this host uses.</summary>
    public required PersistenceProvider Provider { get; init; }

    /// <summary>The connection string, parseable by the selected provider.</summary>
    public required string ConnectionString { get; init; }

    /// <summary>How long a SQLite write waits for the single write lock before failing. Part of the
    /// contract, because it decides whether contention shows up as latency or as a failed request.</summary>
    public TimeSpan SqliteBusyWaitBound { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>Outbox retention, retry, deferral and dispatch settings.</summary>
public sealed record OutboxOptions
{
    /// <summary>How long a processed row is kept. Required: "configurable with no default" silently
    /// becomes "never prune".</summary>
    [Fingerprinted]
    public required TimeSpan ProcessedRetention { get; init; }

    /// <summary>How long a poisoned or discarded row is kept, so forensics outlive routine cleanup.</summary>
    [Fingerprinted]
    public required TimeSpan PoisonedRetention { get; init; }

    /// <summary>How long a claim is honoured before the row is eligible again.</summary>
    [Fingerprinted]
    public TimeSpan ClaimWindow { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How many attempts a row gets before it is poisoned.</summary>
    [Fingerprinted]
    public int PoisonAttemptCount { get; init; } = 12;

    /// <summary>The first retry delay.</summary>
    [Fingerprinted]
    public TimeSpan RetryBackoffBase { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The multiplier applied to each successive retry delay.</summary>
    [Fingerprinted]
    public double RetryBackoffFactor { get; init; } = 2;

    /// <summary>The ceiling on a retry delay.</summary>
    [Fingerprinted]
    public TimeSpan RetryBackoffCap { get; init; } = TimeSpan.FromHours(6);

    /// <summary>How long a row may keep deferring, measured from its first deferral, before it is
    /// poisoned.</summary>
    [Fingerprinted]
    public TimeSpan DeferralAge { get; init; } = TimeSpan.FromHours(24);

    /// <summary>How long a deferred row waits before the next attempt. Fixed, not backed off:
    /// resolution flips when a deploy finishes, so polling faster buys nothing.</summary>
    public TimeSpan DeferralRetryInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>How many rows one dispatch tick may handle. Bounds a tick, never a claim.</summary>
    public int DispatchTickBudget { get; init; } = 20;

    /// <summary>How many rows one prune statement may delete.</summary>
    public int PruneBatchSize { get; init; } = 500;

    /// <summary>How often Hosting invokes a dispatch tick.</summary>
    public TimeSpan DispatchInterval { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>Background work lease settings.</summary>
public sealed record LeaseOptions
{
    /// <summary>How long a lease is held before it expires. Deliberately the claim window's twin.</summary>
    [Fingerprinted]
    public TimeSpan Duration { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>Host registration and peer-detection settings.</summary>
public sealed record HostRegistrationOptions
{
    /// <summary>How often a host renews its registration row.</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How long a dead registration row is kept before the prune pass removes it.</summary>
    public TimeSpan RetentionWindow { get; init; } = TimeSpan.FromDays(7);

    /// <summary>How long a peer's absence must persist before it degrades readiness. A rolling
    /// measure on the observing host's clock, never a startup-scoped exemption.</summary>
    public TimeSpan PeerAbsenceGrace { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How stale a heartbeat may be before its host is not counted live. Derived, so the
    /// two values cannot disagree, and wide enough that one missed beat cannot flap readiness.</summary>
    public TimeSpan PeerLivenessThreshold => HeartbeatInterval * 3;
}

/// <summary>Readiness thresholds.</summary>
public sealed record HealthOptions
{
    /// <summary>How long the oldest pending row may be past due before readiness degrades.</summary>
    public TimeSpan BacklogAgeThreshold { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How many pending rows are tolerated before readiness degrades.</summary>
    public long PendingCountThreshold { get; init; } = 100_000;
}

/// <summary>Shutdown and probe-surface settings.</summary>
public sealed record HostingOptions
{
    /// <summary>How long a shutting-down worker finishes in-flight messages for.</summary>
    public TimeSpan GracefulShutdownDrainWindow { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The port the worker serves its probes on. One per box, not one per design.</summary>
    public int WorkerProbePort { get; init; } = 5100;

    /// <summary>Whether the worker probe binds loopback only. It exists for the operator and for
    /// CI, not for the network.</summary>
    public bool WorkerProbeLoopbackOnly { get; init; } = true;
}
