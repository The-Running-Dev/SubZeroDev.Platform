---
sidebar_position: 7
sidebar_label: The Minimal Package Set
---

# The Minimal Package Set — Boundaries and Done-Criteria

**Document status:** Design.

**Reading order:** after [`platform-specification.md`](platform-specification.md), which
names the six packages and argues for the split. This document does not restate that
argument; it draws the boundaries **between** the six and says what "done" means for each.

> **Scope of this document**
>
> The six near-term packages, what each owns and refuses, and how each is verified. Package
> *names* follow the ecosystem naming convention, which
> [ADR-002](adr/ADR-002-implementation-technology.md) confirmed by settling on .NET. The
> boundaries below were written to be technology-neutral and did not change when it was
> taken.

:::caution The count is no longer settled

[ADR-004](adr/ADR-004-framework-build-not-adopt.md) decided that Platform is built in-house
rather than adopting a framework — and in reaching that, established that modern .NET already
ships much of what these six describe: hosting, DI, typed and validated configuration, health
and readiness endpoints, OpenTelemetry integration, EF Core migrations, `IHostedService`.

Measured against that, the genuine gaps are narrow — a transactional outbox, tenant column and
query filtering, and module registration conventions. **Whether the near-term set is six
packages or three is an open scope decision for the brief**, not something this document
settles. The *boundaries* below stand however the count lands; what is provisional is how many
of them need a package of their own.

:::

---

## 1. Why These Six

Six is not a compromise between one and twenty-four. It is the set that is **genuinely hard
to retrofit**: hosting shape, persistence and transaction boundaries, observability wiring,
and test infrastructure all cost far more to introduce later than to start with. Everything
else is cheap to extract once a second consumer proves it is needed.

The risk of the middle path is honest and worth naming: it becomes the twenty-four package
plan by increments, one reasonable-looking addition at a time. **The guard is the only thing
preventing that**, so it is restated here in its operative form:

> A candidate becomes a package when a **second** consumer needs it. Until then it lives
> inside the product that wants it.

[Platform Identity](platform-identity.md) §4 records that four candidates now have their
second consumer. That makes them *justified*, not *scheduled* — see
[`implementation-plan.md`](implementation-plan.md).

---

## 2. The Six

### Abstractions

**Owns** the interfaces and contracts every other package and every consumer depends on:
result and error types, the clock abstraction, the current-principal and current-tenant
abstractions, and the module contract.

**Refuses** every implementation. It is the one package a product may depend on without
inheriting a runtime choice, which is the whole reason it is separate from Core.

**Done when** it has no dependency on any other Platform package, and a consumer can
compile against it alone.

### Core

**Owns** the default implementations of the Abstractions contracts, module registration and
ordering, startup validation, and typed configuration binding.

**Refuses** anything that touches a network, a database, or a filesystem — those are
Hosting and Persistence. The split is what lets Core be tested without infrastructure.

**Done when** module registration is explicit rather than only assembly-scanned (scanning
may exist; it must not be the only route), and a module graph with a missing or cyclic
dependency fails **at startup with a named error**, not at first use.

### Hosting

**Owns** the host bootstrap: environment detection, DI wiring, middleware conventions,
graceful shutdown, health and readiness endpoints, correlation ids, and the request,
principal and tenant contexts.

**Refuses** to know what any product does. A route that mentions a game session or a plugin
execution is in the wrong package.

**Done when** a product with one endpoint runs with health, readiness, correlation ids and
graceful shutdown having been configured by nothing but `AddPlatform()`, and a
misconfiguration fails startup rather than the first request.

### Persistence

**Owns** the transaction abstraction, migration ownership by module, the outbox for
integration events, audit fields, and soft-delete where required.

**Refuses** to impose a repository pattern. A product may use the data-access layer
directly.

**Done when** two modules each own their migrations independently and can be applied in
either order; the outbox survives a process kill between the domain write and the publish;
and **the tenant column exists in the first schema**, defaulted to a single implicit tenant,
regardless of whether tenancy is built.

> That last one is the one item here that is not deferrable. Adding the column later is
> easy; adding tenant *isolation* to queries, storage paths and secret scopes after data
> exists is a correctness migration on every table at once.

### Observability

**Owns** the OpenTelemetry wiring — logs, traces, metrics — service name and version,
correlation id propagation, endpoint and database instrumentation, and provider health.

**Refuses** to define what a product's events mean. It collects; it does not interpret.

**Done when** a trace spans an inbound request through a background job and out to a hosted
workload without manual context plumbing, secrets never appear in any exported field, and
telemetry export is **opt-in** with console and file as the defaults.

> **The Game Engine constrains this package in a way the Automator does not.** The engine
> emits its own clock-free event stream and guarantees that dropping every event changes
> nothing about the game. Platform's collector must preserve that: it stamps time and trace
> ids at the boundary, and it must never become a path by which collection can fail a
> game. A sink that throws is the engine's own test case, and it stays the engine's
> guarantee — Platform must not undo it by making export synchronous or fallible on the
> request path.

### Testing

**Owns** the test host builder, the fake clock, fake principal and fake tenant, in-memory or
container-backed persistence, notification and event capture, deterministic background jobs,
and provider contract tests.

**Refuses** to be a development dependency of anything shipped.

**Done when** a product's integration test runs against a real persistence provider and a
frozen clock with no bespoke setup, and **the provider contract tests exist and fail against
a deliberately broken provider.** A suite that has never failed is not evidence.

---

## 3. The Line Between Core and Hosting

This is the boundary most likely to erode, so it is stated separately.

Core is constructible with no I/O. Hosting is where I/O begins. The test is the one the
engine already uses for its own two-layer split: **anything that cannot be constructed in a
plain unit test without a socket, a file or a container belongs in Hosting.**

The failure mode is specific — a configuration binder that reads an environment variable is
Core; one that fetches from a secret provider is Hosting, and the second arriving inside the
first is how Core stops being testable without infrastructure.

---

## 3a. What the Gaps Actually Need — a First Evaluation

[ADR-004](adr/ADR-004-framework-build-not-adopt.md) §4 requires each gap to be checked against
existing packages before anything is written, and requires the reason to be recorded either way.
This is that check. **Licences verified against each project; capability claims are from project
documentation and have not been tested against these requirements.**

### The outbox — do not decide yet

| Candidate | Licence | Finding |
|---|---|---|
| MassTransit | v8 MIT; **v9 commercial** (Q1 2026), v8 maintenance ends after 2026 | **Disqualified.** A dependency chosen for a years-long lifespan cannot be one whose free line stops being maintained |
| DotNetCore.CAP | MIT | Real outbox, EF Core, PostgreSQL. **But every listed transport is a broker** — RabbitMQ, Kafka, Azure Service Bus, SQS, NATS, Redis, Pulsar |
| Wolverine | MIT, paid support only | Plausible fit; the outbox claim was **not confirmed** from the repository page and needs checking before it is relied on |

**CAP's mismatch is the useful finding.** The specification calls for an *in-process* event bus
with a durable outbox, with distributed transports as future providers, and deployment mode 1 is
local developer execution. Adopting CAP now would put a message broker into local development to
serve a transport decision that is explicitly **not yet taken**.

**And that reframes the difficulty.** The outbox is hard when it spans a real broker — dedup,
ordering, redelivery across transports. Against an in-process bus it is a table, a hosted-service
dispatcher, and idempotent handlers. Bounded work.

**Recommendation: defer.** Take no outbox dependency until the transport is chosen. Revisit
Wolverine and CAP at that point, when the requirement is real.

### Tenant isolation — built-in now, a package later

**Finbuckle.MultiTenant** (Apache 2.0) is a direct fit: tenant resolution, EF Core data isolation
with query filters, per-tenant options and authentication.

It is also more than the near-term requirement, which is only *carry a tenant column from the
first schema, defaulted to a single implicit tenant*. **EF Core global query filters are built in**,
and a column is a column — so the near-term need costs no dependency at all.

**Recommendation: EF Core query filters now; adopt Finbuckle when tenancy becomes a feature.** What
Finbuckle earns its place for is per-request tenant *resolution* from host, header or claim, and
that is D5 work. Nothing is retrofitted by waiting, because the column — the part that is genuinely
expensive later — ships regardless.

### Module registration — probably not a gap

Nothing mainstream packages "modules with declared dependencies and startup validation" outside the
full application frameworks, and ABP's own version exists because ABP-scale applications compose
dozens of modules with real interdependencies.

At two or three products, the .NET convention — `IServiceCollection` extension methods, composed
explicitly in `Program.cs` — is sufficient, needs no package, and needs no Platform code.

**Recommendation: no package and no abstraction until a product has enough modules to hurt.**

### What this leaves

| Concern | Answer |
|---|---|
| Hosting, DI, configuration, health, readiness, migrations, background work, telemetry | **.NET already ships it** |
| Outbox | Deferred with the transport decision |
| Tenant isolation | Built-in query filters; a package later |
| Module conventions | Not a gap at this scale |

**So the near-term set is plausibly one or two thin packages, not six** — something that wires the
.NET defaults consistently (telemetry, health, problem details, correlation) and, if a shared
contract type is genuinely needed, an abstractions package beneath it.

That is a scope proposal, not a decision: it belongs in `design/00-brief.md`.

---

## 4. What Is Deliberately Not Here

Configuration, Events, Identity, Authorization, Organizations, Tenancy, Notifications,
Storage, BackgroundJobs, Scheduling, Plugins, Billing, Licensing, Audit, Api, Mcp, Web, UI.

Each is specified in [`platform-specification.md`](platform-specification.md) so the shape is
agreed, and none is built. Four of them now have a justified second consumer —
[`second-consumer-packages.md`](second-consumer-packages.md).

**`Platform.Plugins` deserves particular care.** Plugin abstractions belong to the plugin
contract, which has its own repository precisely so that a non-.NET plugin need not depend
on a framework. If this package ever exists, it is a *client* of that contract, never the
contract.
