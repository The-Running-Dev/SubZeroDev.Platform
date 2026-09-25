# unit/script/read-designstate
Kind: script
Status: active
Anchor: tools/Read-DesignState.ps1
Consumes:
Exposes:
Binds:
Live:
Questions:
Work:
Evidence:

## Owns
Reads `design/state/` into a graph — one record per file, parsed as constrained Markdown.
Never throws, never writes, never skips a line; a line matching no production is a reported
parse failure, not a dropped one. An absent `design/state/` is an empty graph, not an error.
