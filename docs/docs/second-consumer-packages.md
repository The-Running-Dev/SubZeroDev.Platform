---
sidebar_position: 8
sidebar_label: Second-Consumer Packages
---

# The Four Packages That Just Earned a Second Consumer

**Document status:** Design. Justified, not scheduled.

**Reading order:** after [Platform Identity](platform-identity.md) §4, which establishes
that Game Engine as a Service is a second consumer, and
[`minimal-platform-packages.md`](minimal-platform-packages.md), which is what is being built
first.

> **Scope of this document**
>
> Identity, Tenancy, Billing and Mcp: what each must own to serve **both** consumers rather
> than one. The value of writing this now is not the schedule — it is that a package
> designed against two consumers is shaped differently from one designed against the first,
> and the difference is cheapest to see before either exists.

---

## 1. Why Two Consumers Changes the Design

The extraction guard's usual justification is that a second consumer proves the concern is
real. That is true, but it undersells it.

A package designed from one consumer encodes that consumer's assumptions as though they were
general. The first product then bends around interfaces that were never tested against
anything, and the second discovers the mismatch after the API is public — which is expensive
precisely because it is public.

Below, each package is stated as **what both consumers need**, with the divergence called
out. Where the two want different things, that is the design constraint, not a conflict to
resolve later.

---

## 2. Identity

| | Automator | Game Engine as a Service |
|---|---|---|
| Principal | Operator, service account, API key | Player, creator, studio member |
| Auth | Interactive login, API keys, service accounts | Federated login, API keys, and **agent clients** |
| Sessions | Long-lived operator sessions | Both interactive and machine callers, at volume |

**The shared requirement** is a principal abstraction that the rest of Platform can depend on
without knowing which product produced it.

**The divergence that matters:** the Game Engine's callers include AI agents connecting over
MCP. Under the ecosystem's MCP decision, **secrets are never tool parameters** — an argument
enters the model's context and therefore any provider the client forwards to, persists in
logs and conversation history, and a credential a model has seen should be treated as
disclosed. So Identity must support authenticating an MCP connection at the *transport*,
never per call.

**Identity must stay optional for local-only products.** A single-user local deployment
should not need artificial account setup.

---

## 3. Organizations and Tenancy

| | Automator | Game Engine as a Service |
|---|---|---|
| Tenant | A team or organization | A studio, on the white-label tier |
| Isolation | Executions, secrets, plugin installs | Sessions, saves, published campaigns |
| Single-user | The common case | The common case — free tier is one player |

**The shared requirement** is that isolation is enforced in one place rather than
per-product, and that the single-tenant case costs nothing.

**The divergence that matters:** the Game Engine has a *public* tier. Published campaigns are
readable across tenants by design, while sessions and saves never are. So tenancy cannot
assume "tenant-scoped" is the only mode — it needs an explicit, auditable notion of a
deliberately shared resource, or every catalogue read becomes a hand-written escape from the
isolation rule. **That escape is the thing to design, because an unmodelled one is how
isolation quietly stops holding.**

**The column ships regardless of the feature**, per
[`minimal-platform-packages.md`](minimal-platform-packages.md) — Persistence carries a tenant
identifier from the first schema.

---

## 4. Mcp — the package with a real constraint

This is the one where a single-consumer design would have been actively wrong.

The ecosystem's MCP decision holds that tool surfaces are **projected from the plugin
manifest, never hand-written** — and it is right, for plugins: hand-written tool definitions
drift from the manifest the CLI implements, and then a plugin's own MCP surface disagrees
with the Automator's projection of the same plugin.

**The Game Engine's tool surface is hand-written, and equally right.** Its tool table is a
fixed set of operations mapping one-to-one onto the engine's client API, tested against the
engine end to end, with the one-to-one mapping *itself* being the property that proves no
AI-specific game path exists. There is no manifest to project from, and introducing one would
add a second source of truth to a contract whose whole value is that it has one.

**Therefore:**

> `Platform.Mcp` owns transport, authentication, consent, logging, authorization and tool
> **registration**. It does not own where tool definitions come from. Manifest projection is
> one producer; a fixed, product-owned table is another.

If projection is baked into the package, the Game Engine cannot use it — and that is exactly
the designed-from-one-consumer failure the extraction guard exists to prevent, arriving
through the door the guard was watching.

Two further constraints, both inherited and both non-negotiable:

- **No secrets as tool parameters.** Authentication belongs to the connection.
- **Exposure is opt-in per tool, default closed.** Automatic exposure on install is
  explicitly rejected, and a hosted game surface has the same requirement for a different
  reason: nine safe tools plus one unreviewed one is one unreviewed tool.

---

## 5. Billing

| | Automator | Game Engine as a Service |
|---|---|---|
| Model | Open-core, feature-tiered, per installation | Subscription tiers — Free, Creator, Studio |
| Paid dimension | Agents | Campaigns, saves, AI authoring, seats |
| Metering | Completed executions, stored bytes | To be decided — but see below |

**The shared requirement** is plans, prices, entitlements, subscriptions, metered usage,
invoices, provider adapters, trials, grace periods and plan transitions — with business code
depending on the abstraction rather than the provider.

**The divergence that matters:** the metered-dimension decision is *"completed executions and
stored bytes, never execution minutes"*, and the reasoning generalizes — a dimension the
customer cannot predict before committing is a dimension that produces disputes. Applied to
the Game Engine, **sessions and stored saves are meterable; playtime is not.** That is worth
recording now, because "per hour played" is the obvious first suggestion and it is the same
mistake execution-minutes would have been.

**Two properties must hold for both consumers:**

- **Billing is never required for self-hosted use.** The community edition has no licence
  code path at all — not a check that passes.
- **Expiry degrades features, never data or running work, and fails open.** A player mid-game
  when a subscription lapses does not lose the session.

---

## 6. Storage, Briefly

Also justified — execution artifacts for one consumer, saves and campaign assets for the
other — but it needs no reconciliation: streams, metadata, checksums, signed URLs, retention
and tenant isolation serve both unchanged. Noted so its absence from the sections above is
deliberate.

---

## 7. What This Document Does Not Do

It does not schedule anything. All four remain candidates; the near-term set is still the
six. The ecosystem roadmap places identity, tenancy and billing in its commercial phase, and
nothing here moves them — see [`implementation-plan.md`](implementation-plan.md).

What it does is make the *shape* reviewable while changing it is still free.
