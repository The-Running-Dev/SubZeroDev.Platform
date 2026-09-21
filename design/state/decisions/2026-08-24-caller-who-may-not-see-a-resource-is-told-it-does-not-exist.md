# decision/2026-08-24-caller-who-may-not-see-a-resource-is-told-it-does-not-exist
Date: 2026-08-24
Anchor: 2026-08-24 — A caller who may not see a resource is told it does not exist
Status: accepted
SupersededBy:
StatedIn: "unit/document/90-decisions § 2026-08-24 — A caller who may not see a resource is told it does not exist"

## Claim
Context — two boundaries return refusals — the tenant filter and the authorization evaluator — and using one answer for both leaks existence across tenants.

Chosen — a cross-tenant read returns **not found**, and so does switching to an organization the principal is not a member of, because "forbidden" confirms the resource exists. A permission denial on a resource the principal *can* see returns **forbidden**, because there the existence is already known and pretending otherwise only obscures the fix. An invitation token that is unknown, expired or already redeemed does not distinguish a token that never existed from one that did.

Rejected — **one answer for both**, simpler and one fewer distinction for a caller to learn; rejected because the difference is a security property rather than a style choice — a uniform "forbidden" turns every identifier into an existence oracle, and a uniform "not found" makes a genuine permission problem undiagnosable.

Reversibility — cheap in mechanism, expensive in expectation once a consumer's error handling depends on it.
