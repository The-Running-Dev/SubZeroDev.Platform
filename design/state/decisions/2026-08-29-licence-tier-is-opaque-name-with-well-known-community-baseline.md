# decision/2026-08-29-licence-tier-is-opaque-name-with-well-known-community-baseline
Date: 2026-08-29
Anchor: 2026-08-29 — The licence tier is an opaque name with a well-known Community baseline
Status: accepted
SupersededBy:
StatedIn: "unit/document/90-decisions § 2026-08-29 — The licence tier is an opaque name with a well-known Community baseline"

## Claim
Context — `10-design.md` states that Platform is not a licensor and that accepted keys are supplied by the consuming product, while the brief and `tenancy-billing-licensing.md` both name Community, Pro and Enterprise and require a fresh installation with no verified claims to continue at Community. The design does not say which of those two facts decides the type.

Chosen — `LicenceTier` is an opaque stable name struct with one well-known value, `Community`, standing to the tier vocabulary exactly as `TenantId.Implicit` stands to tenants and `Principal.Anonymous` to principals — a real value rather than the absence of one. Entitlement is asked by `FeatureName`, never by tier, so the tier is recorded and displayed and never branched on by Platform.

Rejected — **an enum of Community, Pro and Enterprise**, which is the vocabulary both documents use and which makes the well-known baseline free; rejected because that vocabulary is the Automator's licence model rather than Platform's, a framework type carrying a product's price tiers is the same mistake as a framework type meaning "an organization", and a second consumer with a different ladder would face a framework change to sell anything.

Reversibility — cheap. Narrowing an opaque name to a closed set later is additive for anyone already using the three names; widening a shipped enum is not.
