using System.Data.Common;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;

namespace SubZeroDev.Platform.Persistence;

/// <summary>One host's row in the store it is actually using. Never read by the host that wrote
/// it — its only consumer is the other role's readiness check, so a host writing to the wrong
/// database registers itself <em>there</em>, and its absence from the right one is what makes that
/// detectable.</summary>
public sealed record HostRegistration
{
    /// <summary>The role this instance runs as. Part of the primary key with <see cref="Instance"/>.</summary>
    public required HostRole Role { get; init; }

    /// <summary>The running process instance. Part of the primary key with <see cref="Role"/>.</summary>
    public required InstanceId Instance { get; init; }

    /// <summary>When this instance started. Set once, at the row's first insert, and never
    /// updated afterwards — a heartbeat updates <see cref="HeartbeatAt"/> and no other column.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>The last heartbeat. The only column a heartbeat after the first insert updates.</summary>
    public required DateTimeOffset HeartbeatAt { get; init; }

    /// <summary>This instance's settings fingerprint, computed once and stable for the instance's
    /// lifetime — its options never change while the process runs.</summary>
    public required string SettingsFingerprint { get; init; }
}

/// <summary>Stores host registrations. One implementation, parameterised by
/// <see cref="IProviderCapability"/> — the policy of what counts as live and what to do about a
/// disagreement lives in the two readiness checks that read this, not here.</summary>
public interface IHostRegistrationStore
{
    /// <summary>Inserts or updates a registration. On conflict, only <see cref="HostRegistration.HeartbeatAt"/>
    /// changes — <see cref="HostRegistration.StartedAt"/> and <see cref="HostRegistration.SettingsFingerprint"/>
    /// are write-once, since they describe this running instance and neither can change while it runs.</summary>
    /// <param name="registration">The registration to upsert.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Success, or why the write did not complete.</returns>
    Task<Result<TransactionError>> UpsertAsync(
        HostRegistration registration, CancellationToken cancellationToken);

    /// <summary>Lists every registration whose heartbeat is at or after <paramref name="heartbeatSince"/> —
    /// the live rows, on the terms the reader chooses its own liveness threshold by.</summary>
    /// <param name="heartbeatSince">The earliest heartbeat instant that still counts as live.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The live registrations, or why the read did not complete.</returns>
    Task<Result<IReadOnlyList<HostRegistration>, TransactionError>> ListLiveAsync(
        DateTimeOffset heartbeatSince, CancellationToken cancellationToken);

    /// <summary>Deletes one registration. Called on graceful shutdown, so the surviving peer sees
    /// the absence at once rather than waiting for the liveness threshold to lapse.</summary>
    /// <param name="role">The role of the registration to delete.</param>
    /// <param name="instance">The instance of the registration to delete.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Success, or why the delete did not complete.</returns>
    Task<Result<TransactionError>> DeleteAsync(
        HostRole role, InstanceId instance, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IHostRegistrationStore"/>
internal sealed class HostRegistrationStore(
    IUnitOfWork unitOfWork, IAmbientTransactionAccessor ambient, IProviderCapability capability)
    : IHostRegistrationStore
{
    public Task<Result<TransactionError>> UpsertAsync(
        HostRegistration registration, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            TransactionIntent.Write,
            async token =>
            {
                var current = ambient.Current!;
                await using var command = current.Connection.CreateCommand();
                command.Transaction = current.Transaction;

                // started_at is excluded from the update clause on purpose: a heartbeat updates
                // heartbeat_at and no other column, and started_at describes the row's first insert.
                command.CommandText = """
                    INSERT INTO platform_host_registration (role, instance, started_at, heartbeat_at, settings_fingerprint)
                    VALUES (@role, @instance, @startedAt, @heartbeatAt, @fingerprint)
                    ON CONFLICT (role, instance) DO UPDATE SET heartbeat_at = excluded.heartbeat_at;
                    """;

                AddParameter(command, "@role", registration.Role.ToString());
                AddParameter(command, "@instance", registration.Instance.Value);
                AddParameter(command, "@startedAt", capability.FormatInstant(registration.StartedAt));
                AddParameter(command, "@heartbeatAt", capability.FormatInstant(registration.HeartbeatAt));
                AddParameter(command, "@fingerprint", registration.SettingsFingerprint);

                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            },
            cancellationToken);

    public Task<Result<IReadOnlyList<HostRegistration>, TransactionError>> ListLiveAsync(
        DateTimeOffset heartbeatSince, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync<IReadOnlyList<HostRegistration>>(
            TransactionIntent.ReadOnly,
            async token =>
            {
                var current = ambient.Current!;
                await using var command = current.Connection.CreateCommand();
                command.Transaction = current.Transaction;
                command.CommandText =
                    "SELECT role, instance, started_at, heartbeat_at, settings_fingerprint "
                    + "FROM platform_host_registration WHERE heartbeat_at >= @since;";
                AddParameter(command, "@since", capability.FormatInstant(heartbeatSince));

                var registrations = new List<HostRegistration>();
                await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                while (await reader.ReadAsync(token).ConfigureAwait(false))
                {
                    if (!Enum.TryParse<HostRole>(reader.GetString(0), out var role))
                    {
                        continue;
                    }

                    if (!capability.TryParseInstant(reader.GetString(2), out var startedAt)
                        || !capability.TryParseInstant(reader.GetString(3), out var heartbeatAt))
                    {
                        continue;
                    }

                    registrations.Add(new HostRegistration
                    {
                        Role = role,
                        Instance = new InstanceId(reader.GetString(1)),
                        StartedAt = startedAt,
                        HeartbeatAt = heartbeatAt,
                        SettingsFingerprint = reader.GetString(4),
                    });
                }

                return (IReadOnlyList<HostRegistration>)registrations;
            },
            cancellationToken);

    public Task<Result<TransactionError>> DeleteAsync(
        HostRole role, InstanceId instance, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            TransactionIntent.Write,
            async token =>
            {
                var current = ambient.Current!;
                await using var command = current.Connection.CreateCommand();
                command.Transaction = current.Transaction;
                command.CommandText = "DELETE FROM platform_host_registration WHERE role = @role AND instance = @instance;";
                AddParameter(command, "@role", role.ToString());
                AddParameter(command, "@instance", instance.Value);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            },
            cancellationToken);

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

/// <summary>Creates every table Platform owns and migrates itself, under one module name of its
/// own rather than a product's — one source, since a second <see cref="IModuleMigrationSource"/>
/// declaring the same <see cref="ModuleName"/> is exactly the collision
/// <see cref="MigrationRunner"/>'s own history-table-collision check rejects.</summary>
/// <remarks>Applying and re-applying this migration, on both providers, is exercised in
/// <c>HostRegistrationTests</c> and <c>HostRegistrationPostgresTests</c> (positive cases); a
/// consumer module accidentally reusing the <c>"Platform"</c> name is rejected by
/// <see cref="MigrationRunner"/>'s history-table-collision check before anything applies (negative
/// case, in <c>PersistenceIntegrationTests</c>).</remarks>
internal sealed class PlatformMigrationSource : IModuleMigrationSource
{
    public ModuleName Module { get; } = new("Platform");

    public IReadOnlyList<IModuleMigration> Migrations { get; } =
        [new CreateHostRegistrationTable(), new PlatformOutboxMigration()];

    private sealed class CreateHostRegistrationTable : IModuleMigration
    {
        public string Name => "0001_create_host_registration";

        public async Task ApplyAsync(DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
        {
            await using (var createTable = connection.CreateCommand())
            {
                createTable.Transaction = transaction;
                createTable.CommandText = """
                    CREATE TABLE platform_host_registration (
                        role TEXT NOT NULL,
                        instance TEXT NOT NULL,
                        started_at TEXT NOT NULL,
                        heartbeat_at TEXT NOT NULL,
                        settings_fingerprint TEXT NOT NULL,
                        PRIMARY KEY (role, instance)
                    );
                    """;
                await createTable.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using var createIndex = connection.CreateCommand();
            createIndex.Transaction = transaction;
            createIndex.CommandText =
                "CREATE INDEX ix_platform_host_registration_role_heartbeat "
                + "ON platform_host_registration (role, heartbeat_at);";
            await createIndex.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>Renews this host's row on its own interval. Both roles run it, under the one channel
/// Hosting starts work it cannot otherwise name through.</summary>
internal sealed class HostRegistrationHeartbeat(
    IHostRegistrationStore store,
    ISettingsFingerprint fingerprint,
    PlatformOptions options,
    InstanceId instance,
    IClock clock,
    ILogger<HostRegistrationHeartbeat> logger) : IBackgroundWork
{
    // Captured once, at construction — effectively at process start — because started_at is
    // write-once at the row's first insert and every later upsert excludes it from the update
    // clause. Passing a value that is only ever used once costs nothing.
    private readonly DateTimeOffset _startedAt = clock.UtcNow;

    public BackgroundWorkName Name => PlatformBackgroundWork.HostRegistrationHeartbeat;

    public HostRoles Roles => HostRoles.Both;

    public TimeSpan Interval => options.HostRegistration.HeartbeatInterval;

    public bool RequiresLease => false;

    public async Task TickAsync(CancellationToken cancellationToken)
    {
        var registration = new HostRegistration
        {
            Role = options.Role,
            Instance = instance,
            StartedAt = _startedAt,
            HeartbeatAt = clock.UtcNow,
            SettingsFingerprint = fingerprint.Compute(options),
        };

        // An unreachable or not-yet-migrated store is the ordinary state before migrate mode runs
        // — reported here by simply not writing, exactly as a schema-absent readiness check
        // degrades rather than throws. The next tick, on the ordinary interval, is the retry; no
        // bespoke retry loop exists beside it. Logged rather than thrown, so BackgroundWorkService
        // never turns the ordinary pre-migration case into a per-tick error — but a failure a
        // caller cannot otherwise see still leaves a line an operator can find.
        var written = await store.UpsertAsync(registration, cancellationToken).ConfigureAwait(false);
        if (!written.IsSuccess)
        {
            if (written.Error.Code == nameof(TransactionError.Unavailable))
            {
                logger.LogDebug("Host registration heartbeat did not write: the store is unreachable or not yet migrated.");
            }
            else
            {
                logger.LogWarning(
                    "Host registration heartbeat did not write: {Code}. {Detail}",
                    written.Error.Code,
                    written.Error.Detail);
            }
        }
    }
}

/// <summary>Whether the other role is present in this store — the only mechanism that can see two
/// hosts pointed at different databases, since each would be individually reachable and
/// individually configured correctly.</summary>
internal sealed class PeerHostHealthCheck(
    IHostRegistrationStore store,
    PlatformOptions options,
    IClock clock) : IHealthCheck
{
    private readonly Lock _gate = new();
    private DateTimeOffset? _absentSince;

    public HealthCheckName Name => PlatformHealthChecks.PeerHost;

    public HealthCheckKind Kind => HealthCheckKind.Readiness;

    public HealthCheckCriticality Criticality => HealthCheckCriticality.Required;

    public TimeSpan Timeout { get; } = TimeSpan.FromSeconds(5);

    public bool TouchesExternalDependency => true;

    public async Task<HealthCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        var peerRole = options.Role == HostRole.Web ? HostRole.Worker : HostRole.Web;
        var now = clock.UtcNow;
        var since = now - options.HostRegistration.PeerLivenessThreshold;

        var listed = await store.ListLiveAsync(since, cancellationToken).ConfigureAwait(false);
        if (!listed.IsSuccess)
        {
            return new HealthCheckResult(HealthStatus.Degraded, listed.Error.Detail, new Dictionary<string, string>());
        }

        var present = listed.Value.Any(registration => registration.Role == peerRole);
        var isDevelopment = string.Equals(options.Environment, Environments.Development, StringComparison.OrdinalIgnoreCase);

        // The grace is a rolling measure from the absence first being seen, on this host's own
        // clock — never a startup-scoped exemption — so it is tracked here, across calls, rather
        // than derived from the stored heartbeat alone.
        lock (_gate)
        {
            if (present)
            {
                _absentSince = null;
                return new HealthCheckResult(HealthStatus.Healthy, null, new Dictionary<string, string>());
            }

            _absentSince ??= now;

            if (isDevelopment)
            {
                return new HealthCheckResult(
                    HealthStatus.Healthy,
                    $"Peer role '{peerRole}' is absent. Informational in Development.",
                    new Dictionary<string, string> { ["peer"] = peerRole.ToString() });
            }

            if (now - _absentSince.Value < options.HostRegistration.PeerAbsenceGrace)
            {
                return new HealthCheckResult(HealthStatus.Healthy, null, new Dictionary<string, string>());
            }

            return new HealthCheckResult(
                HealthStatus.Degraded,
                $"Peer role '{peerRole}' is missing.",
                new Dictionary<string, string> { ["peer"] = peerRole.ToString() });
        }
    }
}

/// <summary>Whether a live peer's fingerprinted settings agree with this host's — a stale
/// registration is excluded by construction, since only live rows are ever read.</summary>
internal sealed class SettingsFingerprintHealthCheck(
    IHostRegistrationStore store,
    ISettingsFingerprint fingerprint,
    PlatformOptions options,
    IClock clock) : IHealthCheck
{
    public HealthCheckName Name => PlatformHealthChecks.SettingsFingerprint;

    public HealthCheckKind Kind => HealthCheckKind.Readiness;

    public HealthCheckCriticality Criticality => HealthCheckCriticality.Required;

    public TimeSpan Timeout { get; } = TimeSpan.FromSeconds(5);

    public bool TouchesExternalDependency => true;

    public async Task<HealthCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        var since = clock.UtcNow - options.HostRegistration.PeerLivenessThreshold;
        var listed = await store.ListLiveAsync(since, cancellationToken).ConfigureAwait(false);
        if (!listed.IsSuccess)
        {
            return new HealthCheckResult(HealthStatus.Degraded, listed.Error.Detail, new Dictionary<string, string>());
        }

        var mine = fingerprint.Compute(options);

        // A host's own row always matches its own fingerprint, so comparing against every live row
        // — this host's included — excludes it without a separate identity check.
        var disagreeing = listed.Value.Where(registration => registration.SettingsFingerprint != mine).ToList();

        if (disagreeing.Count == 0)
        {
            return new HealthCheckResult(HealthStatus.Healthy, null, new Dictionary<string, string>());
        }

        var data = disagreeing
            .Select((registration, index) => (registration, index))
            .ToDictionary(pair => $"peer[{pair.index}]", pair => $"{pair.registration.Role}/{pair.registration.Instance}");

        return new HealthCheckResult(
            HealthStatus.Degraded,
            "A live peer's settings fingerprint disagrees with this host's.",
            data);
    }
}

/// <summary>Deletes this host's own registration row on graceful shutdown, so the surviving peer
/// sees the absence at once rather than waiting for the liveness threshold to lapse.</summary>
internal sealed class HostRegistrationShutdownCleanup(
    IHostRegistrationStore store, PlatformOptions options, InstanceId instance) : IHostedLifecycleService
{
    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StoppingAsync(CancellationToken cancellationToken) =>
        // Best-effort: a store that is unreachable at shutdown leaves the row for the peer's
        // liveness threshold and grace to age out instead, exactly as an unclean exit already does.
        await store.DeleteAsync(options.Role, instance, cancellationToken).ConfigureAwait(false);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
