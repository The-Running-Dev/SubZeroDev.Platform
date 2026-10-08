# I-T6
Kind: invariant
Status: active
Anchor: I-T6
Enforcement: code
Consumes:
Exposes:
Binds:
Live:
Archival:
Questions:
Work:
Evidence: tests/SubZeroDev.Platform.Tests/TenancyTests.cs

## Statement
With no resolver registered, `ICurrentTenant.Current` is `TenantId.Implicit` (Owner: Core.) Enforced by code — resolver chain.
