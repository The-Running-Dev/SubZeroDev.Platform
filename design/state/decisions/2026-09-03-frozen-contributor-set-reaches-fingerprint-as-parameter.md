# decision/2026-09-03-frozen-contributor-set-reaches-fingerprint-as-parameter
Date: 2026-09-03
Anchor: 2026-09-03 — The frozen contributor set reaches the fingerprint as a parameter, not as a projected option
Status: accepted
SupersededBy:
StatedIn: "unit/document/90-decisions § 2026-09-03 — The frozen contributor set reaches the fingerprint as a parameter, not as a projected option"

## Claim
Context — `20-contract.md` § *Unresolved* item 1 (numbered 2 in `30-slices.md`) left two defensible readings open, and named S8 as where the answer is needed because I-C4 lands there. The contributor set must be inside the settings-fingerprint input so two instances that disagree about who may grant surface through `platform.settings-fingerprint` rather than through divergent behaviour — but the set is a *registration*, and `ISettingsFingerprint.Compute` took `PlatformOptions` alone.

Chosen — **`Compute` gains a second parameter**, `IReadOnlyCollection<EntitlementContributorName>`. The names are sorted ordinally and emitted one length-prefixed entry each under `EntitlementContributors[NNNN]`, so registration order is not a disagreement — the evaluator takes a union, which is order-independent, and a fingerprint that flapped on registration order would be a permanent false mismatch training an operator to ignore the one check that catches a real one. The format version moved `szdfp2` → `szdfp3` in the same commit. Persistence's two call sites — the heartbeat that writes a host's own row and the readiness check that compares it against peers — both go through one helper, so the written value and the compared value cannot be computed from different inputs.

Rejected — **projecting the frozen set onto a `[Fingerprinted]` property of `PlatformOptions` at startup**, which changes no published signature and was the cheaper option on paper; rejected because `PlatformOptions` is the operator-facing record of what a deployment was configured with, and a property no operator can set — one that appears after startup, from a source the configuration binder never saw — makes the type lie about what it is. The cost of the chosen option is a breaking change to a published 0.x surface, which is the class of change the brief calls those packages explicitly unstable for; paying it after a consumer ships would cost the consumer instead. Also rejected: **joining the names into one entry with a separator**, which needs a character reserved out of a contributor name — a constraint `EntitlementContributorName` does not impose and this decision has no business adding.

Reversibility — expensive. Both the signature and the fingerprint's stored values change; reversing it is a second breaking change plus another format version.
