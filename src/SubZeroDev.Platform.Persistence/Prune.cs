using Microsoft.Extensions.Logging;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;

namespace SubZeroDev.Platform.Persistence;

/// <summary>Deletes past their retention window, under a lease, in bounded batches. One
/// registration — <c>PlatformBackgroundWork.Prune</c> — covers all three windows: processed outbox
/// rows, poisoned (and discarded) outbox rows, and dead host registrations — and then the inbox
/// records whose outbox row is gone.</summary>
internal sealed class PruneWork(
    IOutboxStore outboxStore,
    ILeaseManager leaseManager,
    PlatformOptions options,
    IClock clock,
    ILogger<PruneWork> logger) : IBackgroundWork
{
    // No dedicated setting exists for this in the contract — the three retention windows it prunes
    // against are hours to days wide, so an hourly tick leaves ample headroom without a configuration
    // knob nothing else needs.
    public BackgroundWorkName Name => PlatformBackgroundWork.Prune;

    public HostRoles Roles => HostRoles.Worker;

    public TimeSpan Interval { get; } = TimeSpan.FromHours(1);

    public bool RequiresLease => true;

    public async Task TickAsync(CancellationToken cancellationToken)
    {
        var acquired = await leaseManager.AcquireAsync(Name, cancellationToken).ConfigureAwait(false);
        if (!acquired.IsSuccess)
        {
            if (acquired.Error.Code != nameof(LeaseError.Held))
            {
                logger.LogWarning("Prune could not acquire its lease: {Code}.", acquired.Error.Code);
            }

            return;
        }

        await using var lease = acquired.Value;
        var now = clock.UtcNow;

        await PruneOneAsync(PruneTarget.ProcessedOutboxRows, now - options.Outbox.ProcessedRetention, cancellationToken)
            .ConfigureAwait(false);
        await PruneOneAsync(PruneTarget.PoisonedOutboxRows, now - options.Outbox.PoisonedRetention, cancellationToken)
            .ConfigureAwait(false);
        await PruneOneAsync(PruneTarget.DeadHostRegistrations, now - options.HostRegistration.RetentionWindow, cancellationToken)
            .ConfigureAwait(false);

        // Last, so the records of the outbox rows pruned above go in this same pass. Drained until a
        // short batch: orphans appear only when this leased work deletes outbox rows, so none are
        // added while the loop runs and it ends. A record whose outbox row still exists — pending,
        // processed or poisoned — could still be redelivered and is never touched.
        while (await PruneOneAsync(PruneTarget.OrphanedInboxRecords, now, cancellationToken).ConfigureAwait(false)
            == options.Outbox.PruneBatchSize)
        {
        }
    }

    /// <summary>One bounded statement. Returns how many rows it deleted, or null when it failed.</summary>
    private async Task<int?> PruneOneAsync(PruneTarget target, DateTimeOffset olderThan, CancellationToken cancellationToken)
    {
        var pruned = await outboxStore.PruneAsync(target, olderThan, options.Outbox.PruneBatchSize, cancellationToken)
            .ConfigureAwait(false);
        if (!pruned.IsSuccess)
        {
            logger.LogWarning("Prune of {Target} failed: {Code}.", target, pruned.Error.Code);
            return null;
        }

        return pruned.Value;
    }
}
