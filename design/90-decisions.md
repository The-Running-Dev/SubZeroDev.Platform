# Decision log — G2 effort

Append-only. Newest at the top. The rejected alternatives are the point — without them, every future
session relitigates the same choice.

Completed efforts keep their logs with their design sets:
[`g1/90-decisions.md`](g1/90-decisions.md), [`d3/90-decisions.md`](d3/90-decisions.md).

**This log is effort-local.** `AGENTS.md`, *Decision logging*, decides what belongs here and what
belongs in `docs/docs/adr/`.

### 2026-08-12 — A missing active slices document passes the marker gate with a stated skip

Context: applying the 2026-08-08 archive convention to the G1 set moved `design/30-slices.md` to
`design/g1/`, and `build/Test-SliceStatusMarkers.ps1` threw `FileNotFoundException` on a missing
document. `docs-ci.yml` runs it on every pull request, so the documentation gate would have gone red
from the archive commit until `/slices` writes G2's document. The script was written during G1, when
that file always existed; the D3 archive predates the script and never met this.
Chosen: on the **default** path only, a missing document prints a skip and exits 0. A repository
between `/slices` runs is stage 0 of the pipeline, not a broken repository. An explicitly supplied
`-Path` that does not exist still throws — that is a caller error — and a document that exists but
is malformed still fails. Exercised in both directions before commit: default-missing skips, G1's
nine slices validate, an explicit missing path throws, and a slice with no `**Status:**` line fails.
Rejected: **archiving all five and accepting a red gate meanwhile** — honest, and it makes the
interval visible, but it trains everyone to ignore a red gate, which is the expensive habit and
outlasts the interval. **Leaving `30-slices.md` in the root while its four siblings archive** — keeps
the gate green with no code change, at the cost of a split set where nothing tells a reader which
effort the root's slices describe.
Reversibility: cheap

---

## Open

_(nothing staged)_

---

## Index — decisions whose home is elsewhere

Reasoning, consequences and rejected alternatives live in the linked document, never here —
*Single ownership* in `AGENTS.md`. Effort-scoped decisions from completed efforts live in their
archive's own index; the ADR rows here are the permanent ones every effort inherits.

| Decision | Home |
|---|---|
| G2's durable stores live in the Node workload end to end; Platform's Persistence package gains no consumer | [`00-brief.md`](00-brief.md) |
| Compare-and-swap is proven at one instance and at two, asserted separately | [`00-brief.md`](00-brief.md) |
| G2 delivers one change into the engine: a conflict outcome distinguishable from a storage outage | [`00-brief.md`](00-brief.md) |
| §6.1 names `savedAtSeq` where the evidence says sessions version on `attemptCounter`; logged, resolved in `/design` | [`00-brief.md`](00-brief.md) |
| Session lifecycle is admitted to G2 rather than deferred again | [`00-brief.md`](00-brief.md) |
| Adventures is the reference implementation for G2 and G3, not a source this effort copies from | [`g1/90-decisions.md`](g1/90-decisions.md), 2026-08-09 |
| Completed efforts archive to `design/<effort>/`; the active effort owns the root | [`g1/90-decisions.md`](g1/90-decisions.md), 2026-08-08 |
| SkyNet HR is a second hosted workload; the edge becomes a Platform package | [ADR-007](../docs/docs/adr/ADR-007-second-hosted-workload.md) |
| Platform is a framework plus optional application modules | [ADR-006](../docs/docs/adr/ADR-006-application-modules.md) |
| Boundary contracts are projected, not authored; they get their own repository | [ADR-005](../docs/docs/adr/ADR-005-service-contract.md) |
| Platform is built in-house, with ABP as an architecture reference | [ADR-004](../docs/docs/adr/ADR-004-framework-build-not-adopt.md) |
| Package scope is per-registry, not one global name | [ADR-003](../docs/docs/adr/ADR-003-package-scopes-and-registries.md) |
| Platform is .NET, and the product boundary is a process boundary | [ADR-002](../docs/docs/adr/ADR-002-implementation-technology.md) |
| `SubZeroDev.Platform` is the framework, not the game product | [ADR-001](../docs/docs/adr/ADR-001-platform-identity.md) |
