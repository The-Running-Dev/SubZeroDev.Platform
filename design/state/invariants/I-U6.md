# I-U6
Kind: invariant
Status: active
Anchor: I-U6
Enforcement: instruction
Consumes:
Exposes:
Binds:
Live:
Archival:
Questions:
Work:
Evidence: tests/SubZeroDev.Platform.Tests/AuditTests.cs, tests/SubZeroDev.Platform.Tests/AuditStoreTests.cs, tests/SubZeroDev.Platform.Tests/McpInvocationTests.cs, tests/SubZeroDev.Platform.Tests/TelemetryRedactionTests.cs, tests/SubZeroDev.Platform.Tests/OperatedScenarioTests.cs

## Statement
No secret value or payload reaches a stored record or a log line, through **any** audited input surface (Owner: all.)
