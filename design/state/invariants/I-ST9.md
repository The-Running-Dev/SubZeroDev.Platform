# I-ST9
Kind: invariant
Status: active
Anchor: I-ST9
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
A stored value that fails to parse or validate under the current declaration is answered `StoredValueInvalid`, never replaced by a lower layer or the default (Owner: RuntimeSettings.)
