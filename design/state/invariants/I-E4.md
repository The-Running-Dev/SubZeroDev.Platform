# I-E4
Kind: invariant
Status: active
Anchor: I-E4
Enforcement: code
Consumes:
Exposes:
Binds:
Live:
Archival:
Questions:
Work:
Evidence: workloads/game-edge/SubZeroDev.Platform.GameEdge.Tests/StreamingTests.cs

## Statement
A streamed forward is exactly one attempt to the workload: the edge never retries it, before or after headers, and never resumes a stream (Owner: GameEdge.)
