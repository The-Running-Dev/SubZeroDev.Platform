# I-E1
Kind: invariant
Status: active
Anchor: I-E1
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
A request whose path matches no `GameEdge:StreamingRoutes` prefix is forwarded buffered, under one `ForwardTimeout`, with G1's answers including #106's after-headers `503`; with no route listed the edge is G1's edge unchanged (Owner: GameEdge.)
