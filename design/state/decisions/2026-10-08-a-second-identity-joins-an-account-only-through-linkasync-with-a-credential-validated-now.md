# decision/2026-10-08-a-second-identity-joins-an-account-only-through-linkasync-with-a-credential-validated-now
Date: 2026-10-08
Anchor: 2026-10-08 — #93: a second identity joins an account only through `LinkAsync`, with a credential validated now and issued within five minutes
Status: accepted
SupersededBy:
StatedIn: "unit/document/90-decisions § 2026-10-08 — #93: a second identity joins an account only through `LinkAsync`, with a credential validated now and issued within five minutes"

## Claim
Context — [#93](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/93). "Explicit authenticated linking" means the caller is authenticated as the account and completes sign-in with the second identity in the same flow, never by email or any other claim.

Chosen — the host passes the second sign-in's bearer token (for example from the sign-in module's token endpoint, the module unchanged) to `LinkAsync` on a request authenticated as the account. Identity validates it with its own providers and requires `iat` no more than five minutes before the call, `ClockTolerance` either side; no `iat` is refused; the window is fixed. Any identity except the last may be unlinked under a row lock; an identity on another account answers `IdentityNotLinked` like an unknown one. Create, link and unlink each write one Required audit record; refusals are logged, not audited. No permission is declared.

Rejected — a sign-in callback hook (its subject comes from an unvalidated id token and can differ from the access token's); any currently valid token (a stolen token could attach permanently); `auth_time` (access tokens usually lack it); a link nonce threaded through the sign-in module (it would know about accounts); unlinking the last identity; re-authentication for unlink; a "manage own account" permission every principal holds.

Reversibility — cheap. A silently refreshed token also passes the freshness check; if "completes sign-in" is read as interactive, this is the call to tighten.
