using SubZeroDev.Platform.Abstractions;

namespace SubZeroDev.Platform.Core;

/// <summary>A module's catalogue that is built from the container, validated and frozen in the
/// host's <c>StartingAsync</c>, beside the framework's own registries, so a defect surfaces as
/// <c>HostStartupError.Registration</c> before any <c>StartAsync</c> and before the host serves.
/// Internal: a module cannot reference Hosting, where that error lives, and this seam is not part of
/// the contract. RuntimeSettings' setting catalogue is the one implementation (I-ST2).</summary>
internal interface IStartupRegistration
{
    /// <summary>Builds and freezes the catalogue.</summary>
    /// <returns><see langword="null"/> once frozen, or why it could not be built.</returns>
    StartupRegistrationFailure? Complete();
}

/// <summary>Why an <see cref="IStartupRegistration"/> could not complete.</summary>
/// <param name="Error">The module's own error, carried as the inner error.</param>
/// <param name="Detail">What was being registered.</param>
internal sealed record StartupRegistrationFailure(PlatformError Error, string Detail);
