# decision/2026-09-29-untested-invariants-held-by-instruction
Date: 2026-09-29
Anchor: 2026-09-29 — Twenty-one invariants without a whole-statement test are held by instruction, not code
Status: accepted
SupersededBy:
StatedIn: "unit/document/20-contract § Invariants", "unit/document/90-decisions § 2026-09-29 — Twenty-one invariants without a whole-statement test are held by instruction, not code"

## Claim
Context — `/align`: seventy-three invariants carried `Enforcement: code` with no Evidence, copied from prose by the #245 bootstrap. Tests evidence the whole statement for fifty-two, part of it for nineteen, and none for I-B6 and I-A11.

Chosen — the fifty-two keep `code` and name their tests. The other twenty-one become `instruction` and drop their "Enforced by code" clause. The partly tested ones name those tests as Evidence. Each flips back to `code` in the change that adds the test for the rest of its statement.

Rejected — `code` citing partial tests, which passes the check on rows nothing fully proves; `code, instruction`, which hides which part lacks a test.

Reversibility — cheap; one field per row.
