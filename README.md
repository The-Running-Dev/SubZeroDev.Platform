# SubZeroDev.Platform

The **NEaaS — Narrative Engine as a Service** layer: hosting, accounts, billing, cloud
sync, analytics, multiplayer, white-label. **Deferred** — vision only, not a v1
requirement, explicitly out of scope until the engine is proven.

> The **engine** (source + specs) lives in
> [SubZeroDev.GameEngine](https://github.com/The-Running-Dev/SubZeroDev.GameEngine); the
> flagship **game** lives in
> [SubZeroDev.GameOfLife](https://github.com/The-Running-Dev/SubZeroDev.GameOfLife). This
> repo is only the hosting / business layer that would sit on top.

## What's here

- [`docs/docs/neaas-platform-vision.md`](docs/docs/neaas-platform-vision.md) — the
  hosting / SaaS platform vision.

It renders as a [Docusaurus](https://docusaurus.io) site (requires Docker Desktop):

```powershell
./docs.ps1            # build + serve at http://localhost:3000/docs
./docs.ps1 -Live      # + hot-reload while editing docs/
./docs.ps1 -BuildOnly # build the image only
```

---

Private, work in progress. Deferred until the engine is proven.
