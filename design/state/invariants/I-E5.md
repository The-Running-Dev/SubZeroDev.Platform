# I-E5
Kind: invariant
Status: active
Anchor: I-E5
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
Each aborted stream is logged once as `EdgeStreamAborted` at `Warning` and counted once on `subzerodev.edge.stream.aborts`, carrying the configured prefix and never the request path, query, body or exception message; a caller disconnect is neither counted nor logged above `Debug` (Owner: GameEdge.)
