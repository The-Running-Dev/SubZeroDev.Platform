# I-T2
Kind: invariant
Status: active
Anchor: I-T2
Enforcement: code
Consumes:
Exposes:
Binds:
Live:
Archival:
Questions:
Work:
Evidence:

## Statement
Outside a shared-read scope the query filter is `tenant equals current`, unconditionally, for shareable and non-shareable types alike (Owner: Persistence.) Enforced by code — the consumer's own query code, consulting `ISharedReadScopeFactory.IsOpenFor<TEntity>()` at model build.
