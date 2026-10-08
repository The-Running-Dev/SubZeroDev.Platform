# I-U4
Kind: invariant
Status: active
Anchor: I-U4
Enforcement: instruction
Consumes:
Exposes:
Binds:
Live:
Archival:
Questions:
Work:
Evidence: tests/SubZeroDev.Platform.Tests/AuditTests.cs

## Statement
A denial, a read, or a failure that wrote nothing writes its row in its own transaction after the outcome is known (Owner: each writer.)
