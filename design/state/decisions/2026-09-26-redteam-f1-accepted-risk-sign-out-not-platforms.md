# decision/2026-09-26-redteam-f1-accepted-risk-sign-out-not-platforms
Date: 2026-09-26
Anchor: 2026-09-26 — Red-team F1 is an accepted risk: #94's sign-out promise is not Platform's to deliver
Status: accepted
SupersededBy:
StatedIn: "unit/document/90-decisions § 2026-09-26 — Red-team F1 is an accepted risk: #94's sign-out promise is not Platform's to deliver"

## Claim
Context — red-team F1 (`design/redteam/2026-09-25-10-design.md`): under the resource-server decision a vendor configuration package supplies bearer-validation settings only, so #94's non-standard sign-out stays with each client and its sign-out proof cannot run against Platform.

Chosen — accepted risk. Platform stays a resource server. #94's hook-per-sign-in-method, sign-out-proof and hook-documentation criteria are not delivered by D5; vendor sign-in and sign-out quirks are handled once per client; contract Unresolved item 4 is settled for the validation half only.

Rejected — defect, since no higher-precedence source is contradicted; not sustained, since the consequence was real and unrecorded.

Reversibility — cheap; a backend-for-frontend can be added later as a module that is an ordinary client of Platform.
