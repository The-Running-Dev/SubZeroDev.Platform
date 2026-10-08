using SubZeroDev.Platform.Abstractions;

namespace SubZeroDev.Platform.RuntimeSettings;

/// <summary>Why a setting could not be read or written. <see cref="Detail"/> names the setting and
/// the layer and never a value (<c>design/20-contract.md</c>, Error semantics §15).</summary>
public sealed record SettingError : PlatformError
{
    private SettingError(string code, bool isRetryable, string detail)
        : base(code)
    {
        IsRetryable = isRetryable;
        Detail = detail;
    }

    /// <summary>The setting and the layer concerned. Never a value.</summary>
    public string Detail { get; }

    /// <inheritdoc/>
    public override bool IsRetryable { get; }

    /// <summary>The catalogue does not declare the name, or declares it with another type. A defect in
    /// the calling code.</summary>
    /// <param name="name">The setting asked for.</param>
    /// <returns>The error.</returns>
    public static SettingError NotDeclared(SettingName name) =>
        new(nameof(NotDeclared), isRetryable: false, $"Setting '{name}' is not declared with this type.");

    /// <summary>The declaration does not admit the layer written.</summary>
    /// <param name="name">The setting.</param>
    /// <param name="layer">The layer written.</param>
    /// <returns>The error.</returns>
    public static SettingError LayerNotAdmitted(SettingName name, SettingLayer layer) =>
        new(nameof(LayerNotAdmitted), isRetryable: false, $"Setting '{name}' does not admit the {layer} layer.");

    /// <summary>The layer does not exist for the ambient principal and tenant.</summary>
    /// <param name="name">The setting.</param>
    /// <param name="layer">The layer written.</param>
    /// <returns>The error.</returns>
    public static SettingError LayerUnavailable(SettingName name, SettingLayer layer) =>
        new(
            nameof(LayerUnavailable),
            isRetryable: false,
            $"The {layer} layer of setting '{name}' does not exist for this principal and tenant.");

    /// <summary>The value fails the setting's rule.</summary>
    /// <param name="name">The setting.</param>
    /// <param name="layer">The layer written.</param>
    /// <returns>The error.</returns>
    public static SettingError InvalidValue(SettingName name, SettingLayer layer) =>
        new(nameof(InvalidValue), isRetryable: false, $"The value for the {layer} layer of setting '{name}' fails its rule.");

    /// <summary>The evaluator denied the write's permission. The denial is already audited.</summary>
    /// <param name="name">The setting.</param>
    /// <param name="layer">The layer written.</param>
    /// <returns>The error.</returns>
    public static SettingError PermissionDenied(SettingName name, SettingLayer layer) =>
        new(nameof(PermissionDenied), isRetryable: false, $"Writing the {layer} layer of setting '{name}' was denied.");

    /// <summary>The authorization decision carried a provider or audit failure.</summary>
    /// <param name="name">The setting.</param>
    /// <param name="layer">The layer written.</param>
    /// <returns>The error.</returns>
    public static SettingError AuthorizationUnavailable(SettingName name, SettingLayer layer) =>
        new(
            nameof(AuthorizationUnavailable),
            isRetryable: true,
            $"Writing the {layer} layer of setting '{name}' could not be authorized.");

    /// <summary>A stored value fails to parse or validate under the current declaration. Never
    /// replaced by a lower layer or the default (I-ST9).</summary>
    /// <param name="name">The setting.</param>
    /// <param name="layer">The layer holding the value.</param>
    /// <returns>The error.</returns>
    public static SettingError StoredValueInvalid(SettingName name, SettingLayer layer) =>
        new(
            nameof(StoredValueInvalid),
            isRetryable: false,
            $"The stored {layer} value of setting '{name}' fails its declaration. Set or clear that layer.");

    /// <summary>The store, or the audit write inside the change's transaction, failed. Never replaced
    /// by the default.</summary>
    /// <param name="name">The setting.</param>
    /// <returns>The error.</returns>
    public static SettingError StoreUnavailable(SettingName name) =>
        new(nameof(StoreUnavailable), isRetryable: true, $"The store holding setting '{name}' is unavailable.");
}

/// <summary>Why the setting catalogue could not be built. Raised at startup only, as the inner error
/// of <c>HostStartupError.Registration</c>, and never retryable (I-ST2;
/// <c>design/20-contract.md</c>, Error semantics §9).</summary>
public sealed record SettingCatalogError : PlatformError
{
    private SettingCatalogError(string code, string detail)
        : base(code)
    {
        Detail = detail;
    }

    /// <summary>The setting concerned. Never a value.</summary>
    public string Detail { get; }

    /// <inheritdoc/>
    public override bool IsRetryable => false;

    /// <summary>Two declarations share a name.</summary>
    /// <param name="name">The name.</param>
    /// <returns>The error.</returns>
    public static SettingCatalogError DuplicateSettingName(SettingName name) =>
        new(nameof(DuplicateSettingName), $"Setting '{name}' is declared more than once.");

    /// <summary>A declaration's default fails its own rule.</summary>
    /// <param name="name">The setting.</param>
    /// <returns>The error.</returns>
    public static SettingCatalogError InvalidSettingDefault(SettingName name) =>
        new(nameof(InvalidSettingDefault), $"Setting '{name}' declares a default its own rule rejects.");

    /// <summary>A declaration admits no layer.</summary>
    /// <param name="name">The setting.</param>
    /// <returns>The error.</returns>
    public static SettingCatalogError SettingWithoutLayers(SettingName name) =>
        new(nameof(SettingWithoutLayers), $"Setting '{name}' admits no layer.");

    /// <summary>A name matches the redaction marker set. A secret belongs in startup configuration,
    /// never in a setting.</summary>
    /// <param name="name">The setting.</param>
    /// <returns>The error.</returns>
    public static SettingCatalogError SensitiveSettingName(SettingName name) =>
        new(
            nameof(SensitiveSettingName),
            $"Setting '{name}' matches the redaction marker set. Keep secrets in startup configuration.");
}
