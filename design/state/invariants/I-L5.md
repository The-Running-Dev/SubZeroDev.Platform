# I-L5
Kind: invariant
Status: active
Anchor: I-L5
Enforcement: code
Consumes:
Exposes:
Binds:
Live:
Archival:
Questions:
Work:
Evidence: tests/SubZeroDev.Platform.Tests/LicensingTests.cs

## Statement
Revocation is consulted on no path — not the request path, not startup, not readiness (Owner: Licensing.) Enforced by code — no path calls a registered revocation check.
