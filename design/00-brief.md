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
contract gains its third client and whose coverage checklist gains its third column.

No player, creator, or third-party developer is served by G1 directly. That is deliberate: G1 is
infrastructure proof, and pretending otherwise would smuggle G3's account surface and G4's
catalogue into scope through the audience.

## Scope — two stages, one effort

Settled in [`implementation-plan.md`](../docs/docs/implementation-plan.md) §8.4 and transcribed
here because the brief is where scope binds:

1. **The Node service.** A single Node service consuming the published engine package, composing
   the in-memory stores, exposing the nine-operation game surface. It consumes the engine and not
   Platform.
2. **The .NET edge, as a fast follow.** Platform's packages in front of that workload: transport
   termination, routing to the Node service, a distributed trace across the language boundary.
   The edge is Platform's first consumer outside `samples/`.

The stages are ordered — the byte-identity proof exists before the edge does — and the edge does
not gate G2.

**The wire is JSON over HTTP with version-pathed schema URLs, and MCP is a projection of it.**
Both surfaces are generated from one operation table held as data. The engine's specified tenth
operation (`previewAction`, arriving with `world-graph`) must land as a table entry in both
surfaces, never a rewrite.

**The ServiceContract repository is populated as a G1 slice.** G1 is the first real boundary
ADR-005 was waiting on: `mcp-tool-contract.md` moves to `SubZeroDev.ServiceContract`, the engine's
`09-clients.md` link is updated in the same change, and the wire schema is generated from the
engine's types per ADR-005 Rule 2 — a hand-written schema "just for now" is how Rule 2 gets lost.

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
  endpoint returns engine state. Not for caching, not for debugging.
- **A tenth game operation invented here.** A hosting need the store does not meet is a new store
  operation *in the engine* plus a coverage-checklist row, never transport-side logic. The account
  operations a hosted service will eventually need (`list_saves`, `delete_account`) are the
  account surface — G3's, and never merged with the game surface.
- **Edge scope creep.** The edge terminates transport, routes, traces, and serves probes. No
  authorization, no persistence, no rate-limiting sophistication. A widened edge is G3 pulled
  into G1, losing G1's virtue as the cheapest informative failure.

## Definition of done

**Stage 1 — the Node service:**

- The engine's own invariant, with a third client: the same arc, the same seed, the same counting
  `IdSource` and the same choices, played through the **hosted transport**, serialize
  **byte-identically** to the in-process run.
- **The gate has failed at least once.** A deliberately perturbed run — one action reordered, or
  one response substituted — goes red. A byte-identity suite that has never failed is not known to
  compare anything.
- Every store operation is exercised through the hosted surface, and the engine's API coverage
  checklist gains its third column — **delivered as a PR against `SubZeroDev.GameEngine`**, opened
  by the slice that produced the evidence.
- Both surfaces — HTTP JSON and the MCP projection — are generated from the one operation table,
  and a test proves the table is the only source: removing a row breaks both.
- `mcp-tool-contract.md` lives in `SubZeroDev.ServiceContract`, the engine's `09-clients.md` links
  to it there, and the wire schema is generated, not authored.

**Stage 2 — the edge:**

- The same byte-identity replay passes through **two hops** — edge to Node service — unchanged.
- One distributed trace spans the .NET edge and the Node workload, visible in Platform's
  telemetry, correlation intact across the language boundary.
- The edge is composed by **nothing but Platform's standard registration call** — health,
  readiness, correlation and telemetry included. Bespoke wiring here would fail D3's own
  done-criterion at its first consumer outside `samples/`.

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
