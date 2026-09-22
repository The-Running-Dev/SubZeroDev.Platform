# decision/2026-08-24-organization-holds-a-tenant-it-is-not-one
Date: 2026-08-24
Anchor: 2026-08-24 — An organization holds a tenant; it is not one
Status: accepted
SupersededBy:
StatedIn: "unit/document/90-decisions § 2026-08-24 — An organization holds a tenant; it is not one"

## Claim
Context — all three evidence consumers read one-to-one — a team, a studio, a venue — and the brief requires switching between organizations to isolate. Tenancy is framework and Organizations is a module, so whichever way this goes decides whether a framework type carries a module's concept.

Chosen — one organization has exactly one tenant, minted when the organization is created and held as a column on the organization. The framework never learns that a tenant has an owner. Membership is keyed by the framework's `PrincipalId`, which is what keeps Organizations free of any reference to Identity and lets a `Delegated` principal hold a membership.

Rejected — **making the organization id the tenant id**, one fewer column and no possible disagreement between the two; rejected because `TenantId` would then mean "an organization" — a module's identity inside the framework's most load-bearing value — and a consumer wanting several tenants per organization would face a migration on every table rather than an added row. **Organizations and tenants as orthogonal dimensions**, maximum flexibility; rejected because no consumer needs it and generality invented without a consumer to test it is the failure `second-consumer-packages.md` §1 describes and ADR-006 rejects by name.

Reversibility — cheap. One-to-many is an added table and no framework change.
