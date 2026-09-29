# I-T2
Kind: invariant
Status: active
Anchor: I-T2
Enforcement: instruction
Consumes:
Exposes:
Binds:
Live:
Archival:
Questions:
Work:
Evidence: tests/SubZeroDev.Platform.Tests/SharedReadTests.cs

## Statement
Outside a shared-read scope the query filter is `tenant equals current`, unconditionally, for shareable and non-shareable types alike (Owner: Persistence.)
