using SubZeroDev.Platform.Core;

namespace SubZeroDev.Platform.RuntimeSettings;

/// <summary>The union of every registered <see cref="ISettingCatalog"/>, built once and frozen before
/// the host serves (I-ST2). The reader looks every definition up here, so the declaration applied is
/// the catalogue's and never whatever a caller constructed.</summary>
internal sealed class SettingCatalogue
{
    private IReadOnlyDictionary<string, SettingDefinition>? frozen;

    /// <summary>Validates every declaration and freezes the catalogue.</summary>
    /// <param name="catalogs">Every registered catalogue, in registration order.</param>
    /// <returns><see langword="null"/> once frozen, or the first defect found.</returns>
    internal SettingCatalogError? Build(IEnumerable<ISettingCatalog> catalogs)
    {
        if (frozen is not null)
        {
            throw new InvalidOperationException("The setting catalogue is built once.");
        }

        var definitions = new Dictionary<string, SettingDefinition>(StringComparer.Ordinal);
        foreach (var catalog in catalogs)
        {
            foreach (var definition in catalog.Declares)
            {
                if (definitions.ContainsKey(definition.Name.Value))
                {
                    return SettingCatalogError.DuplicateSettingName(definition.Name);
                }

                if (Redaction.IsSensitiveKey(definition.Name.Value))
                {
                    return SettingCatalogError.SensitiveSettingName(definition.Name);
                }

                if (definition.Layers.Count == 0)
                {
                    return SettingCatalogError.SettingWithoutLayers(definition.Name);
                }

                if (!definition.DefaultIsValid)
                {
                    return SettingCatalogError.InvalidSettingDefault(definition.Name);
                }

                definitions.Add(definition.Name.Value, definition);
            }
        }

        frozen = definitions;
        return null;
    }

    /// <summary>The frozen declaration of <paramref name="name"/>, or <see langword="null"/> when
    /// none is declared.</summary>
    internal SettingDefinition? Find(SettingName name)
    {
        var definitions = frozen
            ?? throw new InvalidOperationException("The setting catalogue is read only once the host has started.");
        return definitions.GetValueOrDefault(name.Value);
    }
}

/// <summary>Builds the <see cref="SettingCatalogue"/> in the host's <c>StartingAsync</c>, before any
/// hosted service's <c>StartAsync</c> and before the host serves.</summary>
internal sealed class SettingCatalogueStartup(SettingCatalogue catalogue, IEnumerable<ISettingCatalog> catalogs)
    : IStartupRegistration
{
    public StartupRegistrationFailure? Complete() =>
        catalogue.Build(catalogs) is { } error
            ? new StartupRegistrationFailure(error, error.Detail)
            : null;
}
