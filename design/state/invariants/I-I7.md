# I-I7
Kind: invariant
Status: active
Anchor: I-I7
Enforcement: code
Consumes:
Exposes:
Binds:
Live:
Archival:
Questions:
Work:
Evidence: tests/SubZeroDev.Platform.Tests/ConfiguredBearerTests.cs

## Statement
The generic bearer path accepts asymmetric signature algorithms only: a shared-secret algorithm or `none` in its settings fails startup, and a token whose header names an algorithm outside the provider's set is rejected (Owner: Identity.)
