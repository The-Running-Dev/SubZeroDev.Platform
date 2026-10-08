# I-ST4
Kind: invariant
Status: active
Anchor: I-ST4
Enforcement: code
Consumes:
Exposes:
Binds:
Live:
Archival:
Questions:
Work:
Evidence: tests/SubZeroDev.Platform.Tests/RuntimeSettingsTests.cs, tests/SubZeroDev.Platform.Tests/AuditInputSurfaceTests.cs

## Statement
Every change to a setting writes exactly one `Required` audit record in the change's transaction, naming the layer and setting and never the value; if the record cannot be written the change does not commit (Owner: RuntimeSettings.)
