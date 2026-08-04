using SubZeroDev.Platform.Abstractions;

namespace SubZeroDev.Platform.Persistence;

/// <summary>Why a transaction did not complete. Platform retries nothing on the request path — the
/// retryable flag is information for the caller, not a promise Platform acts on.</summary>
public sealed record TransactionError : PlatformError
{
    private TransactionError(string code, bool isRetryable, string detail)
        : base(code)
    {
        IsRetryable = isRetryable;
        Detail = detail;
    }

    /// <summary>Names the cause, so an error envelope's log line and a readiness detail can cite it.</summary>
    public string Detail { get; }

    /// <inheritdoc/>
    public override bool IsRetryable { get; }

    /// <summary>The database cannot be reached, or its schema is absent.</summary>
    /// <returns>The error.</returns>
    public static TransactionError Unavailable() =>
        new(nameof(Unavailable), isRetryable: true, "The database could not be reached, or its schema is absent.");

    /// <summary>A concurrency conflict aborted the transaction.</summary>
    /// <returns>The error.</returns>
    public static TransactionError Conflict() =>
        new(nameof(Conflict), isRetryable: true, "A concurrency conflict aborted the transaction.");

    /// <summary>SQLite's busy-wait bound elapsed without acquiring the write lock.</summary>
    /// <returns>The error.</returns>
    public static TransactionError Busy() =>
        new(nameof(Busy), isRetryable: true, "The busy-wait bound elapsed without acquiring the write lock.");

    /// <summary>Any other failure inside the transaction. The rollback is complete.</summary>
    /// <returns>The error.</returns>
    /// <remarks><see cref="PlatformError.Code"/> and <see cref="Detail"/> both cross a wire — a
    /// readiness body renders the detail at full detail — so neither carries the exception's
    /// message. Invariant 46 admits no exception text into a probe body, and the exception itself
    /// belongs in the log, which is where the correlation ties the two together.</remarks>
    public static TransactionError Faulted() =>
        new(nameof(Faulted), isRetryable: false, "The transaction failed and was rolled back.");
}

/// <summary>Why an outbox administration operation did not complete.</summary>
public sealed record OutboxError : PlatformError
{
    private OutboxError(string code) : base(code) { }

    /// <inheritdoc/>
    public override bool IsRetryable => true;

    /// <summary>The outbox store could not be reached.</summary>
    /// <returns>The error.</returns>
    public static OutboxError Unavailable() => new(nameof(Unavailable));
}

/// <summary>Why a migration operation did not complete.</summary>
public sealed record MigrationError : PlatformError
{
    private MigrationError(string code, bool isRetryable, string detail)
        : base(code)
    {
        IsRetryable = isRetryable;
        Detail = detail;
    }

    /// <summary>Names the cause.</summary>
    public string Detail { get; }

    /// <inheritdoc/>
    public override bool IsRetryable { get; }

    /// <summary>A migration failed to apply. Both providers apply one migration atomically, so the
    /// database is left at a known point.</summary>
    /// <param name="module">The module whose migration failed.</param>
    /// <param name="migration">The migration that failed.</param>
    /// <param name="detail">What failed.</param>
    /// <returns>The error.</returns>
    public static MigrationError Failed(ModuleName module, string migration, string detail) =>
        new(nameof(Failed), isRetryable: false, $"Module '{module}' migration '{migration}' failed: {detail}");

    /// <summary>Another invocation holds the provider-native migration lock. Fails fast: nothing is
    /// applied while a competing invocation holds the lock.</summary>
    /// <returns>The error.</returns>
    public static MigrationError Locked() =>
        new(nameof(Locked), isRetryable: true, "Another invocation holds the migration lock.");

    /// <summary>The database cannot be reached.</summary>
    /// <returns>The error.</returns>
    public static MigrationError Unavailable() =>
        new(nameof(Unavailable), isRetryable: true, "The database could not be reached.");

    /// <summary>Two modules' history tables resolve to one name. Caught before anything is applied,
    /// because sharing a history is silent corruption of what per-module histories provide: each
    /// module reads the other's applied list and skips its own migrations.</summary>
    /// <param name="first">One module.</param>
    /// <param name="second">The module colliding with it.</param>
    /// <param name="table">The history table name they share.</param>
    /// <returns>The error.</returns>
    public static MigrationError HistoryTableCollision(ModuleName first, ModuleName second, string table) =>
        new(
            nameof(HistoryTableCollision),
            isRetryable: false,
            $"Modules '{first}' and '{second}' both resolve to migration history table '{table}'. "
            + "Rename one so each module owns its own history.");
}

/// <summary>Why a lease operation did not complete as asked. The lease is an optimisation against
/// duplicate work, not a mutual-exclusion primitive — nothing fences a holder that stalls past its
/// expiry, so leased work must be idempotent.</summary>
public sealed record LeaseError : PlatformError
{
    private LeaseError(string code, bool isRetryable, string detail)
        : base(code)
    {
        IsRetryable = isRetryable;
        Detail = detail;
    }

    /// <summary>Names the cause.</summary>
    public string Detail { get; }

    /// <inheritdoc/>
    public override bool IsRetryable { get; }

    /// <summary>Another holder has an unexpired lease. The caller skips this run entirely.</summary>
    /// <returns>The error.</returns>
    public static LeaseError Held() =>
        new(nameof(Held), isRetryable: true, "Another holder has an unexpired lease.");

    /// <summary>Renewal found the lease held by someone else — the caller must abort its work
    /// immediately rather than continue on the assumption it still holds the lease.</summary>
    /// <returns>The error.</returns>
    public static LeaseError Lost() =>
        new(nameof(Lost), isRetryable: false, "Renewal found the lease held by someone else.");

    /// <summary>The database cannot be reached.</summary>
    /// <returns>The error.</returns>
    public static LeaseError Unavailable() =>
        new(nameof(Unavailable), isRetryable: true, "The database could not be reached.");
}

/// <summary>A rejected event handler registration, or a handler this host could not construct.</summary>
public sealed record EventHandlerRegistrationError : PlatformError
{
    private EventHandlerRegistrationError(string code, string detail)
        : base(code) => Detail = detail;

    /// <summary>Names the type and handler involved.</summary>
    public string Detail { get; }

    /// <inheritdoc/>
    public override bool IsRetryable => false;

    /// <summary>A second handler registered for an <see cref="EventTypeName"/> already registered. A
    /// product that wants two things to happen writes one handler that does two things.</summary>
    /// <param name="type">The event type name.</param>
    /// <param name="first">The handler already registered.</param>
    /// <param name="second">The handler that tried to register alongside it.</param>
    /// <returns>The error.</returns>
    public static EventHandlerRegistrationError DuplicateHandlerForType(EventTypeName type, Type first, Type second) =>
        new(
            nameof(DuplicateHandlerForType),
            $"Event type '{type}' already has handler '{first.FullName}'; '{second.FullName}' cannot also register for it.");

    /// <summary>A second <see cref="EventTypeName"/> registered for a CLR event type already bound.
    /// Enqueue could not choose which name to stamp.</summary>
    /// <param name="eventType">The CLR event type.</param>
    /// <param name="first">The name already bound.</param>
    /// <param name="second">The name that tried to bind alongside it.</param>
    /// <returns>The error.</returns>
    public static EventHandlerRegistrationError DuplicateNameForEventType(Type eventType, EventTypeName first, EventTypeName second) =>
        new(
            nameof(DuplicateNameForEventType),
            $"CLR event type '{eventType.FullName}' is already bound to name '{first}'; it cannot also bind to '{second}'.");

    /// <summary>The handler's constructor dependencies failed to resolve, checked only in the
    /// dispatching role.</summary>
    /// <param name="handlerType">The handler that could not be constructed.</param>
    /// <param name="detail">What could not be resolved.</param>
    /// <returns>The error.</returns>
    public static EventHandlerRegistrationError HandlerNotConstructible(Type handlerType, string detail) =>
        new(nameof(HandlerNotConstructible), $"Handler '{handlerType.FullName}' could not be constructed: {detail}");

    /// <summary>Registration was attempted after the host was built.</summary>
    /// <param name="type">The registration that arrived late.</param>
    /// <returns>The error.</returns>
    public static EventHandlerRegistrationError RegistryFrozen(EventTypeName type) =>
        new(nameof(RegistryFrozen), $"The event handler registry is frozen; '{type}' cannot be registered.");
}

/// <summary>Why a row could not reach a handler. Unlike <see cref="HandlerError"/>, none of these
/// variants consumes an attempt.</summary>
public sealed record DispatchError : PlatformError
{
    private DispatchError(string code) : base(code) { }

    /// <inheritdoc/>
    public override bool IsRetryable => true;

    /// <summary>No handler is registered for the stored event name.</summary>
    public static DispatchError HandlerUnresolved() => new(nameof(HandlerUnresolved));

    /// <summary>The stored payload could not deserialize into its registered CLR type.</summary>
    public static DispatchError PayloadUndeserializable() => new(nameof(PayloadUndeserializable));

    /// <summary>This host has registered migrations that are not applied.</summary>
    public static DispatchError MigrationsPending() => new(nameof(MigrationsPending));
}
