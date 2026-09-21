# I-T1
Kind: invariant
Status: active
Anchor: I-T1
Enforcement: code
Consumes:
Exposes:
Binds:
Live:
Archival:
Questions:
Work:
Evidence:

## Statement
**There is no code path in Platform by which a write reaches another tenant's row.** Isolation is asymmetric on purpose: reads have one modelled audited escape, writes have none (Owner: Persistence.) Enforced by code — the scope is read-only and a write inside it throws.
