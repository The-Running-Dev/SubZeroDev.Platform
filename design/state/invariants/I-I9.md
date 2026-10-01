# I-I9
Kind: invariant
Status: active
Anchor: I-I9
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
A key-set refresh takes no lease, writes nothing durable, and replaces the cached set in one atomic swap; a failed refresh keeps the previous set and degrades readiness, and never empties the cache (Owner: Identity.)
