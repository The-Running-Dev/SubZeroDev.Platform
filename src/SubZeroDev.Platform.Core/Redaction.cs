using System.Text.RegularExpressions;

namespace SubZeroDev.Platform.Core;

/// <summary>The fixed, non-injectable redaction boundary. Not configurable: the contract requires
/// one safety boundary every local and OTLP signal, and every audited value, passes through, and an
/// injectable variant would let a consumer weaken it. Public in Core, absent from Observability, and
/// registered in no container — a consumer cannot replace it. Moved from Observability in D5 S3,
/// because the Audit store module and Mcp both need it and neither may reference Observability or
/// each other. See <c>design/d3/90-decisions.md</c>, "S8 telemetry policy is fixed, typed and
/// non-blocking".</summary>
public static class Redaction
{
    /// <summary>What a redacted value becomes, everywhere.</summary>
    public const string RedactedValue = "[REDACTED]";

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

    /// <summary>Value shapes that mark the whole string as a secret regardless of the key it arrived
    /// under — a bearer credential handed to Platform as an audit action, resource type or resource
    /// id, for instance, carries no key at all.</summary>
    private static readonly string[] ValuePrefixes =
    [
        "bearer ",
        "basic ",
        "sk-",
        "ghp_",
        "gho_",
        "github_pat_",
        "akia",
        "xox",
    ];

    /// <summary>Whether a property, attribute, or metric-label key names a sensitive value.</summary>
    /// <param name="key">The key.</param>
    /// <returns><see langword="true"/> when the key matches a sensitive marker.</returns>
    public static bool IsSensitiveKey(string? key)
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
    public static string RedactInline(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        return InlinePattern.Replace(text, match => $"{match.Groups[1].Value}={RedactedValue}");
    }

    /// <summary>Whether a whole caller-controlled value — with no surrounding key — looks like a
    /// secret on its own shape: a bearer credential, a well-known API key prefix, or a JWT's
    /// three-segment form.</summary>
    /// <param name="value">The value.</param>
    /// <returns><see langword="true"/> when the value's shape marks it as a secret.</returns>
    public static bool LooksLikeSecret(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        if (JwtPattern.IsMatch(value))
        {
            return true;
        }

        foreach (var prefix in ValuePrefixes)
        {
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Redacts a whole caller-controlled value when its shape looks like a secret — the
    /// boundary the audit writer passes every caller-controlled string through before it reaches a
    /// sink.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The redacted marker when the value looks like a secret; the value unchanged
    /// otherwise.</returns>
    public static string RedactValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return LooksLikeSecret(value) ? RedactedValue : value;
    }

    private static readonly Regex InlinePattern = new(
        @"(?i)\b(authorization|cookie|password|pwd|secret|token|api[-_]?key|connection[-_]?string|client[-_]?certificate)\b\s*[:=]\s*(""[^""]*""|'[^']*'|[^;,\r\n]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex JwtPattern = new(
        @"^[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}$",
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
