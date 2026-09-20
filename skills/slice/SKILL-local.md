# `/slice` — this repository's companion

The core is the installed kit's `skills/slice/SKILL.md`. This file adds only what is specific to
this repository, under the categories that core declares it may override.

## vocabulary

**A slice's issue title is qualified by the effort's tag** — match on a title beginning
`<Effort>-S<n> —`, the same way `/track` does: `D5-S3 —`, not `S3 —`.

**The effort tag is not decoration.** A tracker outlives the effort that filled it, and this
repository already holds two retired slice sets both numbered from S1; matching on the bare number
pairs a live slice with a closed issue from a different effort and reads it as done.

The tag is the parenthesised id in the slices document's own title (`# Slices — commercial (D5)`).
Where a document carries none, the unqualified `S<n> —` form is still correct.
