# decision/2026-10-08-global-and-tenant-writes-need-module-declared-permissions-and-every-change-is-a-required-a
Date: 2026-10-08
Anchor: 2026-10-08 — #58: global and tenant writes need module-declared permissions, and every change is a Required audit record without its value
Status: accepted
SupersededBy:
StatedIn: "unit/document/90-decisions § 2026-10-08 — #58: global and tenant writes need module-declared permissions, and every change is a Required audit record without its value"

## Claim
Context — [#58](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/58). A setting change is an administrative act, and role policy belongs to the product.

Chosen — the module's own catalogue declares `Platform.RuntimeSettings.WriteGlobal` and `WriteTenant`, evaluated with resource `RuntimeSetting/<name>` so a grant can name one setting; a user-layer write needs no permission and reaches only the caller's row. In Local the composition provider grants both to `System`; in Operated only the product's provider does, and Platform grants them to nobody. Each set, and each clear that removed a row, writes one `Required` record in the change's transaction, naming layer and setting, never the value. The module maps no endpoint and sweeps no row.

Rejected — `WriteOwn` (every account would hold it); the names in `PlatformPermissions` granted by Organizations (couples its role policy to a module it cannot see); a module-shipped provider; `Recorded` audit (allows an unrecorded change); a module HTTP API (an administration surface); a sweeper.

Reversibility — cheap.
