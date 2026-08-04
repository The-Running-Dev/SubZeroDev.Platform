using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;

namespace SubZeroDev.Platform.Persistence;

/// <summary>An outbox row's identity — a version-7 UUID minted app-side at enqueue, so it exists
/// before the insert and sorts in mint order on both providers.</summary>
/// <param name="Value">The identifier.</param>
public readonly record struct OutboxMessageId(Guid Value)
{
    /// <summary>Mints a new identifier from the clock. Millisecond order, tie unspecified.</summary>
    /// <param name="at">The instant to mint at.</param>
    /// <returns>The identifier.</returns>
    public static OutboxMessageId Create(DateTimeOffset at) => new(Guid.CreateVersion7(at));

    /// <inheritdoc/>
    public override string ToString() => Value.ToString();
}

/// <summary>An outbox row's state, derived from its two mark columns rather than stored — a
/// discriminator column would be a second source of truth for a predicate the columns already
/// carry.</summary>
public enum OutboxMessageState
{
    /// <summary>Not yet processed or poisoned.</summary>
    Pending,

    /// <summary>Delivered successfully.</summary>
    Processed,

    /// <summary>Exhausted its retries, or failed permanently, without an operator disposition yet.</summary>
    Poisoned,

    /// <summary>An operator retired it after it was poisoned. Both marks set — the one state discard
    /// alone may produce.</summary>
    Discarded,
}

/// <summary>One outbox row.</summary>
public sealed record OutboxMessage
{
    /// <summary>The row's identity. Primary key.</summary>
    public required OutboxMessageId Id { get; init; }

    /// <summary>Claim order, provider-allocated. Not an identity — reused on SQLite after a drain
    /// and prune.</summary>
    public required long Sequence { get; init; }

    /// <summary>When the row was enqueued.</summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>The registered stable name, unchanged by a later CLR rename.</summary>
    public required EventTypeName Type { get; init; }

    /// <summary>The serialised event, under Platform's pinned <c>System.Text.Json</c> options.</summary>
    public required string Payload { get; init; }

    /// <summary>The tenant stamped from the ambient scope at enqueue.</summary>
    public required TenantId Tenant { get; init; }

    /// <summary>The complete W3C trace context stamped at enqueue, trace flags included.</summary>
    public required TraceContext TraceContext { get; init; }

    /// <summary>The originating trace-id, stamped from the ambient correlation at enqueue and
    /// unchanged through any depth of derived events.</summary>
    public required CorrelationId Correlation { get; init; }

    /// <summary>The originating culture, stamped from the ambient scope at enqueue and unchanged
    /// through any depth of derived events. <see cref="CultureTag.Invariant"/> means the actor
    /// expressed no preference, not "unknown".</summary>
    public required CultureTag Culture { get; init; }

    /// <summary>How many delivery attempts have consumed a <c>HandlerError</c>. Increases only on a
    /// handler failure; never decreases except through an explicit redrive.</summary>
    public required int Attempts { get; init; }

    /// <summary>The next attempt instant. Null means due at <see cref="OccurredAt"/>.</summary>
    public DateTimeOffset? NextAttemptAt { get; init; }

    /// <summary>When this row first deferred, because no handler was yet registered for its type.</summary>
    public DateTimeOffset? FirstDeferredAt { get; init; }

    /// <summary>The instance currently holding a claim. Null exactly when <see cref="ClaimedAt"/> is.</summary>
    public InstanceId? ClaimedBy { get; init; }

    /// <summary>When the current claim was taken. Null exactly when <see cref="ClaimedBy"/> is.</summary>
    public DateTimeOffset? ClaimedAt { get; init; }

    /// <summary>When the row was delivered successfully.</summary>
    public DateTimeOffset? ProcessedAt { get; init; }

    /// <summary>When the row was poisoned. Set implies <see cref="LastError"/> is non-null.</summary>
    public DateTimeOffset? PoisonedAt { get; init; }

    /// <summary>The last handler or dispatch error, non-null whenever <see cref="PoisonedAt"/> is set.</summary>
    public string? LastError { get; init; }

    /// <summary>The row's state, derived from <see cref="ProcessedAt"/> and <see cref="PoisonedAt"/>
    /// per the predicate table — never a column of its own.</summary>
    public OutboxMessageState State => (ProcessedAt, PoisonedAt) switch
    {
        (null, null) => OutboxMessageState.Pending,
        ({ }, null) => OutboxMessageState.Processed,
        (null, { }) => OutboxMessageState.Poisoned,
        ({ }, { }) => OutboxMessageState.Discarded,
    };

    /// <summary>The due predicate made a member: <see cref="NextAttemptAt"/> when set, otherwise
    /// <see cref="OccurredAt"/>.</summary>
    public DateTimeOffset DueAt => NextAttemptAt ?? OccurredAt;
}

/// <summary>Enqueues an integration event inside the caller's own transaction. The only member a
/// product calls directly.</summary>
public interface IOutboxWriter
{
    /// <summary>Enqueues an event. Synchronous because it does not write: it stamps the row from the
    /// ambient transaction and scope and stages it, and the write happens on commit — which is what
    /// gives a failed insert the unit of work's own <c>TransactionError</c> handling rather than a
    /// bare exception escaping a non-<see langword="async"/> call.</summary>
    /// <typeparam name="TEvent">The event's CLR type. Must be bound to a stable name by a prior
    /// <see cref="IEventHandlerRegistry.Register{TEvent, THandler}"/> call.</typeparam>
    /// <param name="event">The event.</param>
    /// <returns>The id, usable as a dedupe key before the row is durable.</returns>
    /// <exception cref="PlatformContractViolationException">No ambient transaction is open
    /// (<c>NoAmbientTransaction</c>); no ambient operation scope is open
    /// (<c>NoAmbientOperationScope</c>); or no registration bound <typeparamref name="TEvent"/> to a
    /// name (<c>UnregisteredEventType</c>). Nothing is written in any of the three cases.</exception>
    OutboxMessageId Enqueue<TEvent>(TEvent @event) where TEvent : IIntegrationEvent;
}

/// <summary>Recovers poisoned outbox rows or records an operator's decision to retire them. This is
/// deliberately a library surface: D3 ships no administrative endpoint, command, or UI.</summary>
public interface IOutboxAdministration
{
    /// <summary>Resets each named poisoned row so the next dispatch tick may claim it.</summary>
    Task<Result<IReadOnlyList<OutboxAdministrationResult>, OutboxError>> RedriveAsync(
        IReadOnlyCollection<OutboxMessageId> ids, CancellationToken cancellationToken);

    /// <summary>Resets every poisoned row of one registered event type.</summary>
    Task<Result<int, OutboxError>> RedriveByTypeAsync(EventTypeName type, CancellationToken cancellationToken);

    /// <summary>Retires each named poisoned row, retaining its poison record and the operator reason.</summary>
    Task<Result<IReadOnlyList<OutboxAdministrationResult>, OutboxError>> DiscardAsync(
        IReadOnlyCollection<OutboxMessageId> ids, string reason, CancellationToken cancellationToken);

    /// <summary>Retires every poisoned row of one registered event type.</summary>
    Task<Result<int, OutboxError>> DiscardByTypeAsync(
        EventTypeName type, string reason, CancellationToken cancellationToken);

    /// <summary>Lists at most <paramref name="limit"/> non-discarded poisoned rows.</summary>
    Task<Result<IReadOnlyList<OutboxMessage>, OutboxError>> ListPoisonedAsync(
        int limit, CancellationToken cancellationToken);
}

/// <summary>The successful disposition of one named administrative operation.</summary>
public enum OutboxAdministrationOutcome
{
    /// <summary>The requested transition was applied.</summary>
    Applied,

    /// <summary>No row has this identifier.</summary>
    NotFound,

    /// <summary>The row exists but is not eligible for the requested poisoned-row operation.</summary>
    NotPoisoned,
}

/// <summary>The outcome for one requested outbox message.</summary>
/// <param name="Id">The requested message identifier.</param>
/// <param name="Outcome">Whether the operation was applied.</param>
public sealed record OutboxAdministrationResult(OutboxMessageId Id, OutboxAdministrationOutcome Outcome);

/// <summary>Stores outbox rows. One implementation, parameterised by
/// <see cref="IProviderCapability"/>.</summary>
public interface IOutboxStore
{
    /// <summary>Inserts one row against the ambient transaction. Never opens a connection of its
    /// own — the caller's ambient transaction is the only one this ever enlists against.</summary>
    /// <param name="message">The row to insert.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Success, or why the write did not complete.</returns>
    Task<Result<TransactionError>> InsertAsync(OutboxMessage message, CancellationToken cancellationToken);

    /// <summary>Claims one due pending row, or returns null when none is eligible.</summary>
    Task<Result<OutboxMessage?, TransactionError>> ClaimNextAsync(
        InstanceId holder, CancellationToken cancellationToken);

    /// <summary>Marks a live claim processed.</summary>
    Task<Result<ClaimedWriteOutcome, TransactionError>> MarkProcessedAsync(
        OutboxMessageId id, InstanceId holder, CancellationToken cancellationToken);

    /// <summary>Records one transient handler failure against a live claim.</summary>
    Task<Result<ClaimedWriteOutcome, TransactionError>> RecordFailureAsync(
        OutboxMessageId id, InstanceId holder, string error, DateTimeOffset nextAttemptAt,
        CancellationToken cancellationToken);

    /// <summary>Poisons a live claim, explicitly selecting whether this transition consumes a
    /// handler attempt.</summary>
    Task<Result<ClaimedWriteOutcome, TransactionError>> PoisonAsync(
        OutboxMessageId id, InstanceId holder, string error, PoisonAttemptMode attemptMode,
        CancellationToken cancellationToken);

    /// <summary>Defers a live claim without consuming an attempt.</summary>
    Task<Result<ClaimedWriteOutcome, TransactionError>> DeferAsync(
        OutboxMessageId id, InstanceId holder, DateTimeOffset nextAttemptAt,
        CancellationToken cancellationToken);

    /// <summary>Releases a live claim without changing retry state.</summary>
    Task<Result<ClaimedWriteOutcome, TransactionError>> ReleaseClaimAsync(
        OutboxMessageId id, InstanceId holder, CancellationToken cancellationToken);

    /// <summary>Resets each named poisoned row and returns an outcome for each id.</summary>
    Task<Result<IReadOnlyList<OutboxAdministrationResult>, TransactionError>> RedriveAsync(
        IReadOnlyCollection<OutboxMessageId> ids, CancellationToken cancellationToken);

    /// <summary>Resets every poisoned row with the given type.</summary>
    Task<Result<int, TransactionError>> RedriveByTypeAsync(EventTypeName type, CancellationToken cancellationToken);

    /// <summary>Discards each named poisoned row and returns an outcome for each id.</summary>
    Task<Result<IReadOnlyList<OutboxAdministrationResult>, TransactionError>> DiscardAsync(
        IReadOnlyCollection<OutboxMessageId> ids, string reason, CancellationToken cancellationToken);

    /// <summary>Discards every poisoned row with the given type.</summary>
    Task<Result<int, TransactionError>> DiscardByTypeAsync(
        EventTypeName type, string reason, CancellationToken cancellationToken);

    /// <summary>Lists at most <paramref name="limit"/> non-discarded poisoned rows.</summary>
    Task<Result<IReadOnlyList<OutboxMessage>, TransactionError>> ListPoisonedAsync(
        int limit, CancellationToken cancellationToken);

    /// <summary>The due instant of the oldest pending row — <c>next_attempt_at</c> when set,
    /// otherwise <c>occurred_at</c> — or null when nothing is pending. Readiness measures time past
    /// due against this, never time since occurred.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The oldest pending due instant, or why the read did not complete.</returns>
    Task<Result<DateTimeOffset?, TransactionError>> OldestPendingDueAsync(CancellationToken cancellationToken);

    /// <summary>How many rows are pending.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The count, or why the read did not complete.</returns>
    Task<Result<long, TransactionError>> PendingCountAsync(CancellationToken cancellationToken);

    /// <summary>How many rows are poisoned. Excludes discarded rows — the decision a discarded row
    /// was demanding has already been made.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The count, or why the read did not complete.</returns>
    Task<Result<long, TransactionError>> PoisonedCountAsync(CancellationToken cancellationToken);

    /// <summary>Deletes up to <paramref name="batchSize"/> eligible rows for <paramref name="target"/>,
    /// older than <paramref name="olderThan"/>.</summary>
    /// <param name="target">What to prune.</param>
    /// <param name="olderThan">The age boundary.</param>
    /// <param name="batchSize">The most rows one statement may remove.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>How many rows were deleted, or why the delete did not complete.</returns>
    Task<Result<int, TransactionError>> PruneAsync(
        PruneTarget target, DateTimeOffset olderThan, int batchSize, CancellationToken cancellationToken);
}

/// <summary>The result of a conditional dispatch-state write.</summary>
public enum ClaimedWriteOutcome
{
    /// <summary>The conditional write changed the row.</summary>
    Applied,

    /// <summary>The claim expired or was reclaimed before the write.</summary>
    ClaimLost,
}

/// <summary>Whether poisoning consumes a handler attempt.</summary>
public enum PoisonAttemptMode
{
    /// <summary>Increment attempts once for the handler failure that caused poison.</summary>
    Increment,

    /// <summary>Preserve attempts because a dispatch failure caused poison.</summary>
    Preserve,
}

/// <summary>Platform's pinned <c>System.Text.Json</c> options. Not exposed publicly and not
/// resolvable from the container — the durable format, not a preference, per
/// design/20-contract.md's cut converter extension point.</summary>
internal static class OutboxSerializer
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        NumberHandling = JsonNumberHandling.Strict,
    };
}

/// <inheritdoc cref="IOutboxWriter"/>
internal sealed class OutboxWriter(
    IAmbientTransactionAccessor ambientTransaction,
    IOperationScopeAccessor scopeAccessor,
    IEventHandlerRegistry registry,
    IClock clock) : IOutboxWriter
{
    public OutboxMessageId Enqueue<TEvent>(TEvent @event) where TEvent : IIntegrationEvent
    {
        ArgumentNullException.ThrowIfNull(@event);

        var current = ambientTransaction.Current as AmbientTransaction
            ?? throw new PlatformContractViolationException(ContractViolation.NoAmbientTransaction());

        var scope = scopeAccessor.Current
            ?? throw new PlatformContractViolationException(ContractViolation.NoAmbientOperationScope());

        if (!registry.TryResolve(typeof(TEvent), out var registration))
        {
            throw new PlatformContractViolationException(ContractViolation.UnregisteredEventType());
        }

        var now = clock.UtcNow;
        var id = OutboxMessageId.Create(now);
        var payload = JsonSerializer.Serialize(@event, typeof(TEvent), OutboxSerializer.Options);

        current.PendingOutboxMessages.Add(new OutboxMessage
        {
            Id = id,
            Sequence = 0, // provider-allocated at insert; this value is never read back from here
            OccurredAt = now,
            Type = registration.Type,
            Payload = payload,
            Tenant = scope.Tenant,
            TraceContext = scope.Trace,
            Correlation = scope.Correlation,
            Culture = scope.Culture,
            Attempts = 0,
        });

        return id;
    }
}

/// <inheritdoc cref="IOutboxAdministration"/>
internal sealed class OutboxAdministration(IOutboxStore store) : IOutboxAdministration
{
    public async Task<Result<IReadOnlyList<OutboxAdministrationResult>, OutboxError>> RedriveAsync(
        IReadOnlyCollection<OutboxMessageId> ids, CancellationToken cancellationToken) =>
        Map(await store.RedriveAsync(ids, cancellationToken).ConfigureAwait(false));

    public async Task<Result<int, OutboxError>> RedriveByTypeAsync(
        EventTypeName type, CancellationToken cancellationToken) =>
        Map(await store.RedriveByTypeAsync(type, cancellationToken).ConfigureAwait(false));

    public async Task<Result<IReadOnlyList<OutboxAdministrationResult>, OutboxError>> DiscardAsync(
        IReadOnlyCollection<OutboxMessageId> ids, string reason, CancellationToken cancellationToken) =>
        Map(await store.DiscardAsync(ids, reason, cancellationToken).ConfigureAwait(false));

    public async Task<Result<int, OutboxError>> DiscardByTypeAsync(
        EventTypeName type, string reason, CancellationToken cancellationToken) =>
        Map(await store.DiscardByTypeAsync(type, reason, cancellationToken).ConfigureAwait(false));

    public async Task<Result<IReadOnlyList<OutboxMessage>, OutboxError>> ListPoisonedAsync(
        int limit, CancellationToken cancellationToken) =>
        Map(await store.ListPoisonedAsync(limit, cancellationToken).ConfigureAwait(false));

    private static Result<T, OutboxError> Map<T>(Result<T, TransactionError> result) =>
        result.IsSuccess
            ? Result<T, OutboxError>.Success(result.Value)
            : Result<T, OutboxError>.Failure(OutboxError.Unavailable());
}

/// <inheritdoc cref="IOutboxStore"/>
internal sealed class OutboxStore(
    IAmbientTransactionAccessor ambient,
    IProviderCapability capability,
    IClock clock,
    PlatformOptions options,
    ILogger<OutboxStore> logger) : IOutboxStore
{
    public async Task<Result<TransactionError>> InsertAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        var current = ambient.Current
            ?? throw new PlatformContractViolationException(ContractViolation.NoAmbientTransaction());

        try
        {
            await using var command = current.Connection.CreateCommand();
            command.Transaction = current.Transaction;

            // sequence is provider-allocated claim order, not an identity. On SQLite the scalar
            // subquery reproduces the documented rowid-reuse behaviour after a drain and prune (MAX
            // of an empty table is null, so the next row starts back at 1), and is race-free there
            // because a write transaction already serialises against every other writer. That same
            // subquery races under PostgreSQL's real concurrency — two concurrent inserts can read
            // the same MAX and collide on the UNIQUE constraint — so there the column is a BIGINT
            // identity instead, and DEFAULT lets Postgres allocate it.
            var sequenceValue = capability.Provider == PersistenceProvider.PostgreSql
                ? "DEFAULT"
                : "(SELECT COALESCE(MAX(sequence), 0) + 1 FROM platform_outbox)";

            command.CommandText = $"""
                INSERT INTO platform_outbox
                    (id, sequence, occurred_at, type, payload, tenant, trace_parent, trace_state,
                     correlation, culture, attempts, next_attempt_at, first_deferred_at, claimed_by,
                     claimed_at, processed_at, poisoned_at, last_error)
                VALUES
                    (@id, {sequenceValue}, @occurredAt, @type,
                     @payload, @tenant, @traceParent, @traceState, @correlation, @culture, @attempts,
                     @nextAttemptAt, @firstDeferredAt, @claimedBy, @claimedAt, @processedAt, @poisonedAt,
                     @lastError);
                """;

            AddParameter(command, "@id", capability.EncodeIdentifier(message.Id.Value));
            AddParameter(command, "@occurredAt", capability.FormatInstant(message.OccurredAt));
            AddParameter(command, "@type", message.Type.Value);
            AddParameter(command, "@payload", message.Payload);
            AddParameter(command, "@tenant", message.Tenant.ToString());
            AddParameter(command, "@traceParent", message.TraceContext.TraceParent);
            AddParameter(command, "@traceState", message.TraceContext.TraceState);
            AddParameter(command, "@correlation", message.Correlation.TraceId);
            AddParameter(command, "@culture", message.Culture.Value);
            AddParameter(command, "@attempts", message.Attempts);
            AddParameter(command, "@nextAttemptAt", message.NextAttemptAt is { } next ? capability.FormatInstant(next) : null);
            AddParameter(command, "@firstDeferredAt", message.FirstDeferredAt is { } deferred ? capability.FormatInstant(deferred) : null);
            AddParameter(command, "@claimedBy", message.ClaimedBy?.Value);
            AddParameter(command, "@claimedAt", message.ClaimedAt is { } claimedAt ? capability.FormatInstant(claimedAt) : null);
            AddParameter(command, "@processedAt", message.ProcessedAt is { } processedAt ? capability.FormatInstant(processedAt) : null);
            AddParameter(command, "@poisonedAt", message.PoisonedAt is { } poisonedAt ? capability.FormatInstant(poisonedAt) : null);
            AddParameter(command, "@lastError", message.LastError);

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return Result<TransactionError>.Success();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller asked to stop, not the database — Classify exists to turn a provider's own
            // connect-or-command timeout into Unavailable, and applying it here would misreport the
            // caller's own cancellation as a retryable outage. UnitOfWork.ExecuteAsync makes the same
            // distinction for the same reason.
            throw;
        }
        catch (Exception exception)
        {
            return Result<TransactionError>.Failure(capability.Classify(exception));
        }
    }

    public async Task<Result<OutboxMessage?, TransactionError>> ClaimNextAsync(
        InstanceId holder, CancellationToken cancellationToken)
    {
        var claimed = await capability.StampClaimAsync(
            holder, clock.UtcNow, options.Outbox.ClaimWindow, cancellationToken).ConfigureAwait(false);
        if (!claimed.IsSuccess)
        {
            return Result<OutboxMessage?, TransactionError>.Failure(claimed.Error);
        }

        return claimed.Value is { } id
            ? await ReadClaimedAsync(id, holder, cancellationToken).ConfigureAwait(false)
            : Result<OutboxMessage?, TransactionError>.Success(null);
    }

    public Task<Result<ClaimedWriteOutcome, TransactionError>> MarkProcessedAsync(
        OutboxMessageId id, InstanceId holder, CancellationToken cancellationToken) =>
        WriteClaimedAsync(
            "SET processed_at = @now, claimed_by = NULL, claimed_at = NULL",
            id, holder, null, cancellationToken);

    public Task<Result<ClaimedWriteOutcome, TransactionError>> RecordFailureAsync(
        OutboxMessageId id, InstanceId holder, string error, DateTimeOffset nextAttemptAt,
        CancellationToken cancellationToken) =>
        WriteClaimedAsync(
            "SET attempts = attempts + 1, last_error = @error, next_attempt_at = @nextAttemptAt, claimed_by = NULL, claimed_at = NULL",
            id, holder, (error, nextAttemptAt), cancellationToken);

    public Task<Result<ClaimedWriteOutcome, TransactionError>> PoisonAsync(
        OutboxMessageId id, InstanceId holder, string error, PoisonAttemptMode attemptMode,
        CancellationToken cancellationToken)
    {
        var attempts = attemptMode switch
        {
            PoisonAttemptMode.Increment => "attempts = attempts + 1, ",
            PoisonAttemptMode.Preserve => string.Empty,
            _ => throw new ArgumentOutOfRangeException(nameof(attemptMode)),
        };

        return WriteClaimedAsync(
            $"SET {attempts}last_error = @error, poisoned_at = @now, claimed_by = NULL, claimed_at = NULL",
            id, holder, (error, null), cancellationToken);
    }

    public Task<Result<ClaimedWriteOutcome, TransactionError>> DeferAsync(
        OutboxMessageId id, InstanceId holder, DateTimeOffset nextAttemptAt,
        CancellationToken cancellationToken) =>
        WriteClaimedAsync(
            "SET first_deferred_at = COALESCE(first_deferred_at, @now), next_attempt_at = @nextAttemptAt, claimed_by = NULL, claimed_at = NULL",
            id, holder, (null, nextAttemptAt), cancellationToken);

    public Task<Result<ClaimedWriteOutcome, TransactionError>> ReleaseClaimAsync(
        OutboxMessageId id, InstanceId holder, CancellationToken cancellationToken) =>
        WriteClaimedAsync("SET claimed_by = NULL, claimed_at = NULL", id, holder, null, cancellationToken);

    public Task<Result<IReadOnlyList<OutboxAdministrationResult>, TransactionError>> RedriveAsync(
        IReadOnlyCollection<OutboxMessageId> ids, CancellationToken cancellationToken) =>
        WriteAdministrationRowsAsync(ids, null, redrive: true, cancellationToken);

    public Task<Result<int, TransactionError>> RedriveByTypeAsync(
        EventTypeName type, CancellationToken cancellationToken) =>
        WriteAdministrationTypeAsync(type, null, redrive: true, cancellationToken);

    public Task<Result<IReadOnlyList<OutboxAdministrationResult>, TransactionError>> DiscardAsync(
        IReadOnlyCollection<OutboxMessageId> ids, string reason, CancellationToken cancellationToken) =>
        WriteAdministrationRowsAsync(ids, reason, redrive: false, cancellationToken);

    public Task<Result<int, TransactionError>> DiscardByTypeAsync(
        EventTypeName type, string reason, CancellationToken cancellationToken) =>
        WriteAdministrationTypeAsync(type, reason, redrive: false, cancellationToken);

    public Task<Result<IReadOnlyList<OutboxMessage>, TransactionError>> ListPoisonedAsync(
        int limit, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        return ReadOnlyAsync<IReadOnlyList<OutboxMessage>>(async (command, token) =>
        {
            command.CommandText = """
                SELECT id, sequence, occurred_at, type, payload, tenant, trace_parent, trace_state,
                       correlation, culture, attempts, next_attempt_at, first_deferred_at, claimed_by,
                       claimed_at, processed_at, poisoned_at, last_error
                FROM platform_outbox
                WHERE poisoned_at IS NOT NULL AND processed_at IS NULL
                ORDER BY poisoned_at, sequence
                LIMIT @limit;
                """;
            AddParameter(command, "@limit", limit);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            List<OutboxMessage> rows = [];
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                rows.Add(ReadMessage(reader));
            }

            return Result<IReadOnlyList<OutboxMessage>, TransactionError>.Success(rows);
        }, cancellationToken);
    }

    public Task<Result<DateTimeOffset?, TransactionError>> OldestPendingDueAsync(CancellationToken cancellationToken) =>
        ReadOnlyAsync<DateTimeOffset?>(async (command, token) =>
        {
            command.CommandText = """
                SELECT COALESCE(next_attempt_at, occurred_at) AS due
                FROM platform_outbox
                WHERE processed_at IS NULL AND poisoned_at IS NULL
                ORDER BY due
                LIMIT 1;
                """;
            var value = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
            if (value is null or DBNull)
            {
                return Result<DateTimeOffset?, TransactionError>.Success(null);
            }

            return capability.TryParseInstant((string)value, out var due)
                ? Result<DateTimeOffset?, TransactionError>.Success(due)
                : Result<DateTimeOffset?, TransactionError>.Failure(TransactionError.Faulted());
        }, cancellationToken);

    public Task<Result<long, TransactionError>> PendingCountAsync(CancellationToken cancellationToken) =>
        CountAsync("SELECT COUNT(*) FROM platform_outbox WHERE processed_at IS NULL AND poisoned_at IS NULL;", cancellationToken);

    public Task<Result<long, TransactionError>> PoisonedCountAsync(CancellationToken cancellationToken) =>
        CountAsync("SELECT COUNT(*) FROM platform_outbox WHERE poisoned_at IS NOT NULL AND processed_at IS NULL;", cancellationToken);

    public async Task<Result<int, TransactionError>> PruneAsync(
        PruneTarget target, DateTimeOffset olderThan, int batchSize, CancellationToken cancellationToken)
    {
        if (target == PruneTarget.PoisonedOutboxRows)
        {
            await LogPoisonedRowsAboutToBePrunedAsync(olderThan, batchSize, cancellationToken).ConfigureAwait(false);
        }

        return await capability.DeleteBoundedAsync(target, olderThan, batchSize, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Best-effort: names each poisoned row a prune is about to remove, under the same
    /// predicate, order and limit the delete itself uses. Not required to match the delete
    /// row-for-row under concurrent mutation — the delete is what actually prunes and self-guards on
    /// its own terms; this only names what an operator should expect to see go.</summary>
    private async Task LogPoisonedRowsAboutToBePrunedAsync(
        DateTimeOffset olderThan, int batchSize, CancellationToken cancellationToken)
    {
        var opened = await capability.BeginAsync(TransactionIntent.ReadOnly, cancellationToken).ConfigureAwait(false);
        if (!opened.IsSuccess)
        {
            return;
        }

        var support = opened.Value;
        try
        {
            await using var command = support.Connection.CreateCommand();
            command.Transaction = support.Transaction;
            command.CommandText = """
                SELECT id FROM platform_outbox
                WHERE poisoned_at IS NOT NULL AND poisoned_at < @olderThan
                ORDER BY poisoned_at
                LIMIT @batchSize;
                """;
            AddParameter(command, "@olderThan", capability.FormatInstant(olderThan));
            AddParameter(command, "@batchSize", batchSize);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (capability.TryDecodeIdentifier((byte[])reader.GetValue(0), out var id))
                {
                    logger.LogWarning("Pruning poisoned outbox message {MessageId}.", new OutboxMessageId(id));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Best-effort logging only.
        }
        finally
        {
            try { await support.Transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            await support.Connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private Task<Result<long, TransactionError>> CountAsync(string sql, CancellationToken cancellationToken) =>
        ReadOnlyAsync<long>(async (command, token) =>
        {
            command.CommandText = sql;
            var value = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
            return Result<long, TransactionError>.Success(Convert.ToInt64(value));
        }, cancellationToken);

    private async Task<Result<IReadOnlyList<OutboxAdministrationResult>, TransactionError>> WriteAdministrationRowsAsync(
        IReadOnlyCollection<OutboxMessageId> ids, string? reason, bool redrive, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (!redrive)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        }

        var opened = await capability.BeginAsync(TransactionIntent.Write, cancellationToken).ConfigureAwait(false);
        if (!opened.IsSuccess)
        {
            return Result<IReadOnlyList<OutboxAdministrationResult>, TransactionError>.Failure(opened.Error);
        }

        var support = opened.Value;
        try
        {
            List<OutboxAdministrationResult> outcomes = [];
            foreach (var id in ids)
            {
                await using var command = support.Connection.CreateCommand();
                command.Transaction = support.Transaction;
                command.CommandText = AdministrationUpdateSql(redrive, byType: false);
                AddParameter(command, "@id", capability.EncodeIdentifier(id.Value));
                AddParameter(command, "@now", capability.FormatInstant(clock.UtcNow));
                AddParameter(command, "@reason", reason);
                var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                if (changed == 1)
                {
                    outcomes.Add(new OutboxAdministrationResult(id, OutboxAdministrationOutcome.Applied));
                    continue;
                }

                await using var lookup = support.Connection.CreateCommand();
                lookup.Transaction = support.Transaction;
                lookup.CommandText = "SELECT COUNT(*) FROM platform_outbox WHERE id = @id;";
                AddParameter(lookup, "@id", capability.EncodeIdentifier(id.Value));
                var exists = Convert.ToInt64(await lookup.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 0;
                outcomes.Add(new OutboxAdministrationResult(
                    id, exists ? OutboxAdministrationOutcome.NotPoisoned : OutboxAdministrationOutcome.NotFound));
            }

            await support.Transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Result<IReadOnlyList<OutboxAdministrationResult>, TransactionError>.Success(outcomes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            try { await support.Transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            return Result<IReadOnlyList<OutboxAdministrationResult>, TransactionError>.Failure(capability.Classify(exception));
        }
        finally
        {
            await support.Connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<Result<int, TransactionError>> WriteAdministrationTypeAsync(
        EventTypeName type, string? reason, bool redrive, CancellationToken cancellationToken)
    {
        if (!redrive)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        }

        var opened = await capability.BeginAsync(TransactionIntent.Write, cancellationToken).ConfigureAwait(false);
        if (!opened.IsSuccess)
        {
            return Result<int, TransactionError>.Failure(opened.Error);
        }

        var support = opened.Value;
        try
        {
            await using var command = support.Connection.CreateCommand();
            command.Transaction = support.Transaction;
            command.CommandText = AdministrationUpdateSql(redrive, byType: true);
            AddParameter(command, "@type", type.Value);
            AddParameter(command, "@now", capability.FormatInstant(clock.UtcNow));
            AddParameter(command, "@reason", reason);
            var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await support.Transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Result<int, TransactionError>.Success(changed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            try { await support.Transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            return Result<int, TransactionError>.Failure(capability.Classify(exception));
        }
        finally
        {
            await support.Connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static string AdministrationUpdateSql(bool redrive, bool byType)
    {
        var predicate = byType ? "type = @type" : "id = @id";
        return redrive
            ? $"UPDATE platform_outbox SET poisoned_at = NULL, attempts = 0, first_deferred_at = NULL, claimed_by = NULL, claimed_at = NULL, next_attempt_at = @now WHERE {predicate} AND poisoned_at IS NOT NULL AND processed_at IS NULL;"
            : $"UPDATE platform_outbox SET processed_at = @now, claimed_by = NULL, claimed_at = NULL, last_error = last_error || ': ' || @reason WHERE {predicate} AND poisoned_at IS NOT NULL AND processed_at IS NULL;";
    }

    /// <summary>Runs one read-only query against a fresh transaction, self-guarding on an absent
    /// schema the same way every other store method does — the catch classifies the exception rather
    /// than throwing, which is what turns a missing table into <c>TransactionError.Unavailable</c>.</summary>
    private async Task<Result<T, TransactionError>> ReadOnlyAsync<T>(
        Func<DbCommand, CancellationToken, Task<Result<T, TransactionError>>> run,
        CancellationToken cancellationToken)
    {
        var opened = await capability.BeginAsync(TransactionIntent.ReadOnly, cancellationToken).ConfigureAwait(false);
        if (!opened.IsSuccess)
        {
            return Result<T, TransactionError>.Failure(opened.Error);
        }

        var support = opened.Value;
        try
        {
            await using var command = support.Connection.CreateCommand();
            command.Transaction = support.Transaction;
            return await run(command, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Result<T, TransactionError>.Failure(capability.Classify(exception));
        }
        finally
        {
            try { await support.Transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            await support.Connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<Result<OutboxMessage?, TransactionError>> ReadClaimedAsync(
        OutboxMessageId id, InstanceId holder, CancellationToken cancellationToken)
    {
        var opened = await capability.BeginAsync(TransactionIntent.ReadOnly, cancellationToken).ConfigureAwait(false);
        if (!opened.IsSuccess)
        {
            return Result<OutboxMessage?, TransactionError>.Failure(opened.Error);
        }

        var support = opened.Value;
        try
        {
            await using var command = support.Connection.CreateCommand();
            command.Transaction = support.Transaction;
            command.CommandText = """
                SELECT id, sequence, occurred_at, type, payload, tenant, trace_parent, trace_state,
                       correlation, culture, attempts, next_attempt_at, first_deferred_at, claimed_by,
                       claimed_at, processed_at, poisoned_at, last_error
                FROM platform_outbox
                WHERE id = @id AND claimed_by = @holder;
                """;
            AddParameter(command, "@id", capability.EncodeIdentifier(id.Value));
            AddParameter(command, "@holder", holder.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return Result<OutboxMessage?, TransactionError>.Success(null);
            }

            return Result<OutboxMessage?, TransactionError>.Success(ReadMessage(reader));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Result<OutboxMessage?, TransactionError>.Failure(capability.Classify(exception));
        }
        finally
        {
            try { await support.Transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            await support.Connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<Result<ClaimedWriteOutcome, TransactionError>> WriteClaimedAsync(
        string setClause,
        OutboxMessageId id,
        InstanceId holder,
        (string? Error, DateTimeOffset? NextAttemptAt)? values,
        CancellationToken cancellationToken)
    {
        var opened = await capability.BeginAsync(TransactionIntent.Write, cancellationToken).ConfigureAwait(false);
        if (!opened.IsSuccess)
        {
            return Result<ClaimedWriteOutcome, TransactionError>.Failure(opened.Error);
        }

        var support = opened.Value;
        try
        {
            await using var command = support.Connection.CreateCommand();
            command.Transaction = support.Transaction;
            command.CommandText = $"UPDATE platform_outbox {setClause} WHERE id = @id AND claimed_by = @holder AND claimed_at > @expired;";
            var now = clock.UtcNow;
            AddParameter(command, "@id", capability.EncodeIdentifier(id.Value));
            AddParameter(command, "@holder", holder.Value);
            AddParameter(command, "@now", capability.FormatInstant(now));
            AddParameter(command, "@expired", capability.FormatInstant(now - options.Outbox.ClaimWindow));
            AddParameter(command, "@error", values?.Error);
            AddParameter(command, "@nextAttemptAt", values?.NextAttemptAt is { } next ? capability.FormatInstant(next) : null);
            var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await support.Transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Result<ClaimedWriteOutcome, TransactionError>.Success(
                changed == 1 ? ClaimedWriteOutcome.Applied : ClaimedWriteOutcome.ClaimLost);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            try { await support.Transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            return Result<ClaimedWriteOutcome, TransactionError>.Failure(capability.Classify(exception));
        }
        finally
        {
            await support.Connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private OutboxMessage ReadMessage(DbDataReader reader)
    {
        if (!capability.TryDecodeIdentifier((byte[])reader.GetValue(0), out var id)
            || !capability.TryParseInstant(reader.GetString(2), out var occurredAt)
            || !TenantId.TryParse(reader.GetString(5), out var tenant)
            || !TraceContext.TryParse(reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), out var trace))
        {
            throw new InvalidOperationException("The outbox row contains an invalid Platform-owned value.");
        }

        DateTimeOffset? InstantOrNull(int ordinal) => reader.IsDBNull(ordinal)
            ? null
            : capability.TryParseInstant(reader.GetString(ordinal), out var instant)
                ? instant
                : throw new InvalidOperationException("The outbox row contains an invalid instant.");

        return new OutboxMessage
        {
            Id = new OutboxMessageId(id),
            Sequence = reader.GetInt64(1),
            OccurredAt = occurredAt,
            Type = new EventTypeName(reader.GetString(3)),
            Payload = reader.GetString(4),
            Tenant = tenant,
            TraceContext = trace,
            Correlation = new CorrelationId(reader.GetString(8)),
            Culture = new CultureTag(reader.GetString(9)),
            Attempts = reader.GetInt32(10),
            NextAttemptAt = InstantOrNull(11),
            FirstDeferredAt = InstantOrNull(12),
            ClaimedBy = reader.IsDBNull(13) ? null : new InstanceId(reader.GetString(13)),
            ClaimedAt = InstantOrNull(14),
            ProcessedAt = InstantOrNull(15),
            PoisonedAt = InstantOrNull(16),
            LastError = reader.IsDBNull(17) ? null : reader.GetString(17),
        };
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}

/// <summary>Creates <c>platform_outbox</c> — one table, shared by every module, that Platform both
/// defines and stores. Folded into <see cref="PlatformMigrationSource"/> rather than a source of its
/// own: a second <see cref="IModuleMigrationSource"/> naming the same <see cref="ModuleName"/> is the
/// exact collision the migration runner rejects.</summary>
internal sealed class PlatformOutboxMigration : IModuleMigration
{
    public string Name => "0002_create_outbox";

    public async Task ApplyAsync(DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
    {
        // Postgres has no BLOB type; SQLite has no BYTEA. Both accept a same-shaped table
        // otherwise, so only the identifier column's declared type branches.
        var isSqlite = connection is Microsoft.Data.Sqlite.SqliteConnection;
        var blobType = isSqlite ? "BLOB" : "BYTEA";

        // sequence is claim order, not an identity, and OutboxMessage.Sequence is a long — so it
        // must be 64-bit on both providers. On SQLite, MAX(sequence)+1 at insert (see
        // OutboxStore.InsertAsync) reproduces the documented rowid-reuse-after-drain behaviour and
        // is race-free because a write transaction there already serialises against every other
        // writer. That same approach races under PostgreSQL's real concurrency, so there it is a
        // BIGINT identity column instead — provider-allocated, monotonic, never colliding under
        // concurrent inserts.
        var sequenceColumn = isSqlite
            ? "sequence INTEGER NOT NULL,"
            : "sequence BIGINT GENERATED BY DEFAULT AS IDENTITY,";

        await using (var createTable = connection.CreateCommand())
        {
            createTable.Transaction = transaction;
            createTable.CommandText = $"""
                CREATE TABLE platform_outbox (
                    id {blobType} NOT NULL,
                    {sequenceColumn}
                    occurred_at TEXT NOT NULL,
                    type TEXT NOT NULL,
                    payload TEXT NOT NULL,
                    tenant TEXT NOT NULL,
                    trace_parent TEXT NOT NULL,
                    trace_state TEXT NULL,
                    correlation TEXT NOT NULL,
                    culture TEXT NOT NULL,
                    attempts INTEGER NOT NULL DEFAULT 0,
                    next_attempt_at TEXT NULL,
                    first_deferred_at TEXT NULL,
                    claimed_by TEXT NULL,
                    claimed_at TEXT NULL,
                    processed_at TEXT NULL,
                    poisoned_at TEXT NULL,
                    last_error TEXT NULL,
                    PRIMARY KEY (id),
                    UNIQUE (sequence),
                    CHECK ((claimed_by IS NULL) = (claimed_at IS NULL)),
                    CHECK (poisoned_at IS NULL OR last_error IS NOT NULL),
                    CHECK (attempts >= 0)
                );
                """;
            await createTable.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var eligibility = connection.CreateCommand())
        {
            eligibility.Transaction = transaction;
            eligibility.CommandText =
                "CREATE INDEX ix_platform_outbox_eligibility "
                + "ON platform_outbox (processed_at, poisoned_at, next_attempt_at, claimed_at, sequence);";
            await eligibility.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var processed = connection.CreateCommand())
        {
            processed.Transaction = transaction;
            processed.CommandText = "CREATE INDEX ix_platform_outbox_processed_at ON platform_outbox (processed_at);";
            await processed.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var poisoned = connection.CreateCommand();
        poisoned.Transaction = transaction;
        poisoned.CommandText = "CREATE INDEX ix_platform_outbox_poisoned_at ON platform_outbox (poisoned_at);";
        await poisoned.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
