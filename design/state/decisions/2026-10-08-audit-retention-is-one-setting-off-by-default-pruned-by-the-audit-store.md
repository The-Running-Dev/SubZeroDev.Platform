# decision/2026-10-08-audit-retention-is-one-setting-off-by-default-pruned-by-the-audit-store
Date: 2026-10-08
Anchor: 2026-10-08 — #187: audit retention is one setting, off by default, pruned by the Audit store
Status: accepted
SupersededBy:
StatedIn: "unit/document/90-decisions § 2026-10-08 — #187: audit retention is one setting, off by default, pruned by the Audit store"

## Claim
Context — [#187](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/187). D5 selected no retention, so `audit_event` grew without bound; `Prune` existed but covered only outbox rows and host registrations. Ben ruled 2026-10-08: Platform schedules the prune, off by default, set by `Audit:RetentionDays`; no archival or export.

Chosen — `Platform:Audit:RetentionDays` (whole days, 1–36500, `[Fingerprinted]`); absent keeps rows forever. When set, the Audit store registers `platform.audit.prune`: Worker, hourly, leased, at most 500 rows per statement and 100 statements per tick, with a new instant-only index. Not audited; logged. An invalid value fails startup as `Configuration`/`InvalidSetting`. The member moves the fingerprint to `szdfp4`. Archival and export are out of scope.

Rejected — a `PruneTarget` on Persistence's `PruneWork` (Persistence would own the audit table); the module reading `IConfiguration` itself (unfingerprinted); `0` meaning off; per-tenant or per-class retention; a configurable interval or batch size; an audit row per prune; a readiness check on prune failure.

Reversibility — the key name is expensive once operators set it; everything else is cheap.
