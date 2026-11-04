# Vendored package tarballs

Two packed npm tarballs, declared as `file:` dependencies in
[`../package.json`](../package.json):

| Tarball | Built from |
|---|---|
| `subzerodev-service-contract-0.9.0.tgz` | `SubZeroDev.ServiceContract`, `npm pack` of `main` at `d53d8a7` ([ServiceContract #19](https://github.com/The-Running-Dev/SubZeroDev.ServiceContract/pull/19)); sha256 `8f4d4b588a14b3f2f6e3f5fd542d391cc62d4c06894db643d02dfaf159a914d0` |
| `the-running-dev-game-engine-0.14.0.tgz` | `SubZeroDev.GameEngine`, the published `0.14.0` release, tag `v0.14.0`, commit `b7f9925f260959ec02fa874555a45482eb63ab49` (byte-identical to the GitHub Packages archive, whose release run `41686904892` recorded the same digest, and to the copy vendored in `SubZeroDev.ServiceContract`); sha256 `43aa787484ce6ed0418775577cd964831a203e3b2504f26a3dd2df5c87eb90f4` |

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
`SubZeroDev.ServiceContract` generates its `0.9.0` contract against the engine's `0.14.0`
release, so this repository's own engine pin moves to the same `0.14.0` in the same commit —
otherwise every startup fails `EngineVersionMismatch` before the listener ever binds.

**At `0.14.0` neither the session store nor the host ports change**, so `src/store.ts` and the
migrations are untouched. The contract moves only because its recorded `engineVersion` does: the RPC
contract is byte-identical to `0.8.0`'s apart from that field, and the content contract also carries
structure-hash churn in definition names with identical bodies. What `0.14.0` adds (`unexpected_params`
and `unknown_target` action refusals, the capture and record-sink types at the package root, and
`createReplayContext`) sits on the kinds and the host ports this workload does not implement. The
`0.12.0` host-port changes (`revision`, `initialization`, profile `kindData`) landed in #353 and are
recorded in `design/90-decisions.md`.

**The engine tarball is a published release, not a candidate.** The contract tarball is still
unpublished (above). Its digest is not reproducible across checkouts: the content contract's
generated definition names carry a structure hash that differs between builds of the same commit,
with identical bodies. The digest above is the pack of the merged commit, and is the identity.
