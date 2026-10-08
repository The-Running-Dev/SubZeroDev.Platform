# decision/2026-10-08-adventures-becomes-a-hosted-consumer-its-identity-stays-evidence
Date: 2026-10-08
Anchor: 2026-10-08 — #97, #100: Adventures becomes a hosted consumer; its identity stays evidence (supersedes g1 2026-08-09)
Status: accepted
SupersededBy:
StatedIn: "unit/document/90-decisions § 2026-10-08 — #97, #100: Adventures becomes a hosted consumer; its identity stays evidence (supersedes g1 2026-08-09)"

## Claim
Context — [#97](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/97). D3's done-criterion, a product running on Platform by the standard registration call, has been met only by Platform's own sample. The g1 entry of 2026-08-09 made the Adventures POC a reference that G2 and G3 read, not a consumer. [#100](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/100) asks whether Adventures' identity replacement makes an Identity package.

Chosen — Adventures becomes a hosted consumer that restores the packages from the feed. The adoption work happens in the Adventures repository. Each gap it finds is filed here as D4 extraction evidence, not built speculatively. Its identity replacement stays evidence only, and no Identity package is created from it. `implementation-plan.md` §D3 states this. Ben ruled 2026-10-08.

Rejected — keeping Adventures as a reference only, which leaves D3 permanently unproven by an outside consumer; building its identity model into Platform, which is one consumer and the extraction guard refuses it.

Reversibility — cheap — the adoption is the consumer's work, and Platform's surface does not change for it. Supersedes: `g1/90-decisions.md` 2026-08-09 — "The Adventures POC is a reference for G2 and G3, not a source this effort copies from". The g1 log is a closed record and is not edited.
