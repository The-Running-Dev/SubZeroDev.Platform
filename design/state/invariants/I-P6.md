# I-P6
Kind: invariant
Status: active
Anchor: I-P6
Enforcement: code
Consumes:
Exposes:
Binds:
Live:
Archival:
Questions:
Work:
Evidence: tests/SubZeroDev.Platform.Tests/PersistenceIntegrationTests.cs, tests/SubZeroDev.Platform.Tests/PruneLoggingTests.cs

## Statement
An inbox record is pruned only once its outbox row no longer exists (Owner: Persistence.)
