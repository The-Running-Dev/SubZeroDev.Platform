using System.Globalization;
using System.Text.Json;
using Serilog.Events;
using Serilog.Formatting;

namespace SubZeroDev.Platform.Observability;

/// <summary>Writes one UTF-8 JSON Lines record per event, through the fixed redaction boundary. The
/// same formatter serves both mandatory local sinks (console and file), so the two never diverge in
/// shape or in what they redact.</summary>
internal sealed class RedactingJsonFormatter : ITextFormatter
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
    };

    public void Format(LogEvent logEvent, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(output);

        // Properties are redacted first, and the message is rendered from that redacted set — not
        // rendered raw and then regex-scrubbed — so a placeholder naming a sensitive property (for
        // instance "{Password}") never puts the raw value into the rendered text in the first place.
        // RedactInline still runs over the result as a second pass, for free text a marker key never
        // named (a connection string embedded in an exception message, for instance).
        var redactedProperties = new Dictionary<string, LogEventPropertyValue>(StringComparer.Ordinal);
        foreach (var (name, value) in logEvent.Properties)
        {
            redactedProperties[name] = Redaction.IsSensitiveKey(name)
                ? new ScalarValue(Redaction.RedactedValue)
                : value;
        }

        using var messageWriter = new StringWriter();
        logEvent.MessageTemplate.Render(redactedProperties, messageWriter, CultureInfo.InvariantCulture);

        var record = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["@t"] = logEvent.Timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            ["@l"] = logEvent.Level.ToString(),
            ["@mt"] = logEvent.MessageTemplate.Text,
            ["@m"] = Redaction.RedactInline(messageWriter.ToString()),
        };

        if (logEvent.Exception is not null)
        {
            record["@x"] = Redaction.RedactInline(logEvent.Exception.ToString());
        }

        foreach (var (name, value) in redactedProperties)
        {
            record[name] = Redaction.IsSensitiveKey(name) ? Redaction.RedactedValue : Convert(value);
        }

        output.Write(JsonSerializer.Serialize(record, SerializerOptions));
        output.Write(Environment.NewLine);
    }

    private static object? Convert(LogEventPropertyValue value) => value switch
    {
        ScalarValue scalar => scalar.Value,
        SequenceValue sequence => sequence.Elements.Select(Convert).ToList(),
        StructureValue structure => structure.Properties.ToDictionary(
            property => property.Name,
            property => Redaction.IsSensitiveKey(property.Name) ? Redaction.RedactedValue : Convert(property.Value)),
        DictionaryValue dictionary => dictionary.Elements.ToDictionary(
            element => element.Key.Value?.ToString() ?? string.Empty,
            element => Redaction.IsSensitiveKey(element.Key.Value?.ToString()) ? Redaction.RedactedValue : Convert(element.Value)),
        _ => value.ToString(),
    };
}
