# decision/2026-09-21-byactorasync-filters-on-issuer-and-subject
Date: 2026-09-21
Anchor: 2026-09-21 — `ByActorAsync` filters on the issuer and the subject
Status: accepted
SupersededBy:
StatedIn: "unit/document/90-decisions § 2026-09-21 — `ByActorAsync` filters on the issuer and the subject"

## Claim
Context — `PrincipalId` is the (issuer, subject) pair, but the audit read API filtered on the subject alone, so two issuers sharing a subject string read each other's records.

Chosen — filter on both columns. The existing subject-led index still serves the query.

Rejected — documenting the subject-only filter, because a principal's identity is the pair everywhere else in the contract, and one read that disagrees merges two principals' histories.

Reversibility — cheap; the read API is additive-only and the filter only narrows.
