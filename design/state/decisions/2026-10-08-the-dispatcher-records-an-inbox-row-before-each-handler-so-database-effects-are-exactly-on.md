# decision/2026-10-08-the-dispatcher-records-an-inbox-row-before-each-handler-so-database-effects-are-exactly-on
Date: 2026-10-08
Anchor: 2026-10-08 — #62: the dispatcher records an inbox row before each handler, so database effects are exactly-once
Status: accepted
SupersededBy:
StatedIn: "unit/document/90-decisions § 2026-10-08 — #62: the dispatcher records an inbox row before each handler, so database effects are exactly-once"

## Claim
Context — [#62](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/62). Outbox delivery is at-least-once and every handler re-implemented its own duplicate check, or none. Ben ruled 2026-10-08: an inbox seam.

Chosen — `platform_inbox (message_id, consumer, tenant, …)` in a new Platform migration `0004_create_inbox`; the consumer is the registration's `EventTypeName`, unique while one handler per type holds. Every dispatch, always on, runs one `INSERT … ON CONFLICT (message_id, consumer) DO NOTHING` inside the handler's write transaction before the handler; 0 rows means a duplicate, which skips the handler, commits nothing, marks the row processed, consumes no attempt and logs at Information. The tenant column is stamped from the outbox row, not in the key. `PruneTarget.OrphanedInboxRecords` deletes records whose outbox row is gone. The guarantee covers in-database effects only; handlers stay idempotent for external side effects. No public inbox API.

Rejected — the handler's CLR name (renames change the key); a new consumer-name parameter; no tenant column (per-tenant purge cannot find rows); tenant in the key; an opt-in; SELECT-then-INSERT (races under READ COMMITTED); recording after the handler; treating a duplicate as failure or poison; a public record-and-check API (no second consumer; Billing owns its receipts); a cascading foreign key (SQLite does not enforce it by default); an age-only prune window; folding the table into `0002` (I-T7); claiming exactly-once delivery.

Reversibility — the tenant column and migration are expensive once shipped; the consumer value is cheap before first release; the rest is cheap.
