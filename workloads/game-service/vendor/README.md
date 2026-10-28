# Vendored package tarballs

Two packed npm tarballs, declared as `file:` dependencies in
[`../package.json`](../package.json):

| Tarball | Built from |
|---|---|
| `subzerodev-service-contract-0.7.0.tgz` | `SubZeroDev.ServiceContract`, `npm pack` of the `engine-0.12.0` branch ([ServiceContract #17](https://github.com/The-Running-Dev/SubZeroDev.ServiceContract/pull/17)); sha256 `40f8b119487527290862901e2c308738f036efccc3257faef99571e92eb88c41` |
| `the-running-dev-game-engine-0.12.0.tgz` | `SubZeroDev.GameEngine`, `npm pack` of the `0.12.0` candidate, commit `914383610dc2afbb26fe2e224206dbf0a7b2d9e4` (byte-identical to the copy vendored in `SubZeroDev.ServiceContract`); sha256 `e76fbf5d5c0ea97ea0168be0c23100f840ae366f1bb17a32936ab7e7e22d7f63` |

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
`SubZeroDev.ServiceContract` generates its `0.7.0` contract against the engine's `0.12.0`
candidate, so this repository's own engine pin moves to the same `0.12.0` in the same commit —
otherwise every startup fails `EngineVersionMismatch` before the listener ever binds.

**At `0.12.0` the session store is unchanged; the host ports are not.** `StoredSessionRecord`
gains a required `revision` (the engine's own compare-and-swap counter) and an optional
`initialization`, and `PlayerProfile` moves to `formatVersion: 3` with `kindData`. Those land in
`src/store.ts` and the `1787227200000_session-revision-and-profile-kind-data` migration, not in
the contract. Both tarballs are candidates rather than releases: if either is superseded before it
is tagged, both are re-vendored from the commit that is.
