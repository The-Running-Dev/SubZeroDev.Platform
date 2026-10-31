# Immutable pre-upgrade adapter fixture

The canonical source is Platform commit
[`5b587d6990552b85dbf31cb9d82c22869754ac78`](https://github.com/The-Running-Dev/SubZeroDev.Platform/tree/5b587d6990552b85dbf31cb9d82c22869754ac78).
The JavaScript here is TypeScript's type-erased output from that commit's store, local imports,
and migrations, without behavior edits. The engine tarball is copied byte for byte from the same
commit and installed under the test-only `@the-running-dev/game-engine-previous` alias. Its
`@noble/hashes` dependency stays at the pre-upgrade lockfile's 2.3.0. These are fixture pins, not
claims about a deployed release. `manifest.json` records source and output SHA-256 hashes;
the tests verify every output before opening a store.

Do not patch this directory to make a test pass. From `workloads/game-service`, with the pinned
commit available in Git and the lockfile's TypeScript installed, regenerate with:

```bash
node --import tsx scripts/regenerate-mixed-version-fixture.ts pre-upgrade
```

Normal test runs need neither Git history nor a sibling checkout. The candidate adapter is
`src/store.ts` from #353, inspected at
[`d47a14ca4d366e0d5ca98adeef932ce8ab6173a6`](https://github.com/The-Running-Dev/SubZeroDev.Platform/commit/d47a14ca4d366e0d5ca98adeef932ce8ab6173a6),
with its vendored GameEngine 0.12.0. The regression change leaves that adapter unmodified and
adds a corrective migration. Old and new adapters open separate pools against each test's one
schema. The migration tests also seed through the old schema and exercise the interval between
#353's original migration and the correction.

The shared story-graph campaign generates actual blobs with each runtime; both runtimes resume
sessions and load saves. Initialization tests separately preserve absent, empty and populated
metadata across old writes. The old runtime intentionally does not expose initialization or
kindData. A stored profile format 3 intentionally produces `profile_corrupt` there; the principal
profile regression instead requires the old decoder to read the new adapter's normal write
without warnings, so enabling format-3 writes prematurely fails the test.

These tests do not promise that new-only kind state, changed content, or arbitrary future save
formats work in an older runtime. They do not launch a production rolling deployment.

This fixture does not move on engine upgrades: it stays the #353 regression's old side, and the
suite reads the candidate's version rather than naming one. The release-to-release check that
does move is [`../previous-release/README.md`](../previous-release/README.md).
