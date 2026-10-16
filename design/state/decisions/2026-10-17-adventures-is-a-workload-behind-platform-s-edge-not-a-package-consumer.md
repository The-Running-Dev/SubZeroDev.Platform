# decision/2026-10-17-adventures-is-a-workload-behind-platform-s-edge-not-a-package-consumer
Date: 2026-10-17
Anchor: 2026-10-17 — #97: Adventures is a workload behind Platform's edge, not a package consumer; the GameEngine host is the outside package consumer (supersedes 2026-10-08)
Status: accepted
SupersededBy:
StatedIn: "unit/document/90-decisions § 2026-10-17 — #97: Adventures is a workload behind Platform's edge, not a package consumer; the GameEngine host is the outside package consumer (supersedes 2026-10-08)"

## Claim
Context — [#97](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/97). The 2026-10-08 entry had Adventures restore the packages from the feed. It cannot: its API is Node (Fastify, `pg`, `node-pg-migrate`), and the packages are .NET. Its own adoption plan, [Adventures#9](https://github.com/The-Running-Dev/SubZeroDev.Adventures/issues/9), already had it as a workload behind Platform's edge with no package reference, the arrangement `workloads/game-edge` and `workloads/game-service` here already demonstrate. One outside .NET consumer does exist: `SubZeroDev.GameEngine`'s static host restores `SubZeroDev.Platform.Hosting` from the feed by `PackageReference` and calls `AddPlatformWebHost()` and nothing else, and its own tests assert `/health/live` and `/health/ready`. The owner ruled 2026-10-17.

Chosen — D3's outside proof has two halves. The package half is the GameEngine host. It restores from the feed and gets health and readiness from the standard registration call alone. Correlation ids and telemetry come from the same call, but its tests do not assert them. Migrations are not exercised, because that host has no persistence. The hosting half is Adventures, served through Platform's edge as a workload (Adventures#9 stage 3). That means separate liveness and readiness, migrations on start under an advisory lock, and an honest answer from the edge when the backend is stopped. The adoption work stays in the Adventures repository, and each gap it finds is still filed here as D4 extraction evidence. The #100 half of the superseded entry is unchanged: Adventures' identity replacement stays evidence only, and no Identity package is created from it.

Rejected — porting Adventures' API to .NET on the packages, which meets #97's original wording but rewrites a live product's persistence, identity and routes and reverses Adventures#9's "nothing here is a rewrite"; closing #97 with D3 proven only by the sample, which leaves a real consumer that is already available unused.

Reversibility — cheap. Nothing in Platform's surface changes. A later port, or another .NET product, can still meet the original wording. Supersedes: 2026-10-08 — "#97, #100: Adventures becomes a hosted consumer; its identity stays evidence", for its #97 half.
