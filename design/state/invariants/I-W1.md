# I-W1
Kind: invariant
Status: active
Anchor: I-W1
Enforcement: instruction
Consumes:
Exposes:
Binds:
Live:
Archival:
Questions:
Work:
Evidence: tests/SubZeroDev.Platform.Tests/PackageGraphTests.cs, tests/SubZeroDev.Platform.Tests/AdministrationShellTests.cs

## Statement
The shell holds no server-side state and reaches the system only over the public HTTP API; **no backend package references it, and it has no privileged endpoint of its own** (Owner: the shell.)
