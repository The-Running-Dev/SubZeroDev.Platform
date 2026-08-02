---
sidebar_position: 1
sidebar_label: Platform Identity
---

# What SubZeroDev.Platform Is

**Document status:** Current. Settles a collision, and is the reading-order entry point.

> **Scope of this document**
>
> What this repository is, what it is not, and why that was in question. Everything else
> here depends on this answer, so it is stated first.

---

## 1. The Decision

**`SubZeroDev.Platform` is the reusable application framework and hosting layer for
SubZeroDev products.** It supplies cross-cutting infrastructure — hosting, configuration,
identity, authorization, tenancy, billing, notifications, storage, events, observability,
API and MCP conventions — once, so that each product does not build them again.

It is **not** a game-hosting product. Game Engine as a Service is one *workload* Platform
hosts, specified in [`game-engine-as-a-service.md`](game-engine-as-a-service.md), in
exactly the relationship the Automator has to Platform.

```text
              SubZeroDev.Platform
                 ↓            ↓
        SubZeroDev.Automator   Game Engine as a Service
                 ↓
      Plugins / Workflows / Products
```

**Platform never depends on a product, and never on a plugin.** The dependency direction is
the whole content of the rule, and it is checkable: a reference from Platform to Automator
or to the Game Engine is a build failure, not a review comment.

---

## 2. Why This Needed Stating

Two different things were called `SubZeroDev.Platform`, and both had written
specifications.

| | This repository, as it stood | The ecosystem specification set |
|---|---|---|
| Described as | NEaaS — hosting for narrative games | A reusable application framework, "comparable in purpose to ABP" |
| Position | A product on top of an engine | Infrastructure *underneath* products |
| Artifacts | Docs only | Six near-term packages, phased roadmap |

The ecosystem set's own repository-layout table lists `SubZeroDev.Platform` as *"The
reusable application framework. Exists."* — pointing here. It did not exist here; this
repository held the game-hosting vision instead.

The confirming check: **the ecosystem specification set contains no mention** of the game
work — not "narrative", not "GameEngine", not "GameOfLife", not "SunTrap", not the word
"game". The two bodies of work were written without knowledge of each other.

> **This was predicted.** The ecosystem's own root-naming decision argued that *"'Platform'
> is a category, not a name … it becomes ambiguous the moment a second thing could be
> called one"*, considered renaming, and kept the name while accepting "occasional
> conversational ambiguity". What actually materialized was a repository collision rather
> than a conversational one. The decision to keep the name still stands — it is the
> ambiguity that is now resolved, by assigning the name deliberately rather than by
> letting two sets of documents each assume it.

**Why the framework keeps the name.** Every accepted decision in the ecosystem set, the
phase roadmap, the repository-layout table, the package-identifier reservations, and the
`Platform → Automator → Plugins` dependency rule all assume it. Game Engine as a Service is
a product, and under the ecosystem's own boundary test — *a concern belongs in Platform when
a non-automation product would want it unchanged* — a product is not Platform however much
infrastructure it needs.

---

## 3. What Belongs Here, and What Does Not

The boundary test and the extraction guard are inherited unchanged from the ecosystem set,
and they decide every question about this repository's contents.

**The boundary test.** A concern belongs in Platform when a *second, unrelated* product
would want it unchanged. Execution records, plugin manifests, workflow state, campaign
content, session envelopes and save files all fail that test however infrastructural they
look — they are product concepts wearing infrastructure clothes.

**The extraction guard.** A candidate becomes a Platform package when a **second** consumer
needs it, not when the first one does. Until then it lives inside the product that wants
it, where it is cheap to move.

| Belongs in Platform | Belongs in a product |
|---|---|
| Hosting, configuration, DI, startup validation | Workflow state, session state |
| Persistence and transaction boundaries | Schema for executions or game saves |
| Observability wiring | What a specific event means |
| Identity, authorization, tenancy | Who may run *this* plugin, or own *that* save |
| Billing primitives — plans, entitlements, metering | Which dimensions a product meters |
| MCP transport, auth, consent, tool registration | Which tools exist, and where they come from |

---

## 4. The Extraction Guard Now Has Its Second Consumer

The guard exists because the original draft specified twenty-four Platform packages before
a single one had a consumer. It was written with only the Automator in view. **Game Engine
as a Service is now a second, genuinely unrelated consumer**, and it lands on four deferred
candidates at once:

| Candidate package | Consumer 1 — Automator | Consumer 2 — Game Engine as a Service |
|---|---|---|
| Identity | users, API keys, service accounts | player accounts |
| Organizations / Tenancy | teams | studios, white-label, custom domains |
| Billing | open-core; agents as the paid dimension | Free / Creator / Studio tiers |
| Mcp | brokered plugin tools | the game tool surface |
| Storage | execution artifacts | saves, campaign assets |

This is the guard being **satisfied**, not bypassed. Two unrelated products wanting the same
concern is exactly the evidence the guard asks for, and it is stronger evidence than one
product wanting it twice.

**It does not promote these packages on its own.** It moves them from "speculative" to
"justified", which is the precondition for building them — see
[`second-consumer-packages.md`](second-consumer-packages.md) for what each would own, and
[`implementation-plan.md`](implementation-plan.md) for when.

---

## 5. Consequences

- **The near-term package set is unchanged.** Abstractions, Core, Hosting, Persistence,
  Observability, Testing. A second consumer justifies a candidate; it does not reorder the
  phases.
- **The game-hosting vision is demoted from "what this repository is" to "one workload it
  hosts".** Its content survives intact — see
  [`game-engine-as-a-service.md`](game-engine-as-a-service.md).
- **Platform's technology is a decision this repository owns**, and it is open. See
  [`technology-decision.md`](technology-decision.md). The engine-hosting contract is written
  to survive either answer, deliberately.
- **The Game Engine is a product, not a plugin.** The plugin contract is stateless command
  invocation returning a result envelope; the engine holds sessions. Nothing should attempt
  to express it as a `plugin.yaml`. Stated in
  [`engine-hosting-contract.md`](engine-hosting-contract.md) §3.

---

## 6. Follow-Ups This Decision Creates

Named so they are decisions rather than drift.

- **The remaining ecosystem Platform specifications should move here, and their originals
  deleted.** Four documents were brought in with this change — the platform specification,
  events and notifications, tenancy/billing/licensing, and observability. The ecosystem
  set's own rule is *move, do not copy*, and its staging directory is explicitly "not their
  permanent home". The originals are **still in place** and are now duplicates; deleting
  them is a separate, confirmed step, because that staging tree is not version-controlled.
- **The package-identifier scopes disagree.** The ecosystem naming decision fixes the npm
  scope as `@subzerodev`; the Game Engine publishes as `@the-running-dev/game-engine`, and
  the GitHub organization is `The-Running-Dev` while the namespace brand is `SubZeroDev`.
  Nothing has published on the `@subzerodev` scope, so this is still free to settle, and
  the naming decision's own argument — reservation is free before first publish and never
  again — applies with full force.
- **The architecture repository does not yet exist.** The ecosystem set assigns
  cross-cutting specifications and ADRs to one. Until it does, this document is where the
  Platform-side half of that reasoning lives.
