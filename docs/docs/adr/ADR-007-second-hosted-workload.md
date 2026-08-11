---
sidebar_position: 7
sidebar_label: ADR-007 Second Hosted Workload
---

# ADR-007: SkyNet HR Is a Second Hosted Workload, and the Edge Becomes a Platform Package

## Status

Accepted

## Context

[ADR-002](ADR-002-implementation-technology.md) fixed the boundary between Platform and a product
it hosts as a **process and image boundary**, and named polyglot products an accepted consequence
rather than an accident. [`engine-hosting-contract.md`](../engine-hosting-contract.md) §2 had
already chosen workload hosting over in-process port supply on independent grounds. G1 then built
it: a Node workload under `workloads/game-service/`, a .NET edge in front of it, the byte-identity
replay passing through two hops, and one distributed trace spanning both languages. All nine
slices are shipped.

So the principle is settled twice and demonstrated once. **It is nonetheless being read the other
way, repeatedly.** The most recent instance: an agent working in
[SubZeroDev.SkyNetHR](https://github.com/The-Running-Dev/SubZeroDev.SkyNetHR) concluded that
Platform does not support that product's workload *because the workload is a Node process*. That
conclusion is wrong, and the inference behind it is not unreasonable. A reader asking "does
Platform host me" finds a framework specified in .NET, an ADR titled for the technology choice, and
a hosting contract titled for the Game Engine. **Nothing states the workload-to-Platform interface
generally**, so the general rule is only reachable by someone who already knows where it is filed.

### The delivered capability is narrower than the settled principle

That reading also survives contact with the code, which is the part worth stating plainly.
`SubZeroDev.Platform.GameEdge` is a **buffering unary forwarder**: the request body is copied whole
into memory before the forward, and the response is read whole under a single timeout budget. That
was correct for G1 — the byte-identity criterion requires the workload's bytes returned verbatim,
and no G1 criterion involved a connection that outlives a request. It is also game-named, lives
under `workloads/`, and is not a package anything can consume.

### What the second workload actually needs

SkyNet HR is a self-hosted browser console that drives an already-installed coding-agent CLI on the
machine holding the code. Node/TypeScript is a binding constraint in its own brief. Its transport is
**SSE when standalone and WebSocket when proxied**, and it arrived at that split by verifying that
SSE does not survive a buffering HTTP proxy. Its identity is **delegated** — an operator is a claim
asserted by an upstream reverse proxy, not an account it owns.

Put that workload behind today's edge and it fails, for reasons that have nothing to do with Node.
The blocker is the shape of the transport, and secondarily an Identity package that does not exist.

## Decision

**SkyNet HR is a hosted workload — the second — and the edge becomes a Platform package on that
evidence.**

1. **The extraction guard is satisfied for the edge.** GameEdge is consumer one; SkyNet HR is
   consumer two. Transport termination, routing, correlation propagation and probes stop being an
   application under `workloads/` and become a framework package.
2. **The package is scoped against every consumer, not against the workload that justified it.**
   Streaming passthrough and a WebSocket path are admitted because a second consumer needs them.
   Nothing SkyNet HR-shaped — agent supervision, workspace jailing, permission handshakes — crosses
   into the package. Those are product concerns and stay where they are.
3. **The workload stays in its own repository.** This is the deliberate difference from
   `game-service`, which was brought in-tree on 2026-08-08. An edge consumed across a real
   repository boundary is the validation
   [`implementation-plan.md`](../implementation-plan.md) §8.2 wanted and conceded it had lost.
4. **D4's criterion is amended from "two named consumers in the repository" to "two named
   consumers", with a condition attached.** The consumer must actually run on the package before it
   reaches 1.0. A named consumer that never deploys is precisely the "one and a plan" the original
   wording refused, and moving the consumer out of the tree removes the check that made the
   original wording self-enforcing.
5. **This ADR schedules nothing.** It admits a workload and justifies a package;
   [`implementation-plan.md`](../implementation-plan.md) continues to hold the order, and the work
   is a new effort with its own brief.
6. **The `Undecided` tiers are unmoved.** SkyNet HR's delegated identity is evidence about the
   *shape* Identity must support — Platform trusting an upstream assertion — and
   [Platform Identity](../platform-identity.md) §3's four undecided rows stay undecided.
   [ADR-006](ADR-006-application-modules.md) rule 3 settles a tier when the package is designed, and
   that has not happened.

## Consequences

- **The external-validation claim is recovered.** §8.2 valued the G1 edge as genuine external
  validation and then conceded the claim weakened when the workload landed in Platform's own tree.
  A workload in a different repository, consuming a published package across a real boundary, is
  the thing that was wanted. This repository has not had it before.
- **The guard is loosened, and this is the cost most likely to be paid silently.** "In the
  repository" was doing work: a consumer in the tree is checkable by a build, and a consumer
  elsewhere is asserted. Rule 4's deploy condition is the mitigation and it is weaker than what it
  replaces. If SkyNet HR never runs behind the edge, this amendment will have bought nothing and
  the package will have one consumer wearing two names.
- **A cross-repository release path, paid a second time.** [ADR-005](ADR-005-service-contract.md)
  named this cost for `SubZeroDev.ServiceContract`; the edge package now needs the same — versioned,
  published, and consumable before the workload can depend on it. It is no longer possible to change
  the edge and its consumer in one commit.
- **ADR-005 rule 4's trigger has fired.** That rule declined protobuf and named the condition for
  revisiting: *"when a boundary needs streaming."* A boundary now does. This ADR records that the
  trigger fired and **does not settle it** — a follow-up with a named cost, in the shape ADR-005
  used for `mcp-tool-contract.md`, rather than a silent consequence.
- **The dependency-direction gate applies to the move itself.**
  [`build/Test-WorkloadIsolation.ps1`](https://github.com/The-Running-Dev/SubZeroDev.Platform/blob/main/build/Test-WorkloadIsolation.ps1)
  fails any project under `src/` that references `workloads/`. Generalising the edge moves code in
  exactly the direction that gate polices, so it must arrive carrying no game knowledge at all. The
  gate is unchanged and stays enforced; what changes is that it now has something real to catch.
- **Streaming and correlation interact.** A buffering forwarder has one place to attach a trace and
  one place to report a lost hop. A stream has a beginning, an end, and a failure mode in between,
  and G1's honest-answer-when-the-workload-is-gone behaviour has no defined analogue mid-stream.
  That is design work, not a port.
- **The §1 diagram does not change yet, and that is deliberate.** BarStrad is kept out of it
  because it does not run on Platform today, and SkyNet HR does not either — admitting it to the
  diagram now would make the diagram claim something untrue, on a standard this repository has
  already applied to another consumer. It gains its third arm when the workload actually runs
  behind the edge, which is the same trigger as rule 4's deploy condition. Four copies of that
  diagram exist — [Platform Identity](../platform-identity.md) §1, the documentation index,
  `AGENTS.md` and `README.md` — and they change together or not at all.
- **SkyNet HR acquires a dependency it does not currently have.** Its design set mentions Platform
  nowhere. Adopting the edge is a change to that product's architecture, and it is that
  repository's decision to record, not this one's to assume.

## Alternatives considered

**Record SkyNet HR as consumer evidence only, and host nothing.** Add it to
[Platform Identity](../platform-identity.md) §4 as a fourth consumer of Identity, log the streaming
gap, change no code. This is the guard's own answer and it was the recommendation during
evaluation. Rejected by the repository owner in favour of hosting it. **The objection is retained
rather than dropped**, per this repository's rule on declined findings: the edge now grows a
streaming capability for a workload that does not yet run on it, and rule 4's deploy condition
exists to bound what that costs if the objection turns out to be right.

**Move SkyNet HR under `workloads/skynet-hr/`.** Follows the `game-service` precedent exactly and
satisfies D4's original wording with no amendment, which is a real advantage — no loosened guard.
Rejected on two counts. It absorbs a repository that already carries its own agent contract, a
completed nine-slice design pipeline and a Windows-and-Linux platform gate, which is expensive and
expensive to reverse. And it forfeits the external-validation claim a second time, which is the one
thing this decision is best at buying.

**Record the workload and leave its home open.** Honest about what is settled, and it defers the
argument. Rejected because the home *is* the substance: it decides whether D4's criterion is met or
amended, and an ADR whose consequences section cannot be written is not a decision.

**Leave the edge game-shaped and let SkyNet HR build its own.** No package, no amendment, no
cross-repository release path — every cost above avoided. Rejected because it produces two answers
to transport termination, correlation and probes, which is the duplication the boundary test exists
to prevent. It also refuses the second consumer at the exact moment the guard asks for one: the
product that would justify the package is the product being told to write the capability itself.
