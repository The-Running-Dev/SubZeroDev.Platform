# I-T1
Kind: invariant
Status: active
Anchor: I-T1
Enforcement: code
Consumes:
Exposes:
Binds:
Live:
Archival:
Questions:
Work:
Evidence: tests/SubZeroDev.Platform.Tests/PersistenceIntegrationTests.cs, tests/SubZeroDev.Platform.Tests/SharedReadTests.cs, tests/SubZeroDev.Platform.Tests/AuditStoreTests.cs

## Statement
**There is no code path in Platform by which a write reaches another tenant's row.** Isolation is asymmetric on purpose: reads have one modelled audited escape, writes have none (Owner: Persistence.)
