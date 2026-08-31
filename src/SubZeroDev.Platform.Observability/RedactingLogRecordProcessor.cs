using OpenTelemetry;
using OpenTelemetry.Logs;
using SubZeroDev.Platform.Core;

namespace SubZeroDev.Platform.Observability;

/// <summary>The OTLP-logs half of the fixed redaction boundary. <see cref="RedactingJsonFormatter"/>
/// covers the local Serilog sinks; this covers the independent OTLP log pipeline
/// <c>PlatformObservabilityExtensions.ConfigureOpenTelemetry</c> wires alongside it, so a value
/// exported to a collector is redacted on the same terms as one written to console or file.</summary>
internal sealed class RedactingLogRecordProcessor : BaseProcessor<LogRecord>
{
    public override void OnEnd(LogRecord logRecord)
    {
        ArgumentNullException.ThrowIfNull(logRecord);

        logRecord.Attributes = Redact(logRecord.Attributes);
        logRecord.FormattedMessage = Redaction.RedactInline(logRecord.FormattedMessage);
        logRecord.Body = Redaction.RedactInline(logRecord.Body);

        if (logRecord.Exception is not null)
        {
            logRecord.Exception = new RedactedException(Redaction.RedactInline(logRecord.Exception.ToString()));
        }
    }

    private static IReadOnlyList<KeyValuePair<string, object?>>? Redact(
        IReadOnlyList<KeyValuePair<string, object?>>? attributes)
    {
        if (attributes is null)
        {
            return attributes;
        }

        List<KeyValuePair<string, object?>>? redacted = null;
        for (var index = 0; index < attributes.Count; index++)
        {
            var attribute = attributes[index];
            if (!Redaction.IsSensitiveKey(attribute.Key))
            {
                redacted?.Add(attribute);
                continue;
            }

            redacted ??= [.. attributes.Take(index)];
            redacted.Add(new KeyValuePair<string, object?>(attribute.Key, Redaction.RedactedValue));
        }

        return redacted ?? attributes;
    }

    // Swaps in for the original exception once its text has passed through RedactInline. Message and
    // ToString are all the OTLP log exporter reads off Exception, and both return the redacted text
    // here rather than the original's, so nothing unredacted rides along on the exception's own
    // stack trace or nested inner-exception text.
    private sealed class RedactedException : Exception
    {
        public RedactedException(string redactedText) : base(redactedText)
        {
        }

        public override string ToString() => Message;
    }
}
