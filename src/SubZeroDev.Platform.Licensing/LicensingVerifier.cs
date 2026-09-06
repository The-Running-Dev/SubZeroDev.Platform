using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SubZeroDev.Platform.Abstractions;
using SubZeroDev.Platform.Core;
using SubZeroDev.Platform.Persistence;

namespace SubZeroDev.Platform.Licensing;

/// <summary>The in-memory current licence state, and the only thing
/// <see cref="LicensingEntitlementContributor"/> reads. Held in memory so entitlement answers are
/// unaffected by the store being down (<c>design/20-contract.md</c> § <em>Entitlement</em>:
/// "Licensing's answers from the record it read at startup"), and so that a thousand requests against
/// one expired licence touch neither the store nor the audit trail (S12.3, S12.14).</summary>
internal sealed class LicenceStateHolder : ILicenceState
{
    private volatile LicenceStateSnapshot _snapshot = new(null, null);

    public LicenceClaims? Current => _snapshot.Claims;

    public LicenceTier Tier => _snapshot.Claims?.Tier ?? LicenceTier.Community;

    public LicenceVerificationOutcome? LastOutcome => _snapshot.Outcome;

    /// <summary>Replaces the snapshot. One reference assignment, so a concurrent reader sees either
    /// the whole previous state or the whole new one and never a half-updated pair.</summary>
    /// <param name="claims">The claims now in force, or null when none are.</param>
    /// <param name="outcome">The outcome that produced them.</param>
    internal void Set(LicenceClaims? claims, LicenceVerificationOutcome outcome) =>
        _snapshot = new LicenceStateSnapshot(claims, outcome);

    private sealed record LicenceStateSnapshot(LicenceClaims? Claims, LicenceVerificationOutcome? Outcome);
}

/// <summary>Runs verification. <b>Not on the request path and not on a timer.</b></summary>
/// <remarks><c>design/10-design.md</c> § <em>Background work</em> is explicit that D5 adds
/// "no retention job, no revocation poll, no licence re-verification timer", and its startup sequence
/// (step 6) is equally explicit that licence verification "runs once". Licence state therefore changes
/// when a host restarts or when a host is asked to re-verify — a timer would be a second writer
/// contending on the one row for a requirement nobody stated.</remarks>
internal interface ILicenceVerifier
{
    /// <summary>Verifies once: reads the document, applies the monotonic guard, updates the in-memory
    /// state, and audits a change in detected state.</summary>
    /// <param name="cancellationToken">Cancels the verification.</param>
    /// <returns>The outcome, or <see cref="LicensingError.SupersededByNewerVerification"/> when the
    /// monotonic guard rejected the write — which <b>the caller treats as success</b>.</returns>
    Task<Result<LicenceVerificationOutcome, LicensingError>> VerifyOnceAsync(CancellationToken cancellationToken);
}

/// <inheritdoc cref="ILicenceVerifier"/>
internal sealed class LicenceVerifier(
    LicensingOptions options,
    LicensingStore store,
    LicenceStateHolder state,
    IUnitOfWork unitOfWork,
    IOperationScopeFactory scopeFactory,
    IAuditWriter auditWriter,
    IClock clock,
    ILogger<LicenceVerifier> logger) : ILicenceVerifier
{
    /// <summary>The last state this process audited, so an outcome is audited <b>once per detection,
    /// not once per check</b> (I-U9, S12.14). An expired licence checked on every request would
    /// otherwise write an audit record per request, which is a denial-of-service against the audit
    /// trail delivered by the audit trail.</summary>
    private string? _lastAuditedSignature;

    public async Task<Result<LicenceVerificationOutcome, LicensingError>> VerifyOnceAsync(
        CancellationToken cancellationToken)
    {
        var reading = LicenceDocumentReader.Read(options.DocumentPath, options.AcceptedSigningKeys);
        var now = clock.UtcNow;

        var stored = await ReadStoredAsync(cancellationToken).ConfigureAwait(false);

        // A clock reading earlier than the stored verification instant is ClockUnusable — but only
        // when the document behind that stored instant is THIS one. The two situations are otherwise
        // indistinguishable, and they mean opposite things:
        //
        //   same document, earlier clock      -> the machine's clock went backwards (S12.7)
        //   different document, earlier clock -> another host verified something newer (S12.8)
        //
        // The stored fingerprint is what tells them apart, so it is the discriminator rather than the
        // instant alone. Getting this wrong would answer ClockUnusable to a host holding a replaced
        // document, which is precisely the case S12.8 says must answer SupersededByNewerVerification.
        var sameDocument = stored is not null
            && string.Equals(stored.DocumentFingerprint, reading.Fingerprint, StringComparison.Ordinal);

        var clockWentBackwards = stored is not null && now < stored.Claims.VerifiedAt;

        if (reading.Outcome != LicenceVerificationOutcome.Verified)
        {
            // Invalid and Unavailable fall back identically — to the stored record if one exists, to
            // Community if none does — and stay separately named so an operator is sent to the right
            // place (S12.6). Neither writes any column, and nor does ClockUnusable (I-L2).
            return await SettleAsync(
                clockWentBackwards ? LicenceVerificationOutcome.ClockUnusable : reading.Outcome,
                stored,
                superseded: false);
        }

        if (sameDocument && clockWentBackwards)
        {
            // Re-verifying the document already stored, on a clock that has gone backwards. Nothing is
            // written: the stored claims already say everything this verification could, and rewriting
            // verified_at from a backwards clock is exactly how grace would come to be extendable.
            return await SettleAsync(LicenceVerificationOutcome.ClockUnusable, stored, superseded: false);
        }

        var claims = new LicenceClaims(
            reading.Tier,
            reading.Features ?? new HashSet<FeatureName>(),
            reading.IssuedAt,
            reading.ExpiresAt,
            // Grace is computed here, at verification, and stored — never derived later from when an
            // error happened. A licence that never expires has no grace end either.
            reading.ExpiresAt?.AddDays(reading.GraceDays),
            now);

        // The monotonic guard (I-L3, S12.8). Compared and written inside one transaction so two hosts
        // racing on the one row cannot both read "mine is newer".
        var written = await unitOfWork.ExecuteAsync(
            TransactionIntent.Write,
            async ct =>
            {
                var current = await store.FindAsync(ct).ConfigureAwait(false);
                if (current is not null && claims.VerifiedAt <= current.Claims.VerifiedAt)
                {
                    return false;
                }

                await store
                    .WriteAsync(new VerifiedLicenceRecord(claims, reading.Fingerprint, reading.KeyId), ct)
                    .ConfigureAwait(false);
                return true;
            },
            cancellationToken).ConfigureAwait(false);

        if (!written.IsSuccess)
        {
            // The store is unreachable. Verification never fails a host and never fails a request
            // (I-L9), so this degrades to the stored claims this process already read.
            logger.LogWarning(
                "Licence verification could not write the verified-licence record: {Code}.",
                written.Error.Code);
            return await SettleAsync(LicenceVerificationOutcome.Unavailable, stored, superseded: false);
        }

        if (!written.Value)
        {
            // A newer verification already stands. The caller treats this as success, and this host
            // converges on the stored row rather than on the document it is holding.
            var newer = await ReadStoredAsync(cancellationToken).ConfigureAwait(false);
            return await SettleAsync(LicenceVerificationOutcome.Verified, newer ?? stored, superseded: true);
        }

        return await SettleAsync(
            LicenceVerificationOutcome.Verified,
            new VerifiedLicenceRecord(claims, reading.Fingerprint, reading.KeyId),
            superseded: false);
    }

    /// <summary>Publishes the new state, logs it, and audits it if the detected state changed.</summary>
    private async Task<Result<LicenceVerificationOutcome, LicensingError>> SettleAsync(
        LicenceVerificationOutcome outcome, VerifiedLicenceRecord? effective, bool superseded)
    {
        state.Set(effective?.Claims, outcome);

        var tier = effective?.Claims.Tier ?? LicenceTier.Community;

        // Invalid is logged loudly; the other three are ordinary operational facts.
        if (outcome == LicenceVerificationOutcome.Invalid)
        {
            logger.LogError(
                "Licence verification outcome {Outcome}: the document at '{Path}' is not signed by an "
                + "accepted key or is malformed, and grants no tier. Resolved tier is {Tier}.",
                nameof(LicenceVerificationOutcome.Invalid),
                options.DocumentPath,
                tier);
        }
        else
        {
            logger.LogInformation(
                "Licence verification outcome {Outcome} for document '{Path}'. Resolved tier is {Tier}.",
                outcome.ToString(),
                options.DocumentPath,
                tier);
        }

        // Awaited, not fire-and-forget: the record must exist before the caller can observe the new
        // state, or "audited once per detection" would be a claim nothing could check. The write's own
        // failure is swallowed inside, because verification never fails a host (I-L9).
        await AuditIfChangedAsync(outcome, tier).ConfigureAwait(false);

        return superseded
            ? Result<LicenceVerificationOutcome, LicensingError>.Failure(
                LicensingError.SupersededByNewerVerification())
            : Result<LicenceVerificationOutcome, LicensingError>.Success(outcome);
    }

    /// <summary>Writes one <c>platform.licensing.state-changed</c> record when, and only when, the
    /// detected state differs from the last one this process audited (S12.14). The signature covers
    /// the outcome and the resolved tier, so <see cref="LicenceVerificationOutcome.Invalid"/> and
    /// <see cref="LicenceVerificationOutcome.Unavailable"/> are distinguishable in the audit trail
    /// even though they fall back identically (S12.6).</summary>
    private async Task AuditIfChangedAsync(LicenceVerificationOutcome outcome, LicenceTier tier)
    {
        var signature = $"{outcome}:{tier.Value}";
        if (string.Equals(_lastAuditedSignature, signature, StringComparison.Ordinal))
        {
            return;
        }

        _lastAuditedSignature = signature;

        try
        {
            // Licence state is installation-wide, not tenant-scoped, and verification runs outside any
            // request, so the scope is opened here rather than adopted.
            using var scope = scopeFactory.Begin(TenantId.Implicit, Principal.LocalSystem);

            await auditWriter.WriteAsync(
                PlatformAuditActions.LicenceStateChanged,
                // The outcome is named in the resource reference because AuditEvent carries no
                // free-form detail member and none may be added (design/20-contract.md, Audit §6) —
                // this is the only place the record can keep Invalid and Unavailable apart.
                new ResourceRef("VerifiedLicence", signature),
                outcome == LicenceVerificationOutcome.Verified ? AuditOutcome.Allowed : AuditOutcome.Failed,
                AuditClass.Required,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not audit the licence state change.");
        }
    }

    private async Task<VerifiedLicenceRecord?> ReadStoredAsync(CancellationToken cancellationToken)
    {
        var result = await unitOfWork.ExecuteAsync(
            TransactionIntent.ReadOnly,
            store.FindAsync,
            cancellationToken).ConfigureAwait(false);

        return result.IsSuccess ? result.Value : null;
    }
}

/// <summary>Runs verification once at startup, and never again on its own.</summary>
/// <remarks><b>Never fails startup</b> (I-L9, S12.12): every failure — an unreadable document, an
/// unreachable store, a misconfigured key — is swallowed here, because a deployment that cannot
/// verify a licence must start, report ready, and serve at its last known tier. This is a plain
/// <see cref="IHostedService"/> rather than a Platform background-work registration precisely because
/// it must not become a recurring tick.</remarks>
internal sealed class LicenceStartupVerification(
    ILicenceVerifier verifier, ILogger<LicenceStartupVerification> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await verifier.VerifyOnceAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Licence verification did not complete at startup; continuing.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
