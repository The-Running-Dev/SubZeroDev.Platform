# I-E3
Kind: invariant
Status: active
Anchor: I-E3
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
After a streamed route's headers are sent, a workload failure aborts the caller's response — no terminating chunk on HTTP/1.1, a reset stream on HTTP/2 — and the edge writes no byte of its own after headers: no status, envelope, frame or trailer (Owner: GameEdge.)
