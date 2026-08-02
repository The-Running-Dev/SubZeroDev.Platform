---
sidebar_position: 9
sidebar_label: Implementation Plan
---

# Implementation Plan

**Document status:** Design. The ordered plan; each stage names its own done-criteria.

> **Scope of this document**
>
> What to build, in what order, and how each stage is verified. It reconciles two schedules
> that were written independently: the ecosystem phase roadmap, and the Game Engine's own
> post-MVP programme.
>
> **Phase numbers below refer to the ecosystem roadmap.** This document does not maintain a
> second numbering — that rule exists because "Phase One" once meant three different things.

---

## 1. Where Things Actually Stand

Measured, not assumed.

| | State |
|---|---|
| Game Engine | **Past MVP.** Every box in its definition of done is checked against a named test. Two further kinds and four campaign arcs have followed. Currently packaging itself for external consumption |
| Platform | **Documents only.** No packages, no code. The six near-term packages are unstarted |
| Automator | Specified, unstarted |
| Plugin contract | Specified, schemas written and validated, unpublished |
| This repository | Identity reconciled by this change; previously described itself as the game-hosting product |

The gate that used to block hosting work — *"not before the engine MVP is done"* — **is
satisfied.** The engine's own order of operations puts the unified API and MCP surface as
step 3 of 4, and that is the open step.

---

## 2. The Two Tracks

Platform and Game Engine hosting are **not one sequence**, and treating them as one is the
main scheduling error available here.

```text
Track A — Platform     P3 → P4 → P5      (Phase 2, then Phase 5, then Phase 8)
Track B — GEaaS        G1 → G2 → G3 → G4  (blocked on the engine, not on Platform)
```

Track A sits **off the ecosystem critical path** and can proceed in parallel with plugin and
Automator work. Track B blocks on the engine's packaging, not on Platform — which means the
first hosted increment can be built and proven **before** Platform has anything to offer it,
and adopt Platform later.

That is deliberate, and it is the same reasoning the extraction guard applies: build the
product first, extract when a second consumer proves the shape.

---

## 3. Completed by This Change

**P0 — Repository identity.** Platform is the reusable framework and hosting layer; Game
Engine as a Service is one hosted workload. The four ecosystem Platform specifications are
brought in. The engine is renamed from "Narrative Engine" to "Game Engine" throughout.
→ [Platform Identity](platform-identity.md)

**P1 — The engine hosting contract.** Workload hosting rather than in-process ports; who
owns what; two surfaces never merged; and the four questions a hosted deployment must answer
that an in-process one never had to.
→ [`engine-hosting-contract.md`](engine-hosting-contract.md)

**P2 — The technology decision, opened.** Three options with costs, and a recommendation.
Explicitly **not blocking**, because P1 was written to survive any answer.
→ [`technology-decision.md`](technology-decision.md)

---

## 4. Track A — Platform

### P3 — The minimal package set *(ecosystem Phase 2)*

Abstractions, Core, Hosting, Persistence, Observability, Testing. Boundaries and
done-criteria are specified in
[`minimal-platform-packages.md`](minimal-platform-packages.md).

**Blocked on:** [`technology-decision.md`](technology-decision.md). This is the first stage
that is, which is why that document exists now rather than later.

**Done when** every package's stated done-criteria are met, and — the one that matters most —
**a product runs on Platform with health, readiness, correlation ids, migrations and
telemetry configured by nothing but the standard registration call.** A framework whose first
consumer needs bespoke wiring has not proven anything.

### P4 — Extraction, on evidence *(ecosystem Phase 5)*

Configuration, Events, Notifications, Storage, BackgroundJobs, Scheduling, Api — extracted
from the Automator once a second consumer exists.

**Done when** each extracted package has **two** named consumers in the repository, not one
and a plan. The guard is only worth having if it is applied at the moment it is inconvenient.

### P5 — Commercial *(ecosystem Phase 8)*

Identity, Authorization, Organizations, Tenancy, Billing, Licensing, Audit, shared web UI.
Shapes are specified in
[`second-consumer-packages.md`](second-consumer-packages.md).

**Done when** the divergences that document names are satisfied for **both** consumers —
particularly: tenancy models deliberately shared resources explicitly, and `Platform.Mcp`
accepts tool definitions from a producer other than manifest projection.

> **The tenant column does not wait for this stage.** It ships with Persistence in P3.

---

## 5. Track B — Game Engine as a Service

### G1 — One session, over the wire

A single service consuming the published engine package, composing the in-memory stores,
exposing the tool surface over a **real MCP transport**. No database, no accounts, no
billing, no tenancy.

**Blocked on:** the engine's consumer-boundary work being merged and a version published.
Nothing else.

**Done when** — and this is the criterion worth having, because the engine already knows how
to write it:

> The same arc, the same seed, the same counting `IdSource` and the same choices, played
> through the **hosted transport**, serialize byte-identically to the in-process run.

That is the engine's own client-contract invariant with a third client, and it proves the
transport is a projection rather than a participant. Plus the API coverage checklist gains a
third column — every store operation exercised through the hosted surface.

> **Why this is first.** It is the cheapest thing that can fail informatively. If a hosted
> transport cannot reproduce a game byte-for-byte, everything above it is built on sand, and
> that is knowable in the smallest possible increment.

### G2 — Durable sessions

Real `SessionStore` and `ProfileStore` implementations against provisioned persistence, with
compare-and-swap on the sequence number
([`engine-hosting-contract.md`](engine-hosting-contract.md) §6.1).

**Done when** a committed replay fixture round-trips through the durable store
byte-identically to the in-memory one, **and** two concurrent actions against one session
produce one success and one explicit rejection — never a silent overwrite.

> **This stage resolves an open engine question.** The engine records that its session-layer
> composition root has two real call sites and zero real implementations of the abstraction
> it specifies, to be revisited *"when a second `SessionStore` implementation is actually
> needed."* This is that implementation.

### G3 — Principals

Authentication, ownership, and the authorization decorator
([`engine-hosting-contract.md`](engine-hosting-contract.md) §6.2, §6.3). The account surface
appears here, separate from the game surface (§5).

**Done when** the decorated store produces byte-identical `serialize()` output to the
undecorated one, a session id belonging to another principal returns a refusal rather than
data, and no hosted endpoint returns engine state.

**Adopts Platform's Identity** if P5 has landed; otherwise implements it locally and becomes
the second-consumer evidence that promotes it. Either order is fine — that is the guard
working.

### G4 — Catalogue, publishing, metering

Content packs as the hosted artifact, resolution identity stamped into campaign versions,
and an event-derived metering feed that billing consumes.

**Blocked on:** content-pack resolution existing in engine code. Specified, unbuilt.

### Still deferred

Multiplayer, white-label, AI-assisted authoring, and community modding — the last of which
is a trust question this product owns, not a merge-semantics question the engine has since
answered.

---

## 6. Ordering Constraints

The things that genuinely cannot be reordered:

| Constraint | Why |
|---|---|
| Engine packaging **→** G1 | Nothing can consume the engine until it packs and installs |
| G1 **→** G2 | The byte-identity proof must exist before persistence can be checked against it |
| Technology decision **→** P3 | Package identifiers and the module contract depend on it |
| Persistence **→** everything with a tenant | The column ships in the first schema or it is a migration on every table |
| G2 **→** engine's composition-root question | The second implementation is what makes the question answerable |

Everything else is genuinely parallel, including the whole of Track A against Track B.

---

## 7. What Would Make This Plan Wrong

Recorded so the assumptions are visible rather than implied.

- **If the technology decision resolves to TypeScript**, Track A's package names and the
  module contract change, and the transferred ecosystem specifications stop being verbatim.
  The boundaries in [`minimal-platform-packages.md`](minimal-platform-packages.md) survive;
  the layout does not.
- **If GEaaS is deprioritized indefinitely**, the extraction guard loses its second consumer
  and Identity, Tenancy, Billing and Mcp go back to being speculative. They should then not
  be built.
- **If the Automator ships first and grows its own identity and tenancy**, that is the guard
  working as designed, not a failure — extraction follows the second consumer, and P4 is
  where it lands.
