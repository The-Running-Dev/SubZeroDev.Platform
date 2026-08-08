# Decision log — G1 effort

Append-only. The D3 effort's log is archived with its design set at
[`design/d3/90-decisions.md`](d3/90-decisions.md).

### 2026-08-08 — Completed efforts archive to `design/<effort>/`; the active effort owns the root

Context: the G1 effort needs `design/`, but D3's design set occupied it and D3's contract stays
authoritative for the shipped packages — overwriting was not available, and no archive convention
existed because `design/` had only ever held one effort.
Chosen: move a completed effort's design set to `design/<effort>/` (here `design/d3/`), updating
every inbound link in the same commit, including the site's build-time import of `30-slices.md`.
The pipeline always runs against the root paths, so the kit commands need no per-effort
configuration.
Rejected: per-effort subfolders with an empty root — the kit commands assume root paths and would
each need pointing at the active effort. Promoting D3's content into `docs/docs/` before starting
G1 — truer to the source-of-truth chain but a large prerequisite job that gates G1 on editorial
work, and the authority rule ("a contract in `design/` is authoritative for its package") holds
wherever the file lives.
Reversibility: cheap

### 2026-08-08 — G1 is built in this repository, under `workloads/game-service/`

Context: G1 (the hosted Game Engine service) needed a home. The implementation plan describes it
as independent of Platform, and `AGENTS.md` holds that GEaaS is a hosted workload, not what this
repository is — which argued for its own repository.
Chosen: this repository, decided by Ben. Code lives under `workloads/game-service/`, a top-level
tree outside `src/`, so the product/framework boundary and the no-product-reference rule stay
auditable at a glance. The later .NET edge lands beside it.
Rejected: a new repository (`SubZeroDev.GameService`) — declined as unnecessary ceremony for now.
`src/` alongside the packages — interleaves product and framework code and blurs the dependency
direction the build rule enforces. A `samples/`-style tree — understates what G1 is: a product
stage with its own done-criterion, not a framework proof.
Cost, stated rather than hidden: §8.2 of the implementation plan valued the G1 edge as Platform's
first *genuine external* validation, "no cross-repository coupling". An edge living in Platform's
own repository is nearer to framework-authored proof; the byte-identity criterion and the
distributed trace keep their value, the independence claim weakens.
Reversibility: expensive once the workload accumulates history — extraction to its own repository
later is the plugin-contract story again.

### 2026-08-08 — One brief covers both G1 stages: the Node service, then the .NET edge

Context: the split G1 question (Node-only vs. Platform-consuming) was resolved by Ben as a
sequence — thin Node-only service first, the .NET edge in front of it as a fast follow. The brief
could cover the sequence or the first stage only.
Chosen: one brief, both stages. The edge appears as later slices behind an explicit ordering
constraint — the byte-identity proof exists before the edge does. One effort, one decision log,
and the edge's needs inform the transport design from the start.
Rejected: a Node-only brief with the edge as a binding non-goal — cleaner scope, but two pipeline
runs, and the edge's requirements stop informing G1's transport at exactly the moment they are
cheapest to accommodate.
Reversibility: cheap

### 2026-08-08 — The byte-identity proof is two comparisons, not one

Context: the Stage 1 criterion required a hosted run to "serialize byte-identically to the
in-process run" without saying which bytes. `/brief-check` named the ambiguity as the most
load-bearing in the brief: the readings prove different things.
Chosen: both, asserted separately, decided by Ben. The hosted service's own serialization of its
store at the end of the replay, against the in-process run — the engine invariant surviving
hosting. And the projected responses of the two runs against each other — the wire being
deterministic.
Rejected: store serialization alone — proves the invariant but reaches around the wire, so it shows
nothing about what the transport reproduced. Projected responses alone — cheapest, and stays wholly
behind the projection boundary, but proves the projection is stable rather than that engine state
was reproduced byte-for-byte, which is what §5 records as unknown.
Note: the in-process serialization is not a raw-state endpoint, and the non-goal now says so —
building an endpoint to serve it would be one.
Reversibility: cheap

### 2026-08-08 — Both stages are inside G1's done; G2 starts when Stage 1 is green

Context: the brief gave the edge its own done-criteria while calling it a "fast follow" that "does
not gate G2", leaving open whether G1 could close on Stage 1 alone. Distinct from the decision
above that one brief covers both stages — that settled scope, not the closing condition.
Chosen: G1 does not close until the edge criteria are met; G2 may begin the moment Stage 1 is
green. Decided by Ben. Both brief statements stand as written — the edge is in scope, and it is
not a gate on the next effort.
Rejected: Stage 1 closes G1, the edge becoming its own effort — fastest close and cleanest slicing,
but the distributed trace is G1's only evidence that exercises Platform's own packages, and it
would leave with the edge. Both stages strictly ordered with G2 held back — contradicts "the edge
does not gate G2" and would need that line struck.
Reversibility: cheap

### 2026-08-08 — The wire-schema generator lives in `SubZeroDev.ServiceContract`

Context: the brief required the schema be generated from the engine's types per ADR-005 Rule 2, but
not where the generator runs — the workload's build, the contract repository, or a checked-in
artifact refreshed by hand.
Chosen: the generator lives in `SubZeroDev.ServiceContract` and publishes the schema as a
consumable artifact; the workload depends on that. Decided by Ben. It is the only option under
which the criterion "the workload reads the contract from ServiceContract, not a local copy"
asserts anything.
Rejected: generation in the workload's build — cheapest, no new pipeline, Rule 2 still honoured,
but ServiceContract keeps only a document while the workload consumes a copy of its own. A
checked-in artifact refreshed by hand — reviewable diffs and no pipeline, at the cost of an
artifact that can silently fall behind the engine's types.
Cost, stated rather than hidden: a cross-repository release path that does not exist yet, built
inside G1's early slices.
Reversibility: moderate — the generator can move later, but consumers' dependency direction moves
with it.

## Open

- The public site's roadmap renders `design/d3/30-slices.md` (the archived D3 set) since the
  archive move. Whether it should render the active effort's slices instead — or both — is an
  L-track design question, not a G1 one.
