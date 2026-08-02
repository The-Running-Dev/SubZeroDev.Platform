---
sidebar_position: 5
sidebar_label: ADR-005 Service Contract
---

# ADR-005: Boundary Contracts Are Projected, Not Authored — and They Get Their Own Repository

## Status

Accepted

## Context

[ADR-002](ADR-002-implementation-technology.md) fixed that the boundary between Platform and a
product it hosts is a **process** boundary. It did not say what crosses it.
[ADR-004](ADR-004-framework-build-not-adopt.md) then made the host framework a per-product choice,
which makes the answer load-bearing: with .NET products and a Node engine, **the contract is the
only shared artifact between them.**

### What actually crosses a boundary today

| Boundary | Contract | State |
|---|---|---|
| Automator → plugins | Manifest, result envelope, exit codes | **Exists** — `SubZeroDev.PluginContract` |
| GEaaS edge → Game Engine | The MCP tool table, nine operations | **Exists**, implemented and tested in the engine |
| Platform → products | — | Platform is thin packages and conventions; it does not call products |

So a *service* contract has **no consumer today**, and the one real cross-process boundary already
has a contract of its own.

### The drift risk that shapes the answer

The engine's types **are** the contract — `Scene`, `PlayerView`, `SessionActionResult`, and the nine
store operations. A hand-authored `.proto` or OpenAPI document describing them would be a **second
definition of types TypeScript already owns**, maintained in parallel.

That is this project's dominant and repeatedly-documented failure mode. Two byte-identical copies of
a specification drifted under two directory names; two documents described one plugin and disagreed
about exit codes in a way that would have recorded authentication failures as partial successes; a
register claimed one open item while ten had accumulated. Introducing a parallel type definition at
the boundary where correctness matters most would be walking into it deliberately.

**The ecosystem already solved this in a different context.** Its MCP decision holds that tool
surfaces are *"projected from the plugin manifest, never hand-written"*, precisely because
hand-written definitions drift from the code that implements them. The same reasoning applies here
unchanged.

## Decision

**Boundary contracts are projected from their source of truth, and they live in one repository that
depends on nothing.**

1. **`SubZeroDev.ServiceContract` exists as its own repository**, public, mirroring
   `SubZeroDev.PluginContract`: depended on by products, depending on nothing. That dependency shape
   is the reason it is not a folder inside Platform — a Node product must be able to consume the
   contract without conceptually depending on a .NET framework.
2. **A contract is generated from whatever owns the types, never authored alongside them.** Where a
   boundary's types live in TypeScript, the wire schema is emitted from those types. A checked-in
   schema is an *artifact of a build step*, not a document someone maintains by hand.
3. **MCP stays a projection, not the substrate.** The ecosystem's MCP decision fixes MCP as a
   transport whose surface is projected from a contract. Using it as the internal service protocol
   would conflate the AI-facing surface with service-to-service contracts, and forfeit the
   projection property that decision exists to protect.
4. **JSON over HTTP is the first wire, with schemas published at version-pathed URLs.** The nine
   operations are request/response with no streaming and small payloads, so protobuf's advantages do
   not apply yet, and it would add a second serialization format alongside the JSON the engine
   already emits. **protobuf is revisited when streaming or payload size actually justifies it.**
5. **Semantic versioning, with the same discipline the plugin contract uses** — version-pathed
   schema URLs so a 2.0 schema cannot overwrite a pinned 1.0 reference.

### Why create the repository before it has a consumer

This is the part that cuts against the extraction guard, and it is a deliberate exception rather
than an oversight.

The guard says a *package* earns its place from a second consumer, because a package designed
without one encodes a guess about an API. **A contract repository is not that.** It is a home, and
the guess it risks is far smaller — where a file lives, not what an interface looks like. Rule 2
above further limits the exposure: contracts are *generated*, so the repository holds artifacts and
rules rather than invented abstractions.

Against that, the cost of deferring is concrete and this project has already paid it twice. The
plugin contract sat inside a staging tree until it was extracted, and the tree's own split map went
stale describing a directory that had moved. `SubZeroDev.Platform` held two incompatible
definitions of itself because neither had a home that forced the question. **A boundary with no
declared home produces an ad-hoc one**, and ad-hoc boundaries are what this decision exists to
prevent.

An honest counter was weighed and rejected: *fix the rule now, let the first boundary produce the
artifact.* It is cheaper today. It was declined because "extract it when we need it" is a promise
this ecosystem has broken before, and because the first boundary is close enough — the engine
package is published and G1 is unblocked — that the repository will not sit empty for long.

## Consequences

- **The repository starts nearly empty, and that is expected.** It holds the contract rules and,
  once moved, the engine boundary's tool table. It is not evidence of a mistake if it stays small.
- **`mcp-tool-contract.md` is the natural first content, and moving it has a cross-repository
  cost.** It already describes a hosted boundary, and it was moved *here* from the engine repository
  for that reason. Moving it again means updating the engine's `09-clients.md`, which links to it by
  URL. **Not moved by this ADR** — it is a follow-up with a named cost, not a silent consequence.
- **A generated schema needs a generator, and that is real work** the first boundary must pay for.
  Rule 2 is cheap to state and not cheap to honour; a hand-written schema "just for now" is how
  rule 2 gets lost.
- **`Platform.Mcp`'s constraint is reinforced**: it must accept tool definitions from more than one
  producer, because manifest projection and contract projection are now both real sources.
- **Revisit rule 4 when a boundary needs streaming**, batch throughput, or payloads where JSON's
  size is measurable. That is the trigger for protobuf, not preference.

## Alternatives considered

**Fix the rule, defer the artifact.** Recommended during evaluation and rejected above: cheaper
now, but it relies on a later extraction this ecosystem has twice failed to perform on time.

**Author protobuf as the contract IDL.** Strong typing and codegen on both sides, and the obvious
answer for a polyglot boundary. Rejected for this project specifically: it creates a second,
hand-maintained definition of types the engine already owns, in a codebase whose recurring defect is
two copies disagreeing. Revisit under rule 4's trigger, generated rather than authored.

**Declare MCP the service protocol and add nothing.** Smallest possible answer, and the engine
already speaks it. Rejected: it contradicts the ecosystem's own MCP decision, which keeps MCP as a
projection of a contract rather than the contract itself, and it would make every internal service
boundary tool-invocation-shaped.

**A folder inside Platform rather than its own repository.** Rejected on the plugin contract's
reasoning, which applies unchanged: a contract depended on by everything and depending on nothing
does not belong inside one of its dependents, and a Node consumer should not have to reference a
.NET framework's repository to read it.
