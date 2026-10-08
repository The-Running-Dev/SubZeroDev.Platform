# I-E2
Kind: invariant
Status: active
Anchor: I-E2
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
On a streamed route the edge commits the workload's status and `Content-Type` when the workload's headers arrive and flushes each piece of the body as it is read, never coalescing; `ForwardTimeout` does not apply and no edge deadline follows the headers (Owner: GameEdge.)
