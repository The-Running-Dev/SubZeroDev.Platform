# I-ST3
Kind: invariant
Status: active
Anchor: I-ST3
Enforcement: code
Consumes:
Exposes:
Binds:
Live:
Archival:
Questions:
Work:
Evidence: tests/SubZeroDev.Platform.Tests/RuntimeSettingsTests.cs

## Statement
A global or tenant write is authorized against `Platform.RuntimeSettings.WriteGlobal` or `WriteTenant`, scoped to the setting, before the store is touched; a user write reaches only the ambient account's own row (Owner: RuntimeSettings.)
