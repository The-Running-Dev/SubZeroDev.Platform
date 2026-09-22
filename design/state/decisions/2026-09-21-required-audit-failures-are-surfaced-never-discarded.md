# decision/2026-09-21-required-audit-failures-are-surfaced-never-discarded
Date: 2026-09-21
Anchor: 2026-09-21 — Required audit failures are surfaced, never discarded
Status: accepted
SupersededBy:
StatedIn: "unit/document/90-decisions § 2026-09-21 — Required audit failures are surfaced, never discarded"

## Claim
Context — `/align` found three paths where a `Required` audit write's failure was dropped: the authorization evaluator's denial record, `SharedRead.Open`'s escape record, and a `SinkRejected` under `Required`, which the writer answered as success. I-C7 and I-C8's tests passed vacuously, written before these paths existed.

Chosen — fix all three and widen the tests. `AuthorizationDecision.AuditFailure` carries the failure and the HTTP pipeline answers 503 with its code, Mcp a retryable tool result; `Open<TEntity>` returns `Result<IDisposable, AuditError>` and stays synchronous because its state is ambient; `SinkRejected` under `Required` surfaces as `SinkUnavailable` naming the sink. I-C7, I-C8 and I-I4's tests were widened to the paths that now exist. Recorded in `20-contract.md` § *Error semantics* 4 and *Types* 2 and 7.

Rejected — amending the doc to permit best-effort denial records, because a denial that cannot be recorded would then be answered as though it were, which is the silent-loss `Required` exists to refuse.

Reversibility — cheap before external consumers; `Open`'s return type is a breaking change after.
