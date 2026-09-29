# I-A1
Kind: invariant
Status: active
Anchor: I-A1
Enforcement: code
Consumes:
Exposes:
Binds:
Live:
Archival:
Questions:
Work:
Evidence: tests/SubZeroDev.Platform.Tests/AuthorizationTests.cs

## Statement
`AuthorizationDecision.Sources` is non-empty **iff** `Outcome == Allowed` (Owner: Core.) Enforced by code — evaluator construction.
