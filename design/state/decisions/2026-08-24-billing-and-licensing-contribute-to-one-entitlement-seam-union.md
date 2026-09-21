# decision/2026-08-24-billing-and-licensing-contribute-to-one-entitlement-seam-union
Date: 2026-08-24
Anchor: 2026-08-24 — Billing and Licensing contribute to one entitlement seam, resolved as a union
Status: accepted
SupersededBy:
StatedIn: "unit/document/90-decisions § 2026-08-24 — Billing and Licensing contribute to one entitlement seam, resolved as a union"

## Claim
Context — the brief requires product code to consume entitlements and never subscription state, and `tenancy-billing-licensing.md` forbids any code path outside the billing module branching on it. Licensing answers the same "may this feature be used" question from a different source, and both may be absent.

Chosen — one `FeatureName` question. Billing and Licensing register as entitlement contributors and neither is queryable directly. Resolution is a **union** — any contributor granting a feature grants it — and the decision records which contributor did. The contributor set joins the settings fingerprint, so two instances that disagree about who may grant are visible through the existing `platform.settings-fingerprint` check rather than by their behaviour diverging.

Rejected — **two queries**, one for subscription state and one for licence tier, which is the shape both capabilities' own documents describe and needs no new concept; rejected because it reintroduces under a different name exactly the branch the brief forbids — every gated feature would ask which commercial model is in play — and makes the self-hosted and operated shapes structurally different at every call site rather than at composition. **Intersection rather than union**, safer-sounding because every contributor must agree; rejected because installing Licensing into an operated deployment would then silently revoke subscription entitlements, which presents as a working system denying a feature the customer paid for and is a failure nobody would look for.

Reversibility — expensive. It is the public shape product code is written against.
