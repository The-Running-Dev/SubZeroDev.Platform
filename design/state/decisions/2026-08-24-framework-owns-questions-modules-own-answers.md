# decision/2026-08-24-framework-owns-questions-modules-own-answers
Date: 2026-08-24
Anchor: 2026-08-24 — The framework owns the questions; modules own the answers
Status: accepted
SupersededBy:
StatedIn: "unit/document/90-decisions § 2026-08-24 — The framework owns the questions; modules own the answers"

## Claim
Context — `/design` for D5. Nine capabilities that are coupled in use — Authorization must audit, Mcp must authorize, Organizations must provision tenants, Billing and Licensing must both answer one entitlement question — under ADR-006 rule 1 (no framework package references a module) and rule 2 (no module references another). Those two rules together admit very few arrangements, and most obvious designs violate one of them on the first coupling.

Chosen — every commercial capability splits into a **decision seam** in the framework and a **policy store** in an opt-in module. A seam is a question, a contract and a composition point, with a default answer that is correct with every module absent. Modules contribute answers to seams and never reach each other, which makes rule 2 vacuous by construction rather than observed by discipline. A seam enters the framework only when a framework package must consume it on a path that exists with every module absent, or two independent modules must consume it and cannot reach each other — the seam-admission test, recorded because the pressure runs one way and every future capability will have a reason why its contract would be convenient in Abstractions.

Rejected — **capabilities as whole modules**, the extraction guard's usual answer and the reading ADR-006's own examples support; rejected because Authorization audits from inside a framework package, which makes Audit-as-a-module a rule 1 build failure, and Mcp audits from a module, which makes it a rule 2 violation — there is no arrangement of fully-modular capabilities that satisfies both rules while the framework itself has anything to record, and it does. **A third package tier for optional infrastructure**, which would let these rules differ per tier; rejected because nothing in D5 needs the difference and a tier introduced before a rule needs it will acquire one. **Putting the whole of each capability in the framework**, one cadence and no matrix; rejected because the brief requires Identity, Organizations, Billing and Licensing to be absent from the local composition — not registered checks that always pass — and a mandatory package cannot be absent.

Reversibility — expensive. It is the shape every D5 contract is written against.
