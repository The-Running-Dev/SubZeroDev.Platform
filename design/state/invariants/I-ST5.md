# I-ST5
Kind: invariant
Status: active
Anchor: I-ST5
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
Tenant and user setting rows are read and written only with the current tenant; a global row belongs to the installation and is stored under the implicit tenant with layer `global` (Owner: RuntimeSettings.)
