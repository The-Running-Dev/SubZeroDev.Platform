using SubZeroDev.Platform.Abstractions;

namespace SubZeroDev.Platform.Core;

/// <summary>A defect in the module graph. Every variant fails startup rather than first use.</summary>
public sealed record ModuleGraphError : PlatformError
{
    private ModuleGraphError(string code, string detail)
        : base(code) => Detail = detail;

    /// <summary>Names the modules involved, so the operator's next action is obvious.</summary>
    public string Detail { get; }

    /// <inheritdoc/>
    public override bool IsRetryable => false;

    /// <summary>A module declares a dependency no registered module provides.</summary>
    /// <param name="module">The declaring module.</param>
    /// <param name="missing">The dependency nothing provides.</param>
    /// <returns>The error.</returns>
    public static ModuleGraphError MissingDependency(ModuleName module, ModuleName missing) =>
        new(nameof(MissingDependency), $"Module '{module}' depends on '{missing}', which no registered module provides.");

    /// <summary>The dependency graph contains a cycle.</summary>
    /// <param name="cycle">The modules forming the cycle, in order.</param>
    /// <returns>The error.</returns>
    public static ModuleGraphError CyclicDependency(IEnumerable<ModuleName> cycle) =>
        new(nameof(CyclicDependency), $"Module dependency cycle: {string.Join(" -> ", cycle)}.");

    /// <summary>Two modules register the same name.</summary>
    /// <param name="module">The duplicated name.</param>
    /// <returns>The error.</returns>
    public static ModuleGraphError DuplicateModuleName(ModuleName module) =>
        new(nameof(DuplicateModuleName), $"Two modules are registered under the name '{module}'.");
}

/// <summary>A setting that is absent, out of range, or jointly inconsistent with another. Every
/// variant fails startup, because none of them resolves itself.</summary>
public sealed record ConfigurationError : PlatformError
{
    private ConfigurationError(string code, string detail)
        : base(code) => Detail = detail;

    /// <summary>Names the setting and its constraint.</summary>
    public string Detail { get; }

    /// <inheritdoc/>
    public override bool IsRetryable => false;

    /// <summary>A setting with no default is absent.</summary>
    /// <param name="key">The configuration key, as an operator would write it.</param>
    /// <param name="source">The configuration source expected to supply it.</param>
    /// <returns>The error.</returns>
    public static ConfigurationError MissingRequiredSetting(string key, string source) =>
        new(nameof(MissingRequiredSetting), $"Required setting '{key}' is absent. Supply it from {source}.");

    /// <summary>A value is present and outside its permitted range.</summary>
    /// <param name="key">The configuration key.</param>
    /// <param name="constraint">The constraint the value violates.</param>
    /// <returns>The error.</returns>
    public static ConfigurationError InvalidSetting(string key, string constraint) =>
        new(nameof(InvalidSetting), $"Setting '{key}' is invalid: {constraint}.");

    /// <summary>Two settings are individually valid and jointly not.</summary>
    /// <param name="first">The first setting.</param>
    /// <param name="second">The second setting.</param>
    /// <param name="constraint">The joint constraint they violate.</param>
    /// <returns>The error.</returns>
    public static ConfigurationError InconsistentSettings(string first, string second, string constraint) =>
        new(nameof(InconsistentSettings), $"Settings '{first}' and '{second}' are jointly invalid: {constraint}.");

    /// <summary>The SQLite file is open in any mode other than WAL. The contention analysis this
    /// design rests on is false outside WAL, so this aborts startup rather than degrading.</summary>
    /// <param name="path">The SQLite file.</param>
    /// <param name="actualMode">The journal mode the file was found in.</param>
    /// <returns>The error.</returns>
    public static ConfigurationError UnsupportedJournalMode(string path, string actualMode) =>
        new(
            nameof(UnsupportedJournalMode),
            $"SQLite file '{path}' is in journal mode '{actualMode}'; it must be 'wal'.");
}

/// <summary>A rejected health check registration.</summary>
public sealed record HealthCheckRegistrationError : PlatformError
{
    private HealthCheckRegistrationError(string code, string detail)
        : base(code) => Detail = detail;

    /// <summary>Names the check involved.</summary>
    public string Detail { get; }

    /// <inheritdoc/>
    public override bool IsRetryable => false;

    /// <summary>The name is already registered.</summary>
    /// <param name="name">The duplicated name.</param>
    /// <returns>The error.</returns>
    public static HealthCheckRegistrationError DuplicateName(HealthCheckName name) =>
        new(nameof(DuplicateName), $"A health check named '{name}' is already registered.");

    /// <summary>Registration was attempted after the host was built.</summary>
    /// <param name="name">The check that arrived late.</param>
    /// <returns>The error.</returns>
    public static HealthCheckRegistrationError RegistryFrozen(HealthCheckName name) =>
        new(nameof(RegistryFrozen), $"The health check registry is frozen; '{name}' cannot be registered.");

    /// <summary>A check declaring an external dependency was registered as liveness. A database
    /// check reachable from liveness produces a restart loop during the outage it reports.</summary>
    /// <param name="name">The offending check.</param>
    /// <returns>The error.</returns>
    public static HealthCheckRegistrationError ExternalDependencyInLivenessCheck(HealthCheckName name) =>
        new(
            nameof(ExternalDependencyInLivenessCheck),
            $"Health check '{name}' touches an external dependency and cannot be a liveness check.");
}

/// <summary>A rejected background-work registration.</summary>
public sealed record BackgroundWorkRegistrationError : PlatformError
{
    private BackgroundWorkRegistrationError(string code, string detail)
        : base(code) => Detail = detail;

    /// <summary>Names the registration involved.</summary>
    public string Detail { get; }

    /// <inheritdoc/>
    public override bool IsRetryable => false;

    /// <summary>The name is already registered.</summary>
    /// <param name="name">The duplicated name.</param>
    /// <returns>The error.</returns>
    public static BackgroundWorkRegistrationError DuplicateName(BackgroundWorkName name) =>
        new(nameof(DuplicateName), $"Background work named '{name}' is already registered.");

    /// <summary>Registration was attempted after the host was built.</summary>
    /// <param name="name">The registration that arrived late.</param>
    /// <returns>The error.</returns>
    public static BackgroundWorkRegistrationError RegistryFrozen(BackgroundWorkName name) =>
        new(nameof(RegistryFrozen), $"The background work registry is frozen; '{name}' cannot be registered.");

    /// <summary>The registration declares no role, so no host would ever run it.</summary>
    /// <param name="name">The offending registration.</param>
    /// <returns>The error.</returns>
    public static BackgroundWorkRegistrationError NoRoleDeclared(BackgroundWorkName name) =>
        new(nameof(NoRoleDeclared), $"Background work '{name}' declares no role, so no host would ever run it.");
}
