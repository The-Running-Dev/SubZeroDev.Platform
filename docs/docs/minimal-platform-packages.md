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
