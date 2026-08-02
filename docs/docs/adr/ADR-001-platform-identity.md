---
sidebar_position: 1
sidebar_label: ADR-001 Platform Identity
---

# ADR-001: `SubZeroDev.Platform` Is the Framework, Not the Game Product

## Status

Accepted

## Context

Two different things were called `SubZeroDev.Platform`, and both had written
specifications.

This repository described itself as **NEaaS — Narrative Engine as a Service**: the hosting,
accounts, billing and cloud-sync layer for the Game Engine. A product.

The ecosystem specification staging tree described `SubZeroDev.Platform` as **a reusable
application framework**, "comparable in purpose to ABP Framework" — hosting, configuration,
identity, authorization, tenancy, billing, observability — with six near-term packages, a
phase roadmap, and a dependency rule placing it *underneath* the Automator. Infrastructure.

Its own repository-layout table lists `SubZeroDev.Platform` as *"The reusable application
framework. **Exists.**"*, pointing at this repository. It did not exist here.

**The two bodies of work had no knowledge of each other.** The staging tree contains no
mention of the game work at all — not "narrative", not "GameEngine", not "GameOfLife", not
"SunTrap", not the word "game". Neither document set was wrong; each was written as though
it were the only one.

This was predicted. ADR-002 in the ecosystem set argued that *"'Platform' is a category, not
a name … it becomes ambiguous the moment a second thing could be called one"*, considered
renaming, and kept the name while accepting "occasional conversational ambiguity". What
actually materialized was a repository collision.

## Decision

**`SubZeroDev.Platform` is the reusable application framework and hosting layer.** Game
Engine as a Service is a **product it hosts** — a sibling of the Automator, not the thing
this repository is.

```text
              SubZeroDev.Platform
                 ↓            ↓
        SubZeroDev.Automator   Game Engine as a Service
                 ↓
      Plugins / Workflows / Products
```

Platform never depends on a product, and never on a plugin.

Two reasons, in order of weight:

1. **The ecosystem's own boundary test decides it.** *A concern belongs in Platform when a
   non-automation product would want it unchanged.* Game hosting is a product however much
   infrastructure it needs — sessions, saves, campaign catalogues and projection boundaries
   are product concepts wearing infrastructure clothes, exactly as execution records and
   workflow state are.
2. **Everything else already assumes it.** Every accepted ecosystem decision, the phase
   roadmap, the repository-layout table, the package-identifier reservations, and the
   dependency rule are written against the framework reading. The product reading is held by
   two documents in one repository.

The naming ADR is **not** reopened. It remains correct that the name stays; what is added is
that the name is now *assigned deliberately* rather than assumed independently by two
document sets.

## Consequences

- **The game-hosting vision is demoted** from "what this repository is" to "one workload it
  hosts". Its content survives intact in `game-engine-as-a-service.md`.
- **The engine is renamed** from "Narrative Engine" to **Game Engine**, so NEaaS becomes
  GEaaS. Independently justified: the engine ships three kinds — `story-graph`, `simulation`
  and `world-graph` — and a life simulation and a resort-management sim are not narratives.
  "Narrative" now describes one kind, not the engine.
- **The extraction guard gains its second consumer.** It was written with only the Automator
  in view. Identity, Tenancy, Billing, Mcp and Storage now have two unrelated consumers,
  which moves them from speculative to justified — see `second-consumer-packages.md`. This
  is the guard being satisfied, not bypassed.
- **`Platform.Mcp` inherits a constraint** it would not otherwise have had, because the two
  consumers produce tool definitions differently. Recorded in `second-consumer-packages.md`
  §4.
- **Platform's implementation technology becomes a live question** — it was assumed rather
  than decided, and the second consumer is a different runtime. See
  [ADR-002](ADR-002-implementation-technology.md).
- **The remaining ecosystem Platform specifications move here**, and their originals are
  deleted, per the *move, never copy* rule.

## Alternatives considered

**Give the framework a different name, and keep NEaaS here.** Would resolve the collision
without touching this repository's contents. Rejected: it reopens a settled naming ADR whose
reasoning still holds, invalidates the identifier-reservation plan, and requires edits across
a specification set of roughly ninety files — to relocate the thing that has fifteen
repositories and a roadmap depending on it, in favour of the thing that has two documents.

**Both keep the name, disambiguated by context.** Rejected outright: the naming ADR's own
answer to ambiguity is that *"'Platform' is unqualified only inside its own repository"*. Two
repositories both named Platform breaks precisely that rule, and it is the situation that
produced this ADR.

**Merge them — make the framework and the game host one product.** Rejected: it is the exact
failure ADR-001 in the ecosystem set exists to prevent. The reusable half would acquire
game-shaped assumptions — a storage abstraction that knows what a save is, a tenancy model
built around studios — and by the time a third product wanted it, extraction would mean
untangling rather than referencing.
