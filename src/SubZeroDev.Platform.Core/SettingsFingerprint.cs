using System.Buffers.Binary;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using SubZeroDev.Platform.Abstractions;

namespace SubZeroDev.Platform.Core;

/// <summary>Computes the digest two hosts compare to detect a live disagreement on a setting that
/// would change what happens to rows they share.</summary>
public interface ISettingsFingerprint
{
    /// <summary>Computes the fingerprint over every <see cref="FingerprintedAttribute"/>-marked
    /// property reachable from <paramref name="options"/>.</summary>
    /// <param name="options">The options to fingerprint.</param>
    /// <returns>64 lowercase hexadecimal characters.</returns>
    string Compute(PlatformOptions options);
}

/// <inheritdoc cref="ISettingsFingerprint"/>
/// <remarks>Specified to the byte in <c>design/d3/20-contract.md</c>, because agreement between two
/// independently-running processes is the whole of its value: a prose description two
/// implementations could follow differently would reintroduce the permanent false mismatch this
/// exists to prevent. Reflection order is never trusted for that reason — the entries are always
/// sorted by path before hashing.</remarks>
internal sealed class SettingsFingerprint : ISettingsFingerprint
{
    /// <summary>The format version, inside the hashed input so a future encoding change is a
    /// visible break rather than a silent one.</summary>
    private static readonly byte[] FormatVersion = "szdfp1"u8.ToArray();

    public string Compute(PlatformOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var entries = new List<(string Path, string? Value)>();
        Walk(options, string.Empty, entries);
        entries.Sort((left, right) => string.CompareOrdinal(left.Path, right.Path));

        using var buffer = new MemoryStream();
        buffer.Write(FormatVersion);

        foreach (var (path, value) in entries)
        {
            WriteLengthPrefixed(buffer, Encoding.UTF8.GetBytes(path));

            if (value is null)
            {
                buffer.WriteByte(0x00);
            }
            else
            {
                buffer.WriteByte(0x01);
                WriteLengthPrefixed(buffer, Encoding.UTF8.GetBytes(value));
            }
        }

        return Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()));
    }

    /// <summary>Walks every property reachable from <paramref name="instance"/>, recursing one
    /// level per nested settings record. <see cref="Type.GetProperties()"/> guarantees no order, so
    /// nothing here depends on the order this collects entries in — the caller sorts by path.</summary>
    private static void Walk(object instance, string prefix, List<(string Path, string? Value)> entries)
    {
        foreach (var property in instance.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            var value = property.GetValue(instance);
            var path = prefix.Length == 0 ? property.Name : $"{prefix}:{property.Name}";

            if (property.GetCustomAttribute<FingerprintedAttribute>() is not null)
            {
                entries.Add((path, FormatValue(value)));
                continue;
            }

            if (value is not null && IsNestedSettings(property.PropertyType))
            {
                Walk(value, path, entries);
            }
        }
    }

    /// <summary>Whether a property's type is a further Platform-authored settings record to recurse
    /// into, rather than a leaf value. Every settings record is a reference type declared beside
    /// <see cref="PlatformOptions"/> itself; every leaf value <see cref="FormatValue"/> knows how to
    /// render is a value type or <see cref="string"/>. Restricting recursion to this namespace (not
    /// merely "any class but string") is deliberate: an unattributed reference-typed leaf from the
    /// base class library — <see cref="Uri"/>, an array — is not a settings record, and walking one
    /// can recurse forever (an <see cref="Array"/>'s own <c>SyncRoot</c> returns the array itself).
    /// A future settings record needs no change here to be reached, since it lives in this
    /// namespace by construction; a leaf of any other type is simply omitted from the fingerprint,
    /// which is correct for a value nothing requires two peers to agree on.</summary>
    private static bool IsNestedSettings(Type type) =>
        type.IsClass && type != typeof(string) && type.Namespace == typeof(PlatformOptions).Namespace;

    private static string? FormatValue(object? value) => value switch
    {
        null => null,
        TimeSpan span => span.ToString("c", CultureInfo.InvariantCulture),
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        bool flag => flag ? "true" : "false",
        Enum named => named.ToString(),
        string text => text,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    private static void WriteLengthPrefixed(Stream stream, byte[] bytes)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)bytes.Length);
        stream.Write(length);
        stream.Write(bytes);
    }
}
