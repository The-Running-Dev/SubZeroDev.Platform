# decision/2026-09-02-isharedreadscopefactory-gains-isopenfor-tentity
Date: 2026-09-02
Anchor: 2026-09-02 — `ISharedReadScopeFactory` gains `IsOpenFor<TEntity>()`
Status: accepted
SupersededBy:
StatedIn: "unit/document/90-decisions § 2026-09-02 — `ISharedReadScopeFactory` gains `IsOpenFor<TEntity>()`"

## Claim
Context — `20-contract.md` § *Public surface* 7 declared `ISharedReadScopeFactory` with `Open<TEntity>()` only, and invariant I-T2 named its enforcement "code — model-build filter" — EF Core vocabulary. But `SubZeroDev.Platform.Persistence` has no EF Core dependency and imposes no repository or ORM (`design/d3/90-decisions.md`, 2026-08-03): a consumer's own data-access code enlists against the ambient connection and writes its own queries. With only `Open<TEntity>()` declared, nothing public let a consumer's own query code ask whether a scope is currently open, so I-T2 was unimplementable by anyone outside this repository's own test project.

Chosen — **add `bool IsOpenFor<TEntity>() where TEntity : class, IShareable` to the interface.** It reads the same ambient state `Open` sets, on the same terms `IAmbientTransactionAccessor.Current` already exposes the ambient transaction to a consumer's own data-access code — an established precedent, not a new pattern.

Rejected — **leaving the interface as declared and treating "model-build filter" as internal-only, exercised only by Platform's own tests.** Technically implementable, but it would mean I-T2 holds inside this repository's test suite and nowhere else — a consumer adopting Platform could not honour it at all, which defeats the point of declaring it a public invariant.

Reversibility — cheap. Additive; nothing in the tree references `ISharedReadScopeFactory` yet.
