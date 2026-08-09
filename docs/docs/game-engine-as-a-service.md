---
sidebar_position: 3
sidebar_label: Game Engine as a Service
---

# Game Engine as a Service (GEaaS)

**Document status:** Vision. A hosted product, not Platform itself, and not a v1
requirement.

> **Renamed and reframed.** This document was *"Narrative Engine as a Service (NEaaS) —
> Platform Vision"*, and it described what this repository was. It is now one **workload**
> Platform hosts — see [Platform Identity](platform-identity.md). The engine is renamed from
> "Narrative Engine" to **Game Engine**, matching what
> [SubZeroDev.GameEngine](https://github.com/The-Running-Dev/SubZeroDev.GameEngine) calls
> itself and what it has become: it ships three kinds — `story-graph`, `simulation` and
> `world-graph` — and a weekly-budget life simulation and a resort-management sim are not
> narratives. "Narrative" now describes one kind, not the engine.

---

## Vision

A hosted, API-first, MCP-first **game platform** where creators build, publish, and play
games on a shared engine. The hosted service becomes the authoritative source for game
state, rules, saves, campaigns, and AI-assisted authoring.

> Build the engine once. Host it once. Consume it everywhere.

The engine and its kinds (the
[SubZeroDev.GameEngine](https://github.com/The-Running-Dev/SubZeroDev.GameEngine)
architecture) are the foundation this product sits on. Nothing here changes the engine; it
wraps it. **How** it wraps it — the workload boundary, and what Platform supplies versus
what the engine owns — is
[`engine-hosting-contract.md`](engine-hosting-contract.md), which is a contract rather than
a vision.

---

## What the hosted layer would add

Above the pure engine:

- Session persistence as a managed service (the engine's session store becomes hosted).
- Player accounts and cloud-synced saves.
- Campaign publishing, versioning, and a public/private catalogue.
- Community sharing.
- Analytics.
- AI-assisted authoring as a hosted creator tool (the engine's authoring boundary still
  applies — AI authors campaign data, the engine validates it).
- Multiplayer sessions (far future).
- **A live MCP transport**, making the tool contract
  ([`mcp-tool-contract.md`](https://github.com/The-Running-Dev/SubZeroDev.ServiceContract/blob/main/mcp-tool-contract.md))
  reachable by a real AI client rather than only called directly by tests.

Each of those is either a Platform concern or a product concern, and the split is not
obvious by inspection. [`engine-hosting-contract.md`](engine-hosting-contract.md) §4 draws
it explicitly.

## Creator workflow

```text
Create campaign → test locally → upload → publish → players consume from any client
```

## Known deferred gaps (before mods, not before MVP)

Raised in peer review, captured here rather than specified — they have no consumer until
content packs stack, which is post-MVP. Spec them when the first pack that needs them
appears, not before.

**Content-pack merge rules.** Once packs layer —
`base campaign → expansion → localization → culture pack → community pack → user mod` —
the engine needs defined answers to: override or merge? priority order? conflict
resolution? a dependency graph?

> **Partly closed since this was written.** The engine now specifies content-pack
> resolution: a pure ordered fold in which campaigns replace wholesale and strings replace
> per key, exact-version dependencies with no range solving, and `campaignVersion` stamped
> with a digest of the resolution so a game records the content it actually ran against.
> What remains open is **community** packs specifically — the trust half below — not the
> merge semantics.

**Community modding.** Untrusted third-party packs raise validation, sandboxing, and
dependency-resolution questions beyond first-party culture packs. A pack is data and is
validated like any other content, so it needs no sandbox — but **who may publish one** is a
hosting question, and therefore this product's, not the engine's.

## Possible business model

- **Free** — community campaigns, limited saves, limited AI generation, basic hosting.
- **Creator** (subscription) — unlimited campaigns and saves, AI authoring, analytics,
  private campaigns, sharing.
- **Studio** — white-label, custom domains, teams, collaboration, full API, enterprise
  hosting, dedicated MCP endpoint.

Those three tiers are what make this product a second consumer of Platform's billing,
identity and tenancy candidates — see [Platform Identity](platform-identity.md) §4.

## Differentiators

Headless, hosted, API-first, MCP-first, AI-assisted authoring, platform-independent,
campaigns-as-data, UI fully decoupled, deterministic gameplay.

## Inspiration

Twine · Ink · ChoiceScript · RPG Maker · AI Dungeon · NovelAI · Unity · Godot ·
PlayFab. The objective is to combine ideas from these into one hosted platform focused
on AI-native games.

---

## The order of operations

1. Core + the kinds, proven by tests and a text client. **Done** — the engine's MVP
   definition of done is fully checked, and `simulation` and `world-graph` have followed.
2. `story-graph` content model and the Bulgaria adventure as real content. **Done** — four
   arcs built.
3. The unified API + MCP surface. **This is the open step**, and it is where this product
   starts: the engine's tool table is implemented and tested in-process, but no real
   transport serves it over the wire.
4. **Only then** the hosted layer above.

The engine is the foundation. The hosted service is the ecosystem. Campaigns are the
products. In that order.
