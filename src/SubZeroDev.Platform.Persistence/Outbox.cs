using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization;
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

/// <inheritdoc cref="IOutboxStore"/>
internal sealed class OutboxStore(IAmbientTransactionAccessor ambient, IProviderCapability capability) : IOutboxStore
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
