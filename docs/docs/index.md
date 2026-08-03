---
title: Documentation
sidebar_position: 0
---

# SubZeroDev.Platform — Documentation

The reusable application framework and hosting layer for SubZeroDev products. **Design
stage — no packages have been built.**

Read in this order. Each document says what it owns; decisions live in their own records.

| # | Document | Holds |
|---|---|---|
| 1 | [Platform Identity](platform-identity.md) | **Start here.** What this repository is, and the collision it settles |
| 2 | [Platform Specification](platform-specification.md) | The framework — packages, modules, hosting, persistence, identity, billing |
| 3 | [Game Engine as a Service](game-engine-as-a-service.md) | The hosted game product — vision, not contract |
| 4 | [Engine Hosting Contract](engine-hosting-contract.md) | What "Platform hosts the engine" means, and the four questions hosting introduces |
| 5 | [MCP Tool Contract](mcp-tool-contract.md) | The engine's tool table — current, built and tested |
| 7 | [The Minimal Package Set](minimal-platform-packages.md) | The six near-term packages: boundaries and done-criteria |
| 8 | [Second-Consumer Packages](second-consumer-packages.md) | Identity, Tenancy, Billing, Mcp — justified, not scheduled |
| 9 | [Application Modules](application-modules.md) | The framework/module split, a third consumer, and the two modules admitted by decision |
| 10 | [Implementation Plan](implementation-plan.md) | The ordered plan, both tracks, with ordering constraints |

Supporting specifications, moved from the ecosystem staging tree:
[Events and Notifications](events-and-notifications.md) ·
[Tenancy, Billing, Licensing](tenancy-billing-licensing.md) ·
[Observability](observability.md)

## Decisions

Reasoning, consequences and rejected alternatives live in the record, never restated
elsewhere:

- [ADR-001 — `SubZeroDev.Platform` is the framework, not the game product](adr/ADR-001-platform-identity.md)
- [ADR-002 — Platform is .NET, and the product boundary is a process boundary](adr/ADR-002-implementation-technology.md)
- [ADR-003 — Scopes are per-registry, not one global name](adr/ADR-003-package-scopes-and-registries.md)
- [ADR-004 — Platform is built in-house, with ABP as an architecture reference](adr/ADR-004-framework-build-not-adopt.md)
- [ADR-005 — Boundary contracts are projected, not authored](adr/ADR-005-service-contract.md)
- [ADR-006 — Platform is a framework plus optional application modules](adr/ADR-006-application-modules.md)

## The one rule underneath all of it

```text
              SubZeroDev.Platform
                 ↓            ↓
        SubZeroDev.Automator   Game Engine as a Service
                 ↓
      Plugins / Workflows / Products
```

Platform never depends on a product, and never on a plugin. A reference from Platform to
either is a build failure, not a review comment.
