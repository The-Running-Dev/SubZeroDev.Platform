# decision/2026-10-08-seeding-is-iseeder-run-by-migrate-mode-after-migrations-each-seeder-stating-its-tenant
Date: 2026-10-08
Anchor: 2026-10-08 — #61: seeding is `ISeeder`, run by migrate mode after migrations, each seeder stating its tenant
Status: accepted
SupersededBy:
StatedIn: "unit/document/90-decisions § 2026-10-08 — #61: seeding is `ISeeder`, run by migrate mode after migrations, each seeder stating its tenant"

## Claim
Context — D3's `NoAmbientOperationScope` remedy names "a seeder" and none existed. The owner ruled for an `ISeeder` beside `IMigrationRunner`, run by migrate mode.

Chosen — `ISeeder { Module; Name; SeedAsync(ct) }` collected from DI. It runs after every successful `ApplyAsync`, including one with nothing pending, outside the lock, in composed-module topological order then registration order. Migrate mode now registers modules in topological order and the Core ambient defaults. The seeder opens its own scope with an explicit tenant and `Principal.LocalSystem`, and must converge. A throw is `MigrationError.SeedFailed`, exit 1. Development start does not seed.

Rejected — migrate mode opening a scope on the implicit tenant (silent wrong-tenant writes for multi-tenant consumers); seeding under the migration lock (a runner API change); a `SeedError` type and a new exit code; seeding at development start (couples #61 to #68).

Reversibility — expensive for `ISeeder` and the `SeedFailed` code; cheap for ordering and the development choice.
