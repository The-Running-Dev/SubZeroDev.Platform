---
sidebar_position: 6
sidebar_label: ADR-006 Application Modules
---

# ADR-006: Platform Is a Framework Plus Optional Application Modules

## Status

Accepted

## Context

[ADR-001](ADR-001-platform-identity.md) settled that `SubZeroDev.Platform` is a reusable framework
rather than a hosted product, and fixed the dependency direction: Platform never depends on a
product. [Platform Identity](../platform-identity.md) §3 carries the two rules that decide contents —
the **boundary test** (a concern belongs when a *second, unrelated* product would want it unchanged)
and the **extraction guard** (a candidate becomes a package when a second consumer needs it).

A third consumer has now arrived, and it is the first one that is neither workflow automation nor
game hosting. **BarStrad** is a running Discord-and-web ordering product for a bar, bilingual in
Bulgarian and English, deployed in containers. Tested against it, four candidates clear the guard
outright — Notifications, its channel providers, localized structured content, and an inbound command
surface — and two do not: a **catalogue** of priced, categorised, localized items, and **ordering**
over them. Neither the Automator nor the Game Engine wants either.

The repository owner has decided both should exist as Platform modules regardless. That is an
authority the guard does not override, and it creates a question ADR-001 did not answer: **what does
Platform contain, once it contains something with one consumer?**

### Why this needs an ADR rather than a log entry

Left unstated, the answer drifts toward "Platform is whatever the newest product needed", which is
the shape ADR-001 was written to correct — this repository previously held two incompatible
definitions of itself. A module library with no stated rule separating it from the framework
reproduces that, one module at a time, and each individual addition reads as reasonable.

The decision is also cited outside this repository: a product depending on `Platform.Catalogue` needs
a stable statement of what that dependency means and what it does not imply.

## Decision

**Platform is a framework plus a library of optional application modules, separated by one checkable
rule.**

1. **No framework package may reference an application module.** The framework is the set in
   [`minimal-platform-packages.md`](../minimal-platform-packages.md): a consumer cannot decline it and
   still be hosted. A module is opt-in, separately packaged, and its absence is invisible. **A
   reference from a framework package to a module is a build failure, not a review comment** — the
   same standard ADR-001 sets for the product direction, and for the same reason.
2. **No application module may reference another.** Modules compose through the framework's event and
   extension seams or not at all. A notification template that knows about order states, or a content
   schema that knows about prices, welds two modules into one and forfeits the reversal in rule 4.
3. **The boundary test still governs promotion into the framework.** A module does not become
   framework by being useful, popular, or depended upon by several products. It becomes framework when
   a second, unrelated consumer wants it unchanged — unchanged from the guard, which this decision
   does not weaken.
4. **A module with one consumer is admitted by decision, is recorded as such, and is reversible.**
   Where it exists against the boundary test rather than because of it, the affected document says so
   and retains the objection. If a second consumer never appears, the module moves into its one
   consumer and Platform loses a package and no framework code.
5. **The near-term ordering is unchanged.** D3 finishes first. This decision admits a category; it
   schedules nothing, and [`implementation-plan.md`](../implementation-plan.md) continues to hold the
   order.

Catalogue and Ordering are the two modules admitted under rule 4 today. What each owns, the objection
retained against both, and the point at which to revisit are in
[`application-modules.md`](../application-modules.md) §4, which is that decision's home.

## Consequences

- **Two release cadences, and a version matrix.** A framework at 0.x with modules on top means a
  module can be broken by its own foundation. This is real packaging work at D3's release stage, and
  it did not exist when Platform was six packages that shipped together.
- **A larger public surface**, all of which the brief's generated-reference gate applies to. Every
  module's public types need doc comments or the release does not run.
- **The extraction guard becomes harder to apply, not easier.** A module library is a comfortable
  place to put something with one consumer, because separate packaging feels like quarantine. It is
  not: rules 1 and 2 are what make it quarantine, and they only hold if they are checked. **This is
  the cost most likely to be paid silently.**
- **Rule 1 needs a build check.** Stated and unenforced it will be violated by the first module that
  wants a convenience overload in Hosting. An architecture test over package references discharges
  it; without one, this ADR is a preference.
- **BarStrad becomes a fourth thing this repository's documents must keep straight**, alongside the
  Automator, the Game Engine and Platform itself. Its commercial model is unsettled and two binding
  statements in the D3 brief depend on the answer — self-host-only and licensed-per-installation
  against a service operated for venues. Tracked, not assumed.
- **ADR-001 is unchanged and its boundary is narrowed in application, not in force.** The dependency
  direction it fixes still holds absolutely. What this ADR adds is that "belongs in Platform" now has
  two tiers rather than one, and the boundary test decides which tier rather than whether to admit.

## Alternatives considered

**Refuse Catalogue and Ordering; keep them in BarStrad until a second consumer appears.** The
guard's own answer, recommended during evaluation, and the position ADR-001's §3 examples support —
a priced item list is the same class of thing as campaign content or save files. Rejected by the
repository owner in favour of a module library. The objection is retained in
[`application-modules.md`](../application-modules.md) §4 rather than dropped, per this repository's
rule on declined findings, and rules 1, 2 and 4 above exist to bound what it costs if it was wrong.

**Admit them into the framework directly, with no module tier.** The smallest change — one package
set, one cadence, no new rule. Rejected because it puts a concept with one consumer behind a
dependency nobody can decline, and because a framework package that knows what a price is contradicts
ADR-001 outright rather than narrowing it.

**A separate repository for modules, mirroring `SubZeroDev.PluginContract`.** Cleanest separation,
and it makes rule 1 structural rather than a check. Rejected for now on cost: it doubles the release
and CI surface before a single module exists, and unlike a contract repository — whose consumers must
avoid depending on a .NET framework, per [ADR-005](ADR-005-service-contract.md) — modules depend on
the framework by definition, so there is no dependency-shape argument forcing the split. **Revisit if
rule 1 proves unenforceable in one repository**, which is the trigger, not preference.

**Make Catalogue and Ordering generic enough to pass the boundary test by construction.** Design them
abstractly so any product could want them. Rejected as the failure
[`second-consumer-packages.md`](../second-consumer-packages.md) §1 describes: generality invented
without a second consumer to test it is a guess wearing an abstraction, and it is more expensive to
undo than a concrete module, because the abstraction is the part that becomes public API.
