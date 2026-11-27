# Vendored package tarballs

Two packed npm tarballs, declared as `file:` dependencies in
[`../package.json`](../package.json):

| Tarball | Built from |
|---|---|
| `subzerodev-service-contract-0.10.0.tgz` | `SubZeroDev.ServiceContract`, `npm pack` of `main` at `4c8785a` ([ServiceContract #20](https://github.com/The-Running-Dev/SubZeroDev.ServiceContract/pull/20)); sha256 `9bb5c14331a5e87ed24c56a8538a0272b3bf521448618cfe725776863727be87` |
| `the-running-dev-game-engine-0.15.0.tgz` | `SubZeroDev.GameEngine`, the published `0.15.0` release, tag `v0.15.0`, commit `b7517d8d8efb836422085fa1b5bb12057ffa4c04` (byte-identical to the GitHub Packages archive, whose release run `45878037211` recorded the same digest, and to the copy vendored in `SubZeroDev.ServiceContract`); sha256 `e3da18ddcfa84e1f9aa3b210904fa787636a62c85935b5c8f179aac84ea34d70` |

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
`SubZeroDev.ServiceContract` generates its `0.10.0` contract against the engine's `0.15.0`
release, so this repository's own engine pin moves to the same `0.15.0` in the same commit —
otherwise every startup fails `EngineVersionMismatch` before the listener ever binds.

**At `0.15.0` neither the session store nor the host ports change**, so `src/store.ts` and the
migrations are untouched. The contract moves only because its recorded `engineVersion` does: the RPC
contract is byte-identical to `0.9.0`'s apart from that field. What `0.15.0` adds — published
attachments (the engine's S141): `PortableManifest.attachments`, story-graph `includes`,
`digestManifestResolution` at the package root and the `reserved_version_identifier` reason code — sits
on content composition, which this workload does not perform. Its only trace here is that reason's
string, the one key the golden transcript's `get-strings` catalog gains. `0.14.0` likewise touched
only kinds and host ports this workload does not implement. The `0.12.0` host-port changes
(`revision`, `initialization`, profile `kindData`) landed in #353 and are recorded in
`design/90-decisions.md`.

**The engine tarball is a published release, not a candidate.** The contract tarball is still
unpublished (above). Its digest is not reproducible across checkouts: the content contract's
generated definition names carry a structure hash that differs between builds of the same commit,
with identical bodies. The digest above is the pack of the merged commit, and is the identity.
