# decision/2026-10-08-a-mirror-past-its-maximum-age-cannot-answer-so-a-mirrored-role-s-revocation-is-bounded
Date: 2026-10-08
Anchor: 2026-10-08 — #264: a mirror past its maximum age cannot answer, so a mirrored role's revocation is bounded
Status: accepted
SupersededBy:
StatedIn: "unit/document/90-decisions § 2026-10-08 — #264: a mirror past its maximum age cannot answer, so a mirrored role's revocation is bounded"

## Claim
Context — [#264](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/264), red-team F3 (`design/redteam/2026-09-25-10-design.md`). The 2026-09-25 grant-source rule made issuer-managed roles reach the evaluator through a consumer's mirror and left the mirror's freshness to the consumer, unbounded, so a role removed at the issuer kept granting until the sync ran, and the revoke-then-deny harness passed because it revoked the mirror's row. The owner ruled: cap the staleness.

Chosen — a provider over a mirror implements `IMirroredPermissionProvider` (Abstractions) and reports the instant its tenant's last successful sync began reading the issuer, written by the consumer's sync with the rows. The evaluator (Core) reads it before the grants; null or older than `Platform:Authorization:MirrorMaximumAge` means the provider cannot answer, recorded as `ProviderUnavailable` naming it on S24's `ProviderFailure`. The maximum is one `[Fingerprinted]` value per host, default 15 minutes, 30 seconds to 1 day; unset means the default, and no value means unbounded. `PermissionProviderHarness.AssertIssuerRevocationDeniesWithinBoundAsync` proves the bound by revoking at the issuer through the host's evaluator and clock. The promise for a mirrored role is "within the configured maximum of its removal at the issuer"; every other grant keeps the next-request promise.

Rejected — the commit instant as the stamp (a revocation during a long read escapes the bound); a Platform-owned stamp or sync (a role store I-A9 forbids, a vendor adapter per issuer); the provider checking its own age (one rule re-implemented per consumer); per-provider maxima (free-text names as key paths; additive later); no default, or required only when a mirror is registered (most hosts mirror nothing); a one-hour default (no better than Entra's token lifetime); unset or a sentinel meaning unbounded (F3 as a default); asking the issuer per request (a vendor API on the request path); answering a stale mirror as forbidden (an outage read as a refusal).

Reversibility — the default and range are cheap; the interface and the stamp's meaning are expensive once consumers implement them; admitting an unbounded maximum reopens #264.
