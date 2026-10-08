# I-U4
Kind: invariant
Status: active
Anchor: I-U4
Enforcement: code
Consumes:
Exposes:
Binds:
Live:
Archival:
Questions:
Work:
Evidence: tests/SubZeroDev.Platform.Tests/AuditTests.cs, tests/SubZeroDev.Platform.Tests/AuditStoreTests.cs, tests/SubZeroDev.Platform.Tests/McpInvocationTests.cs

## Statement
A denial, a read, or a failure that wrote nothing writes its row in its own transaction after the outcome is known (Owner: each writer.)
