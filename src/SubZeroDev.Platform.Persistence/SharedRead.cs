using SubZeroDev.Platform.Abstractions;

namespace SubZeroDev.Platform.Persistence;

/// <summary>Opens the one modelled cross-tenant read. Read-only, one declared type, audited once per
/// scope.</summary>
public interface ISharedReadScopeFactory
{
    /// <summary>Widens the query filter to "mine, or shared" for <typeparamref name="TEntity"/>
    /// only, for the scope's lifetime. Emits one <see cref="AuditClass.Required"/> audit record when
    /// the scope opens, and does not open at all when that record could not be written.</summary>
    /// <typeparam name="TEntity">The one shareable type this scope widens the filter for.</typeparam>
    /// <returns>A handle that narrows the filter back when disposed, or the audit write's failure
    /// — the retryable failure the class rule makes of it (Error semantics § 4), with the filter
    /// untouched.</returns>
    Result<IDisposable, AuditError> Open<TEntity>() where TEntity : class, IShareable;

    /// <summary>Whether a shared-read scope is currently open for <typeparamref name="TEntity"/>.
    /// Persistence imposes no repository or ORM, so this is the seam a consumer's own query code —
    /// EF's <c>HasQueryFilter</c>, Dapper, raw ADO — consults at model build to decide whether to
    /// widen its own filter.</summary>
    /// <typeparam name="TEntity">The shareable type to check.</typeparam>
    /// <returns><see langword="true"/> if a scope opened for this exact type is currently
    /// open.</returns>
    bool IsOpenFor<TEntity>() where TEntity : class, IShareable;
}

/// <summary>Holds which entity type's shared-read scope is open for the current asynchronous flow.
/// Not static, on the same terms as <see cref="AmbientTransactionState"/>: the ambient value flows
/// with the operation, so overlapping operations on one host never race on shared mutable
/// state, and a scope left undisposed does not survive into a request that did not open it — the
/// next request begins its own asynchronous flow rather than inheriting this one's.</summary>
internal sealed class SharedReadScopeState
{
    private readonly AsyncLocal<Type?> _openFor = new();

    internal Type? OpenFor
    {
        get => _openFor.Value;
        set => _openFor.Value = value;
    }
}

/// <inheritdoc cref="ISharedReadScopeFactory"/>
internal sealed class SharedReadScopeFactory(SharedReadScopeState state, IAuditWriter auditWriter) : ISharedReadScopeFactory
{
    public Result<IDisposable, AuditError> Open<TEntity>() where TEntity : class, IShareable
    {
        // One audit record per scope, not per row (I-T4) — written before the filter widens, so a
        // caller never observes a widened read that was not recorded. Open() stays synchronous
        // because the widened state is an AsyncLocal, and one set inside an async method does not
        // flow back to its caller. An escape that could not be recorded does not open at all, and
        // the filter is left untouched.
        var written = auditWriter.WriteAsync(
            PlatformAuditActions.SharedReadScopeOpened,
            resource: null,
            AuditOutcome.Allowed,
            AuditClass.Required,
            CancellationToken.None).GetAwaiter().GetResult();

        if (!written.IsSuccess)
        {
            return Result<IDisposable, AuditError>.Failure(written.Error);
        }

        var previous = state.OpenFor;
        state.OpenFor = typeof(TEntity);
        return Result<IDisposable, AuditError>.Success(new Scope(state, previous));
    }

    public bool IsOpenFor<TEntity>() where TEntity : class, IShareable => state.OpenFor == typeof(TEntity);

    private sealed class Scope(SharedReadScopeState state, Type? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            state.OpenFor = previous;
        }
    }
}
