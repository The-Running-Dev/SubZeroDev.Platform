---
sidebar_position: 4
sidebar_label: Engine Hosting Contract
---

# Hosting the Game Engine — The Contract

**Document status:** Design. Technology-free by construction — see §2.

**Reading order:** after [Platform Identity](platform-identity.md), which establishes that
the Game Engine is a hosted *workload*, and
[`game-engine-as-a-service.md`](game-engine-as-a-service.md), which is the product vision
this makes concrete.

> **Scope of this document**
>
> What "Platform hosts the Game Engine" means precisely: where the boundary is, what each
> side owns, and the four things a hosted deployment must answer that an in-process one
> never had to.
>
> It specifies **no technology.** That is
> [`technology-decision.md`](technology-decision.md), and this document is deliberately
> written so that it does not depend on the answer.

---

## 1. What the Engine Already Provides

Nothing here is proposed for the engine. It is stated so the boundary can be drawn against
what exists rather than against what might.

The engine is a pure library with a two-layer split: a stateless deterministic core, and a
thin stateful session layer above it. It defines its extension points as **ports** — a
`SessionStore`, a `ProfileStore`, an `IdSource`, a `Clock`, an `Emitter`, and an
`ExperimentSource` — and one rule decides what a host may supply:

> **A host may supply anything that cannot change `serialize()` output.**

That rule makes the determinism boundary and the trust boundary the same line, which is why
this contract can be short. A seam is on one side or the other.

It also defines a **client contract**: a client calls the session store and nothing else,
across nine operations, and *"two different clients, given the same campaign, seed,
`IdSource` and action sequence, must produce byte-identical `serialize()` output."* The MCP
tool table is those same nine operations under different names, one-to-one.

**A hosted API is a client.** That single observation determines most of what follows.

---

## 2. The Boundary: Workload Hosting

Two shapes were available.

**Supply the engine's ports in-process.** Platform implements `SessionStore`,
`ProfileStore`, `Emitter` and the rest directly. Tightest integration, least duplication —
and it forces Platform's technology to match the engine's, because those ports are
TypeScript interfaces.

**Host the engine as a workload.** The engine service is self-contained, owns its own ports,
and Platform supplies everything *around* it: identity and authorization at the edge,
provisioned persistence, telemetry collection, tenancy, billing, routing.

**Decision: workload hosting.**

Three reasons, in order of weight:

1. **It is the only shape compatible with Platform's technology being undecided.** A
   contract that silently requires Platform to be a particular runtime is not
   technology-free; it is a technology decision written somewhere it cannot be reviewed.
2. **It matches how Platform already relates to the Automator's plugins** — a process and
   image boundary, with Platform providing the surround. One hosting model rather than two.
3. **It keeps the determinism boundary inside one process.** The engine's replay guarantee
   is byte-level. Spanning it across a language boundary means serializing state across that
   boundary and trusting both sides to agree byte-for-byte forever. That is a large risk
   taken for no gain the first shape does not also offer.

> **What this costs, stated plainly.** Platform cannot reach into a session. Anything
> Platform needs to know about play — for metering, analytics, or quota enforcement — must
> arrive as an event or a store record, never by inspection. §6 is where that cost is paid,
> and it is the reason the account surface and the game surface are separate.

---

## 3. The Engine Is a Product, Not a Plugin

Worth stating explicitly, because the plugin contract is the nearest existing shape and it
is the wrong one.

| | Plugin contract | The Game Engine |
|---|---|---|
| Invocation | A command, run to completion | A session, resumed across calls |
| State | None between runs | `sessionId`, save/load, a sequenced action log |
| Result | An envelope plus an exit code | A projected `Scene`, and never the raw envelope |
| Identity of a run | The execution record | The game state itself |

A plugin that held sessions would need leases, heartbeats and orphan detection — the
machinery the Automator has precisely because plugin executions are *not* sessions. The
engine is a product, a sibling of the Automator, and nothing should attempt to express it
as a `plugin.yaml`.

**This does not exclude it from the MCP surface.** MCP is a transport, and its tool
definitions are *projected from a manifest* for plugins. The engine's tool table is
hand-written, fixed, and tested against the engine end to end. Both are legitimate
producers of tool definitions — which is a constraint on Platform's MCP package, not on the
engine. See [`second-consumer-packages.md`](second-consumer-packages.md) §4.

---

## 4. Who Owns What

| Concern | Owner | Why |
|---|---|---|
| Game rules, resolution, determinism | Engine | Kinds are engine-owned code |
| Session and profile persistence | Engine | The ports are its own; the *database* is Platform-provisioned |
| Projection — what a player may see | Engine | Structural, not a hosting policy |
| Campaign content and validation | Engine | Data, validated in tiers |
| The nine-operation game surface | Engine | It is the client contract |
| Authentication — who is calling | **Platform** | The one concept the engine does not have |
| Authorization — may they touch this session | **Platform**, enforced at the edge | Ownership is a hosting fact |
| Tenancy and isolation | **Platform** | |
| Accounts, plans, entitlements, metering | **Platform** | |
| Telemetry collection and export | **Platform** | The engine emits; Platform collects |
| Transport, routing, rate limiting, idempotency | **Platform** | API conventions |
| Which campaigns are published, and to whom | **Platform** | Catalogue is a hosting concern |

**The single rule underneath the table:** the engine owns everything that can change the
outcome of a game; Platform owns everything about *who is playing and under what
agreement*. The engine's own rule — a host may supply anything that cannot change
`serialize()` output — is the same line seen from the other side, and it is why this split
needs no negotiation per feature.

---

## 5. Two Surfaces, Never Merged

The **game surface** is the nine operations. The **account surface** is everything about a
player that is not a game: sign-up, plans, API keys, listing your own saves, deleting your
data.

They must not merge, and the reason is specific rather than aesthetic. The client contract
holds that *a client never works around a missing operation* — if a client needs something
the store does not offer, the answer is a new store operation **and** a new row in the
coverage checklist, never client-side logic. That rule is what keeps game logic out of
clients, and it is checkable by counting.

A hosted service genuinely needs `list_saves` and `delete_account`. Adding either as a tenth
store operation would break the one-to-one mapping and put hosting concepts into the
engine's coverage checklist, where they would have to be implemented by the text client to
keep the count honest. **Account lifecycle is not a game operation.** Two surfaces, one
principal, and the game surface stays exactly as wide as the engine's.

> **A tenth operation is coming, and it is not this.** The `world-graph` kind needs
> `previewAction` — checking a parameterized action before committing it — which the engine
> has already specified and which will make the checklist ten operations and ten tools. That
> is a game operation and belongs in the count. It is named here so the two cases are not
> confused: the transport must treat the operation set as data, so a tenth is a table entry
> rather than a rewrite.

---

## 6. Four Things a Hosted Deployment Must Answer

Each of these is invisible in-process and unavoidable over the wire.

### 6.1 Concurrent actions — the sharpest one

Two `submitAction` calls against one session, arriving at two instances, both read the same
envelope, both apply an action, and one write silently overwrites the other. No error is
raised, no validation fails, and the surviving state is one the engine would never have
produced. **It is a determinism break that presents as a lost update.**

The in-memory store never faced this because it was one process.

**Resolution: compare-and-swap on the sequence number.** The envelope already carries a
sequenced action log, and the engine's save handle already exposes `savedAtSeq` — so the
version is present and needs no new concept. A write asserts the sequence it read; a
mismatch is a rejection the caller must handle, never a merge.

**Merging is not available and should not be attempted.** Two actions applied to the same
base state are two different games; there is no rule that combines them, because the engine
resolves actions in order against a specific state and its randomness is derived from the
action sequence.

### 6.2 Session and save ids become capabilities

In-process, `loadGame(saveId)` trusts its caller — correctly, because the only caller is
the application itself. Over the wire, an unauthenticated or guessable `saveId` is a
cross-account read of another player's game.

Two requirements, and both are needed:

- **Ids must be unguessable.** The engine's `IdSource` port exists exactly here and a host
  may supply any implementation, so this is a composition choice rather than a change.
  Sortable-but-predictable ids are not sufficient on their own.
- **Ownership must be checked at the edge**, on every operation carrying an id. Unguessable
  ids are defence in depth, never the authorization.

> The engine's tool contract says nothing about authorization, and that is correct — in
> process there is no principal to authorize. This is the gap hosting introduces, and
> naming it as a gap rather than a defect is the point.

### 6.3 Authorization is a decorator, not a fork

Ownership resolution wraps the store: resolve the principal, verify they own the session,
delegate. It implements the identical interface and holds no game logic.

**This is checkable rather than asserted.** The engine's own rule gives the test directly —
the decorated store must produce byte-identical `serialize()` output to the undecorated one
for the same inputs. A decorator that changed the game would fail it.

Forking the store instead — a "hosted" variant with tenancy woven through — would put
ownership checks on the same side of the line as resolution, and the property above could
no longer be stated.

### 6.4 The projection boundary must survive the transport

A client receives a projected view and never the raw envelope; the engine enforces this by
returning a result type whose success value is a `Scene` rather than the state. A transport
that serialized the envelope — for caching, for debugging, for a "raw state" endpoint —
would put hidden variables, visit counts and the seed on the far side of a boundary the
engine built structurally.

**No hosted endpoint returns engine state.** Persistence writes the canonical serialization
to storage the player cannot read; it never returns it through the API.

---

## 7. What Platform Provisions

Stated as obligations, so an implementation has them in one place. These are the engine's
own port obligations, restated as hosting requirements:

- **Persist the canonical serialization, not live objects.** A store keeping object graphs
  drifts from what the engine's `deserialize` accepts.
- **Never write host metadata into game state.** Timestamps, owner ids, tenant ids and
  principal ids live on the store's own record. This is the boundary that keeps determinism
  intact while still supporting resume-on-another-device.
- **A failed profile write must not roll back a completed game action.** The game is the
  system of record; the profile is a projection of it.
- **A missing or corrupt profile degrades to "no achievements"**, never to a broken game.
- **Carry a tenant identifier from the first schema**, defaulted to a single implicit
  tenant. Adding the column later is easy; adding tenant *isolation* to queries and storage
  paths after data exists is a correctness migration on every table at once.

---

## 8. Cross-Repository Dependencies

This contract is not independently buildable. What it waits on, and why:

| Depends on | State | Consequence |
|---|---|---|
| The engine published as a consumable package | In flight — the consumer-boundary unit is planned and not merged | Nothing can consume the engine until it packs and installs cleanly |
| Content-pack resolution implemented in code | Specified, not built | The catalogue and publishing phases block on it |
| `ExperimentSource` declared in the engine's composition types | Specified, not declared | Needed before experiment-gated content |
| `previewAction` and the tenth operation | Specified, arriving with `world-graph` | The transport must treat the operation set as data |

**One dependency runs the other way.** The engine records that its session-layer composition
root has *"two real call sites and zero real implementations"* of the abstraction it
specifies, and that the question should be revisited when a second `SessionStore`
implementation actually exists. **A hosted, durable store is that second implementation.**
This product is therefore the thing that resolves an open engine question, rather than
routing around it — and the engine's own condition for revisiting is met the moment durable
persistence is built here.
