using System.Text.Json;
using Microsoft.Extensions.Hosting;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Hosting;

namespace SubZeroDev.Platform.Mcp;

/// <summary>Runs every registered producer once, validates every definition it supplies, and
/// freezes the catalogue. A plain <see cref="IHostedService"/> rather than
/// <see cref="IHostedLifecycleService"/>: its work belongs in the <c>StartAsync</c> phase, which the
/// generic host runs for every hosted service only after every <c>StartingAsync</c> hook has
/// completed — so <see cref="SubZeroDev.Platform.Core.IPermissionCatalogRegistry"/> is already
/// populated and frozen by <c>PlatformRegistryStartup</c>'s own <c>StartingAsync</c> by the time
/// this runs, and <see cref="IPermissionCatalogRegistry.EnsureDeclared"/> answers against the whole
/// composition rather than whatever had registered first.</summary>
internal sealed class McpStartupValidation(
    IEnumerable<IToolProducer> producers,
    McpOptions options,
    IPermissionCatalogRegistry permissionCatalogs,
    ToolCatalogue catalogue) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var registrations = new List<ToolRegistration>();

        foreach (var producer in producers)
        {
            var definitions = await producer.ProduceAsync(cancellationToken).ConfigureAwait(false);

            foreach (var definition in definitions)
            {
                var sensitiveParameter = FindSensitiveParameter(definition.ParameterSchema);
                if (sensitiveParameter is not null)
                {
                    throw new PlatformStartupException(HostStartupError.SensitiveToolParameter(
                        $"Tool '{definition.Name}' (producer '{producer.Name}') names parameter "
                        + $"'{sensitiveParameter}', which matches the redaction marker set."));
                }

                var declared = permissionCatalogs.EnsureDeclared(definition.RequiredPermission);
                if (!declared.IsSuccess)
                {
                    throw new PlatformStartupException(HostStartupError.UnregisteredPermission(
                        declared.Error,
                        $"Tool '{definition.Name}' (producer '{producer.Name}') requires permission "
                        + $"'{definition.RequiredPermission}', which no registered catalog declares."));
                }

                registrations.Add(new ToolRegistration(
                    definition,
                    producer.Name,
                    options.ExposedTools.Contains(definition.Name)));
            }
        }

        catalogue.Initialize(registrations);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Walks a JSON Schema document's <c>properties</c> (and, for an array, its
    /// <c>items</c>) looking for a key the redaction marker set treats as sensitive. Recurses into
    /// nested object schemas, since a parameter that is itself an object can name a sensitive field
    /// one level down.</summary>
    /// <param name="schema">The schema to search.</param>
    /// <returns>The first sensitive parameter name found, or <see langword="null"/>.</returns>
    private static string? FindSensitiveParameter(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (schema.TryGetProperty("properties", out var properties)
            && properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject())
            {
                if (Redaction.IsSensitiveKey(property.Name))
                {
                    return property.Name;
                }

                var nested = FindSensitiveParameter(property.Value);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }

        if (schema.TryGetProperty("items", out var items))
        {
            var nested = FindSensitiveParameter(items);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }
}
