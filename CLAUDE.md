# Project Instructions

**SubZeroDev.Platform — NEaaS (Narrative Engine as a Service).** The deferred hosting /
SaaS / business layer: accounts, billing, cloud sync, analytics, multiplayer, white-label.
**Vision only — not a v1 requirement**, explicitly out of scope until the engine is proven.

- The single spec: [`docs/docs/neaas-platform-vision.md`](docs/docs/neaas-platform-vision.md).
- The **engine** (source + specs) is
  [SubZeroDev.GameEngine](https://github.com/The-Running-Dev/SubZeroDev.GameEngine); the
  **game** is [SubZeroDev.GameOfLife](https://github.com/The-Running-Dev/SubZeroDev.GameOfLife).
  This layer depends on both conceptually but ships nothing yet.

The doc renders as a Docusaurus site via `docs.ps1`. The shared docs-site / graphify /
claude-mem tooling notes live in the engine repo's `CLAUDE.md` and apply identically here.

## Working conventions

Findings are presented one at a time for sign-off, not bulk-applied. **This layer stays
deferred** — do not build (or spec out in detail) hosting features before the engine MVP is
done, and record any premature ideas in the vision doc as intent, not a build target.
