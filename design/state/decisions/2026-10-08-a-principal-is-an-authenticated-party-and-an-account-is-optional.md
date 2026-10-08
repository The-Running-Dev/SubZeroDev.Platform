# decision/2026-10-08-a-principal-is-an-authenticated-party-and-an-account-is-optional
Date: 2026-10-08
Anchor: 2026-10-08 — #98: a principal is an authenticated party, and an account is optional
Status: accepted
SupersededBy:
StatedIn: "unit/document/90-decisions § 2026-10-08 — #98: a principal is an authenticated party, and an account is optional"

## Claim
Context — [#98](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/98). BarStrad's principal is a table and SkyNet HR's is an operator asserted by a proxy, and neither is an account. The Automator and GEaaS have accounts.

Chosen — the question is answered by D5's principal model as built. `PrincipalKind` is `Anonymous`, `Account`, `Delegated` or `System`, and `PrincipalId` is the (issuer, subject) pair, so a party with no account is a first-class principal. No change follows. Ben ruled 2026-10-08.

Rejected — requiring an account behind every principal, which forces BarStrad and SkyNet HR to invent one.

Reversibility — expensive — the principal model is public contract.
