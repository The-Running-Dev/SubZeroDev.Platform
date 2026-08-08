using System.Text.RegularExpressions;

namespace SubZeroDev.Platform.Observability;

/// <summary>The fixed, non-injectable redaction boundary. Not configurable: the contract requires
/// one safety boundary every local and OTLP signal passes through, and an injectable variant would
/// let a consumer weaken it. See <c>design/d3/90-decisions.md</c>, "S8 telemetry policy is fixed, typed
/// and non-blocking".</summary>
internal static class Redaction
{
    /// <summary>What a redacted value becomes, everywhere.</summary>
    internal const string RedactedValue = "[REDACTED]";

    /// <summary>Case-insensitive key segments that mark a value as sensitive. Matched after both the
    /// candidate key and each marker are stripped to bare letters and digits, so "Api-Key",
    /// "ApiKey", "api_key" and "Persistence:ConnectionString" all match without a delimiter-aware
    /// path parser.</summary>
    private static readonly string[] Markers =
    [
        "authorization",
        "cookie",
        "password",
        "secret",
        "token",
        "apikey",
        "connectionstring",
        "clientcertificate",
    ];

    /// <summary>Whether a property, attribute, or metric-label key names a sensitive value.</summary>
    /// <param name="key">The key.</param>
    /// <returns><see langword="true"/> when the key matches a sensitive marker.</returns>
    internal static bool IsSensitiveKey(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        var normalized = Normalize(key);
        foreach (var marker in Markers)
        {
            if (normalized.Contains(marker, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Scrubs free text (a rendered message, an exception's text, a span event's
    /// description) for inline <c>key=value</c> or <c>key: value</c> occurrences of a sensitive
    /// marker. A defence-in-depth backstop: Platform's own instrumentation never captures headers,
    /// bodies, SQL parameters or connection strings in the first place, but a provider's own
    /// exception message can still surface one by accident (a broken connection string in a
    /// Postgres/SQLite exception, for instance).</summary>
    /// <param name="text">The text to scrub.</param>
    /// <returns>The scrubbed text, unchanged when nothing matched.</returns>
    internal static string RedactInline(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        return InlinePattern.Replace(text, match => $"{match.Groups[1].Value}={RedactedValue}");
    }

    private static readonly Regex InlinePattern = new(
        @"(?i)\b(authorization|cookie|password|pwd|secret|token|api[-_]?key|connection[-_]?string|client[-_]?certificate)\b\s*[:=]\s*(""[^""]*""|'[^']*'|[^;,\r\n]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static string Normalize(string value)
    {
        Span<char> buffer = value.Length <= 256 ? stackalloc char[value.Length] : new char[value.Length];
        var count = 0;
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                buffer[count++] = char.ToLowerInvariant(character);
            }
        }

        return new string(buffer[..count]);
    }
}
