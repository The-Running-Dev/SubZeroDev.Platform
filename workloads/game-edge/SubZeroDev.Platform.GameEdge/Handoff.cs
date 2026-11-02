using System.Buffers;
using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using SubZeroDev.Platform.Abstractions;

namespace SubZeroDev.Platform.GameEdge;

/// <summary>The startup check on <see cref="GameEdgeOptions.CredentialMode"/> and
/// <see cref="GameEdgeOptions.Handoff"/>. It reads the raw section rather than the bound options: a
/// missing positional field would otherwise surface as the binder's own exception, which names a
/// constructor parameter rather than the setting.</summary>
internal static class CredentialSettings
{
    private const string ModeKey = "GameEdge:CredentialMode";
    private const string HandoffKey = "GameEdge:Handoff";

    /// <summary>Every breach of the credential-mode rules, each naming its full configuration key.
    /// Empty when the settings are valid.</summary>
    public static IReadOnlyList<string> Validate(IConfigurationSection edge)
    {
        var errors = new List<string>();
        var mode = edge["CredentialMode"];
        var handoff = edge.GetSection("Handoff");

        // Names only: the binder would also take a number, and "0" is not a mode anyone means.
        if (string.IsNullOrWhiteSpace(mode)
            || !char.IsLetter(mode[0])
            || !Enum.TryParse<CredentialMode>(mode, ignoreCase: true, out var parsed))
        {
            errors.Add($"'{ModeKey}' is required and must be '{CredentialMode.Terminate}' or '{CredentialMode.PassThrough}'.");
            return errors;
        }

        if (parsed == CredentialMode.PassThrough)
        {
            if (handoff.Exists())
            {
                errors.Add($"'{HandoffKey}' must not be set when '{ModeKey}' is '{CredentialMode.PassThrough}': the edge signs nothing in that mode.");
            }

            return errors;
        }

        if (!handoff.Exists())
        {
            errors.Add($"'{HandoffKey}' is required when '{ModeKey}' is '{CredentialMode.Terminate}'.");
            return errors;
        }

        foreach (var field in new[] { nameof(HandoffOptions.Issuer), nameof(HandoffOptions.Audience), nameof(HandoffOptions.KeyId), nameof(HandoffOptions.SigningKeyPath) })
        {
            if (string.IsNullOrWhiteSpace(handoff[field]))
            {
                errors.Add($"'{HandoffKey}:{field}' is required.");
            }
        }

        // Whole seconds on the wire, and the workload refuses an assertion whose exp is not after its
        // iat, so anything under one second could never be accepted.
        var lifetimeKey = $"{HandoffKey}:{nameof(HandoffOptions.Lifetime)}";
        if (!TimeSpan.TryParse(handoff[nameof(HandoffOptions.Lifetime)], CultureInfo.InvariantCulture, out var lifetime))
        {
            errors.Add($"'{lifetimeKey}' is required and must be a time span.");
        }
        else if (lifetime < TimeSpan.FromSeconds(1))
        {
            errors.Add($"'{lifetimeKey}' must be at least one second.");
        }

        return errors;
    }
}

/// <summary>Signs the principal assertion the edge forwards in terminate mode
/// (<c>20-contract.md</c>, <i>The handoff (wire)</i>). Holds the private key for the process; the
/// assertion it returns is never logged, traced or counted (invariant 108).</summary>
internal sealed class HandoffSigner : IDisposable
{
    /// <summary>The request header that carries the assertion to the workload.</summary>
    public const string HeaderName = "SubZeroDev-Principal-Assertion";

    private const string P256Oid = "1.2.840.10045.3.1.7";

    private readonly HandoffOptions options;
    private readonly ECDsa key;
    private readonly TimeProvider time;
    private readonly string encodedHeader;
    private readonly Lock signing = new();

    private HandoffSigner(HandoffOptions options, ECDsa key, TimeProvider time)
    {
        this.options = options;
        this.key = key;
        this.time = time;

        var header = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(header))
        {
            writer.WriteStartObject();
            writer.WriteString("alg", "ES256");
            writer.WriteString("kid", options.KeyId);
            writer.WriteString("typ", "szd-principal+jwt");
            writer.WriteEndObject();
        }

        encodedHeader = Base64Url.EncodeToString(header.WrittenSpan);
    }

    /// <summary>Reads the P-256 private key at <see cref="HandoffOptions.SigningKeyPath"/>. Fails with a
    /// message naming the setting, and never the key, when the file cannot be read or holds anything
    /// else.</summary>
    public static HandoffSigner Load(HandoffOptions options, TimeProvider time)
    {
        var key = ECDsa.Create();
        try
        {
            key.ImportFromPem(File.ReadAllText(options.SigningKeyPath));

            // Exporting the private half proves the PEM held one; a public key would import and
            // then fail on the first request.
            var parameters = key.ExportParameters(includePrivateParameters: true);
            CryptographicOperations.ZeroMemory(parameters.D);
            if (parameters.Curve.Oid.Value != P256Oid)
            {
                throw new CryptographicException();
            }
        }
        catch (Exception thrown) when (thrown is IOException or UnauthorizedAccessException
            or ArgumentException or CryptographicException or NotSupportedException)
        {
            key.Dispose();
            throw new InvalidOperationException(
                "'GameEdge:Handoff:SigningKeyPath' must name a readable PEM file holding a P-256 private key.");
        }

        return new HandoffSigner(options, key, time);
    }

    /// <summary>An assertion that <paramref name="principal"/> made this one request.</summary>
    /// <param name="principal">An account principal; its issuer and subject are never empty.</param>
    /// <param name="method">The method, as forwarded.</param>
    /// <param name="pathAndQuery">The path and query, exactly as forwarded.</param>
    /// <param name="correlation">The request's correlation id.</param>
    public string Sign(PrincipalId principal, string method, string pathAndQuery, CorrelationId correlation)
    {
        var issuedAt = time.GetUtcNow().ToUnixTimeSeconds();

        var payload = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(payload))
        {
            writer.WriteStartObject();
            writer.WriteString("iss", options.Issuer);
            writer.WriteString("aud", options.Audience);
            writer.WriteString("pis", principal.Issuer);
            writer.WriteString("psub", principal.Subject);
            writer.WriteString("pkind", nameof(PrincipalKind.Account));
            writer.WriteString("htm", method);
            writer.WriteString("htu", pathAndQuery);
            writer.WriteString("rid", correlation.TraceId);
            writer.WriteNumber("iat", issuedAt);
            writer.WriteNumber("exp", issuedAt + (long)options.Lifetime.TotalSeconds);
            writer.WriteEndObject();
        }

        var signingInput = encodedHeader + "." + Base64Url.EncodeToString(payload.WrittenSpan);

        // IEEE P1363 (r || s) is SignData's default format, and the one JWS requires.
        byte[] signature;
        lock (signing)
        {
            signature = key.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256);
        }

        return signingInput + "." + Base64Url.EncodeToString(signature);
    }

    /// <inheritdoc/>
    public void Dispose() => key.Dispose();
}
