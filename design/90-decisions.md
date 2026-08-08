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

## Open

- The public site's roadmap renders `design/d3/30-slices.md` (the archived D3 set) since the
  archive move. Whether it should render the active effort's slices instead — or both — is an
  L-track design question, not a G1 one.
