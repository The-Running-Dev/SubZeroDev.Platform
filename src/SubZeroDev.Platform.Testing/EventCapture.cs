using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Persistence;

namespace SubZeroDev.Platform.Testing;

/// <summary>One recorded enqueue: the id, type, tenant, correlation and instant — everything a test
/// needs to assert what was enqueued without reading the row back through SQL.</summary>
/// <param name="Id">The enqueued message's id.</param>
/// <param name="Type">The event's stable name.</param>
/// <param name="Tenant">The ambient tenant at enqueue.</param>
/// <param name="Correlation">The ambient correlation at enqueue.</param>
/// <param name="Culture">The ambient culture at enqueue.</param>
/// <param name="At">When the enqueue happened, from the test host's clock.</param>
public sealed record CapturedEvent(
    OutboxMessageId Id,
    EventTypeName Type,
    TenantId Tenant,
    CorrelationId Correlation,
    CultureTag Culture,
    DateTimeOffset At);

/// <summary>Records every enqueue a test host observed, without reading the database back.</summary>
public interface IEventCapture
{
    /// <summary>Every event enqueued, in enqueue order.</summary>
    IReadOnlyList<CapturedEvent> Enqueued { get; }

    /// <summary>Every event whose claimed row was marked processed, in completion order.</summary>
    IReadOnlyList<CapturedEvent> Dispatched { get; }

    /// <summary>Clears both capture lists.</summary>
    void Clear();
}

/// <inheritdoc cref="IEventCapture"/>
internal sealed class EventCapture : IEventCapture
{
    private readonly List<CapturedEvent> _enqueued = [];
    private readonly List<CapturedEvent> _dispatched = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<CapturedEvent> Enqueued
    {
        get
        {
            lock (_gate)
            {
                return _enqueued.ToList();
            }
        }
    }

    public IReadOnlyList<CapturedEvent> Dispatched
    {
        get
        {
            lock (_gate)
            {
                return _dispatched.ToList();
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _enqueued.Clear();
            _dispatched.Clear();
        }
    }

    internal void RecordEnqueued(CapturedEvent captured)
    {
        lock (_gate)
        {
            _enqueued.Add(captured);
        }
    }

    internal void RecordDispatched(CapturedEvent captured)
    {
        lock (_gate)
        {
            _dispatched.Add(captured);
        }
    }
}

/// <summary>Wraps the real <see cref="IOutboxWriter"/> so a test can observe every enqueue.
/// Persistence cannot reference Testing — the dependency graph runs the other way — so this decorator
/// lives here and is spliced in over the factory <see cref="PlatformPersistenceExtensions.AddPlatformPersistence"/>
/// registers. Records only after the inner writer succeeds, so a thrown contract violation records
/// nothing, matching <c>Enqueue</c>'s own "nothing is written" guarantee.</summary>
internal sealed class CapturingOutboxWriter(
    IOutboxWriter inner,
    EventCapture capture,
    IOperationScopeAccessor scopeAccessor,
    IEventHandlerRegistry registry,
    IClock clock) : IOutboxWriter
{
    public OutboxMessageId Enqueue<TEvent>(TEvent @event) where TEvent : IIntegrationEvent
    {
        var id = inner.Enqueue(@event);

        // inner.Enqueue would already have thrown if either were absent, so both are guaranteed
        // present here.
        var scope = scopeAccessor.Current!;
        registry.TryResolve(typeof(TEvent), out var registration);

        capture.RecordEnqueued(new CapturedEvent(
            id, registration.Type, scope.Tenant, scope.Correlation, scope.Culture, clock.UtcNow));

        return id;
    }
}

/// <summary>Observes the public outbox-store contract so Testing can record completed dispatches
/// without creating a dependency from Persistence back to Testing.</summary>
internal sealed class CapturingOutboxStore(
    IOutboxStore inner,
    EventCapture capture,
    IClock clock) : IOutboxStore
{
    private readonly Dictionary<(OutboxMessageId Id, InstanceId Holder), OutboxMessage> _claimed = [];
    private readonly Lock _gate = new();

    public Task<Result<TransactionError>> InsertAsync(OutboxMessage message, CancellationToken cancellationToken) =>
        inner.InsertAsync(message, cancellationToken);

    public async Task<Result<OutboxMessage?, TransactionError>> ClaimNextAsync(
        InstanceId holder, CancellationToken cancellationToken)
    {
        var result = await inner.ClaimNextAsync(holder, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess && result.Value is { } message)
        {
            lock (_gate)
            {
                _claimed[(message.Id, holder)] = message;
            }
        }

        return result;
    }

    public async Task<Result<ClaimedWriteOutcome, TransactionError>> MarkProcessedAsync(
        OutboxMessageId id, InstanceId holder, CancellationToken cancellationToken)
    {
        var result = await inner.MarkProcessedAsync(id, holder, cancellationToken).ConfigureAwait(false);
        OutboxMessage? message = null;
        lock (_gate)
        {
            if (_claimed.TryGetValue((id, holder), out var claimed))
            {
                message = claimed;
                _claimed.Remove((id, holder));
            }
        }

        if (result.IsSuccess && result.Value == ClaimedWriteOutcome.Applied && message is not null)
        {
            capture.RecordDispatched(new CapturedEvent(
                message.Id, message.Type, message.Tenant, message.Correlation, message.Culture, clock.UtcNow));
        }

        return result;
    }

    public Task<Result<ClaimedWriteOutcome, TransactionError>> RecordFailureAsync(
        OutboxMessageId id, InstanceId holder, string error, DateTimeOffset nextAttemptAt,
        CancellationToken cancellationToken) => CompleteAsync(
            id, holder, inner.RecordFailureAsync(id, holder, error, nextAttemptAt, cancellationToken));

    public Task<Result<ClaimedWriteOutcome, TransactionError>> PoisonAsync(
        OutboxMessageId id, InstanceId holder, string error, PoisonAttemptMode attemptMode,
        CancellationToken cancellationToken) => CompleteAsync(
            id, holder, inner.PoisonAsync(id, holder, error, attemptMode, cancellationToken));

    public Task<Result<ClaimedWriteOutcome, TransactionError>> DeferAsync(
        OutboxMessageId id, InstanceId holder, DateTimeOffset nextAttemptAt,
        CancellationToken cancellationToken) => CompleteAsync(
            id, holder, inner.DeferAsync(id, holder, nextAttemptAt, cancellationToken));

    public Task<Result<ClaimedWriteOutcome, TransactionError>> ReleaseClaimAsync(
        OutboxMessageId id, InstanceId holder, CancellationToken cancellationToken) => CompleteAsync(
            id, holder, inner.ReleaseClaimAsync(id, holder, cancellationToken));

    private async Task<Result<ClaimedWriteOutcome, TransactionError>> CompleteAsync(
        OutboxMessageId id,
        InstanceId holder,
        Task<Result<ClaimedWriteOutcome, TransactionError>> task)
    {
        var result = await task.ConfigureAwait(false);
        lock (_gate)
        {
            _claimed.Remove((id, holder));
        }

        return result;
    }
}
