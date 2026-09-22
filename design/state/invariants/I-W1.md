# I-W1
Kind: invariant
Status: active
Anchor: I-W1
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
The shell holds no server-side state and reaches the system only over the public HTTP API; **no backend package references it, and it has no privileged endpoint of its own** (Owner: the shell.) Enforced by code — the package graph, plus an assertion that every endpoint the shell calls is callable without it.
