# decision/2026-10-08-runtime-settings-are-read-through-every-time-with-no-cache-and-no-fingerprint-input
Date: 2026-10-08
Anchor: 2026-10-08 — #58: runtime settings are read through every time, with no cache and no fingerprint input
Status: accepted
SupersededBy:
StatedIn: "unit/document/90-decisions § 2026-10-08 — #58: runtime settings are read through every time, with no cache and no fingerprint input"

## Claim
Context — [#58](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/58). A web host and a worker share one database, and a change on one must reach the other without a restart.

Chosen — each read is one query for at most three rows and the module holds no in-process copy (I-ST7). Writes are last-writer-wins by upsert; the row does not opt in to #60's version column. No runtime value or catalogue entry is a settings-fingerprint input, and no setting overrides a startup option (I-ST8), so the S32 `szdfp4` bump is untouched.

Rejected — a per-instance TTL (a staleness promise hard to withdraw); version stamps (still a query per read); an invalidation bus (a distributed bus is a non-goal); fingerprinting the catalogue (rolling-deploy hosts legitimately differ).

Reversibility — a cache is cheap to add and expensive to remove; opting in to the version column is an additive migration.
