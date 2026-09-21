# I-I3
Kind: invariant
Status: active
Anchor: I-I3
Enforcement: code, instruction
Consumes:
Exposes:
Binds:
Live:
Archival:
Questions:
Work:
Evidence:

## Statement
`PrincipalId.ToString()` is never split to recover the pair; anywhere the pair is stored it is two columns (Owner: Abstractions, Audit store, Organizations.) Enforced by code — in each schema; enforced by instruction otherwise.
