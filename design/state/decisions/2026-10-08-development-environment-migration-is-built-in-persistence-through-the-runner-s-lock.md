# decision/2026-10-08-development-environment-migration-is-built-in-persistence-through-the-runner-s-lock
Date: 2026-10-08
Anchor: 2026-10-08 — #68: development-environment migration is built, in Persistence, through the runner's lock
Status: accepted
SupersededBy:
StatedIn: "unit/document/90-decisions § 2026-10-08 — #68: development-environment migration is built, in Persistence, through the runner's lock"

## Claim
Context — D3 promised automatic migration in development and left four questions open (D3 contract, *Unresolved* 8). The owner ruled to build it.

Chosen — an internal Persistence lifecycle service added by `AddPlatformPersistence`. In `StartingAsync` it calls `IMigrationRunner.ApplyAsync` when the derived `PlatformOptions.Environment` is `Development`, with no setting. `Failed` and `HistoryTableCollision` fail startup. `Locked` waits a second and retries until free. `Unavailable` starts not-ready.

Rejected — an opt-in or opt-out setting; a hook-based implementation (Persistence is not a module, and it would couple #68 to #63); failing on `Locked` (web and worker started together would always fail one); retrying on `Unavailable` (a silent hang).

Reversibility — cheap — no public surface.
