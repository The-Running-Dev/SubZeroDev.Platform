---
sidebar_position: 9
sidebar_label: ADR-009 Identity Package
---

# ADR-009: Identity Is a Framework Seam Plus a Module That Owns No Users

## Status

Accepted

## Context

Every consumer answers "who is asking?" differently, and until D5 each answered it alone. The
Automator has users, API keys and service accounts. Game Engine as a Service has player accounts.
BarStrad has a QR-link table and never an account. SkyNet HR trusts an operator asserted by an
upstream reverse proxy ([Platform Identity](../platform-identity.md) §4). Platform could not stamp an
actor on a write, name one in an audit record, or evaluate a permission without that answer, and
three of the four answers are not accounts.

Two earlier choices bound this one. On 2026-08-10 a proposed ADR naming an identity substrate was
withdrawn before it was written, and Keycloak, authentik and Supabase Auth were rejected as a
platform default (`design/g1/90-decisions.md`). The same day, the Automator and Game Engine as a
Service were recorded as not sharing identity, and brief decision 5 of the D5 brief keeps it so.

Issue [#90](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/90) proposed an Identity
package before D5 was designed. That proposal included a Platform-owned user store behind
`UseIdentity()`. The approved D5 brief then made a shared user directory, shared identity storage, and
choosing or operating an identity substrate non-goals. The D5 design placed Identity under
[ADR-006](ADR-006-application-modules.md) in `design/10-design.md` § *Module boundaries*, and
D5-S2 and D5-S9 built it. **This ADR records that placement and its costs as the architectural
decision.** It ratifies the design as built; it does not introduce new behaviour.

## Decision

**Identity is split in two. The framework owns the question "who is the principal?". An optional
module owns the answers that come from authenticating a credential. Neither owns a user.**

1. **The framework owns the principal contract.** `SubZeroDev.Platform.Abstractions` declares
   `Principal`, `PrincipalId`, `PrincipalKind`, the ambient principal, and the transport-authentication
   seam `IAuthenticationProvider`. Core holds the provider registry and the chain that runs it. Every
   Platform host has a principal whether or not it authenticates anyone, because Persistence stamps
   actors, Audit names them and Authorization evaluates them.
2. **The principal is total.** `Principal` is never null. `Anonymous` is a kind of principal, not the
   absence of one. There are four kinds: `Anonymous`, `Account`, `Delegated` and `System`. `Delegated`
   is a first-class kind, not an `Account` with missing fields.
3. **A principal's id is an opaque pair.** `PrincipalId` is (issuer, subject), compared ordinally.
   Neither half is parsed, normalised or case-folded. Two stores minting the same subject are two
   principals, not a collision.
4. **`SubZeroDev.Platform.Identity` is a module.** It holds authentication providers and the mapping
   from an authentication result to a principal. It owns no entity type, no context, no migration, no
   directory and no user table (invariant I-I4). Registering it adds no provider on the consumer's
   behalf.
5. **Platform chooses no identity provider.** Each provider trusts an issuer the consumer names and
   operates. A deployment trusting several issuers registers one provider per issuer.
6. **The composition profile decides whether authentication is required.** An `Operated` host with no
   authentication provider fails startup (I-C1). A `Local` host with one fails startup (I-C3). The
   local host has no reference to Identity (I-C5).

## Consequences

- One actor model serves all four consumers, including the two whose principals are not accounts.
  Stamping, audit and authorization read `PrincipalId` and never a claim.
- **Every consumer brings its own identity provider and its own user store.** Platform offers no
  sign-up, no password handling, no account lifecycle and no user administration. A consumer wanting
  those builds them or adopts a substrate behind the seam. This is the direct cost of the brief's
  non-goals, and it is the largest one.
- The framework carries the principal types and the authentication seam whether or not a host
  authenticates anyone. A host that authenticates no one still pays for those types, though not for a
  module.
- The total principal replaced the 0.x `ClaimsPrincipal?` on `ICurrentPrincipal.Current` and
  `IOperationScopeFactory.Begin`. This was a breaking change to published 0.x packages.
- The opaque pair means an actor is stored as two columns everywhere it must survive storage. The
  single-string `ToString()` form is for display and is never split.
- **The provider shipped in the module is test-grade.** `JwtBearerAuthenticationProvider` verifies
  HMAC-SHA256 compact tokens with no algorithm negotiation and no audience, expiry or key-rotation
  handling. Its own documentation says a real deployment's issuer is the consumer's to choose and
  operate. A production deployment needs a provider this module does not yet contain.
- Supabase, Keycloak or any other substrate remains a deployment choice. Adopting one later adds a
  provider; it does not change the principal contract.

## Alternatives considered

**A Platform-owned user store behind `UseIdentity()`.** This was #90's original proposal: Platform
owns the user schema and migrations, and consumers opt in. Rejected because the approved D5 brief
makes a shared user directory and shared identity storage non-goals, and because a store would give
Platform an opinion about what an account is. BarStrad and SkyNet HR have no account at all.

**A substrate as the platform default.** Keycloak, authentik or Supabase Auth, chosen once for every
consumer. Rejected on 2026-08-10 and not reopened. Choosing or operating an identity substrate is a
brief non-goal, and the seam lets a consumer choose one without Platform depending on it.

**A single-string principal id.** Simpler to store and compare. Rejected because two stores minting
the same subject would collide. Brief decision 5 keeps the Automator's and Game Engine's stores
separate. Adding the issuer later would be a data migration, which is why the pair was decided before
any consumer shipped.

**A nullable principal.** Keeping `ClaimsPrincipal?` avoids the breaking change. Rejected because a
null actor cannot be told apart from an actor that was never resolved. The brief requires allowed,
denied and failed actions each to name an actor.

**Identity entirely in the framework.** No module, with providers in Core. Rejected because a
provider trusts a specific issuer and credential shape, which is a deployment choice. The local host
must not reference it (I-C5), and ADR-006 rule 1 makes that structural only if the providers sit in a
module.

**One identity shared across products.** One principal store, with federation or account linking.
Rejected by brief decision 5 and by the brief's non-goals.

## Not decided here

These questions were raised in #90 or near it. **This ADR leaves each one open**, and none is implied
by the decision above.

- Token exchange between processes and Platform publishing a key set (RFC 8693, JWKS).
- Account lifecycle events and whether they travel through the outbox.
- An API-key authentication scheme, and administration commands for principals.
- A production-grade bearer provider, and a per-vendor provider escape hatch
  ([#94](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/94)).
- Whether roles or permissions may be carried in a login token
  ([#92](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/92)).
- Whether ADR-006's vocabulary is corrected now that Identity and other infrastructure packages sit in
  its application-module tier. This is `design/10-design.md` Open question 3 and is the repository
  owner's call.
