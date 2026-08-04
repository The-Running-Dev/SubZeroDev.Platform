using Microsoft.Extensions.Logging;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;

namespace SubZeroDev.Platform.Persistence;

/// <summary>Deletes past their retention window, under a lease, in bounded batches. One
/// registration — <c>PlatformBackgroundWork.Prune</c> — covers all three windows: processed outbox
/// rows, poisoned (and discarded) outbox rows, and dead host registrations.</summary>
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
    }

    private async Task PruneOneAsync(PruneTarget target, DateTimeOffset olderThan, CancellationToken cancellationToken)
    {
        var pruned = await outboxStore.PruneAsync(target, olderThan, options.Outbox.PruneBatchSize, cancellationToken)
            .ConfigureAwait(false);
        if (!pruned.IsSuccess)
        {
            logger.LogWarning("Prune of {Target} failed: {Code}.", target, pruned.Error.Code);
        }
    }
}
