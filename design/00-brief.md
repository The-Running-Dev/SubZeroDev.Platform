# Brief — one session, over the wire, then the edge (G1)

> Written by me, not by a model. A model may interrogate it (`/brief-check`) but not author it.
>
> **Provenance of this draft:** the decisions below were taken by me in answer to direct questions
> on 2026-08-08 and transcribed. The *Problem* statement and the *Environment* section are drafted
> from this repository's own documents and the engine's published surface, and need my words before
> they are binding.

## Problem

The Game Engine is published and nothing consumes it over a wire. `@the-running-dev/game-engine`
0.4.0 sits on GitHub Packages with a 26-entry public surface, a nine-operation client contract, and
a byte-identity guarantee that has only ever been exercised in-process. Track B —
[`implementation-plan.md`](../docs/docs/implementation-plan.md) §5 — is unblocked and unstarted:
every hosted ambition above it (durable sessions, principals, catalogue, billing) rests on a hosted
transport being able to reproduce a game byte-for-byte, and that is currently unknown.

G1 exists to make it known in the smallest possible increment. It is the cheapest thing that can
fail informatively.

## Who it is for

Me, as the operator of the first hosted engine deployment — and the engine itself, whose client
contract gains its fifth client and whose coverage checklist gains its fifth column. (Four exist
already: text client, MCP tool, simulation kind, browser demo.)

No player, creator, or third-party developer is served by G1 directly. That is deliberate: G1 is
infrastructure proof, and pretending otherwise would smuggle G3's account surface and G4's
catalogue into scope through the audience.

## Scope — two stages, one effort

Settled in [`implementation-plan.md`](../docs/docs/implementation-plan.md) §8.4 and transcribed
here because the brief is where scope binds:

1. **The Node service.** A single Node service consuming the published engine package, composing
   the in-memory stores, exposing the ten-operation game surface. It consumes the engine and not
   Platform.
2. **The .NET edge, as a fast follow.** Platform's packages in front of that workload: transport
   termination, routing to the Node service, a distributed trace across the language boundary.
   The edge is Platform's first consumer outside `samples/`.

The stages are ordered — the byte-identity proof exists before the edge does — and the edge does
not gate G2. Both stages are inside G1's definition of done: G2 may begin the moment Stage 1 is
green, but G1 does not close until the edge criteria are met.

**The wire is JSON over HTTP with version-pathed schema URLs, and MCP is a projection of it.**
Both surfaces are generated from one operation table held as data. The engine's tenth operation
(`previewAction`) is a table entry in both surfaces and never a rewrite — it is in the release G1
pins, so the table starts at ten rows, and the same rule governs the eleventh whenever it arrives.

**The ServiceContract repository is populated as a G1 slice.** G1 is the first real boundary
ADR-005 was waiting on: `mcp-tool-contract.md` moves to `SubZeroDev.ServiceContract`, the engine's
`09-clients.md` link is updated in the same change, and the wire schema is generated from the
engine's types per ADR-005 Rule 2 — a hand-written schema "just for now" is how Rule 2 gets lost.
The generator lives in `SubZeroDev.ServiceContract` and publishes the schema as a consumable
artifact; the workload depends on that rather than generating a copy of its own. The named cost is
a cross-repository release path that does not exist yet — accepted, because a contract home whose
contents nothing consumes is a document, not a boundary.

## Non-goals

The binding list. Everything here is out of scope for every agent until this file changes.

- **Durable persistence.** In-memory stores only. Real `SessionStore` and `ProfileStore`
  implementations, and compare-and-swap on the sequence number, are G2 — the byte-identity proof
  must exist before persistence can be checked against it.
- **Principals.** No authentication, no ownership checks, no authorization decorator, no account
  surface. All G3. G1's deployment surface is trusted-local only, and nothing may be designed on
  the assumption that an untrusted caller reaches it.
- **Tenancy, billing, metering, catalogue, publishing.** G4 and later.
- **A raw-state endpoint, under any name.** Not staged — permanent. The projection boundary
  survives the transport: responses carry a projected `Scene`, never the envelope, and no hosted
  endpoint returns engine state. Not for caching, not for debugging. The byte-identity proof's
  in-process serialization of the store is not an exception: it is not an endpoint, and building
  one to serve it would be.
- **A tenth game operation invented here.** A hosting need the store does not meet is a new store
  operation *in the engine* plus a coverage-checklist row, never transport-side logic. The account
  operations a hosted service will eventually need (`list_saves`, `delete_account`) are the
  account surface — G3's, and never merged with the game surface.
- **Edge scope creep.** The edge terminates transport, routes, traces, and serves probes. No
  authorization, no persistence, no rate-limiting sophistication. A widened edge is G3 pulled
  into G1, losing G1's virtue as the cheapest informative failure.
- **Authoring `previewAction`, or any change to engine behaviour.** The specified tenth operation is
  the engine's to write, not this effort's. A behaviour change made to ease hosting is transport-side
  logic wearing a different hat. **Consuming it is not the same thing**, and as of 2026-08-08 G1
  does: `previewAction` is already merged on the engine's `main`, the release S1 cuts carries it, and
  the operation table therefore has ten rows rather than nine. Not building it is the binding rule;
  refusing to route an operation the pinned engine exports would fail the arity gate instead.
  **One carve-out, decided 2026-08-08:** the engine gains a
  host-suppliable source for session and save ids on its session composition root, defaulting to
  what it does today. It changes no game and cannot — those ids never enter game state, which is
  the engine's own stated reason for minting them where it does — and without it the byte-identity
  criterion below is unachievable rather than merely hard, because three operations return a fresh
  random id in every run. G1's deliverables into the engine are therefore two: that seam, and the
  coverage-checklist column.
- **A human-facing interface.** No front end, no playground, no operator console. G1's audience is
  a test suite and a trace.
- **Reachability beyond trusted-local.** No public exposure, no transport security, no
  cross-origin access. Designing for a caller who cannot reach the deployment is G3 arriving early.
- **Performance work.** No latency or throughput target, no load test, no benchmark. G1 answers
  whether the bytes match, not how fast they arrive.
- **Serving more than one wire version at once.** Version-pathed URLs are required so a second
  version *can* exist later. A second version existing now is not.
- **Deployment machinery.** No container images, no release publishing of the workload, no process
  supervision. Two processes started by hand is the whole of G1's operations story.
- **Session lifecycle management.** No eviction, no expiry, no quotas, no size limits. Sessions are
  lost on restart and bounded by my own use of them; anything cleverer is G2 sizing a store it does
  not have yet.
- **Observability beyond the single trace.** No metrics, no dashboards, no log aggregation, no
  alerting. One trace across the language boundary is Stage 2's evidence, not the first of a set.

## Definition of done

**Stage 1 — the Node service:**

- The engine's own invariant, with a fifth client: the same arc, the same seed, the same counting
  `IdSource`, the same counting record-id source and the same choices, played through the **hosted
  transport**, serialize
  **byte-identically** to the in-process run. **Two comparisons, asserted separately:** the hosted
  service's own serialization of its store at the end of the replay, against the in-process run —
  the engine invariant surviving hosting; and the projected responses of the two runs against each
  other — the wire being deterministic. Neither alone answers §5. The first reaches around the
  wire; the second proves only that the projection is stable.
- **The gate has failed at least once.** A deliberately perturbed run — one action reordered, or
  one response substituted — goes red. A byte-identity suite that has never failed is not known to
  compare anything.
- Every store operation is exercised through the hosted surface, and the engine's API coverage
  checklist gains its fifth column — **delivered as a PR against `SubZeroDev.GameEngine`**, opened
  by the slice that produced the evidence.
- Both surfaces — HTTP JSON and the MCP projection — are generated from the one operation table,
  and a test proves the table is the only source: removing a row breaks both.
- `mcp-tool-contract.md` lives in `SubZeroDev.ServiceContract`, the engine's `09-clients.md` links
  to it there, and the wire schema is generated, not authored.
- **Misuse has a defined answer.** A malformed payload, an unknown session, and an unsupported wire
  version each produce a specified, tested response. A surface whose behaviour under misuse is
  undefined is not a wire — and whatever is chosen here, G2's persistence and G3's principals
  inherit.
- **The projection boundary is gated, not merely promised.** A test asserts that no hosted endpoint
  returns the envelope. It is the one non-goal declared permanent; it is the one most worth a gate.
- **Real responses validate against the generated schema.** Both surfaces deriving from the one
  table does not make the schema true of what the service actually sends.
- **One operation is carried out through the MCP projection end-to-end.** Proving the projection is
  generated is not proving it works.
- **The workload reads the contract from `SubZeroDev.ServiceContract`, not a local copy.** A
  contract that lives in the right repository but is consumed from the wrong one has moved nothing.

**Stage 2 — the edge:**

- The same byte-identity replay passes through **two hops** — edge to Node service — unchanged.
- One distributed trace spans the .NET edge and the Node workload, visible in Platform's
  telemetry, correlation intact across the language boundary.
- The edge is composed by **nothing but Platform's standard registration call** — health,
  readiness, correlation and telemetry included. Bespoke wiring here would fail D3's own
  done-criterion at its first consumer outside `samples/`.
- **The probes are exercised by a test**, and readiness's meaning when the Node service is
  unreachable is decided and asserted. A probe nobody has watched fail is the byte-identity suite's
  problem in another costume.
- **Stage 1's single-hop replay is still green after the edge lands.** Two hops passing is not
  evidence that one still does.

**Both stages:**

- **The evidence runs in CI from a fresh clone**, not only on my machine. A proof that exists once,
  on one working copy, is an anecdote.
- **A gate fails the build if a Platform package references `workloads/game-service/`.** Decision 1
  states the dependency rule; a rule with no gate is a comment.
- **The repository tells a reader how to start both processes, replay the byte-identity proof, and
  regenerate the contract.** G2 begins by rerunning G1's proof; it should not begin by
  reconstructing it.

## Environment

The Node service consumes `@the-running-dev/game-engine` from GitHub Packages over authenticated
restore, current LTS Node. It is a sibling process to Platform's web and worker hosts under the
same self-host constraint set as D3: local developer execution, homelab, single-server. **Fully
offline** — nothing at startup or in steady state requires outbound network.

Code lives in this repository under `workloads/game-service/` — the decision, its rejected
alternatives, and its named cost to §8.2's external-validation claim are in
[`design/90-decisions.md`](90-decisions.md), 2026-08-08.

Scale is one: a single service instance, in-memory state, sessions lost on restart by design.
Anything that survives a restart is G2 arriving early.

## Lifespan

**Stage scaffolding, except the seams.** The in-memory composition is disposable — G2 replaces the
stores, G3 wraps them. What must be built to last: the operation table as data, the projection
boundary, and the generated contract in ServiceContract. Those three survive every later stage;
everything else in Stage 1 is allowed to be replaced without ceremony.

> **One tension this brief does not resolve, stated rather than hidden.** The edge is valued by
> §8.2 as *genuine external* validation, and it now lives in the framework's own repository. The
> byte-identity criterion and the real distributed trace keep their value; the independence claim
> is weakened, accepted, and recorded — not denied.

---

## Decisions taken here that override a recommendation elsewhere

1. **G1 is built in this repository.** `implementation-plan.md` §5 framed G1 as proceeding
   "independently of Platform", and `AGENTS.md` holds that GEaaS is a hosted workload, not what
   this repository is. Overridden: one repository, `workloads/game-service/`, product visibly
   outside `src/`. The dependency rule is unchanged — a reference from a Platform package to the
   workload remains a build failure.

2. **The first wire is settled at G1, not at the edge.** ADR-005 named JSON over HTTP as the first
   wire but left open when it would exist; the GEaaS docs read MCP-first. Settled: the Node
   service exposes the JSON wire from the start and MCP is a projection of the same table, so the
   edge proxies an existing wire rather than defining one.

3. **ADR-005's "next concrete step" stops being deferred.** The ServiceContract repository is
   populated by a G1 slice rather than "on its own schedule" — the first real boundary is here,
   and shipping it without its contract home is the drift ADR-005 exists to prevent.
