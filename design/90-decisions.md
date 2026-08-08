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

### 2026-08-08 — The operation table is authored data over derived types

Context: the brief requires both surfaces to be generated from one operation table, and ADR-005 Rule
2 requires the schema to be generated from the engine's types. Those two do not cover the same
ground, and the design had to say which parts of a row come from where.
Chosen: the row's *shapes* are derived from the engine's published declarations; the row's *names and
routing* — MCP tool name, path segment, and the per-surface narrowings — are authored, because
nothing in `SessionStore`'s declaration says `submitAction` is `choose`, that `start_game` drops
`audience`, or that `save_game` returns `{ saveId }` rather than the store's `SaveHandle`. Two
generation-time gates make the table load-bearing: arity (the rows must exactly cover the exported
interface's methods) and error coverage (every declared `SessionStoreErrorCode` must have a
status mapping). One row means one request shape and one response shape for both surfaces, so MCP
inherits the wire's narrowings rather than applying its own.
Rejected: deriving the whole row from the type — impossible, the naming and narrowing information is
not in the declaration. Authoring the whole row including shapes — violates ADR-005 Rule 2 and
recreates the second definition the ADR exists to prevent. Per-surface narrowings — would make MCP a
differently-narrowed sibling of the wire rather than a projection of it, falsifying the brief's
second decision at the one place it is checkable.
Cost, stated rather than hidden: `saveGame`'s `savedAtSeq` is narrowed away, and it is the value
G2's compare-and-swap wants. Widening one row later is an additive contract change.
Reversibility: cheap

### 2026-08-08 — One HTTP route per operation, uniformly `POST`

Context: the wire's endpoint shape was open — nine (or ten) addressable endpoints, one dispatch
endpoint, or REST-resource shaping.
Chosen: `POST /v1/<operation>` with a JSON object body, the path segment derived mechanically from
the operation id, queries included.
Rejected: a single dispatch endpoint carrying `{ operation, args }` — smallest routing surface, but
it is MCP's own shape, so adopting it inverts "MCP is a projection of the wire"; it also collapses
every operation into one route template, and route template is the only per-operation label
Platform's metric allowlist permits. REST resource shaping — more conventional and makes queries
cacheable, but invents a resource model the engine does not have and breaks the one-to-one
row-to-route mapping that makes coverage checkable by counting. `GET` for the four read operations —
idiomatic, but `previewAction` is a query whose arguments include an arbitrary action-parameter
object with no defensible URL encoding, so the rule would need an exception, and an exception inside
a generated table is a hand-written special case.
Cost, stated rather than hidden: no HTTP caching and no method-level idempotency. Caching is a
binding non-goal; idempotency is Platform's per the hosting contract's ownership table.
Reversibility: cheap while the contract is pre-1.0

### 2026-08-08 — Both surfaces are constructed at startup from the table, not code-generated

Context: the table could produce the surfaces at build time as generated source, at runtime by
construction, or merely validate hand-written surfaces.
Chosen: the workload reads the pinned contract artifact and builds the HTTP routes and the MCP tool
list from it at startup, before the listener binds.
Rejected: build-time code generation into the workload — better type-safety at the seam and a
reviewable diff, but it puts a generated copy of the contract inside the workload, which is what
"the workload reads the contract from ServiceContract, not a local copy" forbids in substance; and it
moves the "removing a row breaks both" proof from an assertion about the running surfaces to a diff
of generated files, which tests the generator instead. Hand-written surfaces validated against the
schemas — cheapest, and payloads are still constrained, but the table would then check the surfaces
rather than be them, so a row could exist with no route.
Reversibility: cheap

### 2026-08-08 — The byte-identity proof reads the store through a shutdown dump, under a non-default profile

Context: comparison A needs the hosted service's own serialization of its store, and the permanent
non-goal forbids any endpoint returning engine state. The harness is out of process from the service.
Chosen: a determinism profile selected by startup configuration supplies the counting `IdSource` and
a fixed clock, and writes the ordered set of canonical serializations — the blobs only, keyed by id,
never the host-owned record fields around them — to a configured path at graceful shutdown. The
profile is startup configuration only: never a request field, never a header, never a route, and
with the default profile nothing is written. Both runs are real processes.
Rejected: hosting the workload inside the harness's process and reading the blobs through the
supplied persistence port — clean, needs no dump path, and the wire stays real; but the harness
cannot read the memory of a workload behind an edge, so Stage 2's "the same byte-identity replay
passes through two hops" would quietly narrow to the response comparison. An endpoint returning the
serialization, however named — the brief's one permanent non-goal, anticipated by name. Deriving the
hosted serialization from the response transcript — no new path and wholly behind the projection
boundary, but it is comparison B wearing comparison A's name.
Reversibility: cheap

### 2026-08-08 — The edge forwards by prefix and does not consult the contract

Context: the edge's routing depth was open — a prefix proxy, a contract-aware router, or full
termination and re-serialization.
Chosen: one route template covering the version and operation segments, forwarded unaltered. The edge
sets the outbound `traceparent` explicitly from the ambient operation scope's trace context, because
Platform deliberately does not wire HttpClient instrumentation — enabling it would activate .NET's
process-wide diagnostics handler and overwrite a header a caller set deliberately.
Rejected: a contract-aware edge — the right answer for G3, where authorization is enforced per
operation, but it needs a second distribution channel for the contract artifact (a .NET consumer
needs a .NET package), doubling the cross-repository release path the brief accepted once, inside the
effort whose virtue is being the cheapest informative failure. Terminating and re-serializing per
operation — full knowledge and independent validation, but a second implementation of the wire in a
second language, which is the drift failure recorded three times in this ecosystem.
Cost, stated rather than hidden: the edge cannot reject an unknown operation locally, no client-side
span means the hop's latency is unattributable, and G3 pays for the channel.
Reversibility: cheap

### 2026-08-08 — Engine error codes travel verbatim; status is a gated function of the code

Context: the brief requires a defined, tested answer for malformed payloads, unknown sessions and
unsupported versions, and says G2 and G3 inherit whatever is chosen.
Chosen: the wire carries the engine's `SessionStoreErrorCode` unchanged, plus a closed set of
transport-only codes (`malformed_payload`, `unsupported_version`, `unknown_operation`). Status is a
function of the code, held in the contract, with no default branch and a generation-time gate
asserting every declared engine code has a mapping. A rejected *action* — unknown action id, unmet
requirement — is a `200` carrying the store's unsuccessful result: HTTP status describes the
transport's ability to deliver the operation's result, never the game's verdict on the action.
Rejected: transport-normalized codes — tidier and independent of engine changes, but the engine ships
a registered localized message per reason code and its client contract requires clients to render the
code rather than parse the message, so a hosted client would be the only client unable to resolve its
own errors. Status alone with no body code — fewer concepts, but `unsupported_version` and
`unknown_operation` share a status by design, as do `unknown_session` and `unknown_campaign`.
Mapping a rejected action to `403`/`422` — more conventional, but it requires the transport to
classify a game outcome, and the transport is a client.
Reversibility: expensive once G2 and G3 have inherited it

### 2026-08-08 — The edge is not ready when the workload is unreachable

Context: the brief requires readiness's meaning when the Node service is unreachable to be decided
and asserted.
Chosen: unhealthy, and therefore not ready — the readiness check declares that it touches an external
dependency, is `Required`, and probes the workload's liveness endpoint rather than any game
operation. Liveness does not depend on the workload; Platform rejects a liveness check that declares
an external dependency, at registration. The edge starts whether or not the workload is up and
reports not-ready until it answers, the same shape Platform already uses for an unreachable database.
Rejected: degraded — which Platform produces automatically for an unhealthy *optional* check — would
be right if there were other backends to fall back to. There is exactly one, and an edge reporting
ready while it can serve nothing tells an operator nothing. Liveness following the workload — would
have an orchestrator restart the edge for a fault it cannot fix.
Reversibility: cheap

### 2026-08-08 — The trace evidence runs in CI against a loopback collector

Context: the Stage 2 criterion requires a distributed trace "visible in Platform's telemetry", and
the both-stages criterion requires the evidence to run in CI from a fresh clone. The offline
constraint forbids outbound network.
Chosen: CI runs an OTLP sink on loopback, both processes export to it, and the assertion is over the
collected spans — one trace id spanning both processes, correlation unchanged across the language
boundary. Loopback is not outbound network, and the existing build job already accepts loopback while
rejecting the OTLP ports outbound.
Rejected: asserting only on the propagated header with visibility demonstrated by the operator — no
collector needed, but it proves propagation rather than the criterion, and an operator-demonstrated
criterion is the anecdote that "runs in CI from a fresh clone" exists to rule out. A collector
reachable over the network — against the offline constraint.
Reversibility: cheap

### 2026-08-08 — The MCP projection is served by the workload process, over HTTP

Context: the MCP surface could run as a separate stdio server, the common MCP deployment shape, or
inside the workload alongside the JSON wire.
Chosen: the same process, over MCP's HTTP transport, sharing one dispatch and one store.
Rejected: a separate stdio process — needs no HTTP transport work and matches how MCP is usually
deployed, but a second process composes a second store, so a session started over the JSON wire would
be unknown to the MCP surface; two surfaces that agree about their shapes while disagreeing about
which sessions exist are not one service. It also makes G1's operations story three processes against
the brief's two.
Cost, stated rather than hidden: the edge routes the JSON wire only, so an MCP caller bypasses the
edge. Harmless while reachability is trusted-local and no principal exists; at G3 a surface that
bypasses the edge bypasses authorization.
Reversibility: cheap

### 2026-08-08 — G1 builds no compare-and-swap, and the single-instance invariant is unguarded

Context: `engine-hosting-contract.md` §6.1 names concurrent actions as the sharpest hosted problem and
resolves it with compare-and-swap on the sequence number. G1 has in-memory state and scale of one.
Chosen: no CAS in G1. The engine's own session store already queues same-session commands behind
their predecessor, so a lost update is not reachable within one process; the lost update §6.1
describes arrives with a second instance, which G1 does not have. Nothing enforces single-instance —
what stands in for enforcement is that G1's operations story is two processes started by hand, and
the design says so rather than implying a guard exists.
Rejected: building CAS now — it would be built against no failure and proven by no test, and G2's
durable store is where the sequence is actually contended. Enforcing single-instance with a lock file
or a registration record — real enforcement, but it is G2's host-registration machinery arriving
early for a deployment shape that is one hand-started process.
Reversibility: cheap

## Open

- The public site's roadmap renders `design/d3/30-slices.md` (the archived D3 set) since the
  archive move. Whether it should render the active effort's slices instead — or both — is an
  L-track design question, not a G1 one.
