---
sidebar_position: 3
sidebar_label: ADR-003 Package Scopes
---

# ADR-003: Scopes Are Per-Registry, Not One Global Name

## Status

Accepted

## Context

Two package scopes are in use across these repositories and they appeared to disagree.

The ecosystem naming ADR fixes the npm scope as **`@subzerodev`**, alongside a NuGet
`SubZeroDev.*` ID prefix, a `subzerodev-*` container namespace and a `SubZeroDev.*`
PowerShell module prefix — and requires all four be reserved *before* first publish, on the
argument that reservation is free now and never again.

The Game Engine publishes as **`@the-running-dev/game-engine`**.

At first reading this is drift. It is not. The engine's own packaging plan records the
reason: **GitHub Packages requires the npm scope to match the repository owner**, and the
owner is the `The-Running-Dev` GitHub organization. The engine ships to a *private* GitHub
Packages registry, so `@subzerodev` was never available to it — not as a preference, as a
constraint of the registry it publishes to.

Underneath both sits a third mismatch that neither ADR names: the **GitHub organization is
`The-Running-Dev` while the brand namespace is `SubZeroDev`**. Every scope question
downstream is a consequence of that.

## Decision

**Scope is a property of the registry, not a single global identity.**

| Registry | Scope / prefix | Why |
|---|---|---|
| GitHub Packages (private) | `@the-running-dev/*` | Forced — the scope must match the organization |
| Public npm | `@subzerodev/*` | The brand identity; reserved now, published to later or never |
| NuGet | `SubZeroDev.*` | The ecosystem convention; Platform's packaging target |
| Container registry | `subzerodev-*` | |
| PowerShell Gallery | `SubZeroDev.*` | |

**The engine does not rename.** `@the-running-dev/game-engine` is correct for where it
publishes, and changing it would trade a working private-registry coordinate for a naming
preference.

**Reserve all four brand identifiers now**, per the naming ADR, independently of which are
published to first. Reservation prevents both squatting and honest collision, and it is the
one part of this that expires.

> **Reservation is a human action.** It requires registry accounts and credentials, and is
> deliberately not automated or delegated. This ADR records *what* to reserve and *why*; the
> reserving is the repository owner's.

## Consequences

- **A package's coordinate does not identify its project by name alone.** Reading
  `@the-running-dev/game-engine` you cannot tell it is a SubZeroDev component without knowing
  this rule. Accepted, and the mitigation is that it is written down here rather than
  rediscovered.
- **Anything Platform publishes to GitHub Packages inherits the same constraint**, so a
  hypothetical `@subzerodev/platform-*` on GitHub Packages is not available under the current
  organization. Under [ADR-002](ADR-002-implementation-technology.md) Platform is .NET and
  ships to NuGet, where `SubZeroDev.*` is unaffected — so this bites the engine and any future
  Node package, not Platform.
- **The org-versus-brand mismatch is recorded rather than resolved**, and it is the thing to
  revisit if the coordinates ever become genuinely confusing.
- **Nothing has published on `@subzerodev`**, so the reservation window is *probably* still
  open. Stated with that hedge deliberately: a read-only check of the public registries found
  no published packages under the npm scope, the NuGet prefix or the PowerShell Gallery
  prefix — but *not published* is not *not owned*, an npm organisation can be held without
  publishing, and the container namespace needs authentication to check at all. Only signing
  in settles it. The operational state and the four actions are tracked in
  [issue #81](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/81).

## Alternatives considered

**Rename the engine to `@subzerodev/game-engine`.** Uniform naming everywhere. Rejected: it
does not work on GitHub Packages under the current organization, so it would force either
public npm publication of a package intended to be private, or a move off GitHub Packages
entirely — a delivery decision taken for a cosmetic reason.

**Rename the GitHub organization to `SubZeroDev`.** This is the only alternative that
resolves the mismatch at its root rather than routing around it, and it would make every
scope uniform. Rejected *for now*, not on merit: renaming an organization redirects every
repository URL, every remote, every published package coordinate and every CI reference at
once, and the benefit is consistency rather than capability. Worth reconsidering before
anything publishes publicly, and not after.

**Drop `@subzerodev` and standardize on `@the-running-dev`.** Simplest, and it matches what
actually exists. Rejected: it abandons the brand identity the naming ADR deliberately
preserved across the container registry, NuGet and PowerShell Gallery, in favour of an
artifact of which GitHub account happened to hold the repositories.
