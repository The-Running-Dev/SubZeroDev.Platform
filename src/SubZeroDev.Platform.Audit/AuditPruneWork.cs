using Microsoft.Extensions.Logging;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Persistence;

namespace SubZeroDev.Platform.Audit;

/// <summary>D5-S32: deletes audit rows older than <c>Platform:Audit:RetentionDays</c>, under a lease,
/// oldest first, in bounded statements. Registered only when that setting is present, so a host with
/// no value keeps every row forever, as it did before (I-U11). Its own work rather than a fourth target
/// of Persistence's prune, because Persistence knows nothing of Audit's table.</summary>
internal sealed class AuditPruneWork(
    AuditStore store,
    IProviderCapability capability,
    ILeaseManager leaseManager,
    PlatformOptions options,
    IClock clock,
    ILogger<AuditPruneWork> logger) : IBackgroundWork
{
    /// <summary>How many rows one statement may delete. Fixed, not a setting: small enough that one
    /// write transaction never holds SQLite's single write lock for long.</summary>
    internal const int BatchSize = 500;

    /// <summary>How many statements one tick may run, so a first tick over years of backlog still
    /// ends; whatever remains is taken by the next tick.</summary>
    internal const int MaximumStatementsPerTick = 100;

    internal static BackgroundWorkName WorkName { get; } = new("platform.audit.prune");

    public BackgroundWorkName Name => WorkName;

    public HostRoles Roles => HostRoles.Worker;

    // Not a setting: retention is days wide, so an hourly tick is ample headroom (as for PruneWork).
    public TimeSpan Interval { get; } = TimeSpan.FromHours(1);

    public bool RequiresLease => true;

    public async Task TickAsync(CancellationToken cancellationToken)
    {
        if (options.Audit.RetentionDays is not { } retentionDays)
        {
            return;
        }

        var acquired = await leaseManager.AcquireAsync(Name, cancellationToken).ConfigureAwait(false);
        if (!acquired.IsSuccess)
        {
            if (acquired.Error.Code != nameof(LeaseError.Held))
            {
                logger.LogWarning("Audit prune could not acquire its lease: {Code}.", acquired.Error.Code);
            }

            return;
        }

        await using var lease = acquired.Value;
        var cutoff = clock.UtcNow - TimeSpan.FromDays(retentionDays);
        var deleted = 0;

        for (var statement = 0; statement < MaximumStatementsPerTick; statement++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var batch = await DeleteBatchAsync(cutoff, cancellationToken).ConfigureAwait(false);
            if (batch is not { } count)
            {
                break;
            }

            deleted += count;
            if (count < BatchSize)
            {
                break;
            }
        }

        if (deleted > 0)
        {
            logger.LogInformation(
                "Audit prune deleted {Count} rows older than {Cutoff:O}.", deleted, cutoff);
        }
    }

    /// <summary>One statement in its own write transaction — never an ambient one, so a prune can
    /// never be rolled back by, or roll back, anyone else's work. Null when the statement failed, which
    /// is logged and ends the tick; readiness is not affected.</summary>
    private async Task<int?> DeleteBatchAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        var opened = await capability.BeginAsync(TransactionIntent.Write, cancellationToken).ConfigureAwait(false);
        if (!opened.IsSuccess)
        {
            logger.LogWarning("Audit prune could not open a transaction: {Code}.", opened.Error.Code);
            return null;
        }

        var transaction = opened.Value;
        try
        {
            var count = await store
                .DeleteOlderThanAsync(transaction.Connection, transaction.Transaction, cutoff, BatchSize, cancellationToken)
                .ConfigureAwait(false);
            await transaction.Transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return count;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await RollBackAsync(transaction).ConfigureAwait(false);
            return null;
        }
        catch (Exception exception)
        {
            await RollBackAsync(transaction).ConfigureAwait(false);
            logger.LogWarning("Audit prune statement failed: {Code}.", capability.Classify(exception).Code);
            return null;
        }
        finally
        {
            await transaction.Connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task RollBackAsync(IAmbientTransaction transaction)
    {
        try
        {
            await transaction.Transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort: the connection may already be gone.
        }
    }
}
