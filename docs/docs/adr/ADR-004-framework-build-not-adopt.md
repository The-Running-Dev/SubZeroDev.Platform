---
sidebar_position: 4
sidebar_label: ADR-004 Build, Not Adopt
---

# ADR-004: Platform Is Built In-House, With ABP as an Architecture Reference

## Status

Accepted

## Context

[ADR-002](ADR-002-implementation-technology.md) settled that Platform is .NET. It did not settle
whether Platform *implements* its infrastructure or *adopts* an existing application framework —
and the platform specification's own comparison ("comparable in purpose to ABP Framework") invited
the question without answering it.

The question is not idle. Platform's own extraction guard says a framework earns its abstractions
from its second and third consumer. **Platform has zero.** ABP has thousands. Building a module
system next to a proven one, for no consumer, is the position that guard exists to flag.

Two candidates were evaluated against the near-term package set.

### What the evaluation found

**ABP Framework covers most of it.** Against the six near-term packages, verified from its
documentation rather than recalled:

| Package | Its done-criterion | ABP |
|---|---|---|
| Core | Missing or cyclic module dependency fails **at startup**, named error | `AbpModule`, `[DependsOn]`, startup dependency-graph validation and ordering |
| Persistence | Two modules' migrations in either order; outbox survives a process kill; tenant column from the first schema | EF Core, Unit of Work, PostgreSQL and SQLite, distributed-event outbox, multi-tenancy filters |
| Hosting | Health, readiness, correlation ids, shutdown from one registration call | Init/shutdown lifecycle, correlation id, ASP.NET Core integration |
| Testing | Test host, fake clock/principal/tenant | Integration-test infrastructure, module test hosts, `IClock` |
| Abstractions | Compiles alone, no implementations | Layered, but no single pure-abstractions package |
| Observability | Trace spans request → job → hosted workload | Correlation id only; OpenTelemetry is not first-class |

It also covers much of the deferred candidate list — Identity, Authorization, Tenancy,
BackgroundJobs, Scheduling, Audit, Storage. What it does **not** cover is Billing, Licensing, MCP
conventions, and anything cross-language: precisely the SubZeroDev-specific parts.

**Licensing is not an obstacle for either.** Verified: ABP Framework is **LGPL-3.0**, ASP.NET
Boilerplate is **MIT**. ABP's own guidance is that commercial and closed-source use is permitted,
including SaaS; consuming it as packages carries no publication obligation, while *modifying ABP
itself* requires publishing those modifications under LGPL. Since this decision adopts neither as a
dependency, none of that binds — it is recorded so the evaluation does not have to be redone. Note
separately that ABP Commercial is a distinct paid product; some modules sit behind it.

**ASP.NET Boilerplate is not a lighter alternative.** It self-describes as "a full application
framework" with DDD/NLayer architecture, conventional DI, repository and Unit of Work patterns,
multi-tenancy, modules, audit logging and dynamic API generation — the same surface as ABP, and
arguably more prescriptive. Its distinguishing feature is the MIT licence, not reduced weight.

### The finding that decided it

**The weight question has a different answer per product, so it cannot be a Platform-wide
decision.**

- The **Automator** — plugin registry, execution history, workflows, scheduling, permissions,
  audit, admin UI, tenancy — is exactly the shape ABP was built for. The weight is earned there.
- The **Game Engine as a Service edge** — an authenticating proxy in front of a Node workload — is
  a case where either framework is overwhelming overkill.

Same framework, opposite verdicts, and neither product exists yet to argue its case.

## Decision

**Platform implements its own infrastructure. ABP is retained as an architecture reference, not as
a dependency.**

Three parts, and the second and third are what keep the first from being a mistake:

1. **No application framework is adopted as a Platform dependency.** Not ABP, not ASP.NET
   Boilerplate.
2. **The host framework is a per-product choice, not Platform's.** A product may sit on ABP, on
   plain ASP.NET Core, or on something else. Platform does not require or forbid any of them. This
   is [ADR-002](ADR-002-implementation-technology.md)'s process boundary doing the work it was
   chosen for.
3. **ABP is studied deliberately where it has solved something well** — its module lifecycle and
   dependency-graph validation, its outbox, and its tenancy query filters are the three worth
   reading closely before the thin equivalents are written. Reading for architecture carries no
   licence obligation; **no ABP source is copied into this repository.**
4. **What is rejected is adopting a whole application framework — not using libraries.** Reuse an
   existing, well-scoped NuGet package wherever one exists and fits. **The burden of proof runs the
   other way from the usual instinct: hand-rolling is what needs justifying, not taking a
   dependency.**

### On reuse, because this decision is easy to misread as "build everything"

It is not. "Build in-house" here means *Platform owns its own composition and conventions*; it does
not mean writing infrastructure that a maintained library already provides. A framework asks you to
adopt its architecture wholesale — which is what §"The finding that decided it" rejected. A library
does one thing behind an interface you chose, and swapping it later is a contained change.

The narrow gaps this ADR identifies should each be checked against existing packages **before** any
is written. Candidates worth evaluating — named as starting points to assess, not endorsements, and
none verified against these requirements yet:

| Gap | Where to look first |
|---|---|
| Transactional outbox | The messaging libraries that already ship one, rather than a bespoke implementation |
| Tenant resolution and query filtering | EF Core global query filters directly; the established multi-tenancy libraries above that |
| Health and readiness | The ASP.NET Core health-check ecosystem |
| Telemetry | The official OpenTelemetry .NET packages — this is not a place to invent |
| Validation, resilience, configuration | The `Microsoft.Extensions.*` family and the mainstream libraries around it |

The outbox is the sharpest case. This ADR's own consequences call it "well-understood but not
trivial, and getting it wrong is the kind of defect that surfaces only under failure" — which is
precisely the argument for taking a proven one rather than writing it.

**This does not weaken the dependency rule in `AGENTS.md`.** Every new dependency still gets a
decision-log entry naming what was rejected and why. The rule was never "avoid dependencies"; it is
"choose them deliberately and say why". What changes here is the default: reach for a package
first, and record the reason when you do not.

### One qualifier the first evaluation forced

**Check the licence's durability, not just its current text.** The .NET ecosystem saw several
foundational libraries move to commercial licensing during 2025 — MassTransit v9 is the case that
disqualified it here, with v8 remaining MIT but its maintenance ending after 2026.

A dependency taken under this ADR is being taken for a codebase whose stated lifespan is *years,
with the public API as a commitment*. So "it is MIT today" is not sufficient on its own. Prefer
projects under a foundation or with a long stable licensing history, and treat a recent commercial
pivot in a project's neighbourhood as a reason to look harder rather than a coincidence.

This does not argue for hand-rolling — a library that goes commercial can be replaced behind the
interface that wrapped it, which is exactly why §4 asks for the interface to be ours.

## Consequences

- **Platform stops being a framework every product must use**, and becomes conventions plus the
  contract between products. That is a smaller and better-defended thing than the original
  six-package framing.
- **The scope of what to build is now the open question, and it is not settled here.** Modern .NET
  already ships hosting, DI, typed and validated configuration, health and readiness endpoints,
  OpenTelemetry integration, EF Core migrations and `IHostedService`. Measured against that, the
  genuine gaps are narrow — a transactional outbox, tenant column and query filtering, and module
  registration conventions. **Whether the near-term set is six packages or three is a scope
  decision for the brief**, and `minimal-platform-packages.md` still describes the six.
- **The evaluation does not have to be redone.** The coverage map, the licence findings, and the
  per-product weight argument are recorded above precisely so a future reader asking "why not just
  use ABP?" gets an answer rather than a rediscovery.
- **Revisit when** a second .NET product exists and both want the same infrastructure. Two real
  consumers is the extraction guard's own condition, and it is also the point at which adopting a
  framework wholesale could be judged on evidence rather than on anticipation.
- **A cost accepted honestly:** building even the narrow gaps means owning an outbox, which is
  well-understood but not trivial, and getting it wrong is the kind of defect that surfaces only
  under failure. ABP's implementation is the reference for exactly this reason — and under §4 the
  first question is whether it needs writing at all rather than taking a proven library.
- **The reuse rule inverts the usual default and should be applied visibly.** If a package exists
  and fits, the decision-log entry records why it was taken; if one exists and was passed over, the
  entry records why hand-rolling won. An empty log next to hand-written infrastructure is the
  signal that this clause was skipped.

## Alternatives considered

**Adopt ABP as a dependency, and let Platform become a thin convention layer over it.** The
strongest alternative, and the one the extraction guard's logic points at: thousands of consumers
against Platform's zero, most of the six covered, commercial use permitted. Rejected because it
commits every .NET product to one framework's opinions — DDD layering, its DI conventions, its
repository and Unit of Work patterns — before either product exists to say whether those opinions
fit. It is right for the Automator's shape and clearly wrong for a thin edge service, and a
Platform-wide adoption cannot express that difference.

**Adopt ASP.NET Boilerplate instead, for the MIT licence.** Rejected: it answers a question that
was not the objection. It is the same architectural weight one generation older, and trading a
primary framework for its predecessor to avoid an LGPL obligation that does not bind a
non-modifying consumer is a poor exchange.

**Fork either framework.** Rejected: it is the one variant where the licence genuinely bites for
ABP, and for both it means owning a divergence permanently.

**Build the original six packages as specified, without evaluating alternatives.** What was in
flight before this ADR. Rejected because it re-derives solved problems — and because the evaluation
above changed the scope question materially, which is worth knowing before code rather than after.
