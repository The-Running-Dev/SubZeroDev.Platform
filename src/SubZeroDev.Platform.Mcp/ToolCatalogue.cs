namespace SubZeroDev.Platform.Mcp;

/// <inheritdoc cref="IToolCatalogue"/>
/// <remarks>A holder rather than a value built at construction: the catalogue is registered once
/// into the container, before startup has run any producer, and <see cref="Initialize"/> replaces
/// its content exactly once, from <see cref="McpStartupValidation"/>. One reference assignment, so
/// a concurrent reader sees either the empty catalogue or the whole frozen one, never a
/// half-populated set.</remarks>
internal sealed class ToolCatalogue : IToolCatalogue
{
    private volatile CatalogueSnapshot _snapshot = new(
        new Dictionary<ToolName, ToolRegistration>(),
        []);

    /// <inheritdoc/>
    public IReadOnlyCollection<ToolRegistration> Exposed => _snapshot.Exposed;

    /// <inheritdoc/>
    public bool TryGetExposed(ToolName name, out ToolRegistration registration)
    {
        if (_snapshot.ByName.TryGetValue(name, out var found) && found.IsExposed)
        {
            registration = found;
            return true;
        }

        registration = default!;
        return false;
    }

    /// <summary>Freezes the catalogue's content. Called exactly once, at startup, after every
    /// producer has run and every registration has passed validation (I-M1) — nothing calls this
    /// again afterward, and nothing else in this module's public surface could.</summary>
    /// <param name="registrations">Every registration, exposed and unexposed alike.</param>
    internal void Initialize(IReadOnlyCollection<ToolRegistration> registrations)
    {
        var byName = registrations.ToDictionary(registration => registration.Definition.Name);
        var exposed = registrations.Where(registration => registration.IsExposed).ToList();
        _snapshot = new CatalogueSnapshot(byName, exposed);
    }

    private sealed record CatalogueSnapshot(
        IReadOnlyDictionary<ToolName, ToolRegistration> ByName,
        IReadOnlyCollection<ToolRegistration> Exposed);
}
