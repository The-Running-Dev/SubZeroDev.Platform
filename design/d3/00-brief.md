# Brief — the minimal package set (D3)

> Written by me, not by a model. A model may interrogate it (`/brief-check`) but not author it.
>
> **Provenance of this draft:** the decisions below were taken by me in answer to direct questions
> on 2026-08-03 and transcribed. The *Problem* statement is the one section I did not dictate
> — it is drafted from this repository's own documents and needs my words before it is binding.

## Problem

Two unrelated products — the Automator and Game Engine as a Service — now need the same
cross-cutting infrastructure, and there is nothing shared for either to build on. Without it each
one re-derives hosting shape, configuration binding and startup validation, observability wiring,
persistence and transaction boundaries, and test infrastructure. Those are precisely the four
concerns `minimal-platform-packages.md` §1 identifies as far more expensive to retrofit than to
start with.

Observable now: Platform is specification only. Five ADRs, twelve documents, no code.
`implementation-plan.md` §4 states it plainly — **D3 is unblocked and unstarted.**

## Who it is for

Me, plus other developers building on the packages. Single-digit consumers initially, expert .NET.

The consequence is deliberate: a third party compiling against these packages makes the public API
a real commitment and makes startup error messages a feature rather than a nicety. Read this
against *Lifespan*, which pulls the other way — see the tension noted there.

## Non-goals

The binding list. Everything here is out of scope for every agent, permanently, until this file
changes.

- **Tenancy beyond the column and the constant that fills it.**
  **In scope:** the non-null tenant column on every table from the first migration, defaulted to a
  single implicit tenant, and the ambient tenant abstraction that supplies that constant. The
  abstraction is included deliberately — `minimal-platform-packages.md` §2 assigns a current-tenant
  abstraction to Abstractions, a tenant context to Hosting and a fake tenant to Testing, and this
  brief's definition of done requires all six packages to meet their §2 criteria. Excluding it would
  make three of them unmeetable.
  **Out of scope:** **query filters**, and per-request tenant resolution from host, header or claim.
  Both are D5.
  **Why the line falls there:** the column is data and is a correctness migration on every table at
  once if added after products have rows. A filter is code and is cheap whenever tenancy becomes a
  feature.
  *Settled 2026-08-03, after an adversarial review found the earlier wording admitted two readings
  and the design had silently taken the permissive one.*
- **Hosted multi-tenant SaaS deployment.** See *Environment*. Nothing may be designed on the
  assumption that a vendor-operated tenant exists.
- **Adopting an application framework.** Settled by ADR-004 and restated here only because it binds
  agents: ABP is an architecture reference, never a dependency, and no ABP source is copied.
- **Any runtime dependency on outbound network.** Licensing verifies locally. Nothing at startup or
  in steady state may require the internet to be reachable, because homelab installations may have
  no outbound path at all. Telemetry export remains opt-in and its absence is not a failure.

## Definition of done

- Every one of the six packages meets its stated done-criteria in `minimal-platform-packages.md`
  §2. Those criteria are not restated here — that document owns them.
- **All six land together.** D3 is not done partially; there is no package-by-package release.
- **A sample in `samples/` runs in CI**, and its requirements are derived from what the G1 engine
  edge will actually need, so a real consumer shapes it rather than its own authors.
- **CI asserts four things, and three of them are failures.** A suite that has never failed is not
  known to constrain anything:
  1. the sample starts and serves, with health, readiness, correlation and telemetry working through
     the standard registration call alone;
  2. a deliberately broken configuration **aborts startup with a named error**;
  3. a process killed between the domain commit and the dispatch **delivers the message on
     restart**;
  4. the provider contract tests **go red against a deliberately broken provider**.
- A product runs on Platform with health, readiness, correlation ids, migrations and telemetry
  configured by **nothing but the standard registration call**. Bespoke wiring by the first
  consumer means this is not done.
- The tenant column exists in the first schema, defaulted to a single implicit tenant.
- **Both persistence providers pass the contract tests**, not one with the other assumed.
- **The packages publish to GitHub Packages as a private feed, and the sample consumes them from
  it** — proving pack, publish and authenticated restore without spending the public identifiers,
  which are still unreserved. GitHub Packages because the organisation is already there, it costs
  nothing additional, and ADR-003 has already reasoned about its registry constraints.
- **A full API reference is generated and published for every public type, and it gates the
  release.** Generated output satisfies this: every public type carries doc comments, and the
  reference is built from them. No hand-written prose is required beyond that. Third-party developers
  are the stated audience, and nothing else in this list serves them.
- Each narrow gap was checked against existing packages **before** anything was written, with the
  reason recorded either way, per ADR-004 §4. An empty decision log next to hand-written
  infrastructure means this was skipped.

**The G1 edge is a follow-up, not a done-criterion.** It cannot be satisfied at the moment D3
completes, and `implementation-plan.md` §8.2 values Track A not waiting on Track B. When the edge
lands it becomes the first genuine external validation, and any API change it forces is expected and
cheap at 0.x — but D3 is done before then, on the sample.

## Environment

Self-host only: local developer execution, homelab, and single-server. Licensed per installation.

That licence model is a hard constraint on dependencies, not a preference — a customer running on
their own hardware cannot depend on a vendor's SaaS tenant. ADR-004's rule applies in full: **depend
on the protocol, not the vendor.** A SaaS-only dependency is acceptable only where a self-hostable
path exists.

**Fully offline.** The licence verifies locally and never calls out. See *Non-goals*.

**Two processes per installation:** a web host serving HTTP, and a worker host owning **all**
background work — outbox dispatch, scheduled work, anything long-running. The web host runs none of
it. The two may overlap briefly during a restart, so nothing may assume a single process.

**Two persistence providers:** PostgreSQL and SQLite, on EF Core per ADR-002. SQLite serves local
developer execution and single-file homelab installations; PostgreSQL serves everything else. Both
are production paths, not one plus a test double.

**Runtime: latest, upgraded every release.** ADR-004's narrowing rests on what the runtime already
ships, and this reading is the most generous one — newer features count as shipped, so fewer gaps
need Platform code. The cost is an upgrade cadence self-hosters must follow.

Scale is small: single-digit concurrent users per installation, no cross-node coordination, no
clustering.

## Lifespan

**Prove it, then revisit.** Build to the sample, learn from the first real consumer, expect to
redesign. The API stays explicitly unstable at 0.x.

> **Two tensions this brief does not resolve, stated rather than hidden.**
>
> **Audience against stability.** *Who it is for* admits third parties compiling against these
> packages; *Lifespan* keeps the API unstable. The working reconciliation is that 0.x means breaking
> changes are permitted but must be deliberate and recorded — not that the API is careless.
>
> **Feedback against batch size.** *Definition of done* requires all six packages to land together,
> while this section says build to the sample and learn from the first real consumer. All-six-at-once
> is the longest possible path to that feedback, and the first thing learned may invalidate work
> already finished. This is accepted deliberately: partial release would put packages with no
> consumer in front of one, which is what the extraction guard exists to prevent.

---

## Decisions taken here that override a recommendation elsewhere

Recorded in this file because the brief is where scope is settled, and because each contradicts a
document that expected to be followed.

1. **Six packages, not one or two.** `minimal-platform-packages.md` §3a proposed the near-term set
   was "plausibly one or two thin packages, not six" and labelled it a proposal for the brief.
   ADR-004 and `implementation-plan.md` §8.1 both deferred the count here. **The count is six** —
   Abstractions, Core, Hosting, Persistence, Observability, Testing. This does not disturb ADR-004's
   actual decision (build, not adopt), which anticipated either answer; §4's requirement to check
   each gap against existing packages before writing binds regardless, and six packages is not a
   licence to hand-roll six.

2. **The transactional outbox is in scope.** §3a recommended deferring it until the transport is
   chosen, on the ground that adopting CAP or MassTransit now would put a broker into local
   development for a decision not yet taken. That recommendation is overridden: Persistence's
   done-criterion — the outbox survives a process kill between the domain write and the publish —
   stands as written.
   **Left to the design stage:** whether it is built or taken as a dependency. ADR-004 §4 requires
   the evaluation either way, and §3a's own observation applies — against an in-process bus the
   outbox is a table, a hosted-service dispatcher and idempotent handlers, which is bounded work.

3. **Both instruments prove D3.** `implementation-plan.md` §8.2 decided a sample, reasoning that
   thin packages need no real product to demonstrate — and explicitly noted that argument was made
   when D3 was one or two packages, naming the six-package case as the argument for the G1 edge
   instead. Choosing six reopened it. **The answer is both:** the sample now, with its requirements
   derived from the edge's needs, and the edge as first genuine validation when it exists. §8.2's
   stated weakness — a sample written by the framework's authors confirms rather than challenges —
   is accepted and mitigated this way rather than denied.
