---
sidebar_position: 2
sidebar_label: ADR-002 Implementation Technology
---

# ADR-002: Platform Is .NET, and the Product Boundary Is a Process Boundary

## Status

Accepted

> Supersedes the open `technology-decision.md` draft, which is deleted rather than kept
> alongside — a decision has one home.

## Context

The ecosystem specification set assumes .NET throughout, and never records it as a decision.
It is visible only in the specification's own examples: `SubZeroDevApplication
.CreateBuilder(args)`, `IPlatformModule`, EF Core with SQLite and PostgreSQL, NuGet as the
packaging target, "comparable in purpose to ABP Framework". None of that is wrong; it was
assumed, and it was assumed when the Automator was the only consumer in view.

[ADR-001](ADR-001-platform-identity.md) added a second consumer, and it is a different
runtime. The Game Engine is a TypeScript/Node package whose central guarantee is **byte-level
determinism** — the same seed and action log must reproduce identical serialized state —
enforced by a lint rule banning `Math.random`, `Date.now` and the non-bit-stable `Math.*`
functions.

So the question became live: is Platform .NET because it was chosen, or because nobody
asked?

## Decision

**Platform is built in .NET, and the boundary between Platform and a product it hosts is a
process and image boundary, stated explicitly rather than left as an accident.**

Products are whatever suits them. The Automator is .NET; the Game Engine stays Node. They
meet Platform over HTTP and MCP, exactly as the Automator's plugins already do.

The second half is the part that is actually new. The technology choice alone is Option A;
naming the boundary makes it Option C, and the difference is not the runtime — it is whether
polyglot products are an **accepted consequence** or an accident nobody decided.

Three reasons:

1. **It is what the hosting contract already describes**, so it costs nothing to adopt.
   `engine-hosting-contract.md` §2 chose workload hosting over in-process port supply on
   independent grounds — chiefly that spanning the determinism boundary across a language
   boundary means trusting two runtimes to agree byte-for-byte forever, for no gain.
2. **The ecosystem specifications transfer verbatim**, including the module model, the
   persistence stack and the configuration precedence chain. .NET is also simply a strong
   ecosystem for what Platform is: hosting, DI, migrations, OpenTelemetry wiring and
   background jobs are mature and boring there, which is what infrastructure should be.
3. **Adding a product in a third language later needs no architectural change.** That is the
   plugin contract's own thesis — a process boundary survives language choices a library
   interface cannot — applied one level up.

## Consequences

- **Platform and the Game Engine are permanently different runtimes.** Nobody works across
  both without a context switch. Accepted: they are different kinds of software, and the
  boundary between them is a wire protocol either way.
- **In-process port supply is closed for good**, not merely deferred. The engine's
  `SessionStore`, `Emitter` and `Clock` ports will never be implemented by Platform directly.
  This is the real cost, and it is small because the hosting contract had already declined
  that shape.
- **Shared client code must be written per language, or not shared.** A .NET client for the
  hosted game API and a TypeScript one are two implementations. Named here so it is a design
  item rather than a surprise.
- **Two toolchains, two dependency ecosystems, two CI shapes.** The operational cost of the
  decision, and the honest reason Option B was tempting.
- **The near-term package set, boundaries and done-criteria are unchanged** — they were
  written to be technology-neutral, and `minimal-platform-packages.md` needs no revision
  beyond package naming.
- **Package identifiers are now decidable**, which unblocks
  [ADR-003](ADR-003-package-scopes-and-registries.md) and the P3 stage of the implementation
  plan.

## Alternatives considered

**TypeScript/Node, matching the engine.** One runtime across Platform and the Game Engine,
and it keeps in-process ports genuinely open. Rejected: it optimizes Platform for its
*second* consumer at the expense of its first, which inverts the extraction guard's own
logic — the Automator is specified against .NET and is the consumer that exists. The
ecosystem specification set would stop transferring verbatim, and the prize it buys is an
in-process integration the hosting contract already declined on determinism grounds.

**Defer the decision further.** Tempting, since nothing in P0–P2 was blocked by it. Rejected
for the reason the naming ADR gives about identifiers: deferring moves a decision to the
moment it becomes expensive. P3 cannot start without it, and the reservations in
[ADR-003](ADR-003-package-scopes-and-registries.md) are free now and never again.
