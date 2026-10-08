# I-P4
Kind: invariant
Status: active
Anchor: I-P4
Enforcement: code
Consumes:
Exposes:
Binds:
Live:
Archival:
Questions:
Work:
Evidence: tests/SubZeroDev.Platform.Tests/OutboxDispatchTests.cs, tests/SubZeroDev.Platform.Tests/PersistenceIntegrationTests.cs

## Statement
A handler is never invoked for a (message id, consumer) whose earlier invocation committed (Owner: Persistence.)
