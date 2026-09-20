# `/track` — this repository's companion

The core is the installed kit's `skills/track/SKILL.md`. This file adds only what is specific to
this repository, under the categories that core declares it may override.

## vocabulary

**A slice's issue title is qualified by the effort's tag** — `D5-S3 — <name>`, not `S3 — <name>`.
The tag is the parenthesised id in the slices document's own title (`# Slices — commercial (D5)`).

Wherever the core says to search for, open, or match an issue on a title beginning `S<n> —`, read
that as `<Effort>-S<n> —` here. A closed issue still means the slice is done — the tag changes what
counts as a match, nothing else about the procedure.

One tracker outlives the effort that filled it, and slice numbering restarts at S1 with each. This
repository already holds two retired slice sets both numbered from S1, so without the tag a live S3
matches a retired effort's closed S3 and is silently skipped as done.

Where a slices document carries no tag, the unqualified `S<n> —` form is correct, and it is what
`tools/Test-DesignDrift.ps1` falls back to.
