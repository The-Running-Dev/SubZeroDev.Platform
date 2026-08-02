# Decision log

Append-only. Newest at the top. The rejected alternatives are the point — without them, every future session relitigates the same choice.

## Open
- **Two decision-recording mechanisms.** This log versus `docs/docs/adr/`. `AGENTS.md` names this file; the ecosystem convention inherited with the moved-in specifications is *a decision gets an ADR*, and those specifications cite ADRs by number across repositories, so they need a stable target. ADRs are used as the home and this log indexes them. Only one should survive — collapse it deliberately rather than letting both accumulate.
- **Brand identifier reservation is unperformed.** NuGet `SubZeroDev.*`, npm `@subzerodev`, container `subzerodev-*`, PowerShell Gallery `SubZeroDev.*`. Free now, not free after anything publishes. Requires registry credentials, so it is the repository owner's action.
- **The docs site has no root page, and this repository cannot author one.** `baseUrl` is `/` and `routeBasePath` is `docs`, so nothing serves `/` while the navbar brand links there from every page. Attempted the obvious fix — `docs/src/pages/index.md` — and it does not work: the base image's pre-build step strips everything under `src/pages`, reporting them as "routes this project did not author". The file reaches the image and is deleted before compilation. Verified by building with `onBrokenLinks: 'throw'`: 16 broken links, all of them `/`, nothing else. So the link check cannot be hardened until this is fixed, and fixing it needs either a `docs-template` change or `routeBasePath: '/'`, which moves every page URL and is an information-architecture decision to take deliberately. Worth checking whether `SubZeroDev.GameEngine` has the same problem silently — its `CLAUDE.md` describes `docs/src/pages/index.md` as load-bearing there.

---

### 2026-08-02 — Package scope is per-registry, not one global name
Context: The ecosystem naming ADR fixes the npm scope as `@subzerodev`; the Game Engine publishes `@the-running-dev/game-engine`. This looked like drift and is not — GitHub Packages requires the npm scope to match the repository owner, and the owner is the `The-Running-Dev` organization. Underneath sits a mismatch neither ADR names: the GitHub organization is `The-Running-Dev` while the brand namespace is `SubZeroDev`.
Chosen: Scope follows the registry — `@the-running-dev` on GitHub Packages, `@subzerodev` reserved for public npm, `SubZeroDev.*` on NuGet and PowerShell Gallery, `subzerodev-*` for containers. The engine does not rename. Recorded as `docs/docs/adr/ADR-003-package-scopes-and-registries.md`.
Rejected: **Rename the engine to `@subzerodev/game-engine`** — does not work on GitHub Packages under this organization, so it forces either public publication of a private package or a move off that registry, a delivery decision taken for a cosmetic reason. **Rename the GitHub organization** — the only option that fixes the root rather than routing around it, but it redirects every URL, remote, coordinate and CI reference for consistency rather than capability; reconsider before anything publishes publicly, not after. **Standardise on `@the-running-dev`** — abandons the brand identity for an artifact of which account happens to hold the repositories.
Reversibility: cheap now, expensive after first publish

### 2026-08-02 — Platform is .NET, and the product boundary is a process boundary
Context: The ecosystem specifications assume .NET without ever recording it as a decision — it is visible only in their own examples. The identity decision below added a second consumer on a different runtime (the Game Engine is TypeScript with a byte-level determinism guarantee), so the assumption needed testing rather than inheriting.
Chosen: .NET, with polyglot products as an accepted consequence rather than an accident nobody decided. Products meet Platform over a process and image boundary, exactly as the Automator's plugins already do. Recorded as `docs/docs/adr/ADR-002-implementation-technology.md`.
Rejected: **TypeScript/Node to match the engine** — optimises Platform for its second consumer at the expense of its first, inverting the extraction guard's own logic, and buys mainly an in-process integration the hosting contract had already declined on determinism grounds. **Defer further** — nothing in P0–P2 was blocked, but P3 cannot start without it and identifier reservation stops being free; deferring moves the decision to the moment it becomes expensive.
Reversibility: expensive once packages publish; cheap today

### 2026-08-02 — Hosting is a workload boundary, not in-process port supply
Context: "Platform hosts the Game Engine" admits two readings — Platform implements the engine's `SessionStore`, `Emitter` and `Clock` ports directly, or the engine runs as a self-contained service with Platform supplying identity, persistence, telemetry and routing around it. The choice had to be made before Platform's technology was settled.
Chosen: Workload hosting. Recorded in `docs/docs/engine-hosting-contract.md` §2.
Rejected: **In-process ports** — forces Platform's runtime to match the engine's, which is a technology decision written where it cannot be reviewed; and spanning the engine's byte-level determinism guarantee across a language boundary means trusting two runtimes to agree byte-for-byte indefinitely, for no gain the workload shape does not also offer.
Reversibility: expensive

### 2026-08-02 — "Narrative Engine" renamed to "Game Engine"
Context: The engine ships three kinds — `story-graph`, `simulation`, `world-graph`. A weekly-budget life simulation and a resort-management sim are not narratives, and the engine repository already calls itself the Game Engine.
Chosen: Game Engine throughout, so NEaaS becomes GEaaS. Folded into ADR-001's consequences.
Rejected: **Keep "Narrative Engine"** — a theme word in a name smuggles in a decision, and this one had already stopped being true.
Reversibility: cheap

### 2026-08-02 — `SubZeroDev.Platform` is the framework, not the game product
Context: Two document sets each defined a `SubZeroDev.Platform` — a game-hosting product in this repository, a reusable application framework in the ecosystem staging tree, whose repository-layout table names this repository as its home and marks it "Exists." It did not. The staging tree contains no mention of the game work at all: not "narrative", not "GameEngine", not "SunTrap", not the word "game". Neither set knew the other existed.
Chosen: The framework. Game Engine as a Service becomes one hosted workload, a sibling of the Automator. Recorded as `docs/docs/adr/ADR-001-platform-identity.md`.
Rejected: **Rename the framework and keep the game product here** — reopens a settled naming ADR whose reasoning still holds, and relocates the side with fifteen repositories and a roadmap depending on it in favour of the side with two documents. **Both keep the name, disambiguated by context** — breaks the naming ADR's own answer to ambiguity, that "Platform" is unqualified only inside its own repository. **Merge them into one product** — the reusable half acquires game-shaped assumptions (a storage abstraction that knows what a save is), which is precisely the failure the platform/automator split exists to prevent.
Reversibility: expensive

### 2026-08-02 — Design docs live at `design/`, not `docs/design/`
Context: Installing the agent kit. The kit ships its five design documents at `docs/design/`. In this repository `docs/` is the Docker build context for the documentation site — `docs.ps1` builds from it and `docs/Dockerfile` does `COPY . .` onto `/template`. A `docs/design/` directory would therefore be baked into the published image at `/template/design/`. It would not render as pages (the autogenerated sidebar's `dirName: '.'` resolves to the content root `/template/docs`), but internal design documents would ship inside a distributed artifact.
Chosen: Install at `design/` in the repository root, outside the build context. The path was rewritten in every file that names it — the seven stage commands under `.claude/commands/` and `AGENTS.md` — in one pass. The kit has since made `design/` its default for the same reason, so this repository is now on the standard layout rather than an exception.
Rejected: **Keep `docs/design/` and exclude it via `docs/.dockerignore`** — works, but the exclusion is invisible, nothing fails loudly if it is lost, and a docs-template upgrade that regenerates that file would drop it silently. **Keep `docs/design/` and accept publication** — no edits, but shipping internal design documents in a public image is not a default anyone would choose deliberately.
Reversibility: expensive — every cross-reference to `design/` breaks if the directory moves again.

### 2026-08-02 — Standing instructions moved from `CLAUDE.md` to `AGENTS.md`
Context: Installing the agent kit. This repository held its standing instructions in `CLAUDE.md` with no `AGENTS.md` — the inverse of the kit's arrangement, and of the SubZeroDev specification repositories. `CLAUDE.md` had uncommitted edits in flight on `design/platform-identity-and-engine-hosting` at the time; they were carried across verbatim.
Chosen: Move the content to `AGENTS.md` verbatim and reduce `CLAUDE.md` to a pointer. Matches the specification repositories, and `AGENTS.md` is the filename read by every tool rather than one vendor's.
Rejected: **Keep the content in `CLAUDE.md` and make `AGENTS.md` the pointer** — the smaller change, and what `SubZeroDev.GameEngine` does; rejected in favour of consistency with the specification repositories, which this one sits alongside. **Keep both files with content** — a copy that can disagree with its original is the exact failure this repository's own move-don't-copy rule exists to prevent.
Reversibility: cheap

### 2026-08-02 — Seven existing rules were not duplicated from the kit
Context: The kit's `AGENTS.md` carries conventions harvested from ten repositories. This repository already stated seven of them independently: one-at-a-time sign-off, recording declined suggestions as known-and-retained, staging by named path, never force-pushing, move-don't-copy, leaving the merge to the owner, and descriptive commit messages over Conventional Commits.
Chosen: Keep this repository's wording and do not add the kit's second copy. Where the local rule was more specific, it stands as written. The kit's placement test — "would a second consumer face this question?" — was already present here in sharper form as the package extraction guard, so the local wording was kept.
Rejected: **Add the kit's phrasing alongside** — two copies of a rule is a promise they will diverge. **Replace the local wording with the kit's** — the local rules were written against this repository and are more specific; the generic version loses information.
Reversibility: cheap

### 2026-08-02 — The source-of-truth chain is dual until a brief exists
Context: The kit asserts a five-document precedence chain under `design/`. This repository already has one: `docs/docs/platform-identity.md` is authoritative, followed by the sidebar reading order. Installing the kit's chain wholesale would assert authority over five files that do not exist while ignoring the one that does.
Chosen: State both in `AGENTS.md`. `platform-identity.md` is authoritative today; the `design/` chain governs design work once a brief is written, and a contract there is authoritative for its own package only. `platform-identity.md` remains authoritative for what this repository is.
Rejected: **Install the kit's chain as written** — asserts precedence for empty files over the document that currently decides everything. **Omit the kit's chain until a brief exists** — leaves the pipeline commands referring to an authority the contract never grants them.
Reversibility: cheap
