# Site slices — the public face (L-track)

**Document status:** Slices, for a track that is not D3. `30-slices.md` is derived from
[`20-contract.md`](20-contract.md) and every slice in it names signatures the contract carries. A web
page names none, which is why it is not appended there — S9 is the release, and a marketing page is
not a tenth package.

**The prefix is `L`, and it is not a phase.** Per `AGENTS.md` *Single ownership*, a local sequence
uses a letter that cannot be misread as an ecosystem phase, and must say which phase each stage maps
to. `L1` maps to **ecosystem Phase 2** — the same window D3 occupies — as a parallel, non-gating
deliverable within it, never a milestone inside D3's own D0–D5 package pipeline. It does not gate D3,
D3 does not gate it, and no package's done-criteria reference it.

**This document does not outrank the code, the contract or the brief.** It is a work breakdown for
one deliverable that ships no public API. Where it and `30-slices.md` appear to disagree about what a
slice *is*, `30-slices.md` wins and this document is the defect.

---

## L1 — The status page that is the product

Delivers: `platform.subzerodev.com` serves a landing page at `/` and a roadmap at `/roadmap/`, both
built from a standalone `site/` project and overlaid onto the existing documentation deploy, with the
roadmap's slice inventory derived from `design/30-slices.md` rather than transcribed beside it.

Depends on: nothing in D3. Buildable at any point; the roadmap's content grows on its own as slices
merge, with no edit to this project.

### The conceit

Platform is the layer nobody demos. The site does not fight that — it commits to it. **The whole
landing page is an operational status page**, and every section is a component with a status pill.
The humour is entirely this repository's own vocabulary — `Degraded`, `Unhealthy`, split-brain,
poisoned, drain window, `MissingRequiredSetting` — and never the engine's. The two sites must not
read as one voice in two fonts.

The roadmap is an **incident history**: every merged slice is a resolved incident, the current slice
is ongoing, the queue is scheduled maintenance, and the brief's non-goals are closed as won't-fix.

One line governs every joke: **nothing may be funnier than it is true.** A status pill that claims a
package is published before S9 is a defect, not a bit.

### Design language — and what it may not be

Light, dense, monospaced, chrome-heavy: a control surface, not an essay. The engine's site is a dark
cinematic scroll with a narrow measure, display type and reveal-on-scroll. This one is its opposite
in every axis, and the following are **binding anti-criteria**, checkable in review:

- No dark-by-default page background. Light is the default; a `prefers-color-scheme: dark` variant is
  in scope and is a console treatment, not the engine's palette.
- The accent is status semantics — operational green, degraded amber, unknown grey. The engine's
  ice-blue (`#82d8ff`) appears nowhere.
- No reveal-on-scroll, no `data-reveal`, no scroll-position-driven motion of any kind. The only
  motion is a slow pulse on the live status dot, and it stops under `prefers-reduced-motion`.
- No full-bleed display type. The largest type on the page is smaller than the engine's smallest
  heading, because status pages do not shout.
- Structural type is a system monospace stack; prose is a system sans. No serif, no webfont, no
  network request for a font.
- Content is dense: hairline-ruled panels and table rows edge to edge, not a centred column with air
  around it.

**Status is never colour alone.** Every pill carries a glyph and a word as well as a colour, and the
component list is a real `<table>` or a list with `<dt>`/`<dd>` — not a grid of `<div>`s coloured
green.

### Landing page — `/`

A status-page shell: wordmark, global status banner, component list, then sections that are
themselves components.

- **Banner** — `● ALL SYSTEMS OPERATIONAL`, with the subtext that the last incident was never and the
  last user is a sample. Under it: `uptime 100% · open incidents 0 · consumers 0 (2 planned)`. **Open**,
  not a bare `incidents 0` — the roadmap page one click away frames every merged slice as a resolved
  incident, and a banner claiming zero incidents at all would contradict a page showing four. "Open"
  keeps both claims true in the same breath: reconciled during L1's implementation, recorded in
  `90-decisions.md`.
- **01 / COMPONENTS** — the six packages as monitored components, each with a pill and a one-line
  status message in the register of an ops summary: Abstractions depends on nothing and is smug about
  it; Core refuses to start rather than start wrong; Hosting starts, serves, and stops when asked;
  Persistence has two providers, one connection, and no opinion about your repository pattern;
  Observability exports nowhere by default and will not be taking questions; Testing ships with
  nothing and is depended on by nothing shipped. Two non-package components sit in the same list and
  carry the joke: `Marketing — ○ NOT MONITORED (no telemetry configured)` and `Adoption — ◐ DEGRADED
  (peer host missing; it's you)`.
- **02 / WHAT THIS IS** — the part nobody demos. Six packages, two processes, zero excitement, and a
  dependency direction enforced by the build rather than by intent.
- **03 / INCIDENT REPORT** — the founding problem as a postmortem. *Impact:* two unrelated products
  each re-deriving hosting shape, configuration binding, startup validation and test infrastructure.
  *Root cause:* nothing shared existed. *Time to detect:* years. *Time to resolve:* ongoing.
  *Resolution:* this repository. *Action items:* the parsed slice count — nine at the time of writing,
  never the literal word: the "no hard-coded slice count" acceptance criterion below binds here too.
- **04 / RETURNS 503** — things the platform refuses to do, rendered as declined checks: start on a
  configuration it cannot explain; retry your request on your behalf; impose a repository pattern on
  your tables; depend on a product; reach the internet at startup; report a missing check as a
  passing one.
- **05 / RETURNS 200** — things it does, loudly: aborts startup by name; survives being killed
  between the commit and the publish; notices two hosts pointed at different databases and says so
  out loud; sorts timestamps identically on PostgreSQL and SQLite, which took more effort than it
  sounds.
- **06 / UPTIME** — ninety day-bars, all green, legend: each bar is a day nobody filed an issue,
  largely because nobody knew where to file one.
- **07 / THE ONLY DEMO WE HAVE** — a readiness response as the hero image, because a probe body is
  the only screenshot infrastructure ever gets. It shows an aggregate `Degraded` over a `PeerHost`
  entry, with the caption that `Degraded` returns 200 and `Unhealthy` returns 503, and the difference
  is the whole personality. **Every entry name in it exists in `30-slices.md`**, and a name that does
  not is a defect.
- **08 / SUBSCRIBE** — the status-page subscribe box, except there is no mailing list, there is a
  repository. Rendered as prose and a link; **it is not a `<form>` and collects nothing.**
- **Footer** — this page is not wired to anything; if it were, it would be `Degraded`.

### Roadmap page — `/roadmap/`

Incident history, in four sections: `RESOLVED` (merged slices, newest first), `ONGOING` (the current
slice), `SCHEDULED` (the remainder, in dependency order), and `WON'T FIX` — the brief's non-goals,
each closed with its reason: query filters are D5, hosted multi-tenant SaaS is closed by design,
adopting an application framework is closed as not planned, and outbound network is closed
aggressively.

Every count on the page is derived. **No number is typed.**

### Where the roadmap's content comes from

**One source.** `design/30-slices.md` gains a done marker, and the page reads nothing else:

| Fact | How |
|---|---|
| Which slices exist, their titles, their `Depends on:` | Parsed from the headings and bodies |
| Which slices have shipped | The `**Status:**` line beneath each heading |
| Which slice is current | The one status line reading `in progress` |

The document is imported raw at build time — a static import, no generator, no git read, no network.

**The marker goes beneath the heading, never inside it.** Docusaurus derives anchors from heading
text, so `## [x] S1 — …` would change every slice's anchor and break the existing in-document link at
[`30-slices.md`](30-slices.md) line 10, `[S9](#s9--pack-publish-consume-and-the-api-reference)`, along
with any inbound link written later. The form is a status line as the first line of each slice body:

```markdown
## S4 — Outbox enqueue
**Status:** shipped · [#32](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/32)

Delivers: …
```

with `**Status:** in progress` on the slice being worked and `**Status:** queued` on the rest.

**This creates a second place done-ness is written, and that is a real cost.** `AGENTS.md` *Tracking
work* assigns what a slice *is* to this document and whether it is *done* to its issue, and says that
when the two disagree, say so rather than editing either. The marker does not repeal that: the issue
stays the tracker's record, the marker is the document's own statement, and a disagreement between
them is still a thing to report rather than quietly reconcile. What the marker buys is that the
public page cannot invent a status of its own — the alternative was a hand-authored list that could
disagree with both.

The slice workflow acquires one obligation as a consequence: **a slice sets its own marker to
`shipped` in the same change that satisfies it**, and sets the next one to `in progress`. That is one
line per slice, in a document the slice is already touching.

### Toolchain — the engine's, transcribed

Node and Vite, and the same setup as the engine's `site/`, so there is one toolchain to know across
both repositories rather than two. **The scaffolding is ported deliberately; the design is not.**

Ported effectively verbatim, adjusted only for this repository's paths and names:

- `package.json` with the same script names and the same meanings — `dev`, `build`, `check`,
  `format`, `format:check`, `lint`, `preview`, `test`, `test:build`, `typecheck`. `check` is the one
  command CI runs, and it runs all of them.
- Vite with the React plugin and **two rollup inputs** — `index.html` and `roadmap/index.html` —
  producing `/` and `/roadmap/`; `server.fs.allow` widened to the repository root, which is what makes
  the `?raw` import of `../design/30-slices.md` resolve in dev as well as in build.
- Vitest with `environment: "jsdom"`, `src/test/setup.ts`, testing-library and jest-dom.
- oxlint, prettier, TypeScript strict with project references.
- `scripts/verify-build.mjs`, asserting the built HTML rather than trusting the build.
- `public/` carrying the favicon set, the apple-touch icon and an og-image — **PNG, not WebP**, per
  house conventions.
- The `SiteHeader` / `SiteFooter` / `ExternalLink` shared-module shape, including the visually-hidden
  "(opens in a new tab)" suffix.

Written fresh, sharing nothing with the engine: every stylesheet, every token, all copy, both page
compositions, and the roadmap's parser and data model. The engine's `hooks/useRevealOnScroll.ts` and
`css/motion.css` are **not** ported — this design forbids what they do.

### Documentation routes

The docs stay exactly where they are: `docs/docs/*` rendered by the existing Docusaurus config
(`baseUrl: '/'`, `routeBasePath: 'docs'`), untouched by this slice. The site links into them at
`/docs/<path>`, root-relative, since both are served from one origin after the merge.

**Nothing validates those links automatically, and that is the trap this section exists to name.**
`Test-Documentation.ps1` skips site-absolute targets by design, and Docusaurus's own link checker
only sees routes inside its own build — so a link from `site/` into `/docs/` is checked by neither.
The engine documented the same gap and answered it with a written route inventory; this slice answers
it with a test.

- Doc destinations live in one `routes` constant, never inline in a component.
- No file in `docs/docs/` carries a `slug:`, so each route is its path without the extension:
  `/docs/platform-identity`, `/docs/platform-specification`, `/docs/minimal-platform-packages`,
  `/docs/implementation-plan`. **ADR routes carry the file's exact casing** —
  `/docs/adr/ADR-004-framework-build-not-adopt` — and GitHub Pages is case-sensitive, so a lowercased
  ADR route is a 404 that works locally on Windows.
- A test asserts that every value in `routes` corresponds to a real file under `docs/docs/`, matching
  case exactly. It goes red when a doc is renamed, which is the whole point.

### Deploy

The pattern is already proved next door and is ported, not invented: `build/Merge-LandingPage.ps1`
overlays the Vite `dist/` onto the documentation build, so one GitHub Pages deployment serves the
landing page at `/` and the docs at `/docs`. The two builds must not write the same paths, and the
script verifies that rather than assuming it.

**A cost, stated rather than discovered later:** the documentation site's own generated homepage
(`docs/src/pages/index.md`, generated from `README.md` by `build/ConvertTo-DocumentationHomepage.ps1`)
is superseded at `/`. It stays generated and its drift check stays green — it simply stops being what
anyone sees. If that is not wanted, the alternative is serving the landing page at a sub-path, and
this slice does not do that.

### Touches

- **site/** — the project described under *Toolchain*: Node and Vite, the engine's setup transcribed,
  two entry points, fresh design and copy
- **design/30-slices.md** — a `**Status:**` line beneath all nine headings, backfilled once against
  what has actually merged, and the note in its preamble that the line is what the site reads. **No
  heading text changes**, so every existing anchor survives
- **build/** — `Merge-LandingPage.ps1`, ported from the engine with this repository's paths;
  `Test-SliceStatusMarkers.ps1`, a **new, repository-owned script**, checks the markers' well-formedness.
  **Not `Test-Documentation.ps1`** — `90-decisions.md` already records that file as installed
  byte-identical from the docs-template image, kept that way deliberately so re-running the installer
  keeps picking up upstream fixes; a repository-specific check belongs in a repository-owned file
  instead. Found during L1's implementation, reconciled here rather than left to drift
- **.github/workflows/** — `docs-ci.yml` gains a `slice-status-markers` job running the new script,
  alongside the unmodified `documentation` job; its verify job builds and checks `site/` and performs
  the merge before archiving; `docs-deploy.yml` does the same before uploading. None needs a deeper
  checkout: nothing in this slice reads git
- **design/90-decisions.md** — two entries, both required before implementation is complete
- **.gitignore** — `site/node_modules` and `site/dist`

### Acceptance

**Build and shell**

- `npm --prefix site run check` passes from a clean clone, and its failure fails CI. A deliberately
  broken type, a lint violation, a failing component test and an unformatted file each fail it —
  asserted once, not assumed.
- `npm --prefix site run build` produces `dist/index.html` and `dist/roadmap/index.html`, each with a
  distinct `<title>` and a meta description, and `verify-build.mjs` fails when either is removed.
- Neither page issues a network request at runtime — no font, no analytics, no image from another
  origin. Asserted against the built HTML and CSS, not by inspection.
- Every `/docs/` destination in the `routes` constant resolves to a real file under `docs/docs/`,
  **matching case**. Renaming a doc, or lowercasing an ADR route, fails the test. Grepping `site/`
  for a `/docs/` string outside that constant finds nothing.

**Derived content — the criteria that keep the page honest**

- The roadmap lists exactly the S-numbers present in `design/30-slices.md`. Adding a tenth slice
  heading to a fixture makes a tenth entry appear with no edit to `site/`; removing one removes it.
- `shipped` renders `RESOLVED`, `in progress` renders `ONGOING`, `queued` renders `SCHEDULED`.
  Asserted **against a fixture document, not against the live one**, so the tests do not go red the
  day S5 merges. One further test reads the real `30-slices.md` and asserts only that every slice
  carries a parseable status — the assertion that survives every future merge.
- Changing the heading or status format in `30-slices.md` fails a test rather than silently emptying
  the page. **A parser that returns zero slices, or a slice whose status line is missing or
  unrecognised, is a build failure — never an empty roadmap and never a silent `SCHEDULED`.**
- `Test-SliceStatusMarkers.ps1` fails when the markers are internally inconsistent: more than one
  `in progress`, none at all while a `queued` slice exists, or a `queued` slice ordered before a
  `shipped` one. This is what catches a slice that merges without setting its own marker, which is
  the failure mode the marker introduces. **The check is asserted against a deliberately inconsistent
  fixture, since a validator that has never failed is not known to constrain anything.**
- Every count rendered on either page is computed from the parsed inventory. Grepping the source for
  a hard-coded slice count, package count or percentage finds nothing.
- No page states or implies that any package is published, released, or installable, while S9 is
  unshipped. The site's own uptime and consumer figures are the joke's material and must remain
  literally true against the repository.

**Design distinctness**

- Every anti-criterion above holds. The reviewer checks the built page against the engine's at
  `game-engine.subzerodev.com` side by side; "different enough" is not the test — the listed axes are.
- The split holds: configuration and scaffolding are ported from the engine (see *Toolchain*), and
  **no stylesheet, token, copy block or page composition is**. Specifically, none of the engine's
  `landing.css`, `site.css`, `roadmap.css` or `css/motion.css` appears here in whole or in part, and
  no custom property from its `:root` block is reused under any name.
- Every status pill carries a glyph and a word in addition to colour, and the page is legible in
  greyscale. Asserted by a test reading the accessible name of each pill.
- With `prefers-reduced-motion: reduce`, no element animates.
- Keyboard traversal reaches every link in visual order, and the roadmap's four sections are
  landmarks with accessible names.

**Deploy**

- `Merge-LandingPage.ps1` refuses a target that is not a documentation build and refuses a source
  with no `index.html`, each with a message naming what it expected. **Both refusals are asserted,
  since a merge script that has never refused anything is not known to guard anything.**
- After the merge, the docs subtree's file count is unchanged and every route under `/docs/`
  resolves; `/` is the landing page and `/roadmap/` is the roadmap.
- `./build/Test-Documentation.ps1` passes with this document and `site/README.md` in the tree.
- CI performs the merge on every pull request, before the deploy path ever runs it on `main`.
- The deploy is verified by polling the deployment for the exact commit and reading the served page —
  **not by a merged pull request.** Nothing in the pull request states the URL is live.

### Out of scope

- Any change to `src/`, `samples/`, `tests/`, or any package's behaviour. The site observes this
  repository; it does not participate in it.
- Any change to `design/30-slices.md` beyond the status line and the preamble note. No slice's
  delivers, touches, acceptance or out-of-scope text is edited, and **no heading text is touched** —
  the anchors are the reason the marker sits where it does.
- A live status feed. Every status on the page is authored copy about a design-stage project, and
  wiring the page to a real probe would require a hosted instance, which the brief's *Environment*
  does not have. The footer says so, which is what keeps the conceit honest rather than misleading.
- Analytics, cookie banners, consent, and any form that submits anywhere.
- A blog, a changelog page, or docs navigation restructuring. The documentation site keeps its
  sidebar and its routes exactly as they are.
- Custom domain or DNS changes. `platform.subzerodev.com` already resolves to this deployment.
- The engine's landing page. It is a separate repository and nothing here edits it.

### Decision-log entries this slice must produce

Both are `design/90-decisions.md` entries, and `AGENTS.md` requires them before this slice is
complete:

1. **A Node/Vite/React toolchain enters a .NET repository, matching the engine's.** Decided by the
   repository owner, not derived here; the entry records it rather than reopening it. The
   alternatives to name and reject: Docusaurus pages inside the existing docs site (no design
   freedom, and the conceit needs control of the page shell); hand-written static HTML with no build
   (no tests, and every count becomes a typed number this slice forbids); and a *different* Node
   toolchain from the engine's (two setups to maintain for one person, for no gain — the requirement
   was that the sites look unalike, not that they build unalike). Record the accepted cost: this
   repository now carries a second package manager and a second lockfile to keep current.
2. **`30-slices.md` gains a done marker, and it sits beneath the heading rather than inside it.**
   Record both halves. The alternatives to name and reject: deriving shipped-ness from `git log`
   subjects (`S4 — Outbox enqueue (#32)` — no second home for done-ness, but it makes a static page
   depend on clone depth, git availability inside a container, and a commit convention nobody
   validates); querying the issue tracker at build time (a network dependency in the build);
   a hand-authored status list in `site/` (declined when this slice was scoped); and an `[x]` prefix
   in the heading itself (the engine's convention, rejected here because Docusaurus derives anchors
   from heading text and it would break the existing `[S9](#s9--…)` link and every inbound one).
   Record the accepted cost plainly: done-ness is now written in the document *and* tracked in the
   issue, and `AGENTS.md` *Tracking work* still governs what to do when they disagree — report it,
   do not silently reconcile.
