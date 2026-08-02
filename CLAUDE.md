# Project Instructions

**SubZeroDev.Platform — the reusable application framework and hosting layer.** Hosting,
configuration, identity, authorization, tenancy, billing, notifications, storage, events,
observability, API and MCP conventions — built once, reused by every product.

**Platform is not a product.** It never depends on one, and never on a plugin:

```text
              SubZeroDev.Platform
                 ↓            ↓
        SubZeroDev.Automator   Game Engine as a Service
                 ↓
      Plugins / Workflows / Products
```

A reference from Platform to a product is a build failure, not a review comment.

- **Start here:** [`docs/docs/platform-identity.md`](docs/docs/platform-identity.md) — what
  this repository is, and the collision it settles. Everything else depends on it.
- The reading order is the sidebar order: identity → platform specification → Game Engine as
  a Service → engine hosting contract → MCP tool contract → technology decision → packages →
  implementation plan.
- **Game Engine as a Service (GEaaS)** is one *hosted workload*, not what this repository is.
  Formerly "NEaaS — Narrative Engine as a Service"; renamed because the engine ships three
  kinds and only one of them is narrative.

**Companions:**
- **Engine** — [SubZeroDev.GameEngine](https://github.com/The-Running-Dev/SubZeroDev.GameEngine):
  the Game Engine, source and specs. Past MVP.
- **Game** — [SubZeroDev.GameOfLife](https://github.com/The-Running-Dev/SubZeroDev.GameOfLife)
  (Life in the Fast Lane) and
  [SubZeroDev.SunTrap](https://github.com/The-Running-Dev/SubZeroDev.SunTrap).
- **Ecosystem specifications** — staged at `D:\Dropbox\Projects\SubZeroDev\Specs`, split by
  destination repository. The `SubZeroDev.Platform/` documents there have been **copied**
  here; the originals are duplicates pending deletion (that tree is not version-controlled,
  so removal is a confirmed step). The rule is **move, do not copy** — a second copy drifts
  the moment either is edited.

The docs render as a Docusaurus site via `docs.ps1`. The shared docs-site / graphify /
claude-mem tooling notes live in the engine repo's `CLAUDE.md` and apply identically here.

## Working conventions

Findings and review items are presented **one at a time for sign-off**, not applied in bulk.
When a suggestion is declined, record it in the affected document as a known-and-retained
issue rather than dropping it silently.

**This repository is design-stage, and that is the current constraint** — not deferral. The
near-term package set is unstarted and blocked on
[`docs/docs/technology-decision.md`](docs/docs/technology-decision.md). Do not build Platform
packages before that decision is taken, and do not build hosted game features ahead of the
[implementation plan](docs/docs/implementation-plan.md)'s stated ordering constraints.

**The extraction guard governs what may be added.** A candidate becomes a Platform package
when a **second** consumer needs it, not when the first one does. Record premature ideas as
intent, not as a build target.

### Git

Branch off `main`; stage by explicit named path — never `git add -A`, `git add .`, or a bare
directory. Never force-push or rewrite published history. Open the PR and leave the merge to
the repository owner. Commit messages follow the descriptive style used across these
repositories, not Conventional Commits.
