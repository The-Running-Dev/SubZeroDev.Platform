# I-ST7
Kind: invariant
Status: active
Anchor: I-ST7
Enforcement: code
Consumes:
Exposes:
Binds:
Live:
Archival:
Questions:
Work:
Evidence: tests/SubZeroDev.Platform.Tests/RuntimeSettingsTests.cs, tests/SubZeroDev.Platform.Tests/RuntimeSettingsPostgresTests.cs

## Statement
The module holds no in-process copy of a stored value, so a committed write is seen by the next read on every instance of every role (Owner: RuntimeSettings.)
