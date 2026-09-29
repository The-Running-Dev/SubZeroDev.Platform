# I-O1
Kind: invariant
Status: active
Anchor: I-O1
Enforcement: code
Consumes:
Exposes:
Binds:
Live:
Archival:
Questions:
Work:
Evidence: tests/SubZeroDev.Platform.Tests/OrganizationsTests.cs

## Statement
Creating an organization mints the tenant, writes the organization and writes the owner's membership in one transaction with its audit row (Owner: Organizations.)
