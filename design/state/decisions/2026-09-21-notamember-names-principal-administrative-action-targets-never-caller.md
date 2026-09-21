# decision/2026-09-21-notamember-names-principal-administrative-action-targets-never-caller
Date: 2026-09-21
Anchor: 2026-09-21 — `NotAMember` names the principal an administrative action targets, never the caller
Status: accepted
SupersededBy:
StatedIn: "unit/document/90-decisions § 2026-09-21 — `NotAMember` names the principal an administrative action targets, never the caller"

## Claim
Context — the contract defined `NotAMember` as a member-only action by a revoked principal; the code answers a non-member caller `OrganizationNotFound` and raises `NotAMember` only about the principal an administrative action names. `/align` asked which was right.

Chosen — amend the doc to the code. A caller who is not an active member gets `OrganizationNotFound`, so existence is never confirmed to a caller who may not see it.

Rejected — raising `NotAMember` for the caller, because it would confirm the organization exists to someone outside it — the leak `OrganizationNotFound` exists to prevent.

Reversibility — cheap; the variant's meaning is fixed by the doc and one test.
