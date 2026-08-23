# Brief — commercial (D5)

> **Provenance, stated honestly.** The d3, g1 and g2 briefs each open by saying they were
> written by Ben and not by a model. **This one was not.** It was assembled by a model from
> decisions Ben took in session on 2026-08-22 and from constraints already recorded in the
> tree. Every section below is either sourced or marked `TODO — Ben`. **The marked sections are
> intent, and a model cannot supply them.** Fill them before `/brief-check` runs; a brief whose
> problem statement was inferred is a brief `/design` will design against an inference.

---

## Problem

> **TODO — Ben.** Why D5, why now. Nothing in the repository can answer this — D5 sits off the
> ecosystem critical path (`implementation-plan.md:68`), nothing is blocked on it, and the
> alternative use of the same time (G3 — Principals) is the one with a queue position. That
> makes the reason for starting it *now* a statement of intent rather than a derivation, which
> is exactly what this section is for.
>
> Two sentences is enough. What it should answer: what becomes possible when D5 lands that is
> not possible today, and what is currently being worked around in its absence.

---

## Who it is for

> **TODO — Ben.** The four consumers and their shapes are recorded at `platform-identity.md`
> §4 and reproduced in `## Scope` below, so the *evidence* is settled. What is not settled is
> which of them this effort is actually serving first, and that changes what gets built.

---

## Scope

**Nine capabilities.** The eight D5 lists at `implementation-plan.md:161` — Identity,
Authorization, Organizations, Tenancy, Billing, Licensing, Audit, shared web UI — plus
`Platform.Mcp`.

**Mcp is admitted deliberately.** D5's stated done-when at `implementation-plan.md:167` already
required Mcp to accept tool definitions from a producer other than manifest projection, while
D5's package list omitted it. Scope follows the done-when rather than the list.

Where each capability is already specified, and whether it carries consumer evidence:

| Capability | Specification | Divergence analysis | Row in the §4 consumer table |
|---|---|---|---|
| Identity | `platform-specification.md:215` | `second-consumer-packages.md:40` | yes — all four consumers |
| Authorization | `platform-specification.md:231` | **none** | **none** |
| Organizations / Tenancy | `platform-specification.md:254` | `second-consumer-packages.md:63` | yes — three consumers |
| Billing | `platform-specification.md:275` | `second-consumer-packages.md:121` | yes — but BarStrad's cell is empty |
| Licensing | `platform-specification.md:299` | **none** | **none** |
| Audit | `platform-specification.md:401` | **none** | **none** |
| Shared web UI | `platform-specification.md:455` | **none** | **none** |
| Mcp | `platform-specification.md:449` | `second-consumer-packages.md:87` | yes — three consumers |

**Four capabilities are in scope without consumer evidence**, and this brief says so rather
than letting a later stage discover it. Authorization, Licensing, Audit and the shared web UI
have no divergence analysis in `second-consumer-packages.md` and no row in the canonical
consumer count at `platform-identity.md:120`. They are included because D5 lists them and
because Identity is hard to ship without at least Authorization and Audit beside it. **The
objection is retained, not resolved** — the pattern ADR-006 rule 4 establishes for a package
admitted against the boundary test.

### What D5 inherits and does not redesign

The tenant column is built, and the expensive half of it is done:

- Non-null logical tenant type with a well-known all-zero sentinel, not nullable and not a slug
  — `design/d3/10-design.md:1507`, which records its own reversibility as *"the most expensive
  decision here."*
- Part of every table's primary key from the first migration, with every query supplying the
  implicit constant — `design/g2/90-decisions.md:1149`.
- The implicit tenant participates in storage keys without becoming request tenancy; no request
  resolves or carries a tenant and no behaviour varies by tenant — `design/g2/90-decisions.md:775`.

> **TODO — Ben.** State whether D5's Tenancy is *shipping the feature over this column* or
> something larger. A brief that reads as "introduce tenancy" invites `/design` to revisit a
> settled schema whose migration cost `d3/10-design.md` flags as touching every table at once.

---

## Non-goals

> **TODO — Ben — confirm or strike each.** These are candidates drawn from the tree, not
> decisions. **Non-goals are binding** (`AGENTS.md` §Hard rules), and `/contract` stopped dead
> on one during G2 (`design/g2/90-decisions.md:775`) — so each of these has to be one you can
> live with through stage 5.

- **Federation, account linking, and a shared user directory.** Follows from the re-affirmed
  decision that the Automator and GEaaS do not share identity
  (`design/g1/90-decisions.md:532`). Platform's Identity is at most a consistent contract over
  per-application principals, never a shared store.
- **Marketplace, distributed event bus, enterprise tenancy** — already deferred indefinitely at
  `platform-specification.md:523`.
- **Choosing an identity substrate.** A substrate ADR was withdrawn before it was written
  (`design/g1/90-decisions.md:504`) on the grounds that an OIDC provider answers one consumer
  column out of four. The tier and the substrate are settled when the package is designed —
  ADR-006 rule 3 — which is `/design`'s output, not this brief's.
- **Settling the framework-vs-application-module tier for any of the nine.** Same reason. Five
  of them sit at `Undecided` in `platform-identity.md:87`, and that is a real state rather than
  a gap to fill in passing.
- **Metering execution minutes or playtime.** Settled twice —
  `tenancy-billing-licensing.md` §Metering caution and `second-consumer-packages.md:139`.
- **Requiring billing, licensing or identity for self-hosted or local-only use.** Community has
  no licence code path at all, not a check that passes.

---

## Definition of done

**A sample in `samples/`, run in CI, exercises all nine capabilities.** D3's precedent at
`implementation-plan.md:313`, chosen over per-divergence checks and over waiting for an
external consumer to deploy. It is what makes the four evidence-thin capabilities checkable:
they get the same criterion as the other five.

**The sample is therefore the whole criterion, so it is specified per capability rather than
named.** A sample that authenticates and never touches Licensing proves nothing about
Licensing.

> **TODO — Ben — one line per row.** Draft criteria below where the tree already determines
> them; the blanks are where it does not. These nine lines are what `/slices` will cut against,
> so they are worth more attention than anything else in this document.

| Capability | The sample must demonstrate |
|---|---|
| Identity | A principal authenticates, and a second deployment mode runs with identity absent entirely — `second-consumer-packages.md:58` requires local-only products need no account setup |
| Authorization | *TODO* — a named permission denies an action, and the denial is audited (`platform-specification.md:243` lists audit of security-sensitive decisions) |
| Organizations / Tenancy | A deliberately shared resource is read across tenants **through the modelled escape, not a hand-written one** — `second-consumer-packages.md:74` calls the unmodelled escape "how isolation quietly stops holding" |
| Billing | *TODO* — an entitlement gates a feature, with no code path outside the billing module branching on subscription state |
| Licensing | *TODO* — a signed licence verifies **offline**; verification failure fails **open** at the last known tier; expiry degrades a feature without touching data or running work |
| Audit | *TODO* — an audited action records actor, tenant, action, resource, correlation id and outcome, and a secret does not appear in the log (`platform-specification.md:415`) |
| Shared web UI | *TODO* — and note `platform-specification.md:471`: UI must not become a dependency for backend packages |
| Mcp | A tool definition from a **product-owned fixed table** registers alongside one from manifest projection — this is D5's named done-when at `implementation-plan.md:167` |
| Mcp (auth) | An MCP connection authenticates at the **transport**; no tool call carries a secret as a parameter — `second-consumer-packages.md:55` |

---

## Environment

- **.NET**, with the boundary between Platform and a hosted product as a process and image
  boundary — [ADR-002](../docs/docs/adr/ADR-002-implementation-technology.md).
- **Two deployment shapes, both first-class:** operated SaaS, and self-hosted / homelab. The
  self-hosted shape is offline-capable and must stay so — `tenancy-billing-licensing.md`
  §Enforcement rules: "An offline-capable homelab deployment that stops working because a
  license server is unreachable is a support burden and a reputational cost."
- **Two database providers**, whose collation defaults differ — which is why the tenant
  identifier is opaque rather than a readable slug (`design/d3/10-design.md:1514`).
- **Single-user is the common case for both consumers**, not an edge case —
  `second-consumer-packages.md:71`.
- **No consumer runs on Platform today.** BarStrad and SkyNet HR are both evidence rather than
  deployed dependents (`platform-identity.md:138`, `:145`). This is why the sample, not a
  consumer, is the proof.

> **TODO — Ben.** Scale, concurrency and data volume are unstated everywhere in the tree. If
> D5 should assume anything about them, it has to be said here or `/design` will assume it
> silently.

---

## Lifespan

**Long-lived. Full pipeline, stages 0 through 5, including `/redteam`.**

Nine capabilities become public API surface with four named consumers, and five of the nine
carry an unsettled framework-vs-module tier that `/design` has to resolve under ADR-006 rule 3.
This is the case the staged pipeline exists for. The short path at `kit-help.md:91` — brief,
contract, slice — is explicitly not taken.

---

## Decisions taken here that override a recommendation elsewhere

**1. `Platform.Mcp` is in scope, though `implementation-plan.md:161` does not list it in D5.**
Scope follows D5's done-when at `:167`, which names Mcp, over D5's package list, which omits
it. The two disagreed; this brief resolves the disagreement toward the done-when.
*Consequence:* `implementation-plan.md`'s D5 package list is now stale and should be corrected
when this effort lands.

**2. Four capabilities are in scope without consumer evidence** — Authorization, Licensing,
Audit, shared web UI. `platform-identity.md:151` says the consumer table "does not promote
these packages on its own," and for these four there is no table row to promote from. Included
anyway, by decision, with the objection retained rather than resolved — following ADR-006
rule 4's pattern for admission against the boundary test.

**3. The proof is a sample, not a consumer.** D5's stated done-when at `:167` is
per-divergence. Four of the nine have no divergence analysis, so a per-divergence criterion
would leave them with no criterion at all. The sample replaces it uniformly.

**4. The Automator and GEaaS still do not share identity.** `design/g1/90-decisions.md:532`
recorded this with "at this point" attached, making it current intent rather than a permanent
boundary; re-affirmed on 2026-08-22 and treated as settled for the duration of this effort.
*Consequence:* federation and account linking are non-goals, and opaque stable principal ids
remain the hedge that keeps a later reversal a retrofit rather than a migration.
