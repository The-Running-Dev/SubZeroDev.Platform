---
sidebar_position: 6
sidebar_label: Technology Decision
---

# Platform's Implementation Technology

**Document status:** **Open.** This is the decision record, written before the decision, so
that the options and their costs are on the table rather than settled by whoever writes
first.

> **Scope of this document**
>
> What technology Platform is built in. It is open, and it is deliberately **not blocking**
> — [`engine-hosting-contract.md`](engine-hosting-contract.md) is written to survive any
> answer, and the near-term package set below is the same shape under all three options.

---

## 1. Why This Is a Real Question

The ecosystem specification set assumes .NET throughout, without ever recording it as a
decision. It is visible in the specification's own examples — `SubZeroDevApplication
.CreateBuilder(args)`, `IPlatformModule`, EF Core with SQLite and PostgreSQL, NuGet as the
packaging target, "comparable in purpose to ABP Framework". None of that is wrong; it is
simply assumed rather than chosen, and it was assumed when the Automator was the only
consumer in view.

There is now a second consumer, and it is **TypeScript**: the Game Engine is a Node package
with a byte-level determinism guarantee enforced by a lint rule that bans `Math.random`,
`Date.now` and the non-bit-stable `Math.*` functions.

So the question is live in a way it was not before, and it is worth answering explicitly.

---

## 2. What the Decision Does Not Affect

Establishing this first keeps the option space honest.

- **The hosting boundary.** [`engine-hosting-contract.md`](engine-hosting-contract.md) §2
  chose workload hosting *because* it survives this decision. The engine runs as its own
  service under every option below.
- **The near-term package set.** Abstractions, Core, Hosting, Persistence, Observability,
  Testing are the same six concerns in any runtime.
- **The dependency rule.** Platform never depends on a product, in any language.
- **The boundary test and the extraction guard.** Both are architectural, not technical.

What it *does* affect: package identifiers and registries, the module-registration shape,
the persistence stack, how much of the ecosystem specification transfers verbatim, and
whether one team can work across Platform and the engine without a context switch.

---

## 3. The Options

### Option A — .NET, as the ecosystem specifications assume

**For.** The entire specification set transfers verbatim, including the module model, the
persistence stack, and the configuration precedence chain. It is the strongest ecosystem
for the things Platform actually is — hosting, DI, EF Core migrations, OpenTelemetry
wiring, and background jobs are mature and boring, which is what infrastructure should be.
The Automator is specified in it. The existing PowerShell tooling across these repositories
sits naturally beside it.

**Against.** Platform and the Game Engine are then permanently different runtimes, and
nobody works across both without a context switch. Any future desire to supply the engine's
ports in-process is closed off for good, not merely deferred.

### Option B — TypeScript/Node, matching the engine

**For.** One runtime across Platform and the Game Engine. Leaves the in-process port option
genuinely open rather than closing it. The engine's existing package tooling and CI patterns
transfer directly.

**Against.** The ecosystem specification set stops transferring verbatim — the module
model, EF Core, and the packaging targets would all need re-specifying, and the Automator is
specified against .NET. It optimizes Platform for its *second* consumer at the expense of
its first, which inverts the extraction guard's own logic. And the argument's main prize —
in-process ports — is one the hosting contract already declined for independent reasons.

### Option C — Polyglot, with the workload boundary as the contract

Platform is .NET; products are whatever suits them; the boundary between them is a process
and image boundary with an HTTP and MCP surface, exactly as it already is for the
Automator's plugins.

**For.** It is what the hosting contract already describes, so it costs nothing new. The
ecosystem specifications transfer. The engine stays Node and keeps its determinism
guarantee inside one process. Adding a product in a third language later needs no
architectural change — which is the plugin contract's own thesis, applied one level up.

**Against.** Cross-language operational surface: two toolchains, two dependency ecosystems,
two sets of CI. Shared client libraries must be written per language, or not shared. No
in-process integration is available, ever.

---

## 4. Recommendation

**Option C, which is Option A plus an explicit statement that the boundary is a process
boundary.**

The distinction between A and C is not the technology — both build Platform in .NET. It is
whether polyglot products are an accepted consequence or an accident nobody decided. Stating
it makes the shared-client question a design item rather than a surprise, and it means the
engine never has to justify being Node.

Option B is the one to decline, and for a reason worth recording: it optimizes Platform for
its second consumer against its first, and buys mainly an in-process integration the
hosting contract already turned down on determinism grounds.

**This recommendation is not the decision.** It is recorded so that whoever takes the
decision does so against stated alternatives.

---

## 5. What Settling It Unblocks

| Blocked on this | Why |
|---|---|
| Package identifiers and registry reservations | NuGet prefix versus npm scope |
| The module-registration contract | Its shape is language-shaped |
| The persistence baseline | EF Core migrations versus something else |
| Repository layout for `src/` | The layout tables assume .NET project structure |

**Nothing in P0 or P1 is blocked**, which is why they were written first.

---

## 6. Deciding It

Two things should be true before this is settled, and neither is expensive:

1. **The identifier reservations happen either way** — the ecosystem naming decision already
   requires reserving the NuGet prefix, the npm scope, the container namespace and the
   PowerShell Gallery prefix before first publish. Reservation is free now and never again,
   and it is independent of which one Platform actually publishes to.
2. **The scope disagreement is resolved** — the engine publishes under `@the-running-dev`
   while the naming decision fixes `@subzerodev`. That is a naming question, not a
   technology one, but it will be discovered at the same moment.
