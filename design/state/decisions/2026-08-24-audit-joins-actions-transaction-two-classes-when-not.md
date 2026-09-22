# decision/2026-08-24-audit-joins-actions-transaction-two-classes-when-not
Date: 2026-08-24
Anchor: 2026-08-24 — Audit joins the action's transaction when there is one, and has two classes when there is not
Status: accepted
SupersededBy:
StatedIn: "unit/document/90-decisions § 2026-08-24 — Audit joins the action's transaction when there is one, and has two classes when there is not"

## Claim
Context — the brief requires allowed, denied and failed actions to persist across restart. A denial has no transaction to join; a rolled-back action must not leave an audit row claiming it happened; a committed action must not lack one.

Chosen — a **successful action that wrote state writes its audit row in the same transaction as the state change** — atomic in both directions, which needs no idempotency and no reconciliation, and which is the existing outbox pattern applied to a second writer. A **denial, a read, or a failure that wrote nothing writes its audit row in its own transaction**, after the outcome is known. For the second case only, the record's class decides what an audit-write failure costs: `Required` converts the response to a retryable failure and degrades readiness — authorization denials, shared-resource escapes, membership and ownership changes, entitlement and licence transitions, MCP invocations — while `Recorded` logs, degrades readiness and leaves the response alone.

Rejected — **audit always in its own transaction**, uniform and simple; rejected because a committed state change whose audit write then fails needs an idempotency key on every mutating operation to be retried safely, which is a cost paid on every write to handle a rare failure. **A single class**, in both directions: all-`Required` makes an audit outage a total outage, which is the self-inflicted outage `tenancy-billing-licensing.md` refuses elsewhere; all-`Recorded` makes the brief's durability criterion true only when nothing is wrong, which is not what a security control is for.

Reversibility — cheap. The classes are a per-action declaration and the transaction rule is one code path.
