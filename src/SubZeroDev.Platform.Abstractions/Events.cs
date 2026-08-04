namespace SubZeroDev.Platform.Abstractions;

/// <summary>Marks a type as an integration event. Carries no <c>TypeName</c> of its own: the stable
/// name a stored row's <c>type</c> column carries comes from an explicit registration, because
/// dispatch must get from a stored string to a CLR type and has no instance to ask — the instance is
/// what deserialization produces.</summary>
public interface IIntegrationEvent;

/// <summary>Handles one integration event. Returns a result rather than throwing, so the dispatcher
/// can distinguish a handled failure — which participates in the attempt-and-backoff cycle — from a
/// defect. An exception escaping a handler is treated as <see cref="HandlerError.Transient"/>.</summary>
/// <typeparam name="TEvent">The event this handler handles.</typeparam>
public interface IIntegrationEventHandler<in TEvent>
    where TEvent : IIntegrationEvent
{
    /// <summary>Handles one delivery of the event.</summary>
    /// <param name="event">The event.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Success, or why the handler could not complete — both variants consume an attempt.</returns>
    Task<Result<HandlerError>> HandleAsync(TEvent @event, CancellationToken cancellationToken);
}

/// <summary>Why a handler did not complete. Returned by a handler, never raised by the dispatcher —
/// both variants consume an attempt, which is what separates this from <c>DispatchError</c>.</summary>
public sealed record HandlerError : PlatformError
{
    private HandlerError(string code, bool isRetryable)
        : base(code) => IsRetryable = isRetryable;

    /// <inheritdoc/>
    public override bool IsRetryable { get; }

    /// <summary>The handler failed in a way that may succeed later. Also what an exception escaping
    /// the handler is treated as.</summary>
    /// <returns>The error.</returns>
    public static HandlerError Transient() => new(nameof(Transient), isRetryable: true);

    /// <summary>The handler failed in a way that will not succeed on retry. Poisons the row
    /// immediately, without burning the remaining attempts to reach a conclusion the handler already
    /// had.</summary>
    /// <returns>The error.</returns>
    public static HandlerError Permanent() => new(nameof(Permanent), isRetryable: false);
}
