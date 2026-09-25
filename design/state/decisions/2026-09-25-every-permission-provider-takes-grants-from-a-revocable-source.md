# decision/2026-09-25-every-permission-provider-takes-grants-from-a-revocable-source
Date: 2026-09-25
Anchor: 2026-09-25 — Every permission provider takes grants from a source revocable before the credential expires
Status: accepted
SupersededBy:
StatedIn: "unit/document/10-design § 3. Authorization — names, providers, decisions"

## Claim
Context — `20-contract.md` § Unresolved item 3 (#92): may a consumer-registered `IPermissionProvider` derive grants from token claims? A claims-derived grant satisfies I-A10 literally and defeats it, since the token carries the grant until the issuer-chosen expiry. `Principal.Claims` is public and the raw authentication result is reachable through DI, so Platform cannot stop a consumer's provider reading claims.

Chosen — every provider, Platform's and a consumer's, takes grants from a source revocable while the caller's credential is valid; claims and the raw authentication result are never a grant source. Structural for Platform's providers through I-I6 and a revoke-then-deny test; a contract obligation for a consumer's, with the same test shipped in `Platform.Testing` as a harness. Issuer-managed roles reach the evaluator only through a store the consumer mirrors them into.

Rejected — removing `Claims` from `Principal`; claims-derived grants with a declared latency; leaving it to the consumer.

Reversibility — cheap to relax, expensive to tighten.
