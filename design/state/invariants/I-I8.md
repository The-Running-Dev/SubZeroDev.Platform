# I-I8
Kind: invariant
Status: active
Anchor: I-I8
Enforcement: code
Consumes:
Exposes:
Binds:
Live:
Archival:
Questions:
Work:
Evidence: tests/SubZeroDev.Platform.Tests/KeyDiscoveryTests.cs

## Statement
No key material is fetched on the request path; a token naming a key id the cached set does not hold is `CredentialRejected` and never triggers a fetch (Owner: Identity.)
