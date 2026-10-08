# I-P5
Kind: invariant
Status: active
Anchor: I-P5
Enforcement: instruction
Consumes:
Exposes:
Binds:
Live:
Archival:
Questions:
Work:
Evidence: tests/SubZeroDev.Platform.Tests/OutboxDispatchTests.cs

## Statement
The inbox record commits in the same transaction as the handler's effects, and a handler failure leaves no record (Owner: Persistence.)
