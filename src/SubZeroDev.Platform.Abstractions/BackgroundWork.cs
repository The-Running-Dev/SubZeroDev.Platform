namespace SubZeroDev.Platform.Abstractions;

/// <summary>A unit of scheduled work. The contract is tick-shaped, and Hosting owns the timer that
/// invokes the tick — a loop hiding its own schedule could not be run in the role it declares, and
/// no fake clock drives a real timer.</summary>
public interface IBackgroundWork
{
    /// <summary>The registration's name, unique within the registry.</summary>
    BackgroundWorkName Name { get; }

    /// <summary>The roles whose hosts run this work. Empty is a startup failure: silent
    /// never-running is what this field exists to prevent.</summary>
    HostRoles Roles { get; }

    /// <summary>How often Hosting invokes the tick.</summary>
    TimeSpan Interval { get; }

    /// <summary>Whether the work runs under a named lease. Leased work must be idempotent — a lease
    /// reduces duplicate runs, it does not prevent them.</summary>
    bool RequiresLease { get; }

    /// <summary>One tick: a dispatch pass under its budget, one prune batch, one heartbeat.</summary>
    /// <param name="cancellationToken">Cancelled on shutdown.</param>
    /// <returns>A task that completes when the tick does.</returns>
    Task TickAsync(CancellationToken cancellationToken);
}
