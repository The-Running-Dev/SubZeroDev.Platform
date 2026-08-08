# Design — one session, over the wire, then the edge (G1)

**Document status:** Design. Derived from [`00-brief.md`](00-brief.md); if the brief changes, this is
re-derived, not patched.

The boundary this effort crosses is already specified.
[`engine-hosting-contract.md`](../docs/docs/engine-hosting-contract.md) owns who owns what and the
four things a hosted deployment must answer; [ADR-005](../docs/docs/adr/ADR-005-service-contract.md)
owns how a boundary contract comes to exist; the engine's own client contract owns what a client may
do. None of that is restated here. This document decides what those leave open: **what the operation
table is and where each of its fields comes from, how two surfaces are built from it without either
becoming the source, how the byte-identity proof reaches the store's serialization without a
raw-state endpoint, and what every boundary does when it fails.**

Four facts shape almost everything below, stated once rather than rediscovered per section:
**in-memory state and scale of one** (sessions lost on restart, by design); **two processes started
by hand** (the Node workload, and later the .NET edge); **fully offline** — nothing at startup or in
steady state requires outbound network; and **trusted-local reachability** — no principal exists, so
nothing here may be designed as though an untrusted caller arrives.

> **Three facts verified against the published engine and the engine's own repository, which the
> brief stated differently.** They changed no decision the brief took, and they sharpened two. They
> are stated here because the design must be correct against what exists. **The first two are now
> adjudicated** — the brief carries the correction, and the resolutions are recorded in
> [`90-decisions.md`](90-decisions.md), 2026-08-08.
>
> 1. **`previewAction` is already merged on the engine's `main`, unreleased.** The published
>    `@the-running-dev/game-engine` 0.4.0 carries **nine** store operations; the engine's `main`
>    carries **ten**, and its `SessionStore` doc comment already reads *"ten operations, ten MCP
>    tools"*. The `world-graph` kind is itself already exported from 0.4.0. **Resolved:** S1 cuts its
>    release from `main`, so G1 pins a **ten**-operation engine and the table is ten rows.
>    `previewAction` is consumed, never authored here — which is what the brief's non-goal forbids.
> 2. **The engine's API coverage checklist already has four client columns** — text client, MCP tool,
>    simulation kind, browser demo — over ten rows, every box ticked. **Resolved:** the hosted
>    transport's is the **fifth**, the brief says so, and because G1 pins the ten-operation engine the
>    column has ten ticks and no blank row.
> 3. **`09-clients.md` is generated**, from the engine repository's own `design/10-design.md`. The
>    PR that adds the hosted column edits that source, not the rendered document.

---

## Data model

Nothing in G1 is durable. Every entity below is either **an artifact** — a file produced by a build
step and published — or **in-memory state** lost on restart. There is no database, and the absence is
the brief's non-goal rather than an omission.

### Artifact — the operation table

The single row-set from which both surfaces are built. It is **authored data**, versioned and
published with the contract, and it lives in `SubZeroDev.ServiceContract`.

| Field | Type | Source |
|---|---|---|
| `operation` | Stable id, kebab-case | Authored. The row's identity. |
| `storeMethod` | Method name on the engine's `SessionStore` | Authored, **checked against the engine's type** |
| `mcpTool` | Tool name | Authored — `submitAction` → `choose` is a naming choice no type expresses |
| `httpPath` | Path segment under the version prefix | **Derived** from `operation`, mechanically |
| `requestShape` | Reference into the generated schema set | Authored reference; the shape itself is derived |
| `responseShape` | Reference into the generated schema set | Authored reference; the shape itself is derived |
| `narrowings` | Fields of the store's own signature deliberately not exposed | Authored |
| `reachableErrors` | The subset of the closed error-code set this row can produce | Authored, **checked against the mapping** |

**The authored/derived split is the central decision of this design, and it is forced.** The engine's
types are the source of truth for *what a request and a response contain* — ADR-005 Rule 2, and the
whole reason the schema is generated rather than written. They are not, and cannot be, the source of
truth for *what each operation is called on each surface*: nothing in `SessionStore`'s declaration
says `submitAction` is `choose`, that `start_game` drops `audience`, or that `save_game` returns
`{ saveId }` and not the store's `SaveHandle`. Those are contract narrowings the engine's own MCP
adapter already applies in code. Deriving them is impossible; authoring them twice — once per surface
— is the drift ADR-005 exists to prevent. **Authoring them once, as data, is the only remaining
option**, and it is what "the operation table held as data" means concretely.

**The narrowings are the table's, and both surfaces inherit them.** A row has one request shape and
one response shape, used by the JSON wire and by the MCP projection alike. If the wire returned
`savedAtSeq` and MCP did not, MCP would be a differently-narrowed sibling of the wire rather than a
projection of it, and the brief's second decision would be false in the one place it is checkable.

> **What that costs, stated rather than hidden.** `saveGame`'s `savedAtSeq` is narrowed away, and
> [`engine-hosting-contract.md`](../docs/docs/engine-hosting-contract.md) §6.1 names it as the version
> G2's compare-and-swap will assert on. G2 will need it back. Widening a response is an additive
> change to one row — a contract minor version — which is the mechanism working rather than a cost
> avoided.

**Two completeness gates run at generation, and they are what make the table load-bearing rather
than descriptive:**

- **Arity.** The row set must exactly cover the exported `SessionStore` interface's methods. A method
  with no row, or a row naming no method, fails generation. This is what makes a tenth operation a
  table entry rather than a rewrite — and, more usefully, what makes forgetting the entry impossible:
  the engine version bump does not build until the row exists.
- **Error coverage.** Every `SessionStoreErrorCode` the engine declares must appear in the code→status
  mapping. A code added upstream fails generation rather than silently becoming a 500.

### Artifact — the generated schema set

JSON Schema documents projected from the engine's published TypeScript declarations, one per request
and response shape the table references, with the row's narrowings applied. Wholly derived; nothing
in it is authored. Every response schema is **closed** — no additional properties — which is one half
of the projection-boundary gate. **Request schemas are closed too**: a member the row's request shape
does not declare fails validation as `malformed_payload`, which is what makes a request narrowing
irreversible from the wire — a dropped field cannot be re-supplied by a caller — and what leaves the
determinism profile with no caller-reachable path.

Identity is the schema's `$id`, a version-pathed URL per ADR-005 Rule 5. **The URL is an identifier,
not a fetch.** Nothing dereferences it at build time or at runtime; a validator that resolved `$ref`s
over the network would break the offline constraint on its first request. The artifact travels in the
contract package; the URL exists so a pinned `1.0` reference cannot be overwritten by a `2.0`.

### Artifact — the contract package

What the generator publishes and the workload consumes: the operation table, the schema set, the
code→status mapping, and **the exact engine version the schemas were projected from**. That last field
is not decoration — it is what lets a reader answer "which engine does this contract describe?" without
inference, and what makes a stale contract detectable rather than merely suspected.

Lifecycle: regenerated when the engine's types change, published under its own semantic version, pinned
by the workload. Per ADR-005 Rule 5 and the contract repository's own rule 4, it does not reach `1.0.0`
before its generator has rejected something.

### In-memory — session and save records

**Owned by the engine, not by this design.** The workload supplies the engine's optional
`SessionPersistence` port with a map-backed implementation; the engine defines the record shapes and
writes through to them. Each session record carries the canonical serialization as an opaque string
plus host-owned metadata the engine keeps deliberately outside it — created and updated instants, an
attempt counter, the projection audience, a replay-compatibility flag, and an optional profile id.

Two properties of that split matter downstream:

- **The canonical serialization contains no host metadata.** That is the engine's own rule
  ([`engine-hosting-contract.md`](../docs/docs/engine-hosting-contract.md) §7), and it is what makes
  a byte comparison of serializations possible at all — wall-clock instants live on the record, not in
  the blob.
- **Nothing on these records is derived by the workload.** The workload allocates no id, computes no
  sequence, and stamps no field. Every value is the engine's.

Identity: session id and save id, both minted by the engine's **`RecordIdSource`** port — the seam G1
adds, and the reason S1 exists. The engine mints these with `crypto.randomUUID()` behind no seam
today; `IdSource` is an `EngineHost` port and governs `gameId` and `seed` only, not these. The
consequence of the gap and the decision to close it are in [`90-decisions.md`](90-decisions.md),
2026-08-08, and the port's shape is [`20-contract.md`](20-contract.md)'s. Lifecycle: created on
`createSession`/`loadGame`, mutated in place on `submitAction`, never evicted, lost on restart. There
is no expiry, no quota and no size limit — the brief's non-goal, and the reason is that a limit sized
against no measurement is a number G2 would have to relitigate.

### In-memory — the determinism profile

Startup configuration selecting which implementations of the engine's `IdSource`, `RecordIdSource` and
`Clock` ports the composition supplies, and, when the replay profile is selected, the path to write the
store's canonical serialization to at graceful shutdown.

**Default profile:** the engine's random `IdSource`, no `RecordIdSource` — so the engine mints record
ids as it does today — and a real clock; no dump path; nothing written.
**Replay profile:** the engine's exported counting `IdSource`, a counting `RecordIdSource` on
independent counters, and a fixed clock; a dump path. Neither counting source takes a starting value:
the engine's `createCountingIds()` counts from zero and accepts no argument, and G1's
`RecordIdSource` matches it rather than inventing a second convention.

Three constraints, and each exists to stop this becoming something else:

- It is **startup configuration only** — never a request field, never a header, never a route. A gate
  asserts no operation row carries it.
- The dump is **written at shutdown, to a path**, and is not reachable by any caller. It is not an
  endpoint, and the design that would make it one — serving it over the wire — is the permanent
  non-goal.
- With the default profile, **no dump is written**. A test asserts that, because a diagnostic that is
  merely usually off is on.

### In-memory — request context

Per request, on the Node workload: the operation id, the wire version from the path, the inbound
`traceparent` if the caller sent one, and the correlation. Correlation is **derived** — it is the
trace-id of the adopted or minted trace context, which is the same rule Platform's own hosts follow, so
one greppable value spans both processes. A malformed inbound `traceparent` yields a fresh root and
never fails the request, matching Platform's stated behaviour at its own edge.

Nothing here is persisted. Nothing here reaches game state — writing a correlation into a session record
would be host metadata inside the determinism boundary, which is the failure §7 of the hosting contract
names.

### Committed fixture — the replay

A campaign id, a fixed seed, and an ordered action list that exercises **every** row in the operation
table. No counter start: both counting sources begin at zero and take no argument. Because the record
ids are reproducible, a step may name a session or save id an earlier step returned, as a literal.
Committed to the repository, because a proof whose input is generated per run compares two things
nobody has read.

Its companion, also committed, is **the golden transcript**: the canonically-encoded responses of the
in-process run, in order. It is an output of the proof and an input to it — regenerated deliberately,
reviewed as a diff, never rewritten by a passing test.

### Not modelled

No principal, no owner, no tenant, no account, no entitlement, no metering record, no rate-limit bucket,
no idempotency key. Each is a binding non-goal, and each is named here so a later reader can see the
absence was decided.

---

## Module boundaries

### The Node workload

| Module | Owns | Depends on | Exposes |
|---|---|---|---|
| **Contract** | Nothing — it is the pinned external artifact | The contract package only | The table, the schemas, the code→status mapping |
| **Composition** | The engine instance, the content registry, the in-memory `SessionPersistence` and `ProfileStore`, the determinism profile | The engine package, Contract (for nothing but version assertion) | A built `SessionStore`, and a **store-serialization handle** |
| **Dispatch** | The operation id → store call mapping, and the translation of store outcomes into a transport-neutral result | Contract, Composition | One call: an operation id plus validated arguments, in; a result or an error code, out |
| **HTTP surface** | Routing, request validation, response validation, canonical encoding, status mapping | Contract, Dispatch | The versioned JSON wire |
| **MCP surface** | The tool list and tool invocation | Contract, Dispatch | The MCP projection |
| **Probes** | Liveness and readiness for the workload | Nothing but Composition's readiness | Two endpoints |
| **Proof harness** *(test-scope)* | The fixture, the two comparisons, the perturbations | Composition's serialization handle, Dispatch (run 1), and the HTTP surface **as a client over a real socket** (run 2) | Nothing — it is a leaf |

**Dependency direction:**

```text
        Contract  ←──────────────┐
           ↑                     │
      Composition  ←── Dispatch ─┴─→  HTTP surface
           ↑              ↑                ↑
           │              └────────── MCP surface
           │
      Proof harness ──(as an HTTP client, over the wire)──→ HTTP surface
                    ──(run 1, in-process)───────────────→ Dispatch
```

Acyclic, and confirmed by inspection: Contract depends on nothing local; Composition depends on Contract
and the engine; Dispatch depends on both; the two surfaces depend on Dispatch and Contract and on each
other not at all; the harness depends on Composition, on Dispatch and on the HTTP surface but nothing
depends on the harness.

**Two edges deliberately absent, and each is gated rather than promised:**

- **Neither surface imports Composition's store-serialization handle.** That handle is how the byte-identity
  proof reaches the canonical serialization, and it is the only thing in the workload that can produce raw
  engine state. Keeping it out of the surfaces' import graph is the structural half of the
  projection-boundary gate — a route cannot return what its module cannot name.
- **Dispatch does not know it is being called over HTTP.** It receives an operation id and validated
  arguments and returns a result or a code; it maps nothing to a status and formats nothing. That is what
  makes the MCP surface a second consumer of one dispatch rather than a second implementation of the wire.

**Dispatch holds no game logic**, and the engine's own client contract makes that testable rather than
asserted: removing the transport and driving the store directly must not change the game. Dispatch
translates names and shapes. It does not retry, does not reinterpret a reason code, does not decide which
actions are available, and does not cache anything to decide with.

### The .NET edge

One host. It depends on Platform's Abstractions, Core, Hosting and Observability, and on **nothing in
`workloads/game-service/`** — the Node workload is reached over a socket, not referenced.

It owns: transport termination, routing to the workload, trace propagation across the language boundary,
and its own probes. It owns nothing else. No authorization, no persistence, no rate limiting, no caching —
each a binding non-goal, and a widened edge is G3 pulled into G1.

**It is composed by Platform's standard registration call and nothing else.** Health, readiness, correlation
and telemetry arrive with that call. Two things the edge adds are application code rather than composition,
and the distinction matters because the brief's criterion is about wiring, not about the host having no
behaviour: a forwarding route, and one readiness check registered as an ordinary service.

### The dependency rule, and its gate

Platform is not a product and never references one. The rule already holds; G1 gives it a first opportunity
to be violated, so it gains a gate: **a build-time check fails if any project under `src/` or `samples/`
references anything under `workloads/`.** The reverse direction — the edge referencing Platform — is the
whole point and is unaffected. A rule with no gate is a comment.

---

## Control flow

### 1. Startup — triggered by process start

**The Node workload.** Read configuration, including the determinism profile. Load the pinned contract
package. Assert the contract's recorded engine version equals the resolved engine package's version; a
mismatch aborts startup with a named error rather than serving a wire the schemas do not describe. Build
the engine, the content registry, the in-memory persistence and profile store, and the session store, with
the profile's `IdSource`, `RecordIdSource` and `Clock`. Build the HTTP routes and the MCP tool list **from the table, at this
moment** — both surfaces are constructed from the same in-memory row set, not from generated source. Bind
the listener. Report live, then ready.

The order matters in one place: **the surfaces are built before the listener binds**, so a table the
service cannot satisfy fails startup rather than producing a route that 500s on first use.

**The edge.** `AddPlatformWebHost()`. Register the forwarding route and the workload-reachability readiness
check. Bind. The edge starts whether or not the workload is up, and reports not-ready until it is — the same
shape Platform already uses for a database that is not yet reachable, and for the same reason: a host that
refuses to start tells an operator less than one that starts and says why it cannot serve.

### 2. One operation, end to end — triggered by a caller

**Single hop, JSON wire.** A request arrives at `/v1/<operation>` with a JSON object body. The version
prefix is matched; an unrecognised one is `unsupported_version`. The operation segment is matched against the
table; an unrecognised one is `unknown_operation`. The body is validated against the row's request schema;
a failure is `malformed_payload` and the engine is never reached. Dispatch calls the named store method.
The store returns a result, or throws a `SessionStoreError` carrying an engine reason code. On success the
result is projected to the row's response shape, validated against the row's response schema — closed, so an
added field fails — encoded canonically, and returned `200`. On a store error the code maps to a status and
an error body. The correlation travels on the response either way.

**The branch that is not an error.** A rejected action — an unknown action id, an unmet requirement — is a
**`200` carrying the store's own unsuccessful result**, not a 4xx. HTTP status describes the transport's
ability to deliver the operation's result; it never describes the game's verdict on the action. A transport
that turned a gated choice into a 403 would be interpreting game outcomes, which is the definition of
participating.

**Same operation, MCP projection.** The tool list is the same rows. A tool call validates its arguments
against the same request schema, calls the same Dispatch entry, and returns the same projected shape. There
is no MCP-specific path, no richer view, and no tool that is not a row — checkable by counting, which is the
engine's own standard for this claim. The MCP surface is served by the **same process** as the JSON wire, over
MCP's HTTP transport, sharing one store: two surfaces over two stores would agree about their shapes and
disagree about which sessions exist.

**Two hops, Stage 2.** The edge terminates the request, opens or adopts its operation scope, and forwards
method, path, body and the `traceparent` **it sets explicitly from the ambient scope's trace context**. It
does not rewrite the path, does not inspect the body, and does not know which operation it is carrying. The
workload adopts the inbound trace as its parent. The response is returned unaltered — byte-for-byte, because
Stage 2's replay asserts against the same golden transcript Stage 1 does, and any re-encoding at the edge
would fail it.

> **Why the edge sets the header itself, and why that is not bespoke wiring.** Platform's Observability
> package deliberately does not wire HttpClient instrumentation — enabling it activates .NET's process-wide
> diagnostics handler, which overwrites a `traceparent` a caller set deliberately. The edge therefore reads
> the ambient trace context, which Platform's operation scope carries as a first-class member for exactly
> this kind of use, and sets the header. That is the sanctioned path, not a workaround. **The cost:** no
> client-side span, so the edge cannot attribute latency to the hop. Accepted — performance is a binding
> non-goal, and the trace is still one trace.

### 3. The byte-identity proof — triggered by the test suite, in CI

Two runs, then two comparisons that are asserted separately because neither answers §5 alone.

**Run 1, in-process.** Compose the engine and store directly with the replay profile's counting sources
and fixed clock. Play the fixture's action list **through the same dispatcher the surfaces use**, not
against the store directly: the store returns `SaveHandle` and the wire returns `{ saveId }`, so a run
that called the store would diverge from run 2 on `save-game` before any determinism defect could.
Only the transport differs between the two runs. Collect each operation's returned value, canonically
encoded, as the transcript. At the end, read every session and save blob from the supplied persistence,
ordered by id.

**Run 2, hosted.** Start the workload **as a real process** under the replay profile. Play the identical
action list through the HTTP wire, strictly sequentially — each response fully read before the next request
is sent. Collect the response bodies as the transcript. Signal graceful shutdown; the profile writes the same
ordered blob set to the dump path.

**Comparison A — the engine invariant surviving hosting.** Run 2's dumped blobs against Run 1's, byte for
byte. This is the comparison that reaches around the wire, and that is deliberate: it asks whether the game
the hosted service produced is the same game, which is what
[`implementation-plan.md`](../docs/docs/implementation-plan.md) §5 records as unknown.

**Comparison B — the wire being deterministic.** Run 2's transcript against the committed golden transcript,
byte for byte. Run 1's transcript is asserted against the same golden file in the same suite, so one artifact
carries both claims: that the wire reproduces the projection, and that the projection has not moved since it
was last reviewed.

**The perturbations, which are what make the comparisons known to compare anything.** Two negative cases, each
targeting one comparison: a run with two actions transposed must fail A, and a run with one response field
substituted must fail B. A suite that has never gone red is not evidence.

**What the dump is, precisely.** The canonical serializations only — the blobs, keyed by id, in id order —
and never the host-owned record fields around them. Instants and counters live on the record, outside the
engine's serialization, and including them would compare wall clocks. Excluding them is not a normalization
that weakens the assertion; it is the boundary the engine already drew.

**Stage 2 re-runs this against the edge.** Run 2 is started behind the edge, the client addresses the edge,
and both comparisons are asserted again — A works across two processes because the dump is a file the
workload writes, not a value the harness reads out of memory. Stage 1's single-hop run stays in the suite and
must stay green; two hops passing is not evidence that one still does.

---

## Failure modes

### GitHub Packages unreachable — build time, both packages

**What fails:** the engine package or the contract package cannot be restored. **Detection:** the package
manager's own failure, at restore. **What the system does:** the build fails. **What the operator sees:** a
restore error naming the package and the registry. **State left behind:** none — no partial install is used.
**Retry:** the operator's, or CI's rerun. Nothing retries automatically, because a silent retry over an
authentication failure is how a credential problem gets recorded as flakiness.

This is the only outbound network dependency in G1, and it is build-time. Runtime has none.

### Contract and engine disagree

**What fails:** the contract package's recorded engine version does not match the resolved engine package's,
or the arity gate finds a store method with no row.

**Detection is in two places, deliberately.** The arity and error-coverage gates run **at generation**, in the
contract repository, and fail the contract build. The version assertion runs **at workload startup** and fails
the process. Generation-time detection catches the change at its source; startup detection catches a workload
that pinned a contract generated against a different engine than the one it resolved.

**What the system does:** neither builds nor starts. **What the operator sees:** a named startup error stating
both versions. **State left behind:** none; the listener never binds. **Why not a warning:** a service that
starts against a contract describing a different engine serves a wire its own schemas do not describe, and every
downstream assertion in this design becomes conditional.

### Malformed payload

**Detection:** request-schema validation, before Dispatch. **Response:** `400`, error body with code
`malformed_payload`. **State:** none — the store is not reached, so no session is created and no action is
attempted. **Retry:** the caller's, after fixing the payload; nothing is idempotency-sensitive because nothing
happened.

### Unsupported wire version, unknown operation

**Detection:** path matching against the table. **Response:** `404` with code `unsupported_version` or
`unknown_operation`. The status is the same for both and the **code** distinguishes them, which is the engine's
own convention — a client renders the code and never parses the message. **State:** none.

> These two are the answers G2's persistence and G3's principals inherit. They are stated as a closed set with a
> mapping rather than as per-route decisions for that reason.

### Unknown session, unknown save

**Detection:** the engine throws a `SessionStoreError`. **Response:** `404`, carrying the engine's code
verbatim — `unknown_session`, `unknown_save`. Verbatim matters: those codes are registered engine reason codes
with shipped messages, and a transport that paraphrased one would make a client's lookup fail. **State:** none.

### Other engine store errors

`invalid_state`, `unknown_kind`, `save_requires_migration`, `migration_failed` → `409`. `unknown_campaign` →
`404`. `storage_failure` → `503`. Every code in the engine's declared set has a mapping, enforced by the
error-coverage gate; there is no default branch, because a default branch is where a new upstream code goes to
be misreported.

**`storage_failure` is unreachable in G1**, and saying why is worth more than the mapping: the workload's
`SessionPersistence` implementation is map-backed and total — it has no failure mode. **The finding G2
inherits:** the engine writes the mutated blob to its in-memory record *before* writing through to persistence,
so a persistence implementation that can fail leaves the record ahead of the store. G1 cannot hit it and G2 must
answer it. It is recorded here rather than discovered there.

### An unexpected exception in the workload

**Detection:** an unhandled rejection reaching the surface. **Response:** `500`, error body with a generic code
and the correlation, **never exception text and never payload content** — the same envelope discipline Platform
applies on its own side, so the two hops do not disagree about what an error body may contain. The detail goes
to the log line the correlation identifies. **State:** whatever the store had already committed; the engine's
per-session lock means a partially-applied action is not among the possibilities.

### The Node workload is unreachable from the edge

**Detection:** the edge's readiness check probes the workload's liveness endpoint. **What the edge does:**
reports **not ready** — `Unhealthy`, `Required`, and therefore an unhealthy aggregate. **What the operator sees:**
the readiness body enumerating the failed check by name; Platform's probe body lists every registered check, so
the failure is named rather than inferred from a status code.

**The decision the brief demands, made explicitly.** Readiness is unhealthy, not degraded. The edge has exactly
one backend and one job; an edge reporting ready while it can serve nothing tells an operator nothing.
`Degraded` — which Platform produces automatically for an unhealthy *optional* check — would be right if there
were other backends to fall back to. There are not.

**Liveness does not depend on the workload**, and this is structural rather than a convention: Platform rejects a
liveness check that declares it touches an external dependency, at registration. An edge whose liveness followed
its backend would be restarted by an orchestrator for a fault it cannot fix.

**In-flight requests during the outage:** the forward fails. The edge returns `503` with the correlation. It does
not retry — a retry against a `submitAction` whose outcome is unknown is a second action, and merging two is
explicitly not available.

### The edge forwards, the workload answers slowly or not at all

**Detection:** the forward's timeout. **Response:** `504`, correlation carried. **State:** unknown to the edge and
knowable only at the workload — which is exactly the partial-failure case, and G1's honest answer is that the
caller must re-read with a query operation rather than resubmit. **No automatic retry**, for the reason above. The
edge holds no record of the attempt; there is no idempotency key, and inventing one is Platform's API-conventions
work, not G1's.

### The OTLP collector is absent, slow, or unreachable

**Detection:** the exporter's own. **What both processes do:** nothing that affects serving. Telemetry export is
opt-in on both sides — configured endpoint present or absent — and absence is normal rather than a failure, which
is what keeps the offline constraint true. On the .NET side this is Platform's existing behaviour, unchanged. The
Node side must match it: **no endpoint configured, no exporter started, no outbound connection attempted.**

**Consequence for evidence:** the distributed-trace criterion is not satisfiable without a collector, so CI runs
one on loopback and points both processes at it. Loopback is not outbound network, and the existing CI job already
accepts loopback while rejecting the OTLP ports outbound — the precedent is in place.

### A response does not match its schema

**Detection:** response validation, on every response, in the replay run and in the surfaces' own tests.
**Response:** the request fails as a `500` rather than returning an unvalidated body. **Why validate outbound at
all, when the shapes are generated from the types:** generation proves the schema describes the type; it does not
prove the handler returned that type unaltered. The done-criterion says exactly this, and closed schemas are what
give it teeth — an added field is a failure, not a tolerated extra.

### The projection boundary is crossed

**Detection, in two independent places.** Statically, at generation: every response schema is closed and none
resolves to the engine's envelope type. Structurally, in the workload: neither surface's module graph reaches
Composition's serialization handle, asserted as a dependency-direction test in the same shape as the
Platform-references-workload gate. Dynamically, in the replay: no response body anywhere in the transcript contains
a canonical serialization.

**What the system does:** fails the build. This is the one non-goal declared permanent, so it is the one gate that
must not be a runtime check that nobody watches.

### The MCP surface is not reachable through the edge

Not a failure at runtime — a **stated gap**. The edge routes the JSON wire only; an MCP caller addresses the
workload directly. In G1 that is harmless: reachability is trusted-local and no principal exists. **It stops being
harmless at G3**, where authorization is enforced at the edge and a surface that bypasses the edge bypasses
authorization. Named here so G3 inherits a known gap rather than discovering one.

---

## Concurrency and ordering

**Within the Node workload, same-session commands cannot interleave, and the engine is what enforces it.** The
session store queues commands per session id behind their predecessor; a second `submitAction` against one session
waits rather than reading the same blob. That is the engine's own mechanism, verified in its store, and it is the
reason G1 needs no compare-and-swap of its own. Cross-session commands genuinely interleave, and are independent
by construction — no state is shared between sessions except the profile store, which the engine locks on a second,
independent domain keyed by profile id.

**Compare-and-swap is G2's, and G1 does not partially implement it.** §6.1 of the hosting contract describes the
lost update that arrives with *two instances*. G1 has one, by the brief's "scale is one". A CAS built now would be
built against no failure and proven by no test.

**What must not happen: two workload instances.** Each would hold its own memory, and a session created against one
is unknown to the other — which presents as `unknown_session`, not as corruption. Nothing enforces single-instance;
what stands in for enforcement is that G1's operations story is two processes started by hand. **This is a
genuinely unguarded invariant, and naming it is the honest treatment**: the guard is G2's durable store, and until
then a second instance is an operator error with a confusing symptom.

**The replay is strictly sequential, and that is a requirement rather than an artefact of how the harness happens to
be written.** Each response is fully read before the next request is sent. Pipelining would let two actions reach
one session in an order the fixture did not specify, and the failure would present as a byte-identity break —
a determinism defect's exact symptom, in a harness that caused it. The reordering perturbation is the same
mechanism used deliberately, which is what makes the sequencing requirement testable rather than a comment.

**The edge is stateless and its concurrency is its host's.** It holds no per-session state, no connection affinity
and no cache, so two requests against one session are ordered by whichever reaches the workload first — and the
workload's per-session queue then orders them. The edge must not reorder, batch, or coalesce; it does none of
these, and none is a feature it is missing.

**Startup ordering between the two processes is unconstrained.** Either may start first. The edge reports not-ready
until the workload answers, which is the entire ordering contract between them.

---

## Alternatives considered

### Endpoint shape — one route per operation, not one dispatch route

**Chosen:** every operation is `POST /v1/<operation>` with a JSON object body, the path segment derived
mechanically from the operation id.

**Rejected — a single `POST /v1/dispatch` carrying `{ operation, args }`.** Smallest routing surface, and the edge
would need to know nothing at all. Rejected because it is MCP's own shape: adopting it would make the JSON wire a
transcription of the MCP surface rather than the thing MCP projects from, inverting the brief's second decision at
the one place it is visible. It also collapses every operation into one route template, and route template is the
only per-operation label Platform's metric allowlist permits — so the dispatch route buys its simplicity by making
per-operation telemetry unavailable to G4's metering without a second mechanism.

**Rejected — REST resource shaping** (`POST /v1/sessions/{id}/actions`, `GET /v1/sessions/{id}/scene`). More
conventional, and it would make the four queries cacheable. Rejected because it invents a resource model the engine
does not have, and the row-to-route mapping stops being one-to-one — at which point "every store operation is
exercised through the hosted surface" stops being checkable by counting, which is the engine's own standard for
this class of claim.

**Rejected — GET for the four read operations.** Idiomatic, and it is what a reviewer will ask for. Rejected because
the split cannot be uniform: `previewAction` is a query whose arguments include an arbitrary action-parameter object,
which has no defensible URL encoding. One rule with one exception is a hand-written special case inside a generated
table. **The cost of the uniform rule, stated:** no HTTP caching and no method-level idempotency. Caching is a
non-goal; idempotency is Platform's, per the hosting contract's ownership table.

### How the surfaces are built — from the data at startup, not from generated source

**Chosen:** both surfaces are constructed at startup by reading the table.

**Rejected — build-time code generation into the workload**, producing typed handlers per operation. Better
type-safety at the seam, no startup construction, and a reviewable diff when the contract changes. Rejected on two
grounds: it puts a generated copy of the contract inside the workload, which is what "the workload reads the contract
from `SubZeroDev.ServiceContract`, not a local copy" forbids in substance rather than only in location; and it moves
the "removing a row breaks both surfaces" proof from an assertion about the running surfaces to a diff of generated
files, which tests the generator instead.

**Rejected — the workload authoring its own routes and tool list, validated against the generated schemas.** Cheapest
by a wide margin, and the schemas still constrain every payload. Rejected because the table would then *check* the
surfaces rather than *be* them: a row could exist with no route, and the failure mode is a contract that promises an
operation the service does not serve — which is precisely the two-copies-disagreeing defect ADR-005 was written
against.

### Reaching the store's serialization — a shutdown dump under the replay profile

**Chosen:** the replay profile writes the ordered blob set to a configured path at graceful shutdown, and the harness
compares files.

**Rejected — hosting the workload in the harness's own process** and reading the blobs through the supplied
persistence port. Genuinely clean, needs no dump path at all, and the wire is still real — real sockets, real
encoding, real routing. Rejected because comparison A then cannot run through the edge: the harness cannot read the
memory of a workload that lives behind another process, so Stage 2's "the same byte-identity replay passes through
two hops" would quietly narrow to the response comparison only. Paying for a dump path buys the literal criterion at
both stages.

**Rejected — an endpoint returning the serialization**, however named, however restricted. It is the brief's one
permanent non-goal, and the brief anticipates this exact temptation by name.

**Rejected — deriving the hosted serialization from the response transcript.** No new path at all, and it stays wholly
behind the projection boundary. Rejected because it is comparison B wearing comparison A's name: reconstructing state
from projections proves the projections were consistent, and says nothing about the bytes the engine actually holds.

### Edge routing depth — prefix forwarding, not a contract-aware edge

**Chosen:** the edge declares one route template covering the version and operation segments and forwards. It does
not consult the table.

**Rejected — the edge consuming the contract artifact** and routing per operation. The right answer for G3, where
authorization must be enforced per operation on every call carrying an id. Rejected for G1 because it requires a
second distribution channel for the contract — the artifact is consumed by a Node workload today, and a .NET consumer
needs a .NET package — which doubles the cross-repository release path the brief accepted once, inside the effort
whose virtue is being the cheapest informative failure. **The cost:** the edge cannot reject an unknown operation
locally, and G3 pays for the channel.

**Rejected — the edge terminating and re-serializing each operation.** Full knowledge, and the edge could validate
independently. Rejected outright: it puts a second implementation of the wire in a second language, which is the
drift failure this ecosystem has recorded three times.

### Error semantics — engine codes verbatim, status derived by a closed mapping

**Chosen:** the wire carries the engine's `SessionStoreErrorCode` unchanged, plus a small closed set of transport-only
codes; status is a function of the code, held in the contract and gated for completeness.

**Rejected — transport-normalized codes**, translating engine codes into an HTTP-shaped vocabulary. Tidier as a wire,
and independent of engine changes. Rejected because the engine ships a registered, localized message for every reason
code and its client contract requires clients to render the code rather than parse the message. Translating breaks
that lookup, and a hosted client would be the only client that could not resolve its own errors.

**Rejected — status alone, with no body code.** Fewer concepts. Rejected because `unsupported_version` and
`unknown_operation` share a status by design, and `unknown_session` and `unknown_campaign` do too; the status cannot
carry the distinction, and the body is where the meaning already lives.

### A rejected action is a success at the transport layer

**Chosen:** `200`, carrying the store's unsuccessful result.

**Rejected — mapping a rejected action to `403` or `422`.** More conventional HTTP, and a caller could branch on
status. Rejected because it requires the transport to classify a game outcome, and the client contract's sharpest rule
is that a client which reasons about game verdicts has stopped being a projection. The transport is a client. **The
cost:** a caller cannot tell success from rejection by status alone and must read the result — which is the same thing
every other client of this engine already does.

### Trace evidence — a loopback collector in CI, not an operator demonstration

**Chosen:** CI runs an OTLP sink on loopback, both processes export to it, and the assertion is over the collected
spans: one trace id spanning both processes, correlation unchanged.

**Rejected — asserting only on the propagated header**, with visibility demonstrated by the operator. Needs no
collector, and it is what the offline constraint first suggests. Rejected because it proves propagation rather than
the criterion, which says *visible in Platform's telemetry* — and because "the evidence runs in CI from a fresh clone"
binds both stages. An operator-demonstrated criterion is the anecdote that criterion exists to rule out.

**Rejected — a collector reachable over the network.** Straightforwardly against the offline constraint.

### MCP transport — the same process, over HTTP

**Chosen:** the MCP surface is served by the workload process, sharing one Dispatch and one store.

**Rejected — a separate stdio MCP server process.** The common MCP deployment shape, and it needs no HTTP transport
work. Rejected because a second process composes a second store: a session started over the JSON wire would be unknown
to the MCP surface, and two surfaces that agree about their shapes while disagreeing about which sessions exist are
not one service. It also makes G1's operations story three processes, against the brief's two.

---

## Open questions

Each of these needs information the brief does not give, and each changes something concrete.
**Resolved questions keep their number and are struck through rather than removed**, because
[`20-contract.md`](20-contract.md) and [`30-slices.md`](30-slices.md) cite these by number and
renumbering would silently break every reference.

### ~~1. Which engine version does G1 pin~~

**Resolved 2026-08-08, by Ben: S1 cuts its release from the engine's `main`, so G1 pins a
ten-operation engine.** 0.4.0 was never available — [S1](30-slices.md#s1--session-and-save-ids-the-host-can-supply)
requires a *released* engine carrying `RecordIdSource`, and 0.4.0 carries no such port, so the pinned
version is necessarily the one S1 cuts and the only real choice was its base. The table is ten rows;
`previewAction` is consumed, never authored here. The reasoning and the rejected alternative are in
[`90-decisions.md`](90-decisions.md).

### ~~2. Which column does the hosted transport add to the engine's coverage checklist~~

**Resolved 2026-08-08 by question 1.** It is the **fifth** — the checklist already carries text
client, MCP tool, simulation kind and browser demo — and the brief now says so. Because G1 pins the
ten-operation engine, the column has ten ticks and no blank row, so the "is a blank row acceptable"
question does not arise. The PR edits the engine's `design/10-design.md`, from which `09-clients.md`
is generated, not the rendered document.

3. **Does `SubZeroDev.ServiceContract`'s "depends on nothing" survive a generator that reads the engine's types?** The
   contract repository's rule 5 states the property flatly. The generator the brief places there must resolve
   `@the-running-dev/game-engine` at build time to project its declarations. There is no cycle — the engine does not
   depend on the contract — so my reading is that the rule governs the **published artifact's** dependency graph, and
   the generator's build inputs are outside it. That reading needs stating in that repository's own document, which is
   a cross-repository edit and not this design's to make.

4. **Does comparison B compare the hosted run against the in-process run, or two hosted runs?** The brief says "the
   projected responses of the two runs against each other — the wire being deterministic". "The two runs" reads as the
   in-process and hosted runs; "the wire being deterministic" reads as two passes through the wire. This design
   satisfies both with one committed golden transcript, so the answer changes no machinery — but it changes what the
   criterion is understood to assert, and that is worth settling before it is cited.

5. **Is the shutdown serialization dump acceptable, or does the permanent non-goal reach it?** The non-goal forbids a
   raw-state *endpoint* "under any name" and explicitly exempts the proof's in-process serialization. The dump is a
   file written by a non-default startup profile at graceful shutdown, unreachable by any caller. I read it as outside
   the non-goal and as what buys Stage 2's criterion literally. If it is inside, comparison A cannot run through the
   edge and Stage 2's replay criterion narrows to the response comparison — which is a defensible position, but it is a
   change to a done-criterion and therefore yours.

6. **Should the edge cover the MCP surface too?** In G1 it need not, and the design routes only the JSON wire. The gap
   is stated in Failure modes and is harmless while reachability is trusted-local. Closing it in G1 costs edge routing
   for a second protocol; leaving it open hands G3 a surface that bypasses the authorization point.

7. **Is there a name and registry decision to take for the contract package?** ADR-005 fixes the repository and the
   versioning discipline but not what the published artifact is called or where it is published. The engine publishes
   to GitHub Packages under an existing scope; mirroring that is the obvious default, and defaults about published
   names are the kind of thing this repository's own rules say to ask about rather than assume.
