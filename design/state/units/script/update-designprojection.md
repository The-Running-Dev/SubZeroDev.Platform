# unit/script/update-designprojection
Kind: script
Status: active
Anchor: tools/Update-DesignProjection.ps1
Consumes:
Exposes:
Binds:
Live:
Questions:
Work:
Evidence:

## Owns
The projector: reads `design/state/` via `Read-DesignState.ps1` and renders every projection —
`units`, `bound-by`, `consumers`, `decision-affects`, `question-affects`, `outstanding`, and
`invariants` — into the marked regions of their target documents. Writes only between a region's
markers, never repairs a malformed or absent one, and is idempotent and order-independent:
every projection is computed from records alone, never from a document's current content.
