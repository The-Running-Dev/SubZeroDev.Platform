# I-A3
Kind: invariant
Status: active
Anchor: I-A3
Enforcement: instruction
Consumes:
Exposes:
Binds:
Live:
Archival:
Questions:
Work:
Evidence: tests/SubZeroDev.Platform.Tests/RequestOrderTests.cs, tests/SubZeroDev.Platform.Tests/McpTests.cs, tests/SubZeroDev.Platform.Tests/AuthorizationTests.cs

## Statement
Every `PermissionName` reaching the evaluator is declared by some `IPermissionCatalog`; an undeclared one fails **startup**, never a request (Owner: Core.)
