namespace SubZeroDev.Platform.Abstractions;

/// <summary>What a host <em>is</em>. Migrate mode is a one-shot command, not a third role.</summary>
public enum HostRole
{
    /// <summary>Serves HTTP and runs no product background work.</summary>
    Web,

    /// <summary>Owns all background work and serves probes only.</summary>
    Worker,
}

/// <summary>What a background-work registration <em>declares</em>. Separate from
/// <see cref="HostRole"/> because the registration heartbeat declares <see cref="Both"/> while no
/// host is ever both.</summary>
[Flags]
public enum HostRoles
{
    /// <summary>Runs in the web host.</summary>
    Web = 1,

    /// <summary>Runs in the worker host.</summary>
    Worker = 2,

    /// <summary>Runs in both hosts.</summary>
    Both = Web | Worker,
}
