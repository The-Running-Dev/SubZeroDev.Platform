using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;

namespace SubZeroDev.Platform.Observability;

/// <summary>A fatal condition detected while registering standalone observability. This package
/// cannot use Hosting's <c>PlatformStartupException</c> without reversing the dependency edge, so
/// it exposes its own wrapper. See docs/docs/adr/ADR-008-observability-startup-failure.md.</summary>
public sealed class ObservabilityStartupException : Exception
{
    /// <summary>Creates the exception for invalid standalone observability configuration.</summary>
    /// <param name="error">The failure, carried so its code is stable and enumerable.</param>
    public ObservabilityStartupException(PlatformError error)
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
