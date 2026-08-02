---
title: 'SubZeroDev.Platform'
description: 'The reusable application framework and hosting layer for SubZeroDev products.'
---

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

Start with **Platform Identity** — what this repository is, and the naming collision it
settles; everything else depends on it. From there the reading order runs through the
framework specification, the hosted game product and its hosting contract, the package set,
and the implementation plan. Decisions live in numbered ADRs.

The full index, in reading order, is the
[documentation index](https://github.com/The-Running-Dev/SubZeroDev.Platform/blob/main/docs/docs/index.md).
It is kept there rather than repeated here, so there is one list to keep correct.

It renders as a [Docusaurus](https://docusaurus.io) site (requires Docker Desktop):

```powershell
./docs.ps1            # build + serve at http://localhost:3000/docs
./docs.ps1 -Live      # + hot-reload while editing docs/
./docs.ps1 -BuildOnly # build the image only
```

Check it before pushing:

```powershell
./build/Test-Documentation.ps1   # authored links, anchors, terminology, generated-file drift
```

---

Public, work in progress. Design stage — no packages have been built.

[View the documentation](/docs/)
