# I-I4
Kind: invariant
Status: active
Anchor: I-I4
Enforcement: code, instruction
Consumes:
Exposes:
Binds:
Live:
Archival:
Questions:
Work:
Evidence: tests/SubZeroDev.Platform.Tests/IdentityTests.cs, tests/SubZeroDev.Platform.Tests/AccountTests.cs

## Statement
Platform declares no user entity and no directory. The optional account store holds an account id, its creation instant and its linked (issuer, subject) pairs, and nothing else, and exists only in a host that registers it (Owner: Identity.) Enforced by code — an architecture check over the module's types and its migration's columns.
