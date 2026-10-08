# I-P9
Kind: invariant
Status: active
Anchor: I-P9
Enforcement: instruction
Consumes:
Exposes:
Binds:
Live:
Archival:
Questions:
Work:
Evidence: tests/SubZeroDev.Platform.Tests/SeedingTests.cs

## Statement
Seeders run only in migrate mode, only after `ApplyAsync` succeeded, grouped in composed-module topological order and registration order within a module (Owner: Persistence.)
