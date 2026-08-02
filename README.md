# SubZeroDev.Platform

The **reusable application framework and hosting layer** for SubZeroDev products: hosting,
configuration, identity, authorization, tenancy, billing, notifications, storage, events,
observability, and API/MCP conventions — implemented once and reused.

```text
              SubZeroDev.Platform
                 ↓            ↓
        SubZeroDev.Automator   Game Engine as a Service
                 ↓
      Plugins / Workflows / Products
```

**Platform never depends on a product, and never on a plugin.** The dependency direction is
the whole of the rule, and it is enforced by the build rather than by intent.

> **Game Engine as a Service (GEaaS)** — hosting for the
> [Game Engine](https://github.com/The-Running-Dev/SubZeroDev.GameEngine) — is one *workload*
> Platform hosts, in the same relationship the Automator has. It is not what this repository
> is. It was previously called NEaaS (Narrative Engine as a Service); the engine ships three
> kinds and only one of them is narrative.

## What's here

Design documents. No packages yet.

| Document | Holds |
|---|---|
| [`platform-identity.md`](docs/docs/platform-identity.md) | **Start here.** What this repository is, and the naming collision it settles |
| [`platform-specification.md`](docs/docs/platform-specification.md) | The framework specification — packages, modules, hosting, persistence, identity, billing |
| [`game-engine-as-a-service.md`](docs/docs/game-engine-as-a-service.md) | The hosted game product — vision |
| [`engine-hosting-contract.md`](docs/docs/engine-hosting-contract.md) | What "Platform hosts the engine" means: the workload boundary, ownership, and the four questions hosting introduces |
| [`mcp-tool-contract.md`](docs/docs/mcp-tool-contract.md) | The engine's MCP tool table — current, built and tested |
| [`minimal-platform-packages.md`](docs/docs/minimal-platform-packages.md) | The six near-term packages — boundaries and done-criteria |
| [`second-consumer-packages.md`](docs/docs/second-consumer-packages.md) | Identity, Tenancy, Billing, Mcp — justified by a second consumer |
| [`implementation-plan.md`](docs/docs/implementation-plan.md) | The ordered plan, both tracks |
| [`adr/`](docs/docs/adr/) | The decisions: [identity](docs/docs/adr/ADR-001-platform-identity.md), [technology](docs/docs/adr/ADR-002-implementation-technology.md), [package scopes](docs/docs/adr/ADR-003-package-scopes-and-registries.md) |
| [`events-and-notifications.md`](docs/docs/events-and-notifications.md) · [`tenancy-billing-licensing.md`](docs/docs/tenancy-billing-licensing.md) · [`observability.md`](docs/docs/observability.md) | Supporting specifications |

It renders as a [Docusaurus](https://docusaurus.io) site (requires Docker Desktop):

```powershell
./docs.ps1            # build + serve at http://localhost:3000/docs
./docs.ps1 -Live      # + hot-reload while editing docs/
./docs.ps1 -BuildOnly # build the image only
```

---

Private, work in progress. Design stage — no packages have been built.
