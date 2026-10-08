# decision/2026-09-28-provider-failure-carried-normalised-first-registered
Date: 2026-09-28
Anchor: 2026-09-28 — A provider failure is carried normalised, first-registered, and behind an audit failure
Status: accepted
SupersededBy:
StatedIn: "unit/document/20-contract § 2. Authorization — `SubZeroDev.Platform.Abstractions`", "unit/document/90-decisions § 2026-09-28 — A provider failure is carried normalised, first-registered, and behind an audit failure"

## Claim
Context — `/spec` declaring `AuthorizationDecision.ProviderFailure`: the 2026-09-28 retryable-failure decision left open what the member carries for a variant other than `ProviderUnavailable`, which provider it names when several fail, and which answer wins alongside `AuditFailure`.

Chosen — `ProviderFailure` is always `ProviderUnavailable` naming the provider as registered, names the first failing provider in registry order, and yields to `AuditFailure` when both are set. `PermissionDenied` requires every provider to have answered; a provider returns an error only when it could not answer.

Rejected — carrying the provider's error as returned, which lets a non-retryable variant reach a caller that must answer retryable and lets a provider name another; a collection of failing providers, which changes no answer and departs from the `AuditFailure` shape; `ProviderFailure` deciding when both are set, which drops a `Required` write's failure from the response.

Reversibility — cheap until the slice lands; moderate after.
