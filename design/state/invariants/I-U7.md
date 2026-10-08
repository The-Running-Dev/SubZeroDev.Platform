# I-U7
Kind: invariant
Status: active
Anchor: I-U7
Enforcement: instruction
Consumes:
Exposes:
Binds:
Live:
Archival:
Questions:
Work:
Evidence: tests/SubZeroDev.Platform.Tests/AuditStoreTests.cs, tests/SubZeroDev.Platform.Tests/AuditRetentionTests.cs

## Statement
Audit records are never updated, and no surface exposes an update or a delete; the only delete is the retention prune, which removes whole rows by age (Owner: Audit store.)
