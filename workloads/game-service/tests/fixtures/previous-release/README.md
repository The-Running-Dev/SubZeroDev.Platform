# Previous-release adapter fixture

The canonical source is Platform commit
[`7c82c565a6c23743a5914fa2bdfe7fca5feb87e8`](https://github.com/The-Running-Dev/SubZeroDev.Platform/tree/7c82c565a6c23743a5914fa2bdfe7fca5feb87e8),
the last commit to vendor GameEngine 0.12.0. The JavaScript here is TypeScript's type-erased
output from that commit's store, local imports and four migrations, without behavior edits. The
engine tarball is copied byte for byte from the same commit and installed under the test-only
`@the-running-dev/game-engine-previous-release` alias. `manifest.json` records source and output
SHA-256 hashes; the tests verify every output before opening a store.

Do not patch this directory to make a test pass. From `workloads/game-service`, with the pinned
commit available in Git and the lockfile's TypeScript installed, regenerate with:

```bash
node --import tsx scripts/regenerate-mixed-version-fixture.ts previous-release
```

**Unlike [`../pre-upgrade/`](../pre-upgrade/README.md), this fixture moves on every engine
upgrade.** `tests/support/mixed-version.ts` refuses to load it unless the candidate engine is
`PREVIOUS_RELEASE_CANDIDATE`. An upgrade re-pins it: point the `previous-release` entry in
`scripts/regenerate-mixed-version-fixture.ts` at the commit that vendored the outgoing release,
regenerate, update the alias in `package.json` and both constants in the support file.

`tests/previous-release.test.ts` runs the outgoing and incoming releases against one schema:
alternating session writes with saves loaded across, profile reads in both directions with kindData
kept and format 2 stored, and code rollback over the incoming release's schema and data. These are
adapter and runtime checks against PostgreSQL, not evidence that a production rolling deployment
has been exercised.
