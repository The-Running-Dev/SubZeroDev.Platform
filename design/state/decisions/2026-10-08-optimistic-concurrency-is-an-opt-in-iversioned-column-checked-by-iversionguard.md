# decision/2026-10-08-optimistic-concurrency-is-an-opt-in-iversioned-column-checked-by-iversionguard
Date: 2026-10-08
Anchor: 2026-10-08 — #60: optimistic concurrency is an opt-in `IVersioned` column checked by `IVersionGuard`
Status: accepted
SupersededBy:
StatedIn: "unit/document/90-decisions § 2026-10-08 — #60: optimistic concurrency is an opt-in `IVersioned` column checked by `IVersionGuard`"

## Claim
Context — [#60](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/60). Two hosts writing the same row during a restart overlap (Automator's API and worker, BarStrad's staff and table) silently overwrote each other. Ben ruled 2026-10-08: opt-in optimistic concurrency.

Chosen — a marker `IVersioned { long Version { get; } }` mapping to a consumer `version` column (non-null, `1` on insert, advanced by exactly one per guarded write). `IVersionGuard` runs the consumer's own guarded statement on the ambient Write transaction and judges the matched-row count: 1 succeeds, 0 is the new non-retryable `TransactionError.StaleVersion` and dooms the unit of work (staged outbox and audit discarded even if the work ignores the result), more than 1 throws `ContractViolation.GuardedWriteNotSingleRow`; outside a Write transaction throws `GuardedWriteOutsideWriteTransaction`. The consumer's statement predicates and advances the version (I-P2). Product boundaries answer 409 `ErrorEnvelope("StaleVersion", correlation)` or a fixed tool failure, naming no version. No Platform migration; adopting tables add the column in their own new migration. The Node game-service keeps its existing CAS.

Rejected — `xmin` or rowversion (provider-native, none on SQLite); a timestamp (clock skew across hosts); a GUID token (no order); SQL-building helpers (an ORM, against d3 2026-08-03); a convention with no Platform code; reusing the retryable `Conflict`; letting the work commit after a stale write; a Hosting-side mapper (Hosting may not reference Persistence); echoing the current version; a test double in `Platform.Testing` (no second consumer yet).

Reversibility — the marker and column are expensive once a consumer adopts them; the error variant is cheap before release; dooming is cheap to relax and expensive to tighten, which is why the strict form was chosen.
