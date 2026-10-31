# SubZeroDev.Platform Status Site

The standalone landing page and roadmap, built as a status page: `/` is a component list with a
status pill per package, `/roadmap/` is an incident history derived from
the active effort's `design/30-slices.md`. Between efforts that file is absent and the page says
nothing is in progress, linking the last archived ledger
([`design/d5/30-slices.md`](../design/d5/30-slices.md)). See
[`design/d3/40-site.md`](../design/d3/40-site.md) for the design and its acceptance criteria.

The reusable landing-page package owns route builds and the protected documentation merge. This
repository retains the React pages, styles, metadata, static assets, and tests. See
`design/d3/40-site.md`, _L2 — Consume the reusable landing-page package_.

## Development

```powershell
npm install
npm run dev
npm run check
```

`npm run check` verifies formatting, linting, TypeScript, component tests, the production build,
and the built HTML's static metadata.

## Boundaries

- Keep all site work inside `site/`. It observes the rest of this repository — reading
  `design/30-slices.md` at build time, through a glob so a missing file is the between-efforts
  state rather than a build failure — and never edits it. When an effort archives, update
  `lastArchivedLedger` in `src/roadmap/roadmapData.ts`; a test fails if its path stops existing.
- Documentation destinations live in one `routes` constant in `src/shared.tsx`, each checked
  against a real file under `docs/docs/` by `routes.test.ts`.
- `landing.config.ts` is the only route-build configuration; it allows the raw `design/` import
  (`allow: ["../design"]`) and no broader repository path.
- No stylesheet, token, or page composition here is copied from the engine's `site/`. Only the
  toolchain configuration is.
