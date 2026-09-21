# decision/2026-08-24-entitlement-is-decided-at-admission-and-carried-with-work
Date: 2026-08-24
Anchor: 2026-08-24 — Entitlement is decided at admission and carried with the work
Status: accepted
SupersededBy:
StatedIn: "unit/document/90-decisions § 2026-08-24 — Entitlement is decided at admission and carried with the work"

## Claim
Context — the brief and `tenancy-billing-licensing.md` both require that expiry degrades features and never touches running or scheduled work — after the recorded thirty-day grace, new paid-feature work is denied while accepted, running and scheduled work continues.

Chosen — a unit of work carries the entitlement decision that admitted it, and execution does not re-check. An entitlement decision is therefore a **value** that can be persisted with a work item, not only a function call.

Rejected — **re-evaluating on each step**, more current and the naive reading of "gate the feature"; rejected because it makes the guarantee depend on where an expiry lands relative to a step boundary, which is a race, and it puts an entitlement read on every step of every background job.

Reversibility — expensive. It decides whether an entitlement decision is a value or a call, and the work item's persisted shape follows from the answer.
