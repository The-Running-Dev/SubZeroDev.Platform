# decision/2026-08-24-composition-profile-is-declared-validated-at-startup-and-fingerprinted
Date: 2026-08-24
Anchor: 2026-08-24 — The composition profile is declared, validated at startup, and fingerprinted
Status: accepted
SupersededBy:
StatedIn: "unit/document/90-decisions § 2026-08-24 — The composition profile is declared, validated at startup, and fingerprinted"

## Claim
Context — the brief requires the local host to have no package or project reference to Identity, Organizations, Billing or Licensing, and requires their absence to be real rather than "registered checks that always pass". The package graph decides what is present; nothing made the host say so.

Chosen — a host declares `Local` or `Operated`. At startup the provider registries close and are validated against the declaration — `Operated` with no authentication provider fails; `Local` with an authentication provider, a non-baseline entitlement contributor or a tenant resolver fails — and the failure names the profile, the offending registration and which of the two it disagrees with. The profile and the contributor set join the existing settings fingerprint, so a second instance registering a different set is visible through `platform.settings-fingerprint` rather than through divergent behaviour. The seam defaults in `Local` are named local providers that exist and can be pointed at, not stubs standing in for absent modules.

Rejected — **relying on the package graph alone**, which is already checked by the brief's own architecture gate; rejected because the graph is a build-time fact and a misconfigured host is a runtime one — an operated host that started without an authentication provider would serve, and every guarantee in the design is stated relative to a composition it never announced. **Degrading rather than failing startup**; rejected because a host that cannot state its own composition should not serve.

Reversibility — cheap.
