# decision/2026-10-08-the-engine-owns-the-in-memory-rollback-on-a-failed-persistence-write
Date: 2026-10-08
Anchor: 2026-10-08 — #85: the engine owns the in-memory rollback on a failed persistence write
Status: accepted
SupersededBy:
StatedIn: "unit/document/90-decisions § 2026-10-08 — #85: the engine owns the in-memory rollback on a failed persistence write"

## Claim
Context — [#85](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/85). The engine orders its in-memory update differently on each write path. `submitAction` mutates the held record and `saveGame` sets the save before persistence is awaited, and `attemptCounter` is never rolled back. So a failed `put` leaves memory ahead of the store. The Adventures compare-and-swap on `attempt_counter` rejects a lost write but leaves the rollback "out of this repo's control".

Chosen — the correction is the engine's. Its in-memory record rolls back, or is committed only after the write succeeds, uniformly on all four paths. Carried to [The-Running-Dev/SubZeroDev.GameEngine#580](https://github.com/The-Running-Dev/SubZeroDev.GameEngine/issues/580). The game service takes the fix by upgrading the pinned engine; it adds no workaround of its own. Ben ruled 2026-10-08.

Rejected — a workload-side re-read after a failed write, which is a second copy of the engine's state rules in the host; documenting the ordering as accepted, which leaves a non-retryable `503` whose retry applies the action twice.

Reversibility — cheap — the decision moves no Platform code.
