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

### 2026-08-08 — G1 adds one engine seam: a host-suppliable source for session and save ids

Context: `10-design.md` states that session and save ids are minted by the engine's `IdSource` port.
Deriving the contract against the engine found that false — `sessionId` and `saveId` come from a
module-local `mintId()` calling `crypto.randomUUID()`, and neither `SessionHost` nor
`InMemorySessionStoreOptions` carries an `IdSource` at all; `IdSource` is an `EngineHost` port
governing `gameId` and `seed`. The consequence is not cosmetic: `createSession` and `loadGame` return
`SessionHandle { sessionId, scene }` and `saveGame` returns `{ saveId }`, so three of the table's rows
carry a fresh UUID in every run and comparison B — byte-identical projected responses — cannot hold
for them against any committed golden transcript. Comparison A's "ordered by id" is a random order
across runs once more than one session or save exists, and a fixture cannot name a session id an
earlier step returned.
Chosen: change the engine, decided by Ben. G1 delivers a second port, `RecordIdSource`
(`newSessionId`, `newSaveId`), as an optional member of the session layer's composition root,
defaulting to today's `crypto.randomUUID()` so present behaviour is unchanged when it is omitted. It
is permitted by the engine's own rule — a host may supply anything that cannot change `serialize()`
output, and these ids never enter `GameState`, which the engine states in the same comment that
explains why it mints them where it does. It is a second port rather than a widening of `IdSource`
because `gameId` and `seed` are serialized inputs and these are store metadata; one port over both
would put two categories behind one name.
Rejected: excluding session and save ids from the transcript comparison — cheapest and needs no
cross-repository work, but it is an ignore-list, and the design refuses normalization by name;
once one exists the suite's claim to compare anything weakens. Restricting the fixture to one session
and no saves — makes comparison A's ordering trivial, but leaves comparison B failing on
`create-session` and stops the fixture exercising every row, which is its own done-criterion.
Re-running `/design` against the engine's actual surface — correct if this were one of several
findings, but it is one fact with one answer, and the design's other decisions are unaffected by it.
Cost, stated rather than hidden: a cross-repository engine change inside the effort whose virtue is
being the cheapest informative failure, and an amendment to the binding non-goal on engine behaviour.
G1's deliverables into the engine become two — this seam and the coverage-checklist column.
Reversibility: expensive — a published engine port is a compatibility promise

### 2026-08-08 — The Node workload's error channel is a result union, not thrown errors

Context: `20-contract.md` requires an enumerated error type per module and forbids bare exceptions
and string errors. The design specifies error *semantics* for the workload and names no carrier for
them; D3's `Result<T, TError>` is C#, in a package the Node workload cannot reach.
Chosen: `Outcome<T, E>` — a discriminated union on `ok`, carrying a typed error value on the failure
arm. Every boundary in the workload and the generator returns one. The single exception is the
engine's own `SessionStoreError`, which is thrown because no `SessionStore` signature has an error
channel; Dispatch catches it at the boundary and converts it, and it never travels further.
Rejected: thrown typed errors throughout — idiomatic in Node and cheaper to write, but the failure
set of a function stops being visible in its signature, which is the whole point of the contract's
error-semantics section. Mirroring D3's `Result` shape member-for-member — a false kinship between
two languages that share no code.
Reversibility: expensive once every module boundary returns one

### 2026-08-08 — The in-process replay run drives Dispatch, not the store directly

Context: the design has run 1 "play the fixture's action list against the store" and asserts both
runs' transcripts against one committed golden file. Those two statements are only jointly true if
run 1 applies the row's projection and canonical encoding — the store returns `SaveHandle`, the wire
returns `{ saveId }`, so a run 1 that called the store directly would diverge from run 2 on
`save-game` before any determinism defect could.
Chosen: run 1 composes the engine and store directly and drives them through the same `Dispatcher`
the surfaces use. Only the transport differs between the two runs.
Rejected: run 1 calling the store and the harness re-applying the narrowings — a second
implementation of the projection inside the thing that is meant to be checking it. A per-run golden
file — two files, and comparison B stops being one artifact carrying both claims.
Reversibility: cheap

### 2026-08-08 — The workload implements the engine's canonical serialization rule itself

Context: the wire is "encoded canonically" and comparison B is a byte comparison, so the workload
needs a canonical encoder. The engine has one — `canonicalStringify`, keys sorted, no insignificant
whitespace, undefined-valued members dropped, non-finite numbers and `bigint` rejected — and does
**not** export it from its public surface.
Chosen: the workload implements the same rule, and `20-contract.md` states the rule beside
`canonicalEncode` so an implementer reads it rather than inferring it.
Rejected: the engine exporting its encoder — one home for the rule and the right answer, but a
cross-repository change the contract has no standing to make: G1's agreed deliverables into the
engine are the record-id seam and the coverage-checklist column, and the encoder's export is
neither. `JSON.stringify` — key order follows insertion order,
which follows code paths, so two runs can differ for no reason the proof is looking for.
Cost, stated rather than hidden: two copies of one rule, which this repository's own standing
instruction calls a promise they will diverge. The mitigation is that comparison B fails loudly if
they ever do, and it fails on the same suite that exists to fail.
Reversibility: cheap — the copy disappears the day the engine exports its own

### 2026-08-08 — The determinism dump's id ordering is a property of its encoding

Context: the design specifies the dump as "the blobs, keyed by id, in id order" without saying what
produces the order.
Chosen: the dump is written with the same canonical encoder the wire uses, whose object members sort
ascending by code unit — so "in id order" follows from the encoding rather than from a sort the
writer must remember to apply.
Rejected: an explicit sort before writing — equivalent output, and one more step that can be omitted
without any test noticing until the dump has more than one entry.
Reversibility: cheap

### 2026-08-08 — The workload's listener binds loopback unless configured otherwise

Context: the brief's non-goal forbids reachability beyond trusted-local, and the design states
trusted-local reachability as one of its four shaping facts. Neither says what the listener binds.
Chosen: loopback by default, overridable by explicit configuration. It is the same shape D3 already
uses for the worker's probe port.
Rejected: binding all interfaces — the ordinary default, and it makes "no public exposure" a property
of the network the process happens to be on rather than of the process.
Reversibility: cheap

### 2026-08-08 — G1 pins the engine release S1 cuts from `main`: ten operations, not nine

Context: [`10-design.md`](10-design.md)'s open question 1 asked whether G1 pins 0.4.0 with nine store
operations or the release carrying `previewAction` with ten, and recommended 0.4.0 so the arity gate
would fire for real on the next bump. Deriving the slices made that answer unavailable: **S1.5
requires a *released* engine carrying `RecordIdSource`, and 0.4.0 carries no such port.** The pinned
version is therefore necessarily the one S1 cuts, and the only real choice was its base. The
scheduling was wrong too — the slices document listed the question against S2, one slice after the
release that settles it.
Chosen: S1 cuts from the engine's `main`, decided by Ben. `previewAction` is already merged there, so
G1 pins a ten-operation engine, the operation table is ten rows, and the coverage column S5 delivers
has ten ticks and no blank. `previewAction` is **consumed, never authored here** — the brief's
non-goal forbids writing engine behaviour, not routing an operation the pinned engine exports, and
refusing to route it would fail the arity gate rather than honour the non-goal. The brief's non-goal
and its wire paragraph both now say so.
Rejected: **backporting the seam onto the 0.4.0 tag** — preserves the design's stated preference of
nine rows and leaves the arity gate a real future event, at the cost of a release branch off a tag in
the engine repository and a coverage column with nine ticks and one blank against a ten-row
checklist, which is exactly the question open question 2 could not answer. The arity gate is already
proven deliberately by S2.2, so a real bump is not the only evidence it works. **Deferring to the S1
implementer** — correct about where the question belongs, but it is one fact with one answer and
leaving it open would have S1 stop on its first action.
Cost, stated rather than hidden: G1's surface carries an operation the effort did not ask for, and
the ten-row table is pinned to an engine release that does not exist yet.
Reversibility: cheap while the contract is pre-1.0 — dropping to nine rows is a row deletion and a
regeneration.

### 2026-08-08 — Request schemas are closed, like response schemas

Context: the contract closed every response schema and was silent on request schemas, and two
statements resolved the silence in opposite directions: S4.8 as originally written required a
request carrying an undeclared member to "change nothing" (an open-schema reading), while the
request narrowings — `start_game` dropping `audience` — are only enforceable if an undeclared
member cannot pass validation (a closed-schema reading).
Chosen: closed, at every object level, gated at generation (`RequestSchemaOpen`). An undeclared
request member is a `malformed_payload` and the store is never reached, which makes a request
narrowing irreversible from the wire and leaves the determinism profile with no caller-reachable
path. S4.8 is restated to assert the rejection rather than the tolerance.
Rejected: open request schemas with tolerated extras — S4.8's original wording would hold, but a
caller could re-supply any narrowed request field and the narrowing mechanism the design calls
load-bearing would constrain nothing; it also lets unvalidated members ride `ValidatedArguments`
into the engine. Closing responses only — the asymmetry is exactly the ambiguity that produced the
contradiction.
Reversibility: cheap while the contract is pre-1.0 — opening a schema is an additive change for
callers.

### 2026-08-09 — "Depends on nothing" governs the published artifact, not the generator's build inputs

Context: `01-contract-rules.md` rule 5 in `SubZeroDev.ServiceContract` says the repository "depends
on nothing," and S2's generator must resolve the pinned engine package to project schemas from its
types — rule 1, "projected, never authored," requires it. Design Q3 asked which of these two
readings governs.
Chosen: the published `ContractPackage` carries zero runtime dependencies — that is what a consumer
pinning it can rely on. The generator itself, and its tests, may depend on the engine package (to
project types from, resolved at generation time and never dereferenced into the artifact) and on
ordinary dev tooling (a TypeScript compiler, a JSON Schema validator for its own gates). None of
that ships. Decided by Ben.
Rejected: extending "depends on nothing" to the generator's build inputs — the only reading under
which S2.1's projection could not happen at all, since there would be nothing left to project from.
Reversibility: cheap — it is a statement about what the rule means, not a structural choice

### 2026-08-09 — The generated schema set declares JSON Schema draft 2020-12

Context: Unresolved 3 — `JsonSchemaDocument.$schema` has a type and no value, and the choice fixes
which validator the workload can use and whether `additionalProperties: false` composes the way the
closed-schema gates assume.
Chosen: `https://json-schema.org/draft/2020-12/schema` for every emitted document. Ajv supports it
natively (`ajv/dist/2020`), and it is JSON Schema's own current draft. Decided by Ben.
Rejected: draft-07 — still ajv's default and very widely supported, but a step behind the dialect
JSON Schema itself now recommends, with no offsetting advantage for this contract.
Reversibility: expensive once a consumer has pinned a version against it

### 2026-08-09 — The contract package publishes as `@subzerodev/service-contract` on npm; S2's publish
criterion runs against a local registry, not live npm

Context: Unresolved 4 — the package's published name and registry. Issue #81 (the `@subzerodev` npm
organisation reservation) is open as of this decision, so a real `npm publish` under that scope is
not yet available to this session, and would be an external, credentialed action outside what an
agent session performs unprompted regardless.
Chosen: the name is fixed now — `@subzerodev/service-contract`, npm registry — so the workload's
dependency declaration (Unresolved 4's blocking use) has its first line. S2.9's acceptance
("published under a semantic version," "republishing the same version is refused") is satisfied
against a local, ephemeral registry started for the test run, proving the refuse-to-overwrite gate
for real without needing live npm credentials. The actual first publish to the real `@subzerodev`
scope happens later, by Ben, once issue #81 closes. Decided by Ben.
Rejected: blocking S2.9 entirely on #81 — correct about the dependency but leaves a gate the design
calls load-bearing untested for an arbitrarily long time; a local registry proves the same mechanism.
Reversibility: cheap — the name is a string and the registry target is a config value

### 2026-08-09 — The workload's test runner is Vitest, run through `tsx`

Context: S3 needed a test runner and a way to execute TypeScript directly for `start`, and
`AGENTS.md`'s no-new-dependencies rule requires the alternatives considered to be on record.
Chosen: `vitest` for tests and `tsx` for running `src/main.ts` without a separate build step.
Vitest shares its transform pipeline with the rest of the TypeScript-first tooling already in use
across the contract package's own dev dependencies, needs no separate config for ESM and `.js`
specifier resolution against `.ts` sources, and its watch mode is what `test:watch` uses during
development.
Rejected: `node:test` — zero additional dependency and already in Node's own runtime, but it has no
built-in TypeScript transform, pushing that need onto a second tool anyway (`tsx` or `ts-node`) and
losing Vitest's assertion and mocking ergonomics already familiar from the contract package's suite.
`ts-node` in place of `tsx` — older and slower to start; `tsx` is esbuild-backed and is what the
contract package's own generator already uses for the same job.
Reversibility: cheap — dev-only, no published surface depends on the choice

### 2026-08-09 — The Adventures POC is a reference for G2 and G3, not a source this effort copies from

Context: [`SubZeroDev.Adventures`](https://github.com/The-Running-Dev/SubZeroDev.Adventures) runs a
live Fastify service over the same engine release G1 pins — all ten store operations over HTTP,
Postgres-backed `SessionPersistence` and `ProfileStore`, cookie identity, per-player ownership
checks, and replay endpoints. It was read end to end to settle whether G1's remaining slices should
take anything from it.
Chosen: take no code into S5–S9, and treat the POC as the reference implementation G2 and G3 read
when they start. Two facts were harvested instead of code. The engine's write ordering is staged
under `## Open` below. The second needs no work and is recorded here: the POC's hand-written
`ERROR_STATUS` table agrees with the generated `statusMapping` on all eight engine codes —
`unknown_session`, `unknown_save` and `unknown_campaign` at `404`, `invalid_state`, `unknown_kind`,
`save_requires_migration` and `migration_failed` at `409`, `storage_failure` at `503` — which is
independent corroboration of the mapping rather than self-consistency, since it was arrived at
separately against the same engine.
Rejected: porting its routes — they are hand-written REST (`POST /api/sessions/:id/actions`) where
this workload derives uniform `POST /v1/<operation>` from the row table, so adopting them would put
a second source of operations beside the table, which is the thing S6.4 exists to detect. Porting
its replay endpoints as a second byte-identity oracle — they return the stored and replayed blobs in
a failure body, which is the raw-state surface the brief declares permanently out of scope, and they
reach `serialize`/`deserialize` from the request path, which invariant 17 keeps out of Dispatch.
Pulling its Postgres persistence forward into G1 — the brief orders the byte-identity proof before
durable persistence precisely so persistence has something to be checked against.
Reversibility: cheap — nothing was taken, and the POC is unaffected either way

### 2026-08-10 — No identity substrate is chosen; the proposed ADR-007 was withdrawn before it was written

Context: Ben asked whether Supabase should be brought in for identity and adjacent capabilities, and
then asked for the substrate to be settled now as an ADR ahead of G2's persistence work. Research
was carried out and produced findings worth keeping (recorded in
[ADR-004](../docs/docs/adr/ADR-004-framework-build-not-adopt.md) §"Provider re-verification" and in
`minimal-platform-packages.md`). The decision the ADR was to record is a different matter.
Chosen: write no substrate ADR. [`platform-identity.md`](../docs/docs/platform-identity.md) §3
gives Identity the tier `Undecided` and says in terms that filling one in passing "would be the
speculative-package habit the guard exists to break, arriving as a table cell"; ADR-006 rule 3
settles a tier when the package is designed. An ADR naming a substrate settles that tier by
implication. The §4 consumer table is the sharper objection: Identity's three consumers need API
keys and service accounts (Automator), player accounts (GEaaS), and "a table, established by QR
link and never an account" (BarStrad) — an OIDC provider answers exactly one of the three, so any
substrate chosen now would be chosen for the GEaaS column and mislabelled as a Platform decision.
Rejected: **Keycloak as the reference default** — the strongest licence-durability position
available (Apache 2.0, CNCF incubating), and it was the recommendation until the tier objection
landed; declined on weight for the homelab mode, and then moot. **authentik** — MIT outside
`authentik/enterprise/`, better admin UX, but a workforce/gateway design centre rather than a
consumer-signup one, and open-core boundaries move. **Supabase Auth as the platform-wide default**
— fits the GEaaS column well and is now a self-hostable OIDC provider, but that is one column of
three. **Supabase's data plane (PostgREST, RLS, Storage-with-RLS)** — declined on architecture, not
hosting: RLS puts ownership checks in SQL policies while the engine hosting contract §6.2–6.3 and
G3's done-criterion put them in an authorization decorator, and PostgREST would stand up a second
source of operations beside the row table that S6.4 exists to protect.
Reversibility: cheap — nothing was adopted, and every finding above is recorded rather than held in
a conversation

### 2026-08-10 — The Automator and Game Engine as a Service do not share identity

Context: whether a person can hold one account across both products decides whether Platform's
Identity must federate, link accounts, or carry a shared user directory — and that is expensive to
retrofit and equally expensive to build speculatively.
Chosen: they do not share identity. Decided by Ben: the hosted game service serves players and game
creators; the Automator manages plugins and workflows and is not a surface those users reach. The
three consumers in [`platform-identity.md`](../docs/docs/platform-identity.md) §4 are therefore
disjoint principal *domains*, which means Platform's Identity is at most a consistent contract over
per-application principals rather than a shared store — a reading that argues for the application
module tier over the framework tier, without settling it.
Rejected: a shared principal across products — buys single sign-on nobody asked for, and imports
federation and account-linking design into a package that has not been designed. Deferring the
question — it is cheap to answer now and shapes the eventual design, and "obviously they would
share accounts" is exactly the kind of assumption that gets built without ever being written down.
Cost, stated rather than hidden: Ben's answer carried "at this point", so this is current intent
rather than a permanent boundary. The hedge that keeps it cheap is opaque, stable principal ids in
every product from the start, which makes later linking a retrofit rather than a migration.
Reversibility: cheap now; expensive once any product's principal ids become externally meaningful

### 2026-08-10 — `callTool` takes the raw `inboundTraceParent`; the MCP result arm carries the derived correlation

Context: `20-contract.md` contradicted itself, tracked as
[#102](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/102). "Workload — request
context" said the MCP surface adopts whatever `traceparent` the transport carried, and invariant 29
said every response carries the correlation. `McpSurface.callTool` gave the MCP HTTP transport no
parameter to hand a trace in through, and `McpToolOutcome`'s result arm gave a successful call no
member to carry a correlation out — so a successful tool result carried no correlation anywhere, and
even an error's correlation was always freshly minted rather than adopted from an inbound trace,
found by the S6 code review.
Chosen: `callTool(name, args, inboundTraceParent: string | null)`, and `McpToolOutcome`'s result arm
gains a `correlation: CorrelationId` sibling to `value`. `callTool` derives `correlation` from
`inboundTraceParent` by the same rule `correlationFrom` already applies on the JSON wire, and builds
the rest of `RequestContext` itself — `operation` from the row `name` resolves to, `wireVersion` from
the contract. A full `RequestContext` was considered and rejected as the parameter: its `operation`
member must be the resolved row's `operation`, not the requested `name`, because three rows carry a
deliberately renamed `mcpTool` — only `callTool`, which performs the lookup, can set it correctly, so
handing in a `RequestContext` built before that lookup would hand in a context that is wrong for
exactly those three tools. This mirrors the JSON wire, where `10-design.md`'s "the correlation
travels on the response either way" already means outside the body a schema validates — HTTP carries
it on a response header, MCP has no header channel of its own, so it carries it as a sibling field on
the outcome rather than inside `value`. `value` stays exactly the canonically-encoded projected
shape, so S6.3's "identical to the JSON wire's for the same arguments" still holds.
Rejected: a full `RequestContext` parameter — see above. Folding correlation into `value` — would
make the MCP result byte-different from the JSON wire's body for the same call, breaking the "one
dispatch, one shape" claim S6 exists to prove. Leaving `callTool` to mint its own correlation
unconditionally, as the shipped S6 code does — silently drops every inbound trace on the MCP surface,
which invariant 29's neighbors (30, 31) assume does not happen.
Reversibility: cheap — S6 shipped code references the old two-argument signature and is updated in
the same change that closes #102.

### 2026-08-10 — The edge's two wire-visible codes: `workload_unreachable` and `workload_timeout`

Context: Unresolved 2's edge half — `20-contract.md`'s `EdgeError` names two variants,
`WorkloadUnreachable` (503) and `WorkloadTimeout` (504), but leaves their `code` strings unnamed.
S7 is the slice that needs them: `EdgeError` crosses the wire in `WireErrorBody.code` the same way
the workload's own codes do, so a first implementer would otherwise settle two more strings
silently.
Chosen: `workload_unreachable` and `workload_timeout` — lowercase snake_case, on the same terms as
every other transport code the workload already emits (`malformed_payload`, `unsupported_version`,
`unknown_operation`, `internal_failure`), and named for the condition the way those four are rather
than for the status code. Neither joins `WireErrorCode` or the contract's status mapping —
`20-contract.md` is explicit that the edge's two codes are `EdgeError`'s own and enter neither.
Rejected: reusing `internal_failure` for both — collapses two distinguishable conditions (a caller
can retry-with-a-read on either, per `EdgeError`'s own table, but an operator debugging a stuck
process needs to tell "never connected" from "connected and hung" apart). A `503`/`504`-derived
name (`service_unavailable`, `gateway_timeout`) — ties the code to the HTTP status rather than the
condition, and the workload's own codes never do that (`unknown_session` is `404`,
`invalid_state` is `409`, both named for the condition).
Reversibility: cheap — both are new strings with no prior callers.

### 2026-08-10 — `workload_unreachable` covers a forward that fails after the headers arrive

Context: `20-contract.md` describes `WorkloadUnreachable` as "the forward cannot connect", which
reads as the connect phase alone. The forwarder uses `HttpCompletionOption.ResponseHeadersRead`, so
a workload killed mid-response fails at the body read instead, and that read had no classification —
the `HttpRequestException` escaped the edge entirely and Platform's envelope answered `500`
`UnhandledRequestFailure`. Reproduced against a workload that writes headers and then destroys the
socket.
Chosen: classify a transport failure during the body read as `workload_unreachable`, the same as one
during the send. The contract's neighbouring sentence — "the edge produces no other error; every
other status a caller sees came from the workload" — leaves no third answer available, and the two
cases are the same lost hop from the caller's side. The wording in `20-contract.md` is narrower than
this behaviour and is worth widening at the next `/contract` pass; the behaviour is what the
invariant requires either way.
Rejected: letting it escape to the `500` envelope — that status came from neither the workload nor
`EdgeError`, so it breaks the invariant while telling the caller nothing about which hop failed.
A third code for "died mid-response" — `EdgeError` is deliberately two variants, the retry answer is
identical for both, and a caller cannot act on the distinction; an operator gets it from the log
line the correlation identifies.
Reversibility: cheap — one `catch` clause and its test.

### 2026-08-10 — `Logging:LogLevel` is the one home for log levels, and Observability reads it

Context: `AddPlatformObservability` fixed `MinimumLevel.Information()` and never read
configuration, so the `Logging` section every .NET consumer writes was dead config that reads as
live — an operator setting `Microsoft.AspNetCore: Warning` still got a log line per request and per
outbound call. It is not an ordinary filtering gap: `AddSerilog` replaces the logger factory
outright, so `Microsoft.Extensions.Logging`'s own rules never run and there was nowhere else the
section could take effect. Found in S7 review, where the edge's own `appsettings.json` carried such
a section and 70 KB of per-request logging fell out of it.
Chosen: translate `Logging:LogLevel` into Serilog's minimum level and per-prefix overrides inside
`ConfigureLogging`. `Default` is the minimum, every other key an override on that category prefix,
`None` mapped one past the highest level so it admits nothing. Absent section keeps `Information`,
so nothing already deployed changes.
Rejected: a level on `TelemetryOptions` — a second home for one fact, and the one nobody would look
in first. `Serilog.Settings.Configuration` and a `Serilog` section — a new dependency to read a
second, Serilog-shaped section alongside the standard one, which is the same two-homes problem with
a package attached. Per-provider sections (`Logging:Console:LogLevel`) — Platform owns its sinks, so
there is no provider for a consumer to address and the key would name something that does not exist.
Reversibility: cheap — one private method and its tests; the prior behaviour is the no-section path.

### 2026-08-10 — The workload's OTLP export goes through the official OpenTelemetry JS SDK, not a
hand-rolled OTLP encoder

Context: S8 needs the workload to export spans over OTLP/HTTP, and the workload had no OpenTelemetry
dependency of any kind before this — `correlation.ts`'s own hand-rolled `traceparent` regex parsing
is the only precedent for building wire-format handling by hand in this package, and
`AGENTS.md`'s guardrails cut the other way for this class of problem: "prefer an existing package or
service to hand-rolled infrastructure. Hand-rolling is what needs justifying, not taking a
dependency." Encoding OTLP correctly by hand — trace/span id generation, the parent/child and
sampling rules, the export payload shape — is exactly the wire-format work that guidance is warning
against, and getting it subtly wrong would be silent: nothing else in the suite re-derives OTLP
semantics to catch a mistake in ones own encoder. `AGENTS.md`'s no-new-dependencies rule requires the
alternatives considered to be on record, which is this entry. Decided by Ben, in-session, after the
agent stopped rather than choosing silently between the two paths `/slice` itself cannot take
(hand-rolling wire-format code the guardrail steers away from, or adding a dependency `/slice` is
barred from adding without this record).
Chosen: `@opentelemetry/api`, `@opentelemetry/sdk-trace-base` and `@opentelemetry/exporter-trace-otlp-http`
as workload dependencies (`workloads/game-service/package.json`). `telemetry.ts` composes a
`BasicTracerProvider` with one `SimpleSpanProcessor` over the OTLP/HTTP JSON exporter, constructed
only when `otlpEndpoint` is configured — mirroring the `DeterminismProfile` pattern already in this
package, where the type itself makes "nothing exists in the other case" a fact the compiler enforces
rather than a branch that could be gotten wrong. A root span's trace id is forced to equal the
correlation `correlation.ts` already derives, through the SDK's own `IdGenerator` extension point —
one authority per request, not two independent mints that must agree by coincidence.
Rejected: a hand-rolled span/OTLP-JSON encoder, matching `correlation.ts`'s existing style — no new
dependency, but it is the wire-format hand-rolling `AGENTS.md` singles out as needing justification
rather than being the default, and correctness here is exactly the kind of thing a byte-identity-proof
project should not be trusting to an unreviewed-by-anyone-else implementation of a spec with edge
cases (sampling, trace-state, batch export semantics) this effort has no reason to reinvent.
Reversibility: moderate — a workload dependency and its usage inside one module (`telemetry.ts`),
consumed only from `lifecycle.ts`'s internal `serve()`; no exported, contract-governed signature
carries an OpenTelemetry type.

### 2026-08-10 — S8's CI trace-evidence assertion runs a real OpenTelemetry Collector binary, not a
hand-decoded stand-in for two wire formats

Context: `20-contract.md`'s edge signatures and `PlatformObservabilityExtensions.ConfigureOtlp`
(unconditional `OtlpExportProtocol.HttpProtobuf`, outside this slice's `Touches`) mean the .NET edge's
OTLP export is always protobuf, while the workload's own exporter (the decision above) sends OTLP/HTTP
JSON. A collector accepts and normalises both on one receiver; a hand-built sink in this suite would
need to decode two different wire formats, and the .NET SDK's own generated OTLP protobuf types are
`internal` to `OpenTelemetry.Exporter.OpenTelemetryProtocol` — unreachable from a test project without
either vendoring the official `.proto` schemas at build time or adding a second protobuf-handling
dependency solely to read them back. Both are more hand-rolled-infrastructure than running the
standard product built for exactly this job. Decided by Ben, in-session, for the same reason as the
decision above: `/slice` stops rather than silently picking between hand-decoding and standing up new
CI infrastructure.
Chosen: the `game-service` CI job downloads a pinned, checksum-verified `otelcol-contrib` release
(`v0.158.0`, linux_amd64) before the outbound-OTLP-port block (`build.yml`, the same step-ordering
`dotnet`/`node` setup already uses), configures it with an OTLP/HTTP receiver on a loopback port
chosen at run time and a `file` exporter, and `tests/trace-evidence.test.ts` reads that file — Go's
own `protojson` marshaling, base64 trace/span ids per the standard proto3 JSON mapping — after driving
one request through a real edge-plus-workload pair pointed at it. The test skips itself when
`OTEL_COLLECTOR_BIN` is unset, so a local `npm test` with no collector installed still runs everything
else; only CI sets the variable.
Rejected: hand-decoding both wire formats in this suite — no external binary, but two decoders to get
right (one of them binary protobuf) purely to read back facts the collector already computes
correctly, and it duplicates work a widely-used, independently-tested product already does.
Reversibility: cheap — confined to one CI step, one test-support module, and one test file; nothing
outside CI depends on the collector existing.

### 2026-08-11 — `/install` reconciled the kit upgrade from `78ff0de` to `af610a6`

Context: `/install SubZeroDev.Platform` found the target three commits behind the kit
(`3516c15` `/done` auto-run/auto-stash, `8386545` the design freeze mechanism, `af610a6`
`/freeze`/`/unfreeze`). `Sync-Kit -DryRun` found zero `Divergent-Skipped`/`Collision-Skipped`
rows — nothing here had been independently edited since the last sync — so this was a clean
upgrade, not a fork needing adjudication.
Chosen: ran `Sync-Kit.ps1` for real (updates `.claude/commands/{contract,design,done,reconcile,
slices,track}.md` and `tools/Invoke-DoneHousekeeping.ps1`; adds `freeze.md`/`unfreeze.md`), then
hand-merged three purely-additive pieces into `AGENTS.md`: the `/code-review`, `/freeze`,
`/unfreeze` rows in *Command routing*; the full `## The design freeze` section; and the `/done`
branch-deletion delegation paragraph in *Git and delivery*. Everything else in `AGENTS.md` —
project identity, the ADR system, the generic effort/model table, the extraction guard, phase
vocabulary, dependency policy, the junction-path note — is prior, signed-off customization and
was left untouched.
Rejected: skipping the upgrade — no cost to adopting it, and the target hadn't diverged from
any of the incoming content.
Reversibility: cheap

## Open

_(previously tracked out of this section: issues [#98](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/98), [#99](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/99), [#100](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/100), [#102](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/102))

---

## Index — decisions whose home is elsewhere

Reasoning, consequences and rejected alternatives live in the linked document, never here —
*Single ownership* in `AGENTS.md`. Effort-scoped decisions from completed efforts live in their
archive's own index (see [`d3/90-decisions.md`](d3/90-decisions.md)); the rows here are the
permanent ones every effort inherits.

| Decision | Home |
|---|---|
| SkyNet HR is a second hosted workload; the edge becomes a Platform package | [ADR-007](../docs/docs/adr/ADR-007-second-hosted-workload.md) |
| Platform is a framework plus optional application modules | [ADR-006](../docs/docs/adr/ADR-006-application-modules.md) |
| Boundary contracts are projected, not authored; they get their own repository | [ADR-005](../docs/docs/adr/ADR-005-service-contract.md) |
| Platform is built in-house, with ABP as an architecture reference | [ADR-004](../docs/docs/adr/ADR-004-framework-build-not-adopt.md) |
| Package scope is per-registry, not one global name | [ADR-003](../docs/docs/adr/ADR-003-package-scopes-and-registries.md) |
| Platform is .NET, and the product boundary is a process boundary | [ADR-002](../docs/docs/adr/ADR-002-implementation-technology.md) |
| `SubZeroDev.Platform` is the framework, not the game product | [ADR-001](../docs/docs/adr/ADR-001-platform-identity.md) |
