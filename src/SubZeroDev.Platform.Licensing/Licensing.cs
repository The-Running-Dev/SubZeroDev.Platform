using System.Security.Cryptography;
using SubZeroDev.Platform.Abstractions;

namespace SubZeroDev.Platform.Licensing;

/// <summary>The tier a licence document claims. An opaque stable name: Platform is not a licensor and
/// owns no tier vocabulary beyond the well-known baseline. <c>design/20-contract.md</c>, Types §9.</summary>
/// <param name="Value">The tier's stable name.</param>
public readonly record struct LicenceTier(string Value)
{
    /// <summary>The tier's stable name.</summary>
    public string Value { get; } = Value ?? throw new ArgumentNullException(nameof(Value));

    /// <summary>The tier of an installation with no verified claims. Never absent, never null — a
    /// fresh installation with no stored row resolves to this (S12.5), and so does every fallback
    /// from an <see cref="LicenceVerificationOutcome.Invalid"/> or
    /// <see cref="LicenceVerificationOutcome.Unavailable"/> verification that finds no stored row.</summary>
    public static LicenceTier Community { get; } = new("Community");

    /// <inheritdoc/>
    public override string ToString() => Value;
}

/// <summary>The outcome of one verification. Recorded and logged; never returned to a caller as an
/// error, because verification never fails a host and never fails a request (I-L9, S12.12).
/// <c>design/20-contract.md</c>, Error semantics §7.</summary>
public enum LicenceVerificationOutcome
{
    /// <summary>Signature valid against an accepted key, payload well formed. Writes the record,
    /// subject to the monotonic guard (I-L3).</summary>
    Verified,

    /// <summary>Signature fails, wrong key, or malformed payload. The document is ignored entirely
    /// and never grants a tier (I-L4, S12.4); logged loudly; audited once.</summary>
    Invalid,

    /// <summary>Absent, unreadable, or an I/O error. Stored claims stand; logged; audited once. Kept
    /// separately named from <see cref="Invalid"/> even though the two fall back identically: an
    /// operator seeing "unavailable" reaches for the file, one seeing "invalid" reaches for the key
    /// (S12.6).</summary>
    Unavailable,

    /// <summary>The clock reads earlier than the stored verification instant. Evaluated at the stored
    /// verification instant, which neither extends grace nor expires early (S12.7); logged; audited
    /// once.</summary>
    ClockUnusable,
}

/// <summary>The claims in force. Derived from the stored record; never from the document.
/// <c>design/20-contract.md</c>, Types §9.</summary>
/// <param name="Tier">The tier the verified document claimed.</param>
/// <param name="Features">The features the tier grants.</param>
/// <param name="IssuedAt">When the document was issued.</param>
/// <param name="ExpiresAt">When the licence expires, or null when it never does. Expiry starts
/// grace rather than revoking: the contributor keeps granting until <paramref name="GraceEndsAt"/>.</param>
/// <param name="GraceEndsAt">When grace ends, as computed at verification — never derived from when
/// an error happened, which is what makes stored grace unextendable (I-L2).</param>
/// <param name="VerifiedAt">When this verification ran. The monotonic guard's comparand (I-L3), and
/// the instant a backwards clock is evaluated at (S12.7).</param>
public sealed record LicenceClaims(
    LicenceTier Tier,
    IReadOnlySet<FeatureName> Features,
    DateTimeOffset IssuedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? GraceEndsAt,
    DateTimeOffset VerifiedAt);

/// <summary>One accepted signing key. A deployment supplies an ordered set.
/// <c>design/20-contract.md</c>, Types §9.</summary>
/// <param name="KeyId">The key's id, as the document names it. The stored record names which key
/// verified, so rotation is observable.</param>
/// <param name="PublicKey">The public key. <see cref="RSA"/> and <see cref="ECDsa"/> are the two
/// runtime types verification dispatches on; anything else cannot verify and yields
/// <see cref="LicenceVerificationOutcome.Invalid"/>.</param>
public sealed record LicenceSigningKey(
    string KeyId,
    AsymmetricAlgorithm PublicKey);

/// <summary>Why a licensing operation did not complete. <c>design/20-contract.md</c>, Error semantics
/// §7 — exactly one variant, because the four <see cref="LicenceVerificationOutcome"/> values are
/// outcomes rather than errors.</summary>
public sealed record LicensingError : PlatformError
{
    private LicensingError(string code, bool isRetryable, string detail)
        : base(code)
    {
        IsRetryable = isRetryable;
        Detail = detail;
    }

    /// <summary>Names the cause.</summary>
    public string Detail { get; }

    /// <inheritdoc/>
    public override bool IsRetryable { get; }

    /// <summary>The monotonic guard rejected a stale write: another host has already verified a
    /// document more recently than this one (I-L3, S12.8). Not retryable, and <b>the caller treats
    /// it as success</b> — a newer verification already stands, which is the intended outcome.</summary>
    /// <returns>The error.</returns>
    public static LicensingError SupersededByNewerVerification() =>
        new(
            nameof(SupersededByNewerVerification),
            isRetryable: false,
            "A newer verification already stands for this installation.");
}

/// <summary>What a deployment supplies to Licensing. Accepted signing keys are a <b>required</b>
/// ordered set the consuming host provides — no key is compiled into Platform, because Platform is
/// not a licensor (I-L7, S12.11). Ordered so key rotation needs no flag day.</summary>
/// <remarks><b>There is deliberately no grace member, and none may be added</b> (I-L6, S12.10).
/// Grace comes from the document and defaults to thirty days; a grace window an operator can set is
/// a grace window with no end. Not bound from configuration: a
/// <see cref="LicenceSigningKey.PublicKey"/> is a live <see cref="AsymmetricAlgorithm"/>, so the
/// host registers this instance directly.</remarks>
public sealed record LicensingOptions
{
    /// <summary>Creates the options.</summary>
    /// <param name="acceptedSigningKeys">The accepted keys, in preference order. Required and
    /// non-empty.</param>
    /// <param name="documentPath">Where the licence document is read from. The document is an input
    /// file and is never persisted by Platform.</param>
    public LicensingOptions(IReadOnlyList<LicenceSigningKey> acceptedSigningKeys, string documentPath)
    {
        ArgumentNullException.ThrowIfNull(acceptedSigningKeys);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentPath);

        if (acceptedSigningKeys.Count == 0)
        {
            throw new ArgumentException(
                "At least one accepted signing key must be supplied; Platform compiles none in (I-L7).",
                nameof(acceptedSigningKeys));
        }

        AcceptedSigningKeys = acceptedSigningKeys;
        DocumentPath = documentPath;
    }

    /// <summary>The accepted keys, in preference order.</summary>
    public IReadOnlyList<LicenceSigningKey> AcceptedSigningKeys { get; }

    /// <summary>Where the licence document is read from.</summary>
    public string DocumentPath { get; }
}

/// <summary>The revocation extension point: registered by a consumer, or absent.
/// <c>design/10-design.md</c> § <em>Licensing</em> names it as one of this module's exposed
/// extension points.</summary>
/// <remarks><b>D5 registers this seam and never calls it</b> (I-L5, S12.13). It is consulted on no
/// path a deployment without outbound network takes — never the request path, never startup, never
/// readiness — because the brief's self-hosted assumption is a deployment with no outbound network
/// for its entire lifetime, and a revocation check on any of those three paths turns that deployment
/// into a broken one. A later effort is free to add a caller; this slice deliberately adds
/// none.</remarks>
public interface IRevocationCheck
{
    /// <summary>The check's name, so a deployment can see which one is registered.</summary>
    string Name { get; }

    /// <summary>Whether the named document has been revoked. <b>Never invoked by this slice.</b></summary>
    /// <param name="documentFingerprint">The fingerprint of the verified document.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>Whether the document is revoked.</returns>
    Task<bool> IsRevokedAsync(string documentFingerprint, CancellationToken cancellationToken);
}

/// <summary>A read of the current licence claims, for the shell's licence view
/// (<c>design/20-contract.md</c> § <em>Public surface</em>, "Licensing"). <b>Exposes no verify-now
/// call on the request path</b>, and answers wholly from memory, so an unreachable store cannot
/// change what a request is entitled to.</summary>
public interface ILicenceState
{
    /// <summary>The claims in force, or null when the installation has none — a fresh installation
    /// with no stored row, or a fallback that found none (S12.5).</summary>
    LicenceClaims? Current { get; }

    /// <summary>The tier in force. <see cref="LicenceTier.Community"/> when
    /// <see cref="Current"/> is null; never absent, never null.</summary>
    LicenceTier Tier { get; }

    /// <summary>The outcome of the most recent verification, or null before one has run.</summary>
    LicenceVerificationOutcome? LastOutcome { get; }
}
