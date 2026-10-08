# decision/2026-10-08-identity-owns-an-optional-per-host-account-store-keyed-on-as-a-second-module-type
Date: 2026-10-08
Anchor: 2026-10-08 — #93: Identity owns an optional, per-host account store keyed on (issuer, subject), as a second module type
Status: accepted
SupersededBy:
StatedIn: "unit/document/90-decisions § 2026-10-08 — #93: Identity owns an optional, per-host account store keyed on (issuer, subject), as a second module type"

## Claim
Context — [#93](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/93) boxes 3–4. Ben ruled 2026-10-08: "Identity owns an optional account store keyed on (provider, subject), with explicit authenticated linking." Brief decision 6 makes it per host; decision 5 (no linking across products) and #98 (a principal is not an account) stand; ADR-009 decision 4 is amended in place.

Chosen — `IdentityAccountsModule` in the Identity package, depending on `IdentityModule`; only it registers the store, its migration `0001_create_accounts` and `IAccountApi`, and Identity gains a Persistence reference. Two tables, `identity_account(id, created_at)` and `identity_account_link(issuer, subject, account, linked_at)` with primary key (issuer, subject), no email, profile, claim or tenant column. An account id is 128 random bits as 32 lowercase hex, opaque. A linked identity authenticates as (`platform.account`, id), kind `Account`, mapped inside Identity's own providers with one uncached store read; a store failure is `ProviderFailed`, never the raw pair. Creation is explicit (`CreateAccountAsync`, Required audit); the upstream-proxy `Delegated` principal is never mapped.

Rejected — a new package; a flag on `IdentityModule` (a user table as a configuration value on SkyNet HR's host); identities serialised on the account row (uniqueness by check, not constraint); an email column; a ULID or v7 GUID (discloses creation time); an account field on `Principal` (a framework change naming accounts); the first identity's pair as the account id; a configured account issuer; just-in-time creation (an unauditable write before any scope); an `IPrincipalMapper` framework seam (fails seam admission); tenant-owned accounts; a new `AuthenticationError` variant.

Reversibility — cheap for the placement; expensive for the principal shape and the schema, which every membership and audit row stores.
