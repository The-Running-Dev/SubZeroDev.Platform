# decision/2026-10-08-the-user-layer-exists-only-for-an-account-per-tenant-the-implicit-tenant-has-no-tenant-lay
Date: 2026-10-08
Anchor: 2026-10-08 — #58: the user layer exists only for an account, per tenant; the implicit tenant has no tenant layer
Status: accepted
SupersededBy:
StatedIn: "unit/document/90-decisions § 2026-10-08 — #58: the user layer exists only for an account, per tenant; the implicit tenant has no tenant layer"

## Claim
Context — [#58](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/58). Resolution is user, then tenant, then global, then the declared default, over the layers a declaration admits. BarStrad's and SkyNet HR's principals are never accounts (#98).

Chosen — a user row is keyed by (tenant, `PrincipalId`) and exists only for an `Account` principal; `Delegated`, `System` and `Anonymous` fall through it on a read and get `LayerUnavailable` on a write. No tenant layer exists under `TenantId.Implicit`, so Local has the global layer only. A stored value that fails the current declaration answers `StoredValueInvalid` and never falls through.

Rejected — keying `Delegated` rows (a BarStrad table is shared by successive guests); one user row across tenants (the escape I-T2 refuses); treating the implicit tenant as a tenant (global and tenant become two names for one thing); falling through on a bad stored value (silently changes the effective value).

Reversibility — expensive for the resolution order and per-tenant user rows; cheap to admit `Delegated` later.
