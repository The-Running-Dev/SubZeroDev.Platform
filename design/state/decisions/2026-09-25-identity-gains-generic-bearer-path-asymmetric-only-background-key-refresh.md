# decision/2026-09-25-identity-gains-generic-bearer-path-asymmetric-only-background-key-refresh
Date: 2026-09-25
Anchor: 2026-09-25 — Identity gains a production-grade generic bearer path, asymmetric only, with background key refresh
Status: accepted
SupersededBy:
StatedIn: "unit/document/10-design § 10. The generic bearer path, and vendor dialect as configuration"

## Claim
Context — ADR-009 left the production bearer provider open, and #94's vendor packages need a generic path to be sugar over.

Chosen — a generic bearer provider in Identity, one per trusted issuer, configured by issuer or issuer pattern, key source, non-empty audiences, asymmetric algorithms only, bounded clock tolerance, subject claim and optional display-name claim. Settings validated at startup; the key fetch never fails startup. Keys refresh on an interval off the request path with a whole-set atomic swap, per instance, no lease; an unknown key id is `CredentialRejected` and never triggers a fetch; a failed refresh keeps the previous set and degrades readiness. The one exception to D5 adding no background work.

Rejected — request-path fetch on an unknown key id; a production shared-secret mode; deferring the provider until a vendor package needs it.

Reversibility — moderate; the settings schema becomes public contract.
