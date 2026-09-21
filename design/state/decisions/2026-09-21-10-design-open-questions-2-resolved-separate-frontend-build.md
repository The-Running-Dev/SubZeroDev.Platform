# decision/2026-09-21-10-design-open-questions-2-resolved-separate-frontend-build
Date: 2026-09-21
Anchor: 2026-09-21 — `10-design.md` § Open questions 2 resolved: the shell is a separate front-end build
Status: accepted
SupersededBy:
StatedIn: "unit/document/90-decisions § 2026-09-21 — `10-design.md` § Open questions 2 resolved: the shell is a separate front-end build"

## Claim
Context — the question was open since `/design`; `/align` surfaced it with no slice depending on it.

Chosen — a separate front-end build, served as static assets by whichever host chooses to, with no server-side rendering and no .NET package the backend could reference. Q2 and the already-decided Q4 are struck through in `10-design.md`, per its rule that resolved questions keep their number.

Rejected — a .NET-hosted UI shipped as a package, because the reference it needs is the one ADR-006 rule 1 exists to prevent, and every admin-only convenience endpoint it acquires is a step away from replaceable.

Reversibility — cheap while no shell exists; the choice is not yet built.
