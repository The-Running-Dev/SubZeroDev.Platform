using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;

namespace SubZeroDev.Platform.Persistence;

/// <summary>A fatal condition at host start, raised by Persistence's own startup check. Distinct
/// from Hosting's <c>PlatformStartupException</c> — the dependency graph fixes Persistence on
/// Abstractions and Core alone, so Persistence cannot throw Hosting's type without an edge the
/// graph forbids. See design/d3/90-decisions.md, 2026-08-03.</summary>
public sealed class PersistenceStartupException : Exception
{
    /// <summary>Creates the exception for a failed startup precondition.</summary>
    /// <param name="error">The failure, carried so its code is stable and enumerable.</param>
    public PersistenceStartupException(PlatformError error)
        : base(Describe(error))
    {
        ArgumentNullException.ThrowIfNull(error);
        Error = error;
    }

    /// <summary>The failure, carried so the code is stable and enumerable rather than a message.</summary>
    public PlatformError Error { get; }

    private static string Describe(PlatformError error) => error switch
    {
        ConfigurationError configuration => $"{configuration.Code}: {configuration.Detail}",
        MigrationError migration => $"{migration.Code}: {migration.Detail}",
        _ => error.Code,
    };
}

/// <summary>Asserts the provider's startup preconditions — WAL mode on SQLite — before the host
/// starts serving. Runs in <c>StartingAsync</c>, which every hosted lifecycle service runs before
/// any <c>StartAsync</c>, so a bad journal mode aborts before Kestrel binds.</summary>
internal sealed class PersistenceStartupCheck(IProviderCapability capability) : IHostedLifecycleService
{
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        var result = await capability.AssertStartupPreconditionsAsync(cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            throw new PersistenceStartupException(result.Error);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Applies pending migrations at host start when the derived environment is
/// <c>Development</c>, and never otherwise. No setting turns it on or off. It calls the runner and
/// nothing else, so it takes the provider-native migration lock and writes history the one way
/// migrate mode does. It runs in <c>StartingAsync</c>, after <see cref="PersistenceStartupCheck"/>,
/// so it completes before any <c>StartAsync</c>: before module initialization, the first background
/// tick and the listener. It does not seed.</summary>
internal sealed class DevelopmentMigrationStartup(
    PlatformOptions options,
    IMigrationRunner runner,
    ILogger<DevelopmentMigrationStartup> logger) : IHostedLifecycleService
{
    /// <summary>How long a start waits before retrying while another process holds the lock.</summary>
    internal static readonly TimeSpan LockRetryDelay = TimeSpan.FromSeconds(1);

    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        // The derived environment, not a bound setting: Platform:Environment in configuration
        // cannot turn this on.
        if (!string.Equals(options.Environment, Environments.Development, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        while (true)
        {
            var applied = await runner.ApplyAsync(cancellationToken).ConfigureAwait(false);

            if (applied.IsSuccess)
            {
                return;
            }

            switch (applied.Error.Code)
            {
                case nameof(MigrationError.Locked):
                    logger.LogInformation(
                        "Development migration is waiting: another process holds the migration lock.");
                    await Task.Delay(LockRetryDelay, cancellationToken).ConfigureAwait(false);
                    break;

                case nameof(MigrationError.Unavailable):
                    // Starts not-ready rather than failing: the database and pending-migrations
                    // readiness checks report it, and the next start or migrate mode applies it.
                    logger.LogWarning(
                        "Development migration did not run: {Code}. {Detail}",
                        applied.Error.Code,
                        applied.Error.Detail);
                    return;

                default:
                    throw new PersistenceStartupException(applied.Error);
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
