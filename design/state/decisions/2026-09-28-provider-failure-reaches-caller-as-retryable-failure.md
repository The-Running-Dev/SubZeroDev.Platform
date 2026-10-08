# decision/2026-09-28-provider-failure-reaches-caller-as-retryable-failure
Date: 2026-09-28
Anchor: 2026-09-28 — A provider that cannot answer reaches the caller as a retryable failure, not as forbidden
Status: accepted
SupersededBy:
StatedIn: "unit/document/20-contract § 2. Authorization — `SubZeroDev.Platform.Abstractions`", "unit/document/90-decisions § 2026-09-28 — A provider that cannot answer reaches the caller as a retryable failure, not as forbidden"

## Claim
Context — `/align` at f4822a5: the contract (*Public surface* § 3, *Error semantics* § 2) and `10-design.md` *Failure modes* promise a retryable answer when a permission provider cannot answer, but the evaluator discards the provider's error and `AuthorizationDecision` cannot carry it, so the Hosting pipeline answers `PermissionDenied`/403 during a store outage.

Chosen — the code changes to match the documents. A denial reached with a provider unable to answer carries that provider's error on the decision, shaped like `AuditFailure`, and Hosting and Mcp answer it as a retryable failure, not forbidden. The denial still stands for the request. `/spec` declares the member; `/plan` slices it, with a test asserting what the caller receives.

Rejected — rewriting the documents to make a provider failure an ordinary forbidden, which reads an outage as a missing permission; `EvaluateAsync` returning a failure result, which breaks "returns a decision, never a failure result".

Reversibility — cheap until a consumer reads the new member; moderate after.
