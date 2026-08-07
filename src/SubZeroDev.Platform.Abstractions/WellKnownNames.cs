namespace SubZeroDev.Platform.Abstractions;

/// <summary>Platform's own telemetry identity names. Public surface: they are the source and meter
/// names an exporter or a third-party instrument subscribes to.</summary>
public static class PlatformTelemetry
{
    /// <summary>The name of Platform's own <see cref="System.Diagnostics.ActivitySource"/>, shared by
    /// every span Platform starts so one OpenTelemetry subscription sees all of them.</summary>
    public const string ActivitySourceName = "SubZeroDev.Platform";

    /// <summary>The name of Platform's own meter, shared by every standard metric Platform emits.</summary>
    public const string MeterName = "SubZeroDev.Platform";
}

/// <summary>Platform's own background-work names. Public surface, not implementation detail: they
/// are the handles a test invokes a single tick by.</summary>
public static class PlatformBackgroundWork
{
    /// <summary>The outbox dispatcher. Worker only.</summary>
    public static BackgroundWorkName OutboxDispatch { get; } = new("platform.outbox.dispatch");

    /// <summary>One registration covering all three retention windows. Worker only.</summary>
    public static BackgroundWorkName Prune { get; } = new("platform.prune");

    /// <summary>The host registration heartbeat, which both roles run.</summary>
    public static BackgroundWorkName HostRegistrationHeartbeat { get; } = new("platform.host-registration.heartbeat");
}

/// <summary>Platform's own health check names. Public surface: they appear in the probe body an
/// operator reads.</summary>
public static class PlatformHealthChecks
{
    /// <summary>Whether the configured store is reachable.</summary>
    public static HealthCheckName Database { get; } = new("platform.database");

    /// <summary>Whether the other role is present in this store — the only way a split database is
    /// visible.</summary>
    public static HealthCheckName PeerHost { get; } = new("platform.peer-host");

    /// <summary>Whether a live peer's fingerprinted settings agree with this host's.</summary>
    public static HealthCheckName SettingsFingerprint { get; } = new("platform.settings-fingerprint");

    /// <summary>How long the oldest pending outbox row has been past due.</summary>
    public static HealthCheckName OutboxBacklogAge { get; } = new("platform.outbox.backlog-age");

    /// <summary>How many outbox rows are pending.</summary>
    public static HealthCheckName OutboxPendingCount { get; } = new("platform.outbox.pending-count");

    /// <summary>How many outbox rows are poisoned, discarded excluded.</summary>
    public static HealthCheckName OutboxPoisonCount { get; } = new("platform.outbox.poison-count");

    /// <summary>Whether this host's registered migrations match the applied ones.</summary>
    public static HealthCheckName PendingMigrations { get; } = new("platform.pending-migrations");
}
