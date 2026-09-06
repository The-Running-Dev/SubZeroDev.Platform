using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using SubZeroDev.Platform.Abstractions;

namespace SubZeroDev.Platform.Licensing;

/// <summary>The on-disk licence document's envelope. Not a contract-fixed type: no document format is
/// named anywhere in <c>design/</c>, so this shape is Licensing's own.</summary>
/// <remarks>The signed payload travels as base64 of its own UTF-8 bytes rather than as a nested JSON
/// object, so signing and verification agree on the exact byte sequence without either side needing a
/// canonical JSON form — a re-serialisation that reordered a member or changed a number's spelling
/// would otherwise invalidate a document nobody tampered with.</remarks>
/// <param name="KeyId">Which accepted key signed this document.</param>
/// <param name="Payload">Base64 of the signed payload's UTF-8 bytes.</param>
/// <param name="Signature">Base64 of the signature over exactly those bytes.</param>
internal sealed record LicenceDocumentEnvelope(
    [property: JsonPropertyName("keyId")] string? KeyId,
    [property: JsonPropertyName("payload")] string? Payload,
    [property: JsonPropertyName("signature")] string? Signature);

/// <summary>The signed payload: what the licensor claims.</summary>
/// <param name="Tier">The claimed tier.</param>
/// <param name="Features">The features the tier grants.</param>
/// <param name="IssuedAt">When the document was issued.</param>
/// <param name="ExpiresAt">When the licence expires, or null when it never does.</param>
/// <param name="GraceDays">How many days of grace follow expiry. Null means the document names none,
/// and thirty is used (I-L6, S12.10).</param>
internal sealed record LicenceDocumentPayload(
    [property: JsonPropertyName("tier")] string? Tier,
    [property: JsonPropertyName("features")] IReadOnlyList<string>? Features,
    [property: JsonPropertyName("issuedAt")] string? IssuedAt,
    [property: JsonPropertyName("expiresAt")] string? ExpiresAt,
    [property: JsonPropertyName("graceDays")] int? GraceDays);

/// <summary>What reading and verifying one document produced.</summary>
/// <param name="Outcome">The outcome. Never <see cref="LicenceVerificationOutcome.ClockUnusable"/> —
/// that is decided against the stored row, not against the document.</param>
/// <param name="Tier">The verified tier, when <paramref name="Outcome"/> is
/// <see cref="LicenceVerificationOutcome.Verified"/>.</param>
/// <param name="Features">The verified features.</param>
/// <param name="IssuedAt">The verified issue instant.</param>
/// <param name="ExpiresAt">The verified expiry instant.</param>
/// <param name="GraceDays">The grace window the document named, or the thirty-day default.</param>
/// <param name="Fingerprint">SHA-256 of the raw file bytes, hex-encoded.</param>
/// <param name="KeyId">The id of the key that verified the document.</param>
internal sealed record LicenceDocumentReading(
    LicenceVerificationOutcome Outcome,
    LicenceTier Tier = default,
    IReadOnlySet<FeatureName>? Features = null,
    DateTimeOffset IssuedAt = default,
    DateTimeOffset? ExpiresAt = null,
    int GraceDays = LicenceDocumentReader.DefaultGraceDays,
    string Fingerprint = "",
    string KeyId = "");

/// <summary>Reads and verifies the licence document. Every failure mode is one of two outcomes:
/// a file that is absent, unreadable or faults on I/O is
/// <see cref="LicenceVerificationOutcome.Unavailable"/>; anything about the document's own content —
/// an unparseable envelope or payload, a key id no accepted key carries, a signature that does not
/// verify — is <see cref="LicenceVerificationOutcome.Invalid"/>. The two are kept separate because an
/// operator seeing "unavailable" reaches for the file and one seeing "invalid" reaches for the key
/// (S12.6).</summary>
internal static class LicenceDocumentReader
{
    /// <summary>Grace when the document names none. <b>Thirty days, and not a deployment
    /// setting</b> (I-L6, S12.10) — <see cref="LicensingOptions"/> exposes no member that could
    /// change it.</summary>
    internal const int DefaultGraceDays = 30;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Reads the document at <paramref name="path"/> and verifies it against
    /// <paramref name="acceptedKeys"/>.</summary>
    /// <param name="path">The document's path.</param>
    /// <param name="acceptedKeys">The accepted keys, in preference order.</param>
    /// <returns>What reading and verifying produced. Never throws: verification never fails a host
    /// and never fails a request (I-L9, S12.12).</returns>
    internal static LicenceDocumentReading Read(string path, IReadOnlyList<LicenceSigningKey> acceptedKeys)
    {
        byte[] raw;
        try
        {
            raw = File.ReadAllBytes(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            // Absent, unreadable, or an I/O error — the file, not its content.
            return new LicenceDocumentReading(LicenceVerificationOutcome.Unavailable);
        }

        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(raw));

        LicenceDocumentEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<LicenceDocumentEnvelope>(raw, Json);
        }
        catch (JsonException)
        {
            return Invalid(fingerprint);
        }

        if (envelope is null
            || string.IsNullOrWhiteSpace(envelope.KeyId)
            || string.IsNullOrWhiteSpace(envelope.Payload)
            || string.IsNullOrWhiteSpace(envelope.Signature))
        {
            return Invalid(fingerprint);
        }

        byte[] payloadBytes;
        byte[] signature;
        try
        {
            payloadBytes = Convert.FromBase64String(envelope.Payload);
            signature = Convert.FromBase64String(envelope.Signature);
        }
        catch (FormatException)
        {
            return Invalid(fingerprint);
        }

        // The key is chosen by the id the document names, and only among the accepted set. A document
        // naming a key the deployment does not accept is Invalid rather than Unavailable: the file
        // arrived intact and says something the deployment refuses to believe.
        var key = acceptedKeys.FirstOrDefault(
            candidate => string.Equals(candidate.KeyId, envelope.KeyId, StringComparison.Ordinal));

        if (key is null || !Verify(key.PublicKey, payloadBytes, signature))
        {
            return Invalid(fingerprint);
        }

        LicenceDocumentPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<LicenceDocumentPayload>(payloadBytes, Json);
        }
        catch (JsonException)
        {
            return Invalid(fingerprint);
        }

        if (payload is null
            || string.IsNullOrWhiteSpace(payload.Tier)
            || !TryParseInstant(payload.IssuedAt, out var issuedAt))
        {
            return Invalid(fingerprint);
        }

        DateTimeOffset? expiresAt = null;
        if (payload.ExpiresAt is not null)
        {
            if (!TryParseInstant(payload.ExpiresAt, out var parsed))
            {
                return Invalid(fingerprint);
            }

            expiresAt = parsed;
        }

        if (payload.GraceDays is < 0)
        {
            return Invalid(fingerprint);
        }

        var features = (payload.Features ?? [])
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => new FeatureName(name))
            .ToHashSet();

        return new LicenceDocumentReading(
            LicenceVerificationOutcome.Verified,
            new LicenceTier(payload.Tier),
            features,
            issuedAt,
            expiresAt,
            payload.GraceDays ?? DefaultGraceDays,
            fingerprint,
            key.KeyId);
    }

    /// <summary>Signs <paramref name="payloadBytes"/> with <paramref name="privateKey"/>, using the
    /// same algorithm and padding <see cref="Verify"/> checks. Internal so a test can mint a document
    /// the verifier accepts without a second, independently drifting implementation of the format
    /// living in the test assembly.</summary>
    /// <param name="privateKey">The signing key.</param>
    /// <param name="payloadBytes">The exact bytes to sign.</param>
    /// <returns>The signature.</returns>
    internal static byte[] Sign(AsymmetricAlgorithm privateKey, byte[] payloadBytes) => privateKey switch
    {
        RSA rsa => rsa.SignData(payloadBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
        ECDsa ecdsa => ecdsa.SignData(payloadBytes, HashAlgorithmName.SHA256),
        _ => throw new NotSupportedException(
            $"Signing key type '{privateKey.GetType().Name}' is not supported; use RSA or ECDsa."),
    };

    /// <summary>Serialises a payload to the exact bytes that get signed and travel base64-encoded in
    /// the envelope.</summary>
    /// <param name="payload">The payload.</param>
    /// <returns>The bytes.</returns>
    internal static byte[] SerializePayload(LicenceDocumentPayload payload) =>
        JsonSerializer.SerializeToUtf8Bytes(payload, Json);

    /// <summary>Serialises an envelope to the bytes written to disk.</summary>
    /// <param name="envelope">The envelope.</param>
    /// <returns>The bytes.</returns>
    internal static byte[] SerializeEnvelope(LicenceDocumentEnvelope envelope) =>
        JsonSerializer.SerializeToUtf8Bytes(envelope, Json);

    /// <summary>Dispatches on the public key's runtime type. An unsupported type verifies nothing,
    /// which is <see cref="LicenceVerificationOutcome.Invalid"/> rather than an exception — a
    /// misconfigured key must not fail a host (I-L9).</summary>
    private static bool Verify(AsymmetricAlgorithm publicKey, byte[] payloadBytes, byte[] signature)
    {
        try
        {
            return publicKey switch
            {
                RSA rsa => rsa.VerifyData(payloadBytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
                ECDsa ecdsa => ecdsa.VerifyData(payloadBytes, signature, HashAlgorithmName.SHA256),
                _ => false,
            };
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static bool TryParseInstant(string? candidate, out DateTimeOffset instant) =>
        DateTimeOffset.TryParse(
            candidate,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal,
            out instant);

    private static LicenceDocumentReading Invalid(string fingerprint) =>
        new(LicenceVerificationOutcome.Invalid, Fingerprint: fingerprint);
}
