using System.Text.Json;
using SubZeroDev.Platform.Abstractions;

namespace SubZeroDev.Platform.Mcp;

/// <summary>A tool's name, unique across every producer.</summary>
/// <param name="Value">The stable name.</param>
public readonly record struct ToolName(string Value)
{
    /// <summary>The stable name.</summary>
    public string Value { get; } = Value ?? throw new ArgumentNullException(nameof(Value));

    /// <inheritdoc/>
    public override string ToString() => Value;
}

/// <summary>Which producer supplied a definition.</summary>
/// <param name="Value">The producer's name.</param>
public readonly record struct ToolProducerName(string Value)
{
    /// <summary>The producer's name.</summary>
    public string Value { get; } = Value ?? throw new ArgumentNullException(nameof(Value));

    /// <inheritdoc/>
    public override string ToString() => Value;
}

/// <summary>A tool as its producer supplies it. Carries no exposure: a producer cannot expose
/// itself (design/20-contract.md § Public surface 10 — exposure is a fact configuration decides,
/// never one a producer can assert).</summary>
/// <param name="Name">The tool's name, unique across every producer.</param>
/// <param name="Description">A human-readable description of what the tool does.</param>
/// <param name="ParameterSchema">The tool's parameter schema, as the producer supplies it. A
/// <see cref="JsonElement"/> because a manifest-projecting producer has no .NET method to infer a
/// schema from — a shape that could only derive one by reflection would privilege the other kind
/// of producer.</param>
/// <param name="RequiredPermission">The permission a caller must hold to invoke this tool. Not
/// optional: an unauthenticated or unauthorized tool is expressed as a permission the composition
/// grants, never as an absent check.</param>
/// <param name="RequiredFeature">The paid feature this tool admits new work under, or
/// <see langword="null"/> when the tool admits no new paid-feature work — on the same terms as an
/// endpoint that only reads.</param>
public sealed record ToolDefinition(
    ToolName Name,
    string Description,
    JsonElement ParameterSchema,
    PermissionName RequiredPermission,
    FeatureName? RequiredFeature);

/// <summary>A definition after configuration has decided its exposure. What the frozen catalogue
/// holds.</summary>
/// <param name="Definition">The tool as its producer supplied it.</param>
/// <param name="Producer">The producer that supplied <paramref name="Definition"/>.</param>
/// <param name="IsExposed">Whether configuration exposes this tool. An unexposed registration is
/// still held by the catalogue's internal bookkeeping, but <see cref="IToolCatalogue"/> offers no
/// route to it.</param>
public sealed record ToolRegistration(
    ToolDefinition Definition,
    ToolProducerName Producer,
    bool IsExposed);

/// <summary>Supplies definitions at startup. A manifest-projecting producer and a product-owned
/// fixed-table producer both implement this, and neither is privileged over the other (I-M7).</summary>
public interface IToolProducer
{
    /// <summary>The producer's name, unique among registered producers.</summary>
    ToolProducerName Name { get; }

    /// <summary>Produces every definition this producer supplies. Runs once, at startup, and
    /// nothing calls it again — the catalogue it feeds is frozen afterwards (I-M1).</summary>
    /// <param name="cancellationToken">Cancels the production.</param>
    /// <returns>The definitions this producer supplies.</returns>
    ValueTask<IReadOnlyCollection<ToolDefinition>> ProduceAsync(CancellationToken cancellationToken);
}

/// <summary>The catalogue, frozen after startup.</summary>
public interface IToolCatalogue
{
    /// <summary>Every exposed registration. An unexposed one is not here and is not reachable from
    /// here — there is no member by which a caller could reach it (I-M10).</summary>
    IReadOnlyCollection<ToolRegistration> Exposed { get; }

    /// <summary>Looks up an exposed tool. Unregistered and unexposed both answer
    /// <see langword="false"/> — the two must never be distinguished (I-M4).</summary>
    /// <param name="name">The tool name.</param>
    /// <param name="registration">The registration, when found and exposed.</param>
    /// <returns><see langword="true"/> when <paramref name="name"/> names an exposed tool.</returns>
    bool TryGetExposed(ToolName name, out ToolRegistration registration);
}
