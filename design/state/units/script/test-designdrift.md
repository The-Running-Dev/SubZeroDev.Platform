# unit/script/test-designdrift
Kind: script
Status: active
Anchor: tools/Test-DesignDrift.ps1
Consumes:
Exposes:
Binds:
Live:
Questions:
Work:
Evidence: tools/Test-DesignDrift.Tests.ps1

## Owns
Reports drift between `design/30-slices.md` and the GitHub tracker without changing either:
criterion ids compared against each slice's issue checkboxes, and pin ancestry compared against
`git merge-base --is-ancestor`. Read-only on both sides; which side is wrong is the user's call.
Exit codes: 0 no drift, 1 drift found, 2 could not evaluate, 2 takes precedence over 1.
