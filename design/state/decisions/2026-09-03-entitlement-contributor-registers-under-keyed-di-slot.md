# decision/2026-09-03-entitlement-contributor-registers-under-keyed-di-slot
Date: 2026-09-03
Anchor: 2026-09-03 — An entitlement contributor registers under a keyed DI slot, never the plain interface
Status: accepted
SupersededBy:
StatedIn: "unit/document/90-decisions § 2026-09-03 — An entitlement contributor registers under a keyed DI slot, never the plain interface"

## Claim
Context — S7.7 requires that no caller can resolve an entitlement contributor from the container — the evaluator must be the only public entry — while the Community baseline still has to be collected and frozen into `IEntitlementContributorRegistry` at startup, the same way S4's permission providers and S5's tenant resolvers are. The contract does not say how that collection happens without also making `IEntitlementContributor` resolvable.

Chosen — the Community baseline registers as a keyed singleton (`services.AddKeyedSingleton<IEntitlementContributor, CommunityEntitlementContributor>(EntitlementContributorRegistration.ServiceKey)`, the key an internal `const string`), and `PlatformRegistryStartup` collects contributors via `[FromKeyedServices(...)] IEnumerable<IEntitlementContributor>`. Ordinary unkeyed resolution — `GetService<IEntitlementContributor>()`, `GetServices<IEntitlementContributor>()` — finds nothing, which is what S7.7's test asserts directly. The feature set the baseline grants comes from an internal `CommunityBaselineOptions` record, empty by default and overridable by registering one before `AddPlatformWebHost`/`AddPlatformWorkerHost`, on the same override-before-compose precedent `IClock` already establishes.

Rejected — **the `IEnumerable<IEntitlementContributor>` + `TryAddEnumerable` pattern S4 and S5 use**, because that registers contributors under the plain interface and defeats S7.7 outright. **A constructor-injected concrete-type singleton with no keyed slot at all**, which would satisfy S7.7 for the Community baseline alone but gives Billing's and Licensing's contributors (S11, S12) no channel to register through without either reopening this question or inventing their own — the keyed slot is one mechanism both this slice and those two can share. **A public `[Fingerprinted]` `PlatformOptions` property for the baseline's feature set**, matching `CompositionProfile`'s shape; rejected because the feature set joining the settings fingerprint is I-C4, explicitly S8's to decide, and a public contract property is a new declaration `design/20-contract.md` does not carry — the internal record can be widened or replaced without a breaking change once S8 settles that question.

Reversibility — cheap. The keyed slot and `CommunityBaselineOptions` are both internal; nothing external depends on their shape, and S8, S11 and S12 are free to settle the contributor registration channel and the fingerprint input differently.
