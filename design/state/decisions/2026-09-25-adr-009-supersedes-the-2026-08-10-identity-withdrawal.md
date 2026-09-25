# decision/2026-09-25-adr-009-supersedes-the-2026-08-10-identity-withdrawal
Date: 2026-09-25
Anchor: 2026-09-25 — ADR-009 ratifies Identity's placement, superseding the 2026-08-10 withdrawal
Status: accepted
SupersededBy:
StatedIn: "unit/document/90-decisions § 2026-09-25 — ADR-009 ratifies Identity's placement, superseding the 2026-08-10 withdrawal"

## Claim
Context — `design/g1/90-decisions.md`'s 2026-08-10 entry withdrew a proposed identity-substrate ADR before it was written, because `platform-identity.md` gave Identity the tier Undecided deliberately and no G1/D3-era consumer's principal was uniformly an account. Issue #90 later proposed a Platform-owned Identity package with a user store behind `UseIdentity()`. The D5 brief and design settled the question instead — Identity is a framework seam plus an optional module that owns no users — and D5-S2 and D5-S9 built it.

Chosen — [ADR-009](../../../docs/docs/adr/ADR-009-identity-package.md) records that placement as the architectural decision, its costs, and the alternatives rejected, including #90's own original proposal. This entry supersedes the g1 withdrawal rather than editing it — the withdrawal was correct reasoning for the question as it stood in G1/D3, and `g1/90-decisions.md` is that effort's closed log. `platform-identity.md` §3's former "Identity, authorization, tenancy" row is split: Identity now points at ADR-009; authorization and tenancy remain their own, still-open question.

Rejected — editing the 2026-08-10 entry in place to say Identity is now decided; rejected because a withdrawal that was later reversed is a fact about the repository's history, not an error to correct.

Reversibility — cheap; the entry is a pointer to ADR-009, not a second copy of its content.
