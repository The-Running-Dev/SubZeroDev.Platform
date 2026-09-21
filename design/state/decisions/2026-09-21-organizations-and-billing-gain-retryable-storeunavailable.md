# decision/2026-09-21-organizations-and-billing-gain-retryable-storeunavailable
Date: 2026-09-21
Anchor: 2026-09-21 — Organizations and Billing gain a retryable `StoreUnavailable`
Status: accepted
SupersededBy:
StatedIn: "unit/document/90-decisions § 2026-09-21 — Organizations and Billing gain a retryable `StoreUnavailable`"

## Claim
Context — both APIs mapped a failed store transaction onto a business variant — Organizations onto `OrganizationNotFound`, Billing onto `PlanNotFound`, `SubscriptionNotFound`, `InvalidTransition` or `ProviderEventMalformed` depending on the call — so an outage was answered as a non-retryable fact about the caller's data. The code carried comments admitting no variant fit.

Chosen — `StoreUnavailable`, retryable, on both error types, returned from every store-failure fallback including the subscription transition and membership revocation paths. `OrganizationNotFound` is kept for a membership check that failed. Recorded in `20-contract.md` § *Error semantics* 5 and 6.

Rejected — reusing `OrganizationNotFound` on the ground that it already confirms nothing about existence, because a caller told "not found" does not retry, and one told nothing distinguishes an outage from a revoked membership.

Reversibility — cheap before external consumers; a variant removed later breaks every caller's match.
