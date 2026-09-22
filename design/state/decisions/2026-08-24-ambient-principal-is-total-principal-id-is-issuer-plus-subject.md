# decision/2026-08-24-ambient-principal-is-total-principal-id-is-issuer-plus-subject
Date: 2026-08-24
Anchor: 2026-08-24 — The ambient principal is total, and a principal id is issuer plus subject
Status: accepted
SupersededBy:
StatedIn: "unit/document/90-decisions § 2026-08-24 — The ambient principal is total, and a principal id is issuer plus subject"

## Claim
Context — `IOperationScope.Principal` is `ClaimsPrincipal?` and `IAuditable.CreatedBy` is `string?` because D3 had no identity. The brief requires allowed, denied and failed actions each to persist an actor, and admits accountless and delegated principals as first-class rather than degraded.

Chosen — the ambient principal is **non-null**, with `Anonymous` a well-known value exactly as `TenantId.Implicit` is a well-known tenant, and four kinds — `Anonymous`, `Account`, `Delegated`, `System` — where the kind states whether the actor is resolvable afterwards. `PrincipalId` is a pair, issuer and subject, both opaque and compared ordinally; neither half is parsed or normalised by Platform. `ClaimsPrincipal?` stays alongside as the raw authentication result.

Rejected — **keeping the nullable principal**, no breaking change to a published 0.x surface; rejected because a null actor is indistinguishable from an actor that was never resolved, so an audit trail containing them cannot answer the question it exists to answer — the criterion becomes a convention rather than a property of the type. **A single-string principal id**, simpler and adequate today; rejected because brief decision 5 keeps the Automator and Game Engine identity stores separate while preserving a later reversal, and a single string makes that reversal a data migration the first time two stores mint the same subject. **Modelling accountless principals as `Account` with absent fields**; rejected because `application-modules.md` §2 states the correction directly — no account will ever exist for BarStrad's customer-side principal — and every consumer would be defensive about permanently absent fields.

Reversibility — expensive, which is the argument for taking the breaking change now, while the packages are explicitly unstable 0.x, rather than after a consumer ships.
