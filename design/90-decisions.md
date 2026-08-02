# Decision log

Append-only. Newest at the top. The rejected alternatives are the point — without them, every future session relitigates the same choice.

**This log is slice-local.** `AGENTS.md`, *Decision logging*, decides what belongs here and what belongs in `docs/docs/adr/`.

## Open
- **Brand identifier reservation is unperformed**, and it is the repository owner's action — reserving requires authenticating to each registry, so an agent cannot do it. What to reserve and why is [ADR-003](../docs/docs/adr/ADR-003-package-scopes-and-registries.md); this is the operational state.
  **Checked 2026-08-02, read-only, against the public registries:** no published packages under npm `@subzerodev` (scope search returned 0), NuGet `SubZeroDev.*` (search returned one fuzzy match, `jonjubnet.identity`, and no SubZeroDev package), or PowerShell Gallery `SubZeroDev*` (0 entries). Consistent with ADR-003's "nothing has published", now with evidence rather than assumption.
  **What that check does not establish**, and should not be read as: *not published* is not *not owned*. An npm organisation can be held without publishing, and `npmjs.com/org/subzerodev` returned HTTP 403 to an unauthenticated request — bot protection, not a signal either way. The GHCR `subzerodev-*` namespace was not checked at all; it needs authentication. So the window is **probably** still open, not verified open.
  **The four actions**, all requiring a signed-in session: reserve the NuGet ID prefix `SubZeroDev.*` against the verified owner; create or confirm the npm `@subzerodev` organisation; confirm the container namespace; reserve the PowerShell Gallery `SubZeroDev.*` prefix. Doing them settles the ownership question the check above could not.
- **Two automated-review findings are valid and deliberately not fixed here** — both sit in files installed **byte-identical** from `ghcr.io/the-running-dev/docs-template` (verified by diffing against the image), and both are already tracked upstream in `SubZeroDev.GameEngine`'s `TODO.md` as docs-template hardening items.
  1. `docs-ci.yml` and `docs-deploy.yml` run in `ghcr.io/the-running-dev/docs-template:latest`, a **mutable tag** — the same commit can start failing after an image update, and past failures are hard to reproduce.
  2. `Test-Documentation.ps1`'s `Get-DocumentationFile` recurses the whole tree before applying `ExcludedSegments`, so excluded trees are still walked. Performance only.
  **Why not fix them here.** The installer keeps these files byte-identical to the template precisely so re-running it picks up upstream fixes; editing them makes this repository silently miss every future one, which is a worse failure mode than a mutable tag on a docs site. The fix belongs in `docs-template`. **Revisit when** that project ships a pin mechanism, or if a mutable-tag drift actually bites here.
- **The automated reviewer's compliance ruleset predates [ADR-001](../docs/docs/adr/ADR-001-platform-identity.md)** and should be updated. It flagged `tenancy-billing-licensing.md` for naming a billing provider, and flagged the removal of `neaas-platform-vision.md`, on the rule that this repository holds only intent-level hosting material and must keep that filename. That rule described the repository *before* this change — it is now the reusable framework, not the deferred hosting layer, the billing document is a moved ecosystem specification carried in unchanged with a provenance header, and the vision file was renamed to `game-engine-as-a-service.md` by a recorded decision. Updating the ruleset is a reviewer-configuration action, not a repository one.
- **`main` is not protected, so nothing is a required check.** Verified: the branch-protection API returns 404 "Branch not protected". `docs-ci.yml` reports two checks, but a red run does not block a merge until they are made required. Repository settings, so the owner's action.
  **GitHub Pages is already enabled** and needs nothing — `build_type: workflow`, custom domain `platform.subzerodev.com`, `protected_domain_state: verified`. An earlier entry here said it was not enabled; that was asserted from the deploy workflow being newly installed rather than checked against the API, and it was wrong. `status` is `null` only because no deploy has run yet, which is expected — `docs-deploy.yml` triggers on push to `main`. (`https_enforced` is `false`, worth a look but not blocking.)

---

## Index — decisions whose home is elsewhere

Reasoning, consequences and rejected alternatives live in the linked document, never here — *Single ownership* in `AGENTS.md`.

| Decision | Home |
|---|---|
| Package scope is per-registry, not one global name | [ADR-003](../docs/docs/adr/ADR-003-package-scopes-and-registries.md) |
| Platform is .NET, and the product boundary is a process boundary | [ADR-002](../docs/docs/adr/ADR-002-implementation-technology.md) |
| `SubZeroDev.Platform` is the framework, not the game product | [ADR-001](../docs/docs/adr/ADR-001-platform-identity.md) |
| "Narrative Engine" renamed to "Game Engine" | [ADR-001](../docs/docs/adr/ADR-001-platform-identity.md), consequences |
| Hosting is a workload boundary, not in-process port supply | [`engine-hosting-contract.md`](../docs/docs/engine-hosting-contract.md) §2 |

---

### 2026-08-02 — The staging tree became `SubZeroDev.Architecture`, private
Context: The ecosystem specification set — 96 files, fourteen destinations' worth — sat at `D:\Dropbox\Projects\SubZeroDev\Specs` with no version control. Nothing recorded a change and nothing caught a stale one, which is exactly how its directory table came to list `SubZeroDev.Platform` as "Platform repository (exists)" while pointing at a repository holding a different Platform entirely. Found by reading a directory listing, not by any check.
Chosen: `git init`, one commit, and a **private** GitHub repository named `SubZeroDev.Architecture`. Named that because the documents already call it "the Architecture repository" and other repositories cite its ADRs by number, so the name has to resolve. Its README now separates the two kinds of content it holds: `SubZeroDev.Ecosystem/` is at home there, everything else is staging until its destination repository exists.
Rejected: **Public, matching all nine sibling repositories** — it would immediately publish `REVIEW.md` (an internal critique naming blocking defects), the commercial model (billing provider, licence tiers, metered dimensions), the plugin signing and trust model, and thirteen open questions including plugin naming, which the root-naming ADR says is expensive to settle once identifiers are public. Flipping to public later is available; un-publishing is not. **`SubZeroDev.Specs`** — accurate about today's contents but diverges from the name the documents themselves use. **Leaving it on disk** — the drift it has already produced is the argument against.
Reversibility: cheap for visibility; the repository name is expensive once anything cites it by URL.

### 2026-08-02 — Ran `Invoke-SetupDocs`, and `build/` stopped being ignored
Context: This repository had no CI, no documentation gate, and no site root — nothing served `/` while the navbar brand linked there from every page, 16 broken links. Two earlier diagnoses blamed the base image's `src/pages` strip and then installer registration; both were wrong. The strip is correct, and the build was being invoked the wrong way (`docker build` the overlay in, then build inside the derived image, so the file is already there when the leak check runs). The installer was the actual missing piece.
Chosen: Run the installer without `-Overwrite`. It created the homepage generator, the documentation gate, `.config/DocumentationRules.psd1`, both workflows, the generated site root and a docs index, and skipped the five files this repository already owned (`docusaurus.config.ts`, `sidebar.ts`, `Dockerfile`, `.dockerignore`, `docs.ps1`). Fixed the generated title, which the installer took from the container mount point and set to `work`. Authored `docs/docs/index.md` properly rather than leaving the `# work` stub. Removed the README's document table — it now duplicated that index, and every repository-relative link in it broke on the generated homepage. With the root served, `onBrokenLinks` and `onBrokenMarkdownLinks` are now `'throw'`.
Also: **`.gitignore` had a bare `build/`**, which made both installed scripts invisible to git while `docs-ci.yml` runs one of them — green locally, broken in CI. `build/` is a scripts directory here; the ignore now names `dist/`, `artifacts/`, `bin/`, `obj/` instead.
Rejected: **`-Overwrite`** — it would have replaced the five preserved files, including a `docusaurus.config.ts` carrying this repository's own settings. **Converting the README to absolute site URLs with `SiteUrl`** — the documented way to make one README work in both places, but it points readers at a site that is not deployed yet; keeping the index in `docs/docs/index.md` solves the same problem without that claim. **Migrating `routeBasePath` to `'/'`** — considered while the cause was still misdiagnosed; it moves every page URL to fix something the installer fixes for free.
Reversibility: cheap for the config and the ignore; the workflows and generated files are regenerable by re-running the installer.

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
