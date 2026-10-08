# decision/2026-10-08-runtime-settings-are-a-new-module-subzerodev-platform-runtimesettings-the-fourteenth-packa
Date: 2026-10-08
Anchor: 2026-10-08 — #58: runtime settings are a new module, `SubZeroDev.Platform.RuntimeSettings`, the fourteenth package
Status: accepted
SupersededBy:
StatedIn: "unit/document/90-decisions § 2026-10-08 — #58: runtime settings are a new module, `SubZeroDev.Platform.RuntimeSettings`, the fourteenth package"

## Claim
Context — [#58](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/58). Ben ruled 2026-10-08: a layered setting provider (global, then tenant, then user, persisted), distinct from startup configuration, with no administration UI (brief decision 6; the shell's Settings exclusion stands).

Chosen — a module package referencing Abstractions, Core and Persistence only, holding every type, the catalogue and one table `runtime_setting`; no framework seam. Values are a closed set (boolean, 64-bit integer, text) built by three factories with a default and an optional rule. A name matching the redaction marker set fails startup, so a setting is never a secret.

Rejected — Core (touches no database); Abstractions (contracts only); Persistence (puts the table in every host, fails the seam-admission test); Organizations (the self-hosted Automator and SkyNet HR do not run it); the name `Settings` (collides with startup settings); a reader contract in Abstractions (no framework consumer); arbitrary `T` over JSON; encrypted values (key custody Platform does not hold).

Reversibility — expensive once published for the package; cheap to widen the value types or add a seam.
