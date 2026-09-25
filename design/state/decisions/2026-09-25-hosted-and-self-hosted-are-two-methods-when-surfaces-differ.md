# decision/2026-09-25-hosted-and-self-hosted-are-two-methods-when-surfaces-differ
Date: 2026-09-25
Anchor: 2026-09-25 — Hosted and self-hosted deployments of one vendor are two methods when their protocol surfaces differ
Status: accepted
SupersededBy:
StatedIn: "unit/document/10-design § 10. The generic bearer path, and vendor dialect as configuration"

## Claim
Context — #94 asks whether hosted versus self-hosted for one vendor is one method with a discriminator or two. Self-hosted Supabase is an OIDC provider only with `GOTRUE_OAUTH_SERVER_ENABLED` set.

Chosen — two methods whenever the deployments speak different protocol surfaces; one when the same settings with different values describe both.

Rejected — one method with a discriminator.

Reversibility — cheap; two methods can collapse later.
