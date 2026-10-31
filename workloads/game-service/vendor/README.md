# Vendored package tarballs

Two packed npm tarballs, declared as `file:` dependencies in
[`../package.json`](../package.json):

| Tarball | Built from |
|---|---|
| `subzerodev-service-contract-0.8.0.tgz` | `SubZeroDev.ServiceContract`, `npm pack` of `main` at `7db17be` ([ServiceContract #18](https://github.com/The-Running-Dev/SubZeroDev.ServiceContract/pull/18)); sha256 `4adf025cb8727d38cc2b82a65b1fab723706fb81cfb6cdf5898def3aa39a0e7b` |
| `the-running-dev-game-engine-0.13.0.tgz` | `SubZeroDev.GameEngine`, the published `0.13.0` release, tag `v0.13.0`, commit `45b721693d1b92db86bd128c16ae2c753ace190d` (byte-identical to the GitHub Packages archive and to the copy vendored in `SubZeroDev.ServiceContract`); sha256 `aa8f4b91bfcb5597786048f8370bb9a2a72066cb9d55d5d90954391a4030dc1c` |

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

**The engine tarball moves in lockstep with the contract's `engineVersion`, not on its own
schedule.** `compose()` asserts the loaded contract's recorded `engineVersion` against
`ENGINE_VERSION`, resolved from whichever engine package is actually installed
(`20-contract.md`, "the artifact cannot claim a version the resolved engine does not have").
`SubZeroDev.ServiceContract` generates its `0.8.0` contract against the engine's `0.13.0`
release, so this repository's own engine pin moves to the same `0.13.0` in the same commit —
otherwise every startup fails `EngineVersionMismatch` before the listener ever binds.

**At `0.13.0` neither the session store nor the host ports change**, so `src/store.ts` and the
migrations are untouched. The contract moves for one reason: validation `details` on
`preview-action` and `submit-action` widen to `string | number | boolean`. The engine's string
catalog also grows — `get-strings` returns more keys, none removed or changed — which is the only
reason the golden transcript was regenerated. The `0.12.0` host-port changes (`revision`,
`initialization`, profile `kindData`) landed in #353 and are recorded in `design/90-decisions.md`.

**The engine tarball is a published release, not a candidate.** The contract tarball is still
unpublished (above). Its digest is not reproducible across checkouts: the content contract's
generated definition names carry a structure hash that differs between builds of the same commit,
with identical bodies. The digest above is the pack of the merged commit, and is the identity.
