# SubZeroDev.Platform Status Site

The standalone landing page and roadmap, built as a status page: `/` is a component list with a
status pill per package, `/roadmap/` is an incident history derived from
[`design/30-slices.md`](../design/30-slices.md). See
[`design/40-site.md`](../design/40-site.md) for the design and its acceptance criteria.

Toolchain and script names are transcribed from `SubZeroDev.GameEngine/site/` deliberately — the
design and every stylesheet are not. See `design/40-site.md`, _Toolchain_.

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
  `design/30-slices.md` at build time — and never edits it.
- Documentation destinations live in one `routes` constant in `src/shared.tsx`, each checked
  against a real file under `docs/docs/` by `routes.test.ts`.
- No stylesheet, token, or page composition here is copied from the engine's `site/`. Only the
  toolchain configuration is.
