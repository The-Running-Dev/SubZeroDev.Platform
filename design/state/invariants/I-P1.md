# I-P1
Kind: invariant
Status: active
Anchor: I-P1
Enforcement: code
Consumes:
Exposes:
Binds:
Live:
Archival:
Questions:
Work:
Evidence: tests/SubZeroDev.Platform.Tests/PersistenceIntegrationTests.cs, tests/SubZeroDev.Platform.Tests/PostgresPersistenceContractTests.cs

## Statement
A guarded write commits only when it matched exactly one row; zero rows rolls back the whole unit of work — consumer writes, staged outbox rows and staged audit — and returns `StaleVersion`, whether or not the work read the guard's result (Owner: Persistence.)
