# decision/2026-11-01-d5-s-set-archives-to-design-d5-the-root-s-five-document-units-stay-as-role-slots
Date: 2026-11-01
Anchor: 2026-11-01 — D5's set archives to `design/d5/`; the root's five document units stay as role slots
Status: accepted
SupersededBy:
StatedIn: "unit/document/d5-90-decisions § 2026-11-01 — D5's set archives to `design/d5/`; the root's five document units stay as role slots"

## Claim
Context — D5 is complete, and the 2026-08-21 convention archives a completed set on completion, so the root does not advertise a finished effort as live before G3's brief exists. The design-state model anchors each `unit/document/*` record at one path, and 76 decision records cite the five root units as their `StatedIn`.

Chosen — the five root units `unit/document/{00-brief,10-design,20-contract,30-slices,90-decisions}` stay, as role slots: the place the active effort's document of that kind lives, empty between efforts. Five new units `unit/document/d5-*` are anchored at `design/d5/<doc>.md`, and every `StatedIn` that named a D5 document is repointed to its `d5-` unit. The model has no notion of an empty role, so the root slots stay `active` and their Owns text says no file exists at the anchor between efforts. Explicit inbound paths are repointed to `design/d5/`; bare-filename citations are left as they are. Ben ruled 2026-11-01.

Rejected — re-anchoring the five existing units to `design/d5/` (the next effort's `/brief` would have no slot to occupy); holding the archive until G3's brief is written (the root would advertise a finished effort as live for an arbitrary window).

Reversibility — cheap.
