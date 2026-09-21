# decision/2026-08-24-tenant-escape-is-declared-type-plus-audited-read-only-scope
Date: 2026-08-24
Anchor: 2026-08-24 — The tenant escape is a declared type plus an audited read-only scope
Status: accepted
SupersededBy:
StatedIn: "unit/document/90-decisions § 2026-08-24 — The tenant escape is a declared type plus an audited read-only scope"

## Claim
Context — `second-consumer-packages.md` §3 names the deliberately shared resource as the thing to design, "because an unmodelled one is how isolation quietly stops holding". The Game Engine's published campaigns are readable across tenants while sessions and saves never are.

Chosen — shareability is declared on the **entity type** at model build, so it is visible in the model rather than writable by any path that can write a row. A row becomes shared through an explicit, permissioned, audited write by its owning tenant — an ordinary tenant-scoped write. A cross-tenant read happens only inside a named scope that widens the filter for the one declared type it names, emits one audit event per scope rather than per row, and is **read-only**: a write attempted inside it is a contract violation and throws. The resulting invariant is that no code path in Platform lets a write reach another tenant's row.

Rejected — **a per-row `IsShared` flag honoured globally by the filter**, the usual build; rejected because it makes the isolation boundary writable by every path that can write the row, and because no caller ever states an intent to cross, so there is nothing to audit and nothing to grep. **A `ReadAcrossTenants` permission and no scope**, explicit and auditable and consistent with the authorization model; rejected because a permission is held for a session rather than an operation — the holder crosses on every query including the unintended ones, and the audit says a principal *could* have crossed rather than that it did. **Allowing writes inside the scope**, symmetric and it would let a shared resource be edited by a collaborator; rejected because the asymmetric invariant is worth far more than the symmetry, and the consumer that forced this design writes published campaigns from their owner only.

Reversibility — cheap for the read half — widening later is additive. Expensive for the write half: admitting cross-tenant writes later changes an invariant other code will have come to rely on.
