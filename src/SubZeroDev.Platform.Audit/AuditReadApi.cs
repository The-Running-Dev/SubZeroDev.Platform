using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Persistence;

namespace SubZeroDev.Platform.Audit;

/// <summary>Why a read of the audit record did not complete. The three reads below never refuse for
/// business reasons — an empty range is an empty list, not an error — so this carries exactly one
/// variant, for the store being unreachable.</summary>
public sealed record AuditReadError : PlatformError
{
    private AuditReadError(string code, bool isRetryable, string detail)
        : base(code)
    {
        IsRetryable = isRetryable;
        Detail = detail;
    }

    /// <summary>Names the cause.</summary>
    public string Detail { get; }

    /// <inheritdoc/>
    public override bool IsRetryable { get; }

    /// <summary>The store could not be read.</summary>
    /// <returns>The error.</returns>
    public static AuditReadError Unavailable() =>
        new(nameof(Unavailable), isRetryable: true, "The audit record could not be read.");
}

/// <summary>The audit record's read API — scoped by tenant, instant range, actor and correlation
/// (<c>design/20-contract.md</c>, Public surface §10). <b>Exposes no update and no delete</b>: there
/// is no member here, or anywhere in this module's public surface, that could apply one (S13.4).</summary>
public interface IAuditReadApi
{
    /// <summary>Every record naming <paramref name="tenant"/> whose <c>OccurredAt</c> falls within
    /// <paramref name="from"/> and <paramref name="to"/>, inclusive, ordered by
    /// <c>OccurredAt</c>.</summary>
    Task<Result<IReadOnlyList<AuditEvent>, AuditReadError>> ByTenantAsync(
        TenantId tenant, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);

    /// <summary>Every record sharing <paramref name="correlation"/>, ordered by
    /// <c>OccurredAt</c>.</summary>
    Task<Result<IReadOnlyList<AuditEvent>, AuditReadError>> ByCorrelationAsync(
        CorrelationId correlation, CancellationToken cancellationToken);

    /// <summary>Every record naming <paramref name="actor"/> as its actor subject whose
    /// <c>OccurredAt</c> falls within <paramref name="from"/> and <paramref name="to"/>, inclusive,
    /// ordered by <c>OccurredAt</c>.</summary>
    Task<Result<IReadOnlyList<AuditEvent>, AuditReadError>> ByActorAsync(
        PrincipalId actor, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IAuditReadApi"/>
internal sealed class AuditReadApi(
    IUnitOfWork unitOfWork,
    AuditStore store,
    IProviderCapability capability,
    IAmbientTransactionAccessor ambient) : IAuditReadApi
{
    public Task<Result<IReadOnlyList<AuditEvent>, AuditReadError>> ByTenantAsync(
        TenantId tenant, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) =>
        RunAsync(
            "tenant = @tenant AND occurred_at >= @from AND occurred_at <= @to",
            command =>
            {
                AddParameter(command, "@tenant", tenant.ToString());
                AddParameter(command, "@from", capability.FormatInstant(from));
                AddParameter(command, "@to", capability.FormatInstant(to));
            },
            cancellationToken);

    public Task<Result<IReadOnlyList<AuditEvent>, AuditReadError>> ByCorrelationAsync(
        CorrelationId correlation, CancellationToken cancellationToken) =>
        RunAsync(
            "correlation = @correlation",
            command => AddParameter(command, "@correlation", correlation.TraceId),
            cancellationToken);

    public Task<Result<IReadOnlyList<AuditEvent>, AuditReadError>> ByActorAsync(
        PrincipalId actor, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) =>
        RunAsync(
            "actor_subject = @actorSubject AND occurred_at >= @from AND occurred_at <= @to",
            command =>
            {
                AddParameter(command, "@actorSubject", actor.Subject);
                AddParameter(command, "@from", capability.FormatInstant(from));
                AddParameter(command, "@to", capability.FormatInstant(to));
            },
            cancellationToken);

    private async Task<Result<IReadOnlyList<AuditEvent>, AuditReadError>> RunAsync(
        string whereClause,
        Action<System.Data.Common.DbCommand> bind,
        CancellationToken cancellationToken)
    {
        var result = await unitOfWork.ExecuteAsync(
            TransactionIntent.ReadOnly,
            async ct =>
            {
                var current = ambient.Current
                    ?? throw new PlatformContractViolationException(ContractViolation.NoAmbientTransaction());
                return await store.QueryAsync(current.Connection, current.Transaction, whereClause, bind, ct)
                    .ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? Result<IReadOnlyList<AuditEvent>, AuditReadError>.Success(result.Value)
            : Result<IReadOnlyList<AuditEvent>, AuditReadError>.Failure(AuditReadError.Unavailable());
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
