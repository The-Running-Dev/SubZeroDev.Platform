# decision/2026-09-19-d5-package-delivery-uses-approved-lockstep-version
Date: 2026-09-19
Anchor: 2026-09-19 — D5 package delivery uses the approved lockstep version
Status: accepted
SupersededBy:
StatedIn: "unit/document/90-decisions § 2026-09-19 — D5 package delivery uses the approved lockstep version"

## Claim
Context — S18 needed the answer to `10-design.md` § Open questions 4 before producing package artifacts. Ben approved lockstep on 2026-09-17, recorded on issue #186.

Chosen — the framework and all D5 modules use one shared, explicitly unstable 0.x version. CI packs and restores the artifacts without publishing to a public registry. The shared version prefix remains owned by `src/Directory.Build.props`; CI supplies a common prerelease version per run.

Rejected — independent module versions during D5, because there is no independently running consumer to justify the compatibility matrix. Revisit when a module has such a consumer.

Reversibility — cheap before external adoption; independent releases later require a compatibility policy and its tests.
