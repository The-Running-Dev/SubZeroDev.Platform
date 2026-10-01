# decision/2026-10-01-unclaimed-credential-ends-chain-rejected
Date: 2026-10-01
Anchor: 2026-10-01 — A credential no provider claims ends the chain rejected, not anonymous
Status: accepted
SupersededBy:
StatedIn: "unit/document/20-contract § 2. Authentication seam — `SubZeroDev.Platform.Abstractions`, registered in `SubZeroDev.Platform.Core`", "unit/document/20-contract § 10. Modules", "unit/document/20-contract § 1. `AuthenticationError` — `SubZeroDev.Platform.Abstractions`", "unit/document/90-decisions § 2026-10-01 — A credential no provider claims ends the chain rejected, not anonymous"

## Claim
Context — `/spec` #94 closing review. A provider answers `Principal.Anonymous` for a well-formed bearer token from an issuer it does not trust, so a token every provider declines ends the chain `Anonymous`. `10-design.md` Control flow path 1 step 1 says a bad token is rejected at the transport and only an absent one continues as `Anonymous`. Ruled by the owner: the chain end rejects.

Chosen — a provider that sees a credential of its kind it does not claim answers the new `AuthenticationError.CredentialNotClaimed`. The chain treats it as a pass and moves on. If no provider establishes a principal and none rejects, the chain answers `CredentialRejected`, naming the first provider in registry order that answered `CredentialNotClaimed`. `CredentialNotClaimed` never leaves the chain. The test-grade provider and the generic bearer path both answer this way. `/plan` slices the code change.

Rejected — the chain reading the `Authorization` header itself, because Core would then know every credential format; changing `IAuthenticationProvider`'s return type to a three-way result, which breaks every provider's signature for one case an error variant carries; keeping `Anonymous`, which lets a token from an untrusted issuer succeed at being ignored and contradicts path 1.

Reversibility — cheap until the slice lands; moderate after, since a host's callers see 401 where they saw an anonymous answer.
