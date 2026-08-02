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
specifications: this repository described a game-hosting product, while the ecosystem
staging tree described a reusable application framework and named *this repository* as its
home. Neither knew the other existed.

**The full context, the decision, its costs and the three rejected alternatives are
[ADR-001](adr/ADR-001-platform-identity.md).** That is the decision's one home; this
document is orientation and does not restate it.

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
- **Platform's technology was a decision this repository owned**, and it is taken:
  [ADR-002](adr/ADR-002-implementation-technology.md) — .NET, with the boundary between
  Platform and a product it hosts stated explicitly as a process boundary. The
  engine-hosting contract was written to survive either answer and needed no revision.
- **The Game Engine is a product, not a plugin.** The plugin contract is stateless command
  invocation returning a result envelope; the engine holds sessions. Nothing should attempt
  to express it as a `plugin.yaml`. Stated in
  [`engine-hosting-contract.md`](engine-hosting-contract.md) §3.

---

## 6. Follow-Ups This Decision Creates

Named so they are decisions rather than drift.

- **The ecosystem Platform specifications have moved here** — the platform specification,
  events and notifications, tenancy/billing/licensing, and observability — and the staging
  originals are deleted, per *move, never copy*. The staging tree's other directories belong
  to other repositories and are untouched.
- **The package-scope conflict is settled**, and it was not drift:
  [ADR-003](adr/ADR-003-package-scopes-and-registries.md) records that scope is a property of
  the registry. GitHub Packages requires the npm scope to match the organization, which is
  what forces the engine's `@the-running-dev` coordinate. **The brand identifiers still need
  reserving**, and that is a human action requiring registry credentials — free now, and not
  free after anything publishes.
- **The architecture repository now exists** —
  [SubZeroDev.Architecture](https://github.com/The-Running-Dev/SubZeroDev.Architecture),
  private, holding the cross-cutting specifications and ADRs the ecosystem set assigns to it.
  It was an unversioned directory until this change, which is how its own table came to
  describe a `SubZeroDev.Platform/` that had moved. `docs/docs/adr/` remains the home for
  ADRs about *this* repository; ADR numbering is per-repository, so its ADR-001 and this
  one's are different decisions and are meant to be.
- **Where a decision is recorded is settled**, and the boundary is *does anyone outside this
  repository need to cite it?* Yes, or it touches a published contract, means a numbered ADR
  here; no, meaning this repository's own working arrangement, means the decision log. The
  rule and the entry format live in `AGENTS.md`, *Decision logging* — this is a pointer, not
  a second copy.
