# I-P2
Kind: invariant
Status: active
Anchor: I-P2
Enforcement: instruction
Consumes:
Exposes:
Binds:
Live:
Archival:
Questions:
Work:
Evidence: tests/SubZeroDev.Platform.Tests/PersistenceContractTests.cs

## Statement
Every guarded write predicates on `version = @expected` and the tenant, and an UPDATE sets `version = @expected + 1`; no other statement writes `version` (Owner: each consumer declaring `IVersioned`.)
