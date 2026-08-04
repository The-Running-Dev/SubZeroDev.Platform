using System.Data.Common;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;

namespace SubZeroDev.Platform.Persistence;

/// <summary>One background work's lease row. An optimisation against duplicate concurrent runs, not
/// a mutual-exclusion primitive — a stalled holder is not fenced, so leased work must be
/// idempotent.</summary>
public sealed record BackgroundWorkLease
{
    /// <summary>The background work this lease guards. Primary key.</summary>
    public required BackgroundWorkName Name { get; init; }

    /// <summary>The instance currently holding it.</summary>
    public required InstanceId Holder { get; init; }

    /// <summary>When this holder acquired it.</summary>
    public required DateTimeOffset AcquiredAt { get; init; }

    /// <summary>When it expires. A second holder may acquire once this passes; the original holder's
    /// next renewal then returns <see cref="LeaseError.Lost"/>.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>Stores lease rows. One implementation, parameterised by <see cref="IProviderCapability"/>
/// only through the instant formatter — the acquire statement's portable conditional-update shape is
/// identical on both providers, the same as <c>HostRegistrationStore</c>'s upsert.</summary>
public interface ILeaseStore
{
    /// <summary>Acquires the lease when it is absent or its current row has already expired.
    /// Atomic: a second concurrent caller either updates the same expired row or finds it just taken,
    /// never both.</summary>
    /// <param name="name">The background work to acquire the lease for.</param>
    /// <param name="holder">The acquiring instance.</param>
    /// <param name="expiresAt">The new expiry to write on success.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns><see langword="true"/> when this call acquired it; <see langword="false"/> when
    /// another holder's lease is still live; or why the write did not complete.</returns>
    Task<Result<bool, TransactionError>> TryAcquireAsync(
        BackgroundWorkName name, InstanceId holder, DateTimeOffset expiresAt, CancellationToken cancellationToken);

    /// <summary>Renews the lease, only while <paramref name="holder"/> still holds it.</summary>
    /// <param name="name">The background work.</param>
    /// <param name="holder">The renewing instance.</param>
    /// <param name="expiresAt">The new expiry to write on success.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns><see langword="true"/> when renewed; <see langword="false"/> when this holder no
    /// longer holds the row — reclaimed or released — or why the write did not complete.</returns>
    Task<Result<bool, TransactionError>> TryRenewAsync(
        BackgroundWorkName name, InstanceId holder, DateTimeOffset expiresAt, CancellationToken cancellationToken);

    /// <summary>Releases the lease, only while <paramref name="holder"/> still holds it. A no-op
    /// when it does not — best-effort, on dispose.</summary>
    /// <param name="name">The background work.</param>
    /// <param name="holder">The releasing instance.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Success, or why the write did not complete.</returns>
    Task<Result<TransactionError>> ReleaseAsync(
        BackgroundWorkName name, InstanceId holder, CancellationToken cancellationToken);
}

/// <inheritdoc cref="ILeaseStore"/>
internal sealed class LeaseStore(
    IUnitOfWork unitOfWork, IAmbientTransactionAccessor ambient, IProviderCapability capability, IClock clock)
    : ILeaseStore
{
    public Task<Result<bool, TransactionError>> TryAcquireAsync(
        BackgroundWorkName name, InstanceId holder, DateTimeOffset expiresAt, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync<bool>(
            TransactionIntent.Write,
            async token =>
            {
                var current = ambient.Current!;
                await using var command = current.Connection.CreateCommand();
                command.Transaction = current.Transaction;

                // ON CONFLICT ... WHERE is the same portable conditional-update shape as
                // HostRegistrationStore's upsert: absent or expired, this row is taken; live, the
                // clause evaluates false and the statement changes nothing.
                command.CommandText = """
                    INSERT INTO platform_background_work_lease (name, holder, acquired_at, expires_at)
                    VALUES (@name, @holder, @acquiredAt, @expiresAt)
                    ON CONFLICT (name) DO UPDATE SET
                        holder = excluded.holder,
                        acquired_at = excluded.acquired_at,
                        expires_at = excluded.expires_at
                    WHERE platform_background_work_lease.expires_at <= @now;
                    """;

                var now = clock.UtcNow;
                AddParameter(command, "@name", name.Value);
                AddParameter(command, "@holder", holder.Value);
                AddParameter(command, "@acquiredAt", capability.FormatInstant(now));
                AddParameter(command, "@expiresAt", capability.FormatInstant(expiresAt));
                AddParameter(command, "@now", capability.FormatInstant(now));

                var changed = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                return changed == 1;
            },
            cancellationToken);

    public Task<Result<bool, TransactionError>> TryRenewAsync(
        BackgroundWorkName name, InstanceId holder, DateTimeOffset expiresAt, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync<bool>(
            TransactionIntent.Write,
            async token =>
            {
                var current = ambient.Current!;
                await using var command = current.Connection.CreateCommand();
                command.Transaction = current.Transaction;
                command.CommandText =
                    "UPDATE platform_background_work_lease SET expires_at = @expiresAt "
                    + "WHERE name = @name AND holder = @holder;";
                AddParameter(command, "@name", name.Value);
                AddParameter(command, "@holder", holder.Value);
                AddParameter(command, "@expiresAt", capability.FormatInstant(expiresAt));
                var changed = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                return changed == 1;
            },
            cancellationToken);

    public Task<Result<TransactionError>> ReleaseAsync(
        BackgroundWorkName name, InstanceId holder, CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync(
            TransactionIntent.Write,
            async token =>
            {
                var current = ambient.Current!;
                await using var command = current.Connection.CreateCommand();
                command.Transaction = current.Transaction;
                command.CommandText = "DELETE FROM platform_background_work_lease WHERE name = @name AND holder = @holder;";
                AddParameter(command, "@name", name.Value);
                AddParameter(command, "@holder", holder.Value);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            },
            cancellationToken);

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

/// <summary>Acquires background-work leases.</summary>
public interface ILeaseManager
{
    /// <summary>Acquires the lease for one background work, for this process, for
    /// <c>Lease:Duration</c>.</summary>
    /// <param name="name">The background work.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A handle to renew or release it, or why it could not be acquired.</returns>
    Task<Result<ILeaseHandle, LeaseError>> AcquireAsync(BackgroundWorkName name, CancellationToken cancellationToken);
}

/// <summary>A held lease. Renewal obliges the holder to abort on failure — the lease reduces
/// duplicate runs, it does not prevent them, and nothing here fences a stalled holder.</summary>
public interface ILeaseHandle : IAsyncDisposable
{
    /// <summary>The background work this handle holds the lease for.</summary>
    BackgroundWorkName Name { get; }

    /// <summary>When the current hold expires.</summary>
    DateTimeOffset ExpiresAt { get; }

    /// <summary>Renews the hold for another <c>Lease:Duration</c>.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Success, or why renewal failed — a failure obliges the caller to abort.</returns>
    Task<Result<LeaseError>> RenewAsync(CancellationToken cancellationToken);
}

/// <inheritdoc cref="ILeaseManager"/>
internal sealed class LeaseManager(ILeaseStore store, PlatformOptions options, InstanceId instance, IClock clock)
    : ILeaseManager
{
    public async Task<Result<ILeaseHandle, LeaseError>> AcquireAsync(
        BackgroundWorkName name, CancellationToken cancellationToken)
    {
        var expiresAt = clock.UtcNow + options.Lease.Duration;
        var acquired = await store.TryAcquireAsync(name, instance, expiresAt, cancellationToken).ConfigureAwait(false);

        if (!acquired.IsSuccess)
        {
            return Result<ILeaseHandle, LeaseError>.Failure(LeaseError.Unavailable());
        }

        return acquired.Value
            ? Result<ILeaseHandle, LeaseError>.Success(new LeaseHandle(store, name, instance, expiresAt, options, clock))
            : Result<ILeaseHandle, LeaseError>.Failure(LeaseError.Held());
    }
}

/// <inheritdoc cref="ILeaseHandle"/>
internal sealed class LeaseHandle(
    ILeaseStore store, BackgroundWorkName name, InstanceId holder, DateTimeOffset expiresAt,
    PlatformOptions options, IClock clock) : ILeaseHandle
{
    private DateTimeOffset _expiresAt = expiresAt;
    private bool _lost;

    public BackgroundWorkName Name => name;

    public DateTimeOffset ExpiresAt => _expiresAt;

    public async Task<Result<LeaseError>> RenewAsync(CancellationToken cancellationToken)
    {
        var newExpiry = clock.UtcNow + options.Lease.Duration;
        var renewed = await store.TryRenewAsync(name, holder, newExpiry, cancellationToken).ConfigureAwait(false);

        if (!renewed.IsSuccess)
        {
            return Result<LeaseError>.Failure(LeaseError.Unavailable());
        }

        if (!renewed.Value)
        {
            _lost = true;
            return Result<LeaseError>.Failure(LeaseError.Lost());
        }

        _expiresAt = newExpiry;
        return Result<LeaseError>.Success();
    }

    public async ValueTask DisposeAsync()
    {
        if (_lost)
        {
            // Someone else holds the row now — nothing of this holder's to release.
            return;
        }

        try
        {
            await store.ReleaseAsync(name, holder, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort: the lease still expires on its own terms if this fails.
        }
    }
}

/// <summary>Creates <c>platform_background_work_lease</c>. Folded into
/// <see cref="PlatformMigrationSource"/> for the same reason <see cref="PlatformOutboxMigration"/>
/// is — a second <see cref="IModuleMigrationSource"/> naming the <c>"Platform"</c> module is exactly
/// the collision the migration runner rejects.</summary>
internal sealed class CreateBackgroundWorkLeaseTable : IModuleMigration
{
    public string Name => "0003_create_background_work_lease";

    public async Task ApplyAsync(DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
    {
        await using var createTable = connection.CreateCommand();
        createTable.Transaction = transaction;
        createTable.CommandText = """
            CREATE TABLE platform_background_work_lease (
                name TEXT NOT NULL,
                holder TEXT NOT NULL,
                acquired_at TEXT NOT NULL,
                expires_at TEXT NOT NULL,
                PRIMARY KEY (name)
            );
            """;
        await createTable.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
