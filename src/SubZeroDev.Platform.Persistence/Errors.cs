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
