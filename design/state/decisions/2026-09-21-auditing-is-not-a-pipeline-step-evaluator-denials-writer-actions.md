# decision/2026-09-21-auditing-is-not-a-pipeline-step-evaluator-denials-writer-actions
Date: 2026-09-21
Anchor: 2026-09-21 — Auditing is not a pipeline step: the evaluator audits denials, the writer audits actions
Status: accepted
SupersededBy:
StatedIn: "unit/document/90-decisions § 2026-09-21 — Auditing is not a pipeline step: the evaluator audits denials, the writer audits actions"

## Claim
Context — `10-design.md` and `20-contract.md` § *Public surface* 11 listed "audit" as a final step, and I-A8 said the evaluator audits every decision once. The code audits a denial in the evaluator and an allowed action in the writer performing it, inside that action's transaction.

Chosen — amend the doc to the code, in § 11, the provider rule under *Types* 2, and I-A8.

Rejected — a trailing pipeline audit step, because a record written after the transaction commits can be lost while the action stands, and one written by the evaluator on allow records a decision rather than the action that followed.

Reversibility — cheap; the change is to the documents.
