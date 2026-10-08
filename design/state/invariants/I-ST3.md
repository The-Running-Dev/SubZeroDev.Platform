# I-ST3
Kind: invariant
Status: active
Anchor: I-ST3
Enforcement: instruction
Consumes:
Exposes:
Binds:
Live:
Archival:
Questions:
Work:
Evidence:

## Statement
A global or tenant write is authorized against `Platform.RuntimeSettings.WriteGlobal` or `WriteTenant`, scoped to the setting, before the store is touched; a user write reaches only the ambient account's own row (Owner: RuntimeSettings.)
