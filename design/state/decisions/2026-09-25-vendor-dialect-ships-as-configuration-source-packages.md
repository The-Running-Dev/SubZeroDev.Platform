# decision/2026-09-25-vendor-dialect-ships-as-configuration-source-packages
Date: 2026-09-25
Anchor: 2026-09-25 — Vendor dialect ships as configuration-source packages that reference no Platform package
Status: accepted
SupersededBy:
StatedIn: "unit/document/10-design § 10. The generic bearer path, and vendor dialect as configuration"

## Claim
Context — `20-contract.md` § Unresolved item 4 (#94). The owner's 2026-08-09 direction is a named package per vendor, sugar over the generic path, never a parallel implementation; a vendor package implementing a provider would reference the Identity module, which ADR-006 rule 2 forbids.

Chosen — a vendor configuration package is a configuration source writing the generic path's settings and referencing no Platform package, enforced by a build check. A quirk configuration cannot express becomes a generic-path capability first. No vendor package is built in this pass.

Rejected — presets inside Identity; a vendor-options contract in the framework; vendor packages registering their own provider; a raw callback; documentation recipes only.

Reversibility — moderate; configuration keys are public contract once a vendor package writes them.
