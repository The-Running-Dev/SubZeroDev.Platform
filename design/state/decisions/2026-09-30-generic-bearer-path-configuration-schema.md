# decision/2026-09-30-generic-bearer-path-configuration-schema
Date: 2026-09-30
Anchor: 2026-09-30 — The generic bearer path's configuration schema: one section per provider, fixed keys, bounded defaults
Status: accepted
SupersededBy:
StatedIn: "unit/document/20-contract § 12. The generic bearer path — `SubZeroDev.Platform.Identity`", "unit/document/20-contract § 10. Modules", "unit/document/90-decisions § 2026-09-30 — The generic bearer path's configuration schema: one section per provider, fixed keys, bounded defaults"

## Claim
Context — `/spec` #94: `10-design.md` Data model § 10 names the generic path's settings and makes their configuration schema public contract, leaving key names, pattern syntax, key formats, defaults, bounds and refresh mechanics to the contract.

Chosen — each child section `Platform:Identity:Bearer:<name>` is one provider, with `<name>` as its name. The keys are `Issuer`/`IssuerPattern`, `Discovery`/`SigningKeys`, `Audiences`, `Algorithms`, `ClockTolerance`, `SubjectClaim`, `DisplayNameClaim` and `KeyRefreshInterval`. An issuer pattern carries `{tenantid}` exactly once, matching one or more non-`/` characters. `Discovery` must be `https`. `SigningKeys` is an inline public JSON Web Key Set. Algorithms come from RS/PS/ES, default `RS256`. `ClockTolerance` defaults to one minute, capped at five. `KeyRefreshInterval` defaults to five minutes, between 30 seconds and one day. Every fetch has a fixed 30-second timeout. The discovery `issuer` must match ordinally. Background work is named `platform.identity.key-refresh:<name>` and readiness `platform.identity.key-set:<name>`. A token with no key id is tried against the cached keys and never fetches. Registration is the module plus configuration. A vendor method takes the provider name.

Rejected — a regular-expression issuer pattern (it silently over-matches); a tenant list (configuration no issuer publishes); JWK members as configuration keys (collides with the unrecognised-key rule); `http` discovery (the test-grade provider serves development); the library's five-minute and twelve-hour defaults (a failed first fetch would leave the host not-ready for twelve hours); a configurable fetch timeout; a registration call per issuer (makes vendor packages chain off Identity, Alternatives § 12).

Reversibility — moderate: keys are public once a vendor package writes them; they can be added, never withdrawn, and a default never moves in the accepting direction.
