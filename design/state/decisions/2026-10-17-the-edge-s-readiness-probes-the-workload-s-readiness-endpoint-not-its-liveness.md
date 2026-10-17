# decision/2026-10-17-the-edge-s-readiness-probes-the-workload-s-readiness-endpoint-not-its-liveness
Date: 2026-10-17
Anchor: 2026-10-17 — The edge's readiness probes the workload's readiness endpoint, not its liveness (supersedes g1 2026-08-08, in part)
Status: accepted
SupersededBy:
StatedIn: "unit/document/90-decisions § 2026-10-17 — The edge's readiness probes the workload's readiness endpoint, not its liveness (supersedes g1 2026-08-08, in part)"

## Claim
Context — G1's "The edge is not ready when the workload is unreachable" chose a readiness check that "probes the workload's liveness endpoint rather than any game operation". G2's S10 ("The edge asks the right question", 2026-08-19) changed the probe to the workload's `/readyz` and renamed `LivenessTimeout` to `ReadinessTimeout`. The reason was G2's durable store: once the store existed, a workload could be alive but unable to serve. S10 was a slice, so no decision entry recorded the change, and the G1 entry still reads as current.

Chosen — the edge's readiness check probes the workload's readiness endpoint and no game operation. The workload's liveness, the edge's own liveness and the rest of the G1 entry are unchanged: the check is `Required`, it touches an external dependency, and the edge starts while the workload is down. If the workload is alive but its store is unreachable, the edge reports not-ready.

Rejected — keeping the G1 probe of the workload's liveness. With a store behind the workload, that would report the edge ready while every forward would fail. That is the "ready while it can serve nothing" case G1 rejected `Degraded` to avoid. Rejected: having the edge probe the store itself. That would give the edge a second dependency it doesn't own, and the workload already answers the question on `/readyz`.

Reversibility — cheap. It is one path and one option name. Supersedes: g1 2026-08-08 — "The edge is not ready when the workload is unreachable", for its probe target.
