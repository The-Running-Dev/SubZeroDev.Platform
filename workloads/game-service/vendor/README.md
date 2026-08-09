# Vendored package tarballs

Two packed npm tarballs, declared as `file:` dependencies in
[`../package.json`](../package.json):

| Tarball | Built from |
|---|---|
| `subzerodev-service-contract-0.2.0.tgz` | `SubZeroDev.ServiceContract`, `npm pack` |
| `the-running-dev-game-engine-0.5.0.tgz` | `SubZeroDev.GameEngine`, `npm pack` |

**Neither package is published to a public registry yet.** The `@subzerodev` npm organisation is
still unreserved — [Platform issue #81](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/81)
tracks it — and until it closes there is no registry to resolve either name from. The same
constraint is why S2.9's publish criterion runs against a local ephemeral registry rather than live
npm (`design/90-decisions.md`, 2026-08-09).

Vendoring the **packed tarball** rather than linking the sibling source tree is deliberate, and is
the shape `SubZeroDev.ServiceContract` already uses for the engine. A `file:` link into a checkout
resolves through `src/`, so the workload would pass while `exports`, `files` and the declaration
emit were all still broken — proving nothing about the artifact that actually ships.

**This is not a copy of the contract** in the sense S3.15 forbids. It is `SubZeroDev.ServiceContract`'s
own published output, unmodified, consumed through the package's public entry point
(`loadPublishedContract`). Nothing in this repository authors, edits or regenerates a contract; the
row set, the schemas and the status mapping have exactly one home, and it is the other repository.

Both tarballs are replaced wholesale on a version bump — never edited in place.
