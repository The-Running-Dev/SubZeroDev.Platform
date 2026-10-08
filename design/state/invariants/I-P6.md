# I-P6
Kind: invariant
Status: active
Anchor: I-P6
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
An inbox record is pruned only once its outbox row no longer exists (Owner: Persistence.)
