---
sidebar_position: 10
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

:::note D3 scope is settled; reuse remains mandatory

ADR-004 established that .NET already ships much of what the six packages describe, leaving narrow
gaps — outbox, tenant filtering, module conventions — and requires those gaps to be checked against
existing NuGet packages before anything is written. The [D3 brief](https://github.com/The-Running-Dev/SubZeroDev.Platform/blob/main/design/d3/00-brief.md)
settles D3 as **all six packages**, which land together; the check against existing packages remains
mandatory and is not a licence to hand-roll six.

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

> **The count is settled.** The [D3 brief](https://github.com/The-Running-Dev/SubZeroDev.Platform/blob/main/design/d3/00-brief.md) commits D3 to all six
> packages landing together. [ADR-004](adr/ADR-004-framework-build-not-adopt.md) still requires the
> narrow gaps — outbox, tenant filtering, module conventions — to be evaluated against existing
> packages before anything is written; six packages is not a licence to hand-roll six.

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

**Done when** each extracted package has **two** named consumers, not one and a plan, **and at
least one of them actually runs on the package before it reaches 1.0.** The guard is only worth
having if it is applied at the moment it is inconvenient.

> **This wording was amended, and the amendment is a loosening.** It read *"two named consumers in
> the repository"*, and [ADR-007](adr/ADR-007-second-hosted-workload.md) removed *in the
> repository* so that a consumer living in its own repository counts — SkyNet HR, admitted as a
> second hosted workload and deliberately not brought in-tree. Those three words were doing real
> work: a consumer inside the tree is checkable by a build, and one outside it is asserted. The
> deploy condition above is the replacement, and it is weaker than what it replaces. ADR-007 states
> that cost rather than absorbing it.

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

> **One thing G1 had to decide that this stage did not anticipate.** Platform is .NET
> ([ADR-002](adr/ADR-002-implementation-technology.md)) and the engine is Node, so a G1 written
> as a single Node service consumes the engine but **not Platform**. If G1 is also meant to be
> Platform's first consumer, it splits: a .NET edge on Platform in front of the Node engine
> workload. **Decided — see §8.4:** both, in sequence. The thin Node-only service first; the
> .NET edge in front of it as a fast follow, once the byte-identity proof exists.

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
- **If GEaaS is deprioritized indefinitely**, the guard no longer loses everything with it — this
  risk was written when GEaaS was the only second consumer, and BarStrad is now a third. Identity,
  Tenancy and Mcp keep a second consumer and stay justified; **Billing goes back to being
  speculative**, since BarStrad's commercial model is unsettled and an unsettled consumer is not a
  consumer. Notifications and localized content are unaffected — BarStrad wants both independently
  of the engine. **Read the row-by-row count in [Platform Identity](platform-identity.md) §4 before
  mothballing anything**; the earlier wording would have retired four packages at once, and it is
  the kind of statement someone acts on rather than weighs.
- **If the Automator ships first and grows its own identity and tenancy**, that is the guard
  working as designed, not a failure — extraction follows the second consumer, and D4 is
  where it lands.

---

## 8. Decisions and Remaining Questions

Nothing external gates either track now. The first two decisions below are settled by the brief;
the contract decision is settled by ADR-005. They remain here to make the planning consequences
visible rather than quietly assumed.

### 8.1 How much of D3 is built — **decided: six packages**

[ADR-004](adr/ADR-004-framework-build-not-adopt.md) narrowed this twice: .NET already ships most
of what the six packages describe, and the narrow gaps that remain — transactional outbox, tenant
column and query filtering, module registration conventions — must each be checked against
existing NuGet packages before anything is written.

The [D3 brief](https://github.com/The-Running-Dev/SubZeroDev.Platform/blob/main/design/d3/00-brief.md) settles D3 at **six packages** — Abstractions,
Core, Hosting, Persistence, Observability and Testing — and requires them to land together. The
evaluation against existing packages remains a prerequisite for each narrow gap; it informs whether
the capability is taken or built, not whether D3 includes the package.

### 8.2 What proves Platform works — **decided: a sample and the G1 edge**

**A sample in `samples/`, run in CI, is the definition of done for D3; the G1 edge is its first
genuine external validation.** The brief requires both instruments: the sample now, with its
requirements derived from the edge's needs, and the edge when it exists. The sample keeps D3
independent of Track B; the edge challenges the framework-authored proof without delaying D3.

It also keeps Track A genuinely parallel: no cross-repository coupling, and D3 does not wait on G1.

**The known weakness, stated rather than hidden:** a sample is written by the framework's authors,
so it confirms rather than challenges. Deriving its requirements from the G1 edge and treating that
edge as the first genuine validation mitigates rather than removes the weakness. Any API change the
edge forces is expected and cheap while Platform is still `0.x`.

<details>
<summary>The options as they were weighed, kept for the reasoning</summary>

| Instrument | Proves well | Proves badly |
|---|---|---|
| A sample in `samples/` | Persistence and Testing — a kill test mid-outbox is controllable | Written by the framework's authors, so it confirms rather than challenges |
| The G1 .NET edge | Hosting, and a *real* distributed trace to a Node workload | Persistence and Testing — a thin proxy has little durable state |
| A widened edge | Everything | It is G3 pulled into G1, losing G1's virtue as the cheapest informative failure |

</details>

> **The rule the choice has to satisfy**, whichever instrument is used: a package whose only proof
> is its own unit tests reaches a committed public API with no consumer, which is exactly what the
> extraction guard exists to prevent. The sample is the consumer until the edge is.

### 8.3 The contract between hosted things — **decided: ADR-005**

Settled by [ADR-005](adr/ADR-005-service-contract.md): boundary contracts are **projected from
their source of truth, never authored alongside it**, and they live in `SubZeroDev.ServiceContract`
— its own public repository, depended on by products, depending on nothing.

protobuf was evaluated and **declined for now**. It would create a second, hand-maintained
definition of types the engine already owns, in a codebase whose recurring defect is two copies
disagreeing. Revisited when a boundary needs streaming or payloads where JSON's size is measurable
— and generated rather than authored when it is. JSON over HTTP with version-pathed schema URLs is
the first wire. MCP stays a projection, per the ecosystem's own MCP decision.

**Outstanding from that decision**, each with a named cost rather than left implicit:

- **The repository does not exist yet.** Creating it is the next concrete step.
- **`mcp-tool-contract.md` is its natural first content**, and moving it means updating the engine's
  `09-clients.md`, which links to it by URL. ADR-005 deliberately does not move it.
- **A generated schema needs a generator.** Rule 2 is cheap to state and not cheap to honour; a
  hand-written schema "just for now" is how it gets lost.

> **8.2 and 8.3 no longer interact.** Choosing the sample for 8.2 means the contract is not needed
> to prove Platform — so the repository can be created and populated on its own schedule, driven by
> the first real boundary rather than by D3's definition of done.

### 8.4 The shape of G1 — **decided: Node-only first, the .NET edge as a fast follow**

The split named in §5's G1 note is resolved as a sequence rather than a choice. **First**, a thin
Node-only service consuming the published engine package — in-memory stores, the ten-operation
game surface as a JSON-over-HTTP wire with the MCP projection beside it, and G1's byte-identity
done-criterion. It consumes the
engine and not Platform, which keeps it the cheapest informative failure. **Then**, as a fast
follow, the .NET edge in front of that workload: Platform's packages terminating transport,
routing to the Node service, and carrying a distributed trace across the language boundary — the
validation §8.2 assigns to the edge. The edge does not gate G2; the byte-identity proof exists
after the first stage.

**Where it runs, and what that costs.** G1 is built in this repository, under
`workloads/game-service/`, rather than in a repository of its own — decided 2026-08-08, with the
reasoning and the rejected alternatives in `design/g1/90-decisions.md`. The named cost: §8.2 valued
the edge as *genuine external* validation, and an edge living in Platform's own repository is
nearer to framework-authored proof. The byte-identity criterion and the real distributed trace
keep their value; the independence claim weakens, and this section says so rather than quietly
dropping the word "external".
