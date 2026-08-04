using System.Data.Common;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;

namespace SubZeroDev.Platform.Persistence;

/// <summary>Holds the provider-native migration exclusion — an advisory lock on PostgreSQL, an
/// immediate transaction on SQLite. Connection-scoped: releasing it is a disposal, not an
/// expiring lease, so a stalled migrator cannot leave it fenced open.</summary>
/// <remarks>The connection and transaction are exposed for the same reason
/// <see cref="IAmbientTransaction"/> exposes them: a module's migration runs against them, and the
/// runner cannot hand over what it cannot read. On SQLite they are the exclusion itself — the lock
/// <em>is</em> the transaction — which is what makes one migrate-mode run atomic as a whole.</remarks>
public interface IMigrationLock : IAsyncDisposable
{
    /// <summary>The connection the lock is held on, and migrations apply through.</summary>
    DbConnection Connection { get; }

    /// <summary>The transaction migrations apply within. Committed by the runner on success and
    /// rolled back whole on any failure; a migration never completes it itself.</summary>
    DbTransaction Transaction { get; }
}

/// <summary>What a bounded prune statement targets. <see cref="ProcessedOutboxRows"/> and
/// <see cref="PoisonedOutboxRows"/> both live in <c>platform_outbox</c> — the poisoned target also
/// removes discarded rows, since the predicate table prunes both on the poison window.
/// <see cref="DeadHostRegistrations"/> is the third retention window, in
/// <c>platform_host_registration</c>. One registration — <c>PlatformBackgroundWork.Prune</c> —
/// covers all three.</summary>
public enum PruneTarget
{
    /// <summary>Rows in <c>platform_outbox</c> with <c>processed_at</c> set and <c>poisoned_at</c>
    /// null, older than <c>Outbox:ProcessedRetention</c>.</summary>
    ProcessedOutboxRows,

    /// <summary>Rows in <c>platform_outbox</c> with <c>poisoned_at</c> set — poisoned or discarded,
    /// per the predicate table — older than <c>Outbox:PoisonedRetention</c>.</summary>
    PoisonedOutboxRows,

    /// <summary>Rows in <c>platform_host_registration</c> whose heartbeat is older than
    /// <c>HostRegistration:RetentionWindow</c>.</summary>
    DeadHostRegistrations,
}

/// <summary>Everything the two providers must do differently to produce the same observable
/// result. A member belongs here on that test alone; everything identical between providers
/// belongs in a store instead.</summary>
public interface IProviderCapability
{
    /// <summary>Which provider this is.</summary>
    PersistenceProvider Provider { get; }

    /// <summary>Formats an instant for storage. On SQLite, fixed-width ISO-8601 UTC text,
    /// <c>Z</c>-suffixed, exactly seven fractional digits, never trimmed — the same formatter binds
    /// every comparand compared against the column.</summary>
    /// <param name="instant">The instant, with <c>Offset == TimeSpan.Zero</c>.</param>
    /// <returns>The stored form.</returns>
    string FormatInstant(DateTimeOffset instant);

    /// <summary>Parses a stored instant back, returning <see langword="false"/> rather than
    /// throwing.</summary>
    /// <param name="stored">The stored form.</param>
    /// <param name="instant">The parsed instant, or <see langword="default"/> when parsing failed.</param>
    /// <returns><see langword="true"/> when <paramref name="stored"/> parsed.</returns>
    bool TryParseInstant(string stored, out DateTimeOffset instant);

    /// <summary>Encodes an identifier for storage. On SQLite, a 16-byte blob in RFC 4122 network
    /// byte order, so bytewise blob order equals mint order.</summary>
    /// <param name="value">The identifier.</param>
    /// <returns>The stored form.</returns>
    byte[] EncodeIdentifier(Guid value);

    /// <summary>Decodes a stored identifier back, returning <see langword="false"/> rather than
    /// throwing.</summary>
    /// <param name="encoded">The stored form.</param>
    /// <param name="value">The decoded identifier, or <see langword="default"/> when decoding failed.</param>
    /// <returns><see langword="true"/> when <paramref name="encoded"/> decoded.</returns>
    bool TryDecodeIdentifier(ReadOnlySpan<byte> encoded, out Guid value);

    /// <summary>The migration history table's name for one module. One history per module, never
    /// shared, so two modules' migrations apply in either order.</summary>
    /// <param name="module">The module.</param>
    /// <returns>The table name.</returns>
    string MigrationHistoryTable(ModuleName module);

    /// <summary>Opens a connection and begins a transaction of the stated intent. A transaction
    /// that will write begins immediate, never deferred.</summary>
    /// <param name="intent">Whether the transaction will only read, or will write.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The connection and transaction opened, or why they could not be.</returns>
    /// <remarks>Returns the pair rather than reporting success alone, because the caller owns the
    /// lifetime and cannot commit, roll back or dispose what it cannot read. The unit of work is
    /// what makes the returned pair <em>ambient</em>; the capability only opens it.</remarks>
    Task<Result<IAmbientTransaction, TransactionError>> BeginAsync(
        TransactionIntent intent,
        CancellationToken cancellationToken);

    /// <summary>Classifies an exception raised while a transaction was open. A capability member
    /// because it is the definition of a provider difference: what counts as busy, as a
    /// concurrency conflict, or as unreachable is a different exception type and code on each
    /// provider, while the unit of work's response to each is identical.</summary>
    /// <param name="exception">The exception raised.</param>
    /// <returns>The error the caller surfaces.</returns>
    TransactionError Classify(Exception exception);

    /// <summary>Atomically stamps one due pending outbox row with a live claim.</summary>
    /// <param name="holder">The claiming process instance.</param>
    /// <param name="now">The clock instant used for due and expiry predicates.</param>
    /// <param name="claimWindow">How long an existing claim remains live.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The claimed id, null when no row was eligible, or the transaction error.</returns>
    Task<Result<OutboxMessageId?, TransactionError>> StampClaimAsync(
        InstanceId holder,
        DateTimeOffset now,
        TimeSpan claimWindow,
        CancellationToken cancellationToken);

    /// <summary>Acquires the provider-native migration lock. A second concurrent invocation fails
    /// fast rather than waiting.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The lock, or why it could not be acquired.</returns>
    Task<Result<IMigrationLock, MigrationError>> AcquireMigrationLockAsync(CancellationToken cancellationToken);

    /// <summary>Asserts the preconditions startup depends on — WAL mode and the busy-wait bound on
    /// SQLite, reachability on both.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Success, or the named configuration defect.</returns>
    Task<Result<ConfigurationError>> AssertStartupPreconditionsAsync(CancellationToken cancellationToken);

    /// <summary>Deletes up to <paramref name="batchSize"/> rows matching <paramref name="target"/>
    /// and older than <paramref name="olderThan"/>, in one bounded statement — never an unbounded
    /// delete.</summary>
    /// <param name="target">What to prune.</param>
    /// <param name="olderThan">The age boundary; a row at or after this instant is not eligible.</param>
    /// <param name="batchSize">The most rows one statement may remove.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>How many rows were deleted, or why the delete did not complete.</returns>
    Task<Result<int, TransactionError>> DeleteBoundedAsync(
        PruneTarget target,
        DateTimeOffset olderThan,
        int batchSize,
        CancellationToken cancellationToken);
}

/// <summary>The bounded-delete statement for each <see cref="PruneTarget"/>. Identical text for both
/// providers — a subquery ordered and limited, deleted by primary key — the same shape
/// <c>StampClaimAsync</c> already uses for a portable conditional write; each provider still opens
/// the connection itself, which is the actual difference the capability seam exists for.</summary>
internal static class PruneSql
{
    internal static string For(PruneTarget target) => target switch
    {
        PruneTarget.ProcessedOutboxRows => """
            DELETE FROM platform_outbox
            WHERE id IN (
                SELECT id FROM platform_outbox
                WHERE processed_at IS NOT NULL AND poisoned_at IS NULL AND processed_at < @olderThan
                ORDER BY processed_at
                LIMIT @batchSize
            );
            """,
        PruneTarget.PoisonedOutboxRows => """
            DELETE FROM platform_outbox
            WHERE id IN (
                SELECT id FROM platform_outbox
                WHERE poisoned_at IS NOT NULL AND poisoned_at < @olderThan
                ORDER BY poisoned_at
                LIMIT @batchSize
            );
            """,
        PruneTarget.DeadHostRegistrations => """
            DELETE FROM platform_host_registration
            WHERE (role, instance) IN (
                SELECT role, instance FROM platform_host_registration
                WHERE heartbeat_at < @olderThan
                ORDER BY heartbeat_at
                LIMIT @batchSize
            );
            """,
        _ => throw new ArgumentOutOfRangeException(nameof(target)),
    };
}
