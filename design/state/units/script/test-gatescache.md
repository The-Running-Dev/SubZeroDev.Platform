# unit/script/test-gatescache
Kind: script
Status: active
Anchor: tools/Test-GatesCache.ps1
Consumes:
Exposes:
Binds:
Live:
Questions:
Work:
Evidence:

## Owns
Reads or writes `.claude/gates.json` — a cache of the repository's discovered gates, keyed to a
hash of the files whose presence or content determines the gate list. Does not discover gates
itself; that stays `/check`'s judgement call. Reports Fresh, Stale, or Missing against the
current manifest hash, and persists a new cache under `-Write` once `/check` has re-discovered.
