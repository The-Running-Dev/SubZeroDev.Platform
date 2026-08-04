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
}

/// <inheritdoc cref="IEventCapture"/>
internal sealed class EventCapture : IEventCapture
{
    private readonly List<CapturedEvent> _enqueued = [];
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

    internal void RecordEnqueued(CapturedEvent captured)
    {
        lock (_gate)
        {
            _enqueued.Add(captured);
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
