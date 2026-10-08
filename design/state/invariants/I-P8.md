# I-P8
Kind: invariant
Status: active
Anchor: I-P8
Enforcement: instruction
Consumes:
Exposes:
Binds:
Live:
Archival:
Questions:
Work:
Evidence: tests/SubZeroDev.Platform.Tests/DevelopmentMigrationTests.cs

## Statement
`IMigrationRunner.ApplyAsync` is the only path that writes migration history; development application and migrate mode both take the provider-native lock through it (Owner: Persistence.)
