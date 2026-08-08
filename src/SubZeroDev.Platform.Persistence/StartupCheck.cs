using Microsoft.Extensions.Hosting;
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

    private static string Describe(PlatformError error) =>
        error is ConfigurationError configuration ? $"{configuration.Code}: {configuration.Detail}" : error.Code;
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
