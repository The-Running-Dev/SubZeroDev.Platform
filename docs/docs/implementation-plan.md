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
> **"Phase N" always means the ecosystem roadmap's phase, and nothing else.** This document
> does not maintain a second phase numbering — that rule exists because "Phase One" once
> meant three different things across the specification set.
>
> **`D` and `G` are stage labels, not phases.** `D0–D5` are this repository's design and
> build stages; `G1–G4` are the Game Engine hosting stages. They are deliberately *not*
> numbered `P0–P5`, which is how they read at first and which caused a real misreading: `D3`
> was taken for ecosystem Phase 3. It is not — **`D3` is ecosystem Phase 2.** Each stage below
> names its phase explicitly where one applies, and where a stage has no ecosystem phase, it
> has none because Track B is this repository's own work.

---

## 1. Where Things Actually Stand

Measured, not assumed.

| | State |
|---|---|
| Game Engine | **Past MVP, and now consumable.** `@the-running-dev/game-engine` **0.4.0 is published** to GitHub Packages with a 26-entry public surface. Two further kinds and four campaign arcs beyond the MVP |
| Platform | **Documents only.** No packages, no code. Scope is open — see the caution below |
| Automator | Specified, unstarted |
| Plugin contract | **Its own repository**, [SubZeroDev.PluginContract](https://github.com/The-Running-Dev/SubZeroDev.PluginContract), public, tagged `v0.1.0`. Schemas written and validated; not yet published at version-pathed URLs |
| Architecture specs | **Version-controlled**, [SubZeroDev.Architecture](https://github.com/The-Running-Dev/SubZeroDev.Architecture), private |
| This repository | Identity settled ([ADR-001](adr/ADR-001-platform-identity.md)); framework question settled ([ADR-004](adr/ADR-004-framework-build-not-adopt.md)) |

:::caution Platform's scope is smaller than this document was written against

Two decisions narrowed it after the fact, and neither is reflected in the D3 stage below.
[ADR-004](adr/ADR-004-framework-build-not-adopt.md) established that .NET already ships most of
what the six packages describe, leaving narrow gaps — outbox, tenant filtering, module
conventions — and then that those gaps should be checked against existing NuGet packages before
anything is written. **D3 may be three packages, or fewer.** Treat the six below as the boundary
catalogue it is, not as a build list.

:::

The gate that used to block hosting work — *"not before the engine MVP is done"* — **is
satisfied.** The engine's own order of operations puts the unified API and MCP surface as
step 3 of 4, and that is the open step.

---

## 2. The Two Tracks

Platform and Game Engine hosting are **not one sequence**, and treating them as one is the
main scheduling error available here.

```text
Track A — Platform     D3 → D4 → D5      (Phase 2, then Phase 5, then Phase 8)
Track B — GEaaS        G1 → G2 → G3 → G4  (unblocked — the engine package is published)
```

Track A sits **off the ecosystem critical path** and can proceed in parallel with plugin and
Automator work. Track B used to block on the engine's packaging; **it no longer does** — 0.4.0 is
published, so G1 can start whenever it is wanted, independently of Platform, and adopt Platform
later.

That is deliberate, and it is the same reasoning the extraction guard applies: build the
product first, extract when a second consumer proves the shape.

---

## 3. Completed by This Change

**D0 — Repository identity.** Platform is the reusable framework and hosting layer; Game
Engine as a Service is one hosted workload. The four ecosystem Platform specifications are
brought in. The engine is renamed from "Narrative Engine" to "Game Engine" throughout.
→ [Platform Identity](platform-identity.md)

**D1 — The engine hosting contract.** Workload hosting rather than in-process ports; who
owns what; two surfaces never merged; and the four questions a hosted deployment must answer
that an in-process one never had to.
→ [`engine-hosting-contract.md`](engine-hosting-contract.md)

**D2 — The technology decision, taken.** .NET, with the boundary between Platform and a
product it hosts stated as a process boundary rather than left as an accident. D1 was
written to survive any answer and needed no revision when it landed.
→ [ADR-002](adr/ADR-002-implementation-technology.md)

**Also settled:** the repository-identity decision itself
([ADR-001](adr/ADR-001-platform-identity.md)) and the package-scope conflict
([ADR-003](adr/ADR-003-package-scopes-and-registries.md)) — scopes are per-registry, and the
engine's `@the-running-dev` coordinate is forced by GitHub Packages rather than drifted.

---

## 4. Track A — Platform

### D3 — The minimal package set *(ecosystem Phase 2)*

Abstractions, Core, Hosting, Persistence, Observability, Testing. Boundaries and
done-criteria are specified in
[`minimal-platform-packages.md`](minimal-platform-packages.md).

> **The count is provisional.** [ADR-004](adr/ADR-004-framework-build-not-adopt.md) settled that
> Platform builds its own rather than adopting ABP, and in doing so established that .NET already
> provides much of what these six describe. The remaining gaps are narrow — outbox, tenant
> filtering, module conventions — so this stage may be three packages rather than six. The scope
> belongs to the brief; the boundaries do not change either way.

**Was blocked on** the technology decision — the only stage that was, which is why it was
taken early. [ADR-002](adr/ADR-002-implementation-technology.md) settles it: .NET, so the
package names above stand and the persistence baseline is EF Core. **D3 is unblocked and
unstarted.**

**Done when** every package's stated done-criteria are met, and — the one that matters most —
**a product runs on Platform with health, readiness, correlation ids, migrations and
telemetry configured by nothing but the standard registration call.** A framework whose first
consumer needs bespoke wiring has not proven anything.

### D4 — Extraction, on evidence *(ecosystem Phase 5)*

Configuration, Events, Notifications, Storage, BackgroundJobs, Scheduling, Api — extracted
from the Automator once a second consumer exists.

**Done when** each extracted package has **two** named consumers in the repository, not one
and a plan. The guard is only worth having if it is applied at the moment it is inconvenient.

### D5 — Commercial *(ecosystem Phase 8)*

Identity, Authorization, Organizations, Tenancy, Billing, Licensing, Audit, shared web UI.
Shapes are specified in
[`second-consumer-packages.md`](second-consumer-packages.md).

**Done when** the divergences that document names are satisfied for **both** consumers —
particularly: tenancy models deliberately shared resources explicitly, and `Platform.Mcp`
accepts tool definitions from a producer other than manifest projection.

> **The tenant column does not wait for this stage.** It ships with Persistence in D3.

---

## 5. Track B — Game Engine as a Service

### G1 — One session, over the wire

A single service consuming the published engine package, composing the in-memory stores,
exposing the tool surface over a **real MCP transport**. No database, no accounts, no
billing, no tenancy.

**Unblocked.** This stage waited on the engine's consumer-boundary work; that is merged and
`@the-running-dev/game-engine` **0.4.0 is published**. Nothing else gates it.

> **One thing G1 must decide that this stage did not anticipate.** Platform is .NET
> ([ADR-002](adr/ADR-002-implementation-technology.md)) and the engine is Node, so a G1 written
> as a single Node service consumes the engine but **not Platform**. If G1 is also meant to be
> Platform's first consumer, it splits: a .NET edge on Platform in front of the Node engine
> workload. That is a materially different G1, and it is one of the open decisions in §8.

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

**Adopts Platform's Identity** if D5 has landed; otherwise implements it locally and becomes
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

| Constraint | Why | State |
|---|---|---|
| Engine packaging **→** G1 | Nothing can consume the engine until it packs and installs | **Satisfied** — 0.4.0 published |
| Technology decision **→** D3 | Package identifiers and the module contract depend on it | **Satisfied** — ADR-002, ADR-004 |
| G1 **→** G2 | The byte-identity proof must exist before persistence can be checked against it | Open |
| Persistence **→** everything with a tenant | The column ships in the first schema or it is a migration on every table | Open |
| G2 **→** engine's composition-root question | The second implementation is what makes the question answerable | Open |

Everything else is genuinely parallel, including the whole of Track A against Track B.

**Both blocking constraints are now satisfied**, so neither track is externally gated. What gates
them is §8.

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
  working as designed, not a failure — extraction follows the second consumer, and D4 is
  where it lands.

---

## 8. Open Decisions

Nothing external gates either track now. These three do, and each surfaced during design without
being settled — recorded here so they are decisions rather than things that quietly get assumed.

### 8.1 How much of D3 is actually built

[ADR-004](adr/ADR-004-framework-build-not-adopt.md) narrowed this twice: .NET already ships most
of what the six packages describe, and the narrow gaps that remain — transactional outbox, tenant
column and query filtering, module registration conventions — must each be checked against
existing NuGet packages before anything is written.

So the range is genuinely wide: **six packages, three, or nearly none.** The honest next step is
an evaluation of candidate packages against the gaps, and the answer belongs in
`design/00-brief.md` as scope and non-goals.

### 8.2 What proves Platform works

Platform's stated done-criterion is *"a product runs on Platform with health, readiness,
correlation ids, migrations and telemetry configured by nothing but the standard registration
call"* — and neither product exists.

The options were weighed and not chosen:

| Instrument | Proves well | Proves badly |
|---|---|---|
| A sample in `samples/` | Persistence and Testing — a kill test mid-outbox is controllable | Written by the framework's authors, so it confirms rather than challenges |
| The G1 .NET edge | Hosting, and a *real* distributed trace to a Node workload | Persistence and Testing — a thin proxy has little durable state |
| A widened edge | Everything | It is G3 pulled into G1, losing G1's virtue as the cheapest informative failure |

They are not exclusive, and the packages do not all need the same instrument. **Whatever is
chosen, it belongs in the definition of done** — a package whose proof is only its own unit tests
reaches a committed public API with no consumer, which is what the extraction guard exists to
prevent.

### 8.3 The contract between hosted things

[ADR-002](adr/ADR-002-implementation-technology.md) fixed that the product boundary is a *process*
boundary. It did not say what crosses it. With .NET products and a Node engine, the contract is
the only shared artifact, which argues for a real versionable IDL rather than a convention.

Candidates: **protobuf**, giving codegen on both sides; **OpenAPI**, weaker-typed but matching
Platform's existing API conventions and needing no toolchain; or **MCP**, which the ecosystem's own
MCP decision already fixes as *a transport projected from a contract* — so using it as the
substrate would conflate the AI-facing surface with internal contracts rather than layering them.

Whatever is chosen wants the same shape as `SubZeroDev.PluginContract`: depended on by everything,
depending on nothing, in its own repository.

> **8.2 and 8.3 interact.** If G1 is Platform's proving instrument, G1 splits into a .NET edge and
> a Node workload — and the contract between them is 8.3, needed immediately rather than later.
> Choosing a sample instead defers 8.3 until a second product exists.
