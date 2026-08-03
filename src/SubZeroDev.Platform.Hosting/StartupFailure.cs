using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;

namespace SubZeroDev.Platform.Hosting;

/// <summary>Why a host refused to start. Startup aborts; it never degrades.</summary>
public sealed record HostStartupError : PlatformError
{
    private HostStartupError(string code, string detail, PlatformError? inner)
        : base(code)
    {
        Detail = detail;
        Inner = inner;
    }

    /// <summary>The cause, named so the operator's next action is obvious.</summary>
    public string Detail { get; }

    /// <summary>The wrapped cause, so the inner error's name and constraint survive.</summary>
    public PlatformError? Inner { get; }

    /// <inheritdoc/>
    public override bool IsRetryable => false;

    /// <summary>A setting was absent, invalid, or jointly inconsistent with another.</summary>
    /// <param name="inner">The configuration error.</param>
    /// <returns>The error.</returns>
    public static HostStartupError Configuration(ConfigurationError inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        return new HostStartupError(nameof(Configuration), inner.Detail, inner);
    }

    /// <summary>The module graph could not be resolved.</summary>
    /// <param name="inner">The module graph error.</param>
    /// <returns>The error.</returns>
    public static HostStartupError ModuleGraph(ModuleGraphError inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        return new HostStartupError(nameof(ModuleGraph), inner.Detail, inner);
    }

    /// <summary>A registry rejected a registration.</summary>
    /// <param name="inner">The registry's error.</param>
    /// <param name="detail">What was being registered.</param>
    /// <returns>The error.</returns>
    public static HostStartupError Registration(PlatformError? inner, string detail) =>
        new(nameof(Registration), detail, inner);

    /// <summary>The worker probe port could not be bound. Names the setting, because a silent
    /// fallback port would make the probe surface unfindable on a box running two installations.</summary>
    /// <param name="settingKey">The configuration key that decides the port.</param>
    /// <param name="port">The port that could not be bound.</param>
    /// <returns>The error.</returns>
    public static HostStartupError ProbeBindFailed(string settingKey, int port) =>
        new(
            nameof(ProbeBindFailed),
            $"The worker probe port {port} could not be bound. Set '{settingKey}' to a free port.",
            null);
}

/// <summary>A fatal condition at host build or start. Distinct from
/// <see cref="PlatformContractViolationException"/>, which is a defect at a call site: this is the
/// installation being misconfigured, and it will not resolve itself.</summary>
public sealed class PlatformStartupException : Exception
{
    /// <summary>Creates the exception for a startup failure.</summary>
    /// <param name="error">The failure, carried so its code is stable and enumerable.</param>
    public PlatformStartupException(PlatformError error)
        : base(Describe(error))
    {
        ArgumentNullException.ThrowIfNull(error);
        Error = error;
    }

    /// <summary>The failure, carried so the code is stable and enumerable rather than a message.</summary>
    public PlatformError Error { get; }

    private static string Describe(PlatformError? error) => error switch
    {
        HostStartupError host => $"{host.Code}: {host.Detail}",
        not null => error.Code,
        null => "Platform failed to start.",
    };
}

/// <summary>What a failing request returns. Two fields, and never exception text or payload
/// content: the correlation is what ties this to the log line that does carry the detail.</summary>
/// <param name="Code">A stable error code.</param>
/// <param name="Correlation">The request's correlation identity.</param>
public sealed record ErrorEnvelope(string Code, CorrelationId Correlation);
