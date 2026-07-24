# Narrative Engine as a Service (NEaaS) — Platform Vision

**Document status:** Deferred. Vision only — not a v1 requirement.
**Project stage:** Explicitly out of scope until the engine is proven.

> **Why this is its own document (N13).** The hosting, accounts, billing, cloud sync,
> analytics, multiplayer and white-label material was mixed into the engine
> specification. Bolting a SaaS platform onto an engine with no code yet is the exact
> scope-creep the simulation kind's own risk register (`games/01-vision.md` §6.1)
> warns against. The engine is a pure library first; this layer is deferred until it
> is proven. Everything here is recorded intent, not a build target.

---

## Vision

A hosted, API-first, MCP-first narrative platform where creators build, publish, and
play branching narrative games on a shared engine. The hosted service becomes the
authoritative source for game state, rules, saves, campaigns, and AI-assisted
authoring.

> Build the engine once. Host it once. Consume it everywhere.

The engine and its two kinds (the [SubZeroDev.GameEngine](https://github.com/The-Running-Dev/SubZeroDev.GameEngine) architecture) are the
foundation this layer would sit on. Nothing here changes the engine; it wraps it.

---

## What the hosted layer would add

Above the pure engine:

- Session persistence as a managed service (the session store of §2 becomes hosted).
- Player accounts and cloud-synced saves.
- Campaign publishing, versioning, and a public/private catalogue.
- Community sharing.
- Analytics.
- AI-assisted authoring as a hosted creator tool (the boundary of §9 still applies —
  AI authors campaign data, the engine validates it).
- Multiplayer sessions (far future).

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
resolution? a dependency graph? The simulation kind's `ContentPackManifest` (docs/04
§4.2) already carries `dependencies`, but **no merge or override semantics are
specified.** That is the gap.

**Community modding.** Untrusted third-party packs raise validation, sandboxing, and
dependency-resolution questions beyond first-party culture packs. Ties directly into the
merge rules above. Both are the same body of work, deferred together.

## Possible business model

- **Free** — community campaigns, limited saves, limited AI generation, basic hosting.
- **Creator** (subscription) — unlimited campaigns and saves, AI authoring, analytics,
  private campaigns, sharing.
- **Studio** — white-label, custom domains, teams, collaboration, full API, enterprise
  hosting, dedicated MCP endpoint.

## Differentiators

Headless, hosted, API-first, MCP-first, AI-assisted authoring, platform-independent,
campaigns-as-data, UI fully decoupled, deterministic gameplay.

## Inspiration

Twine · Ink · ChoiceScript · RPG Maker · AI Dungeon · NovelAI · Unity · Godot ·
PlayFab. The objective is to combine ideas from these into one hosted platform focused
on AI-native narrative games.

---

## The order of operations

1. Core + the two kinds, proven by tests and a text client. *(Now.)*
2. `story-graph` content model and Bulgarian Edition as real content. *(Next.)*
3. The unified API + MCP surface.
4. **Only then** the hosted layer above.

The engine is the foundation. The hosted service is the ecosystem. Campaigns are the
products. In that order.
