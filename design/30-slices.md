# Slices — commercial (D5)

**Document status:** Slices. Derived from [`10-design.md`](10-design.md) and
[`20-contract.md`](20-contract.md). The contract is authoritative for every signature named below;
**no slice may introduce one that is absent from it.** Where a slice needs a declaration the contract
does not carry — a module's own API surface is the likeliest place, since `20-contract.md`
§ *Public surface* 10 names what each module exposes without declaring it — the slice **stops and asks
for a contract amendment rather than inventing one.**

Each slice is vertical: it runs, and its acceptance criteria are observable from outside the code that
satisfies them. One repository is in scope, this one, plus the two sample hosts and the shell that S1
and S16 create inside it.

**The ordering is the design's risk ordering.** The riskiest thing in this design is not any one
capability — it is the single idea every capability is generated from:
[ADR-006](../docs/docs/adr/ADR-006-application-modules.md) rules 1 and 2 held **structurally**, so that
the framework can own nine questions while six modules own the answers and no module can reach another.
If that cannot be enforced by the build, the shape is wrong and everything downstream rests on a
preference. S1 therefore builds the enforcement
before there is anything to enforce it against, and proves it bites by failing it against a
deliberately broken graph — the standard
[`minimal-platform-packages.md`](../docs/docs/minimal-platform-packages.md) §2 sets. S2 takes the one
change that is cheap now and expensive after a consumer ships. S3 to S8 are the framework seams, each
placed where it first becomes checkable, ending with the fixed request order every later slice plugs
into rather than retrofits. S6 sits as early as its prerequisites allow because the deliberately shared
resource is the part of tenancy no library provides and the part
[`second-consumer-packages.md`](../docs/docs/second-consumer-packages.md) §3 names as how isolation
quietly stops holding. S9 to S16 are the six modules and the shell. S17 is the proof the brief's
definition of done actually asks for, and S18 is public delivery.

## Decisions that must be taken before the slice that needs them starts

Neither the design nor the contract settles these, and none is a slice's to settle silently.

| Question | Needed before |
|---|---|
| Contract Unresolved 1 — whether `IAuditable.CreatedBy` becomes non-null under a total principal | **S2**. Both readings are defensible and produce different declarations. Determined either way: the stored value is `PrincipalId.ToString()` and is never split, and `ModifiedBy`/`DeletedBy` stay nullable |
| Contract Unresolved 2 — how the frozen entitlement-contributor set reaches the settings-fingerprint input | **S8**, which is where I-C4 lands. The two shapes change different public declarations — a second parameter on `Compute`, or a projected `[Fingerprinted]` property. Determined either way: the names are inside the hashed input and `SettingsFingerprint`'s format version changes |
| Design Open question 2 — how the administration shell is delivered | **S16**. The recommendation is a separate front-end build with no .NET package; the alternative is a .NET-hosted UI. Neither introduces a .NET declaration, so nothing before S16 changes shape either way |
| Design Open question 4 — whether the D5 packages version in lockstep with the framework or independently | **S18**. The recommendation is lockstep for the whole of D5 |
| Design Open question 3 — whether ADR-006's vocabulary is corrected now that five infrastructure packages land in its module tier | **Not blocking any slice.** It is an ADR amendment and therefore the repository owner's; the placement in *Module boundaries* does not wait on the answer. Recorded here so `/slice` does not read the silence as settled |
| Issue #94's disposition once S23 lands. By the 2026-09-26 decision, D5 does not deliver #94's criteria 1, 3 and 4 (the sign-in hook, the sign-out proof and the hook documentation), and criterion 2 holds in validation terms only | **Not blocking any slice.** It is the repository owner's call whether #94 closes with those criteria recorded as accepted risk, or is split so they stay tracked. Recorded here so `/track` does not close #94 as fully delivered |

## How this document is kept

A slice's body is its specification while it is outstanding. **A re-run of `/plan` appends new slices
under `## Outstanding` only** — it never rewrites `## Landed`, and it never renumbers or reuses a
retired id, including one that never got an issue. Removing a criterion leaves a gap; the next
criterion takes the next unused number.

When a slice ships and its issue closes, its body is retired to a one-line entry under `## Landed`. The
index carries no criteria, because the issue is then the record of what was accepted — `/track` does
not sync a landed slice and must not re-derive its criteria from the index.

Every slice carries a `**Status:**` line reading `shipped`, `in progress` or `queued`, immediately
under its heading. [`build/Test-SliceStatusMarkers.ps1`](../build/Test-SliceStatusMarkers.ps1) fails the
documentation gate when the markers are inconsistent: at most one slice in progress, at least one in
progress while any is queued, and no shipped slice ordered after a queued one.

---

## Outstanding

S19 to S23 deliver the generic bearer path that issue
[#94](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/94) asks for (`20-contract.md`
*Types* § 12, *Modules* § 10 Identity), and the chain-end rule decided on 2026-10-01 (I-I15). The
riskiest assumption here is that a provider can be produced from configuration alone. That means
validated before anything is fetched, registered without a per-issuer call, and built on IdentityModel
without the library leaking into Platform's surface. S20 exercises that assumption. S19 goes first only
because it is small, and because S20 cannot answer a foreign issuer correctly until the variant S19
adds exists. Key discovery (S21), tenant-shaped issuers (S22) and the vendor configuration source
(S23) each add keys to a schema S20 has already proved. Each slice before them treats those keys as
unrecognised, so every interim state fails closed and no slice ever removes a key.

## S19 — An untrusted credential is refused, not ignored
**Status:** in progress

Delivers: a caller who presents a credential that no configured sign-in trusts is turned away as
unauthenticated, instead of being let through as an anonymous visitor. A caller who presents no
credential at all is still treated as anonymous.

Touches:
- **[`Authentication.cs`](../src/SubZeroDev.Platform.Abstractions/Authentication.cs)** (Abstractions):
  the new authentication error variant
- **[`Authentication.cs`](../src/SubZeroDev.Platform.Core/Authentication.cs)** (Core): the end of the
  chain
- **[`JwtBearerAuthenticationProvider.cs`](../src/SubZeroDev.Platform.Identity/JwtBearerAuthenticationProvider.cs)**:
  how it answers a token from an issuer it does not trust
- **[`20-contract.md`](20-contract.md)**: the *Public surface* § 2 scaffold, and the I-I15 record
- **`tests/`**

Depends on: none. S1 to S18 have landed.

Acceptance:
- **S19.1** `AuthenticationError.CredentialNotClaimed(string provider)` exists exactly as the contract
  declares it. The *Public surface* § 2 scaffold block is replaced by the pointer the contract names, in
  the same change.
- **S19.2** A chain in which no provider establishes a principal or rejects, and at least one answers
  `CredentialNotClaimed`, answers `CredentialRejected`. The rejection names the first provider, in
  registry order, that answered `CredentialNotClaimed`.
- **S19.3** No caller of the chain ever receives `CredentialNotClaimed`. This is asserted at the chain
  and at the request pipeline.
- **S19.4** A chain in which every provider saw no credential of its kind still answers
  `Principal.Anonymous`. A provider that rejects still ends the chain at once, before any later
  provider runs.
- **S19.5** The test-grade bearer provider answers `CredentialNotClaimed` for a well-formed token whose
  `iss` it does not trust, where today it answers `Principal.Anonymous`. It still answers
  `Principal.Anonymous` when no bearer header is present.
- **S19.6** Two test-grade providers for two issuers still chain: a token from either issuer
  authenticates through the chain. S9.2's behaviour survives the change.
- **S19.7** End to end through an operated host: a request bearing a token from an untrusted issuer is
  refused as unauthenticated, never as a server error. A request bearing no credential reaches an
  anonymous-permitted endpoint as anonymous.
- **S19.8** I-I15's record moves from `instruction` to `code`, citing the tests that enforce it, in the
  same change as the code. An invariant is code only once its test lands.

Out of scope: the generic bearer path itself (S20), and any change to the upstream-proxy provider,
which has no foreign-credential case.

## S20 — An issuer with fixed keys is trusted from configuration alone
**Status:** queued

Delivers: an operator trusts a new token issuer by writing configuration and restarting the host,
with no code and no rebuild. A mistake in that configuration stops the host at startup with a message
naming the exact setting to fix, instead of surfacing later as refused sign-ins.

Touches:
- **`src/SubZeroDev.Platform.Identity/`**: the module's registration, the configured provider, settings
  binding and validation, and the project's IdentityModel references
- **[`PackageGraphTests.cs`](../tests/SubZeroDev.Platform.Tests/PackageGraphTests.cs)**: the I-I12
  guard
- **[`20-contract.md`](20-contract.md)**: the I-I7, I-I10, I-I11 and I-I12 records
- **`tests/`**

Depends on: S19.

Acceptance:
- **S20.1** A host that registers the Identity module and writes one child section under
  `Platform:Identity:Bearer` with `Issuer`, `SigningKeys` and `Audiences` gets one provider named after
  that section. The section needs no other key and no registration call. Several child sections give
  several providers, which chain in registry order.
- **S20.2** An `Operated` host whose only authentication provider comes from configuration starts. I-C1
  is satisfied by a configured provider.
- **S20.3** `Algorithms` (`RS*`, `PS*` and `ES*` only, default `RS256`), `ClockTolerance` (at most five
  minutes, default one minute), `SubjectClaim` (default `sub`) and `DisplayNameClaim` (optional) bind,
  and each takes its default when absent.
- **S20.4** Every settings defect in the contract's *Error semantics* § 9 table that falls within this
  slice's keys fails startup as `HostStartupError.Configuration`, naming the full key
  `Platform:Identity:Bearer:<name>:<setting>`. That covers `MissingRequiredSetting` (no `Audiences`, no
  `Issuer`, no `SigningKeys`), `InvalidSetting` (a malformed value, a shared-secret algorithm, `none`,
  a tolerance over five minutes, an unrecognised key, a value where a section belongs or a section
  where a value belongs). One test covers each row. A section whose name clashes with another
  authentication provider's name fails startup as the registry's `DuplicateProviderName` inside
  `Registration`, not as a configuration error.
- **S20.5** Until S21 and S22 land, `Discovery`, `KeyRefreshInterval` and `IssuerPattern` are
  unrecognised keys and fail startup as `InvalidSetting`. When the later slice lands it removes this
  criterion's test rather than inverting it.
- **S20.6** A settings defect fails startup before any provider is registered or any key is read. The
  host never serves, and nothing is left behind (I-I10, settings half).
- **S20.7** Every token outcome in *Error semantics* § 1 that applies to a fixed-key provider holds, one
  test each. `CredentialRejected` covers: an algorithm outside the set, an unknown `kid`, no `kid` with
  no cached key verifying, a bad signature, `exp` or `nbf` outside tolerance, `exp` absent, an audience
  mismatch, a subject that is missing, empty or not a string, and an unreadable `iss`. A token whose `iss`
  is not this provider's `Issuer` gets `CredentialNotClaimed`. A fault inside the validation library
  gets `ProviderFailed`.
- **S20.8** An accepted token gives an `Account` principal. Its `PrincipalId` pairs the validated `iss`
  with the subject claim's value, neither trimmed nor case-folded. Two subjects differing only in case
  give two principals (I-I11, I-I2). The display-name claim is used when it is configured and present,
  and every other claim is carried in `Principal.Claims` (I-I6).
- **S20.9** End to end through an operated host: a token signed by a configured issuer authenticates,
  and a token from an issuer no section names is refused as unauthenticated (S19).
- **S20.10** `Microsoft.IdentityModel.JsonWebTokens` and `Microsoft.IdentityModel.Protocols.OpenIdConnect`
  are referenced by `SubZeroDev.Platform.Identity` and by no other Platform project, and no
  `Microsoft.IdentityModel.*` type appears in any Platform package's public surface. The guard is shown
  failing against a deliberately broken fixture before it is shown passing (I-I12).
- **S20.11** No public .NET type carries these settings. The schema is the configuration keys.
- **S20.12** The I-I7, I-I10, I-I11 and I-I12 records move to `code`, citing their tests, in the same
  change. I-I10 records which half is enforced and leaves the fetch half for S21.

Out of scope: key discovery and refresh (S21), issuer patterns (S22), the vendor configuration source
and the operator documentation (S23), and switching the sample hosts off the test-grade provider.

## S21 — Keys are found through discovery and kept fresh
**Status:** queued

Delivers: an operator names an issuer's discovery address instead of pasting its keys. The host
fetches the keys itself and keeps them current. An issuer outage that is shorter than the issuer's own
key lifetime is invisible to callers, and readiness tells the operator when the keys are stale or
missing.

Touches:
- **`src/SubZeroDev.Platform.Identity/`**: settings for discovery, the startup fetch, the key cache,
  the refresh background work and the readiness check
- **[`IdentityTests.cs`](../tests/SubZeroDev.Platform.Tests/IdentityTests.cs)**: the S8.10 test
- **[`20-contract.md`](20-contract.md)**: the I-I8, I-I9 and I-I10 records

Depends on: S20.

Acceptance:
- **S21.1** `Discovery` and `KeyRefreshInterval` become recognised keys, and S20.5's test for them is
  removed. `KeyRefreshInterval` binds only with `Discovery`: it must be between 30 seconds and one day,
  and defaults to five minutes.
- **S21.2** The settings defects for these keys fail startup as `HostStartupError.Configuration` naming
  the full key, one test each. That covers `InconsistentSettings` for both `Discovery` and `SigningKeys`,
  or for `KeyRefreshInterval` with `SigningKeys`; `MissingRequiredSetting` for neither `Discovery` nor
  `SigningKeys`; and `InvalidSetting` for a malformed address or an interval out of range.
- **S21.3** Each `Discovery` provider makes its first fetch at startup step 8, bounded by a fixed
  30-second timeout that is not a setting. A failed or timed-out first fetch never fails startup: the
  provider starts with no keys and answers every credential it claims with `KeyMaterialUnavailable`
  (I-I10, fetch half).
- **S21.4** A discovery document whose `issuer` does not equal the provider's `Issuer` is a failed
  fetch. No key from it is cached.
- **S21.5** Each `Discovery` provider registers one background work item,
  `platform.identity.key-refresh:<name>`. It runs at the provider's interval in both host roles, takes
  no lease, and writes nothing durable. A `SigningKeys` provider registers none (I-I9).
- **S21.6** A successful refresh replaces the whole cached set in one swap. A concurrent reader sees the
  whole old set or the whole new one, never a mix. A failed refresh keeps the previous set and never
  empties the cache (I-I9).
- **S21.7** Each `Discovery` provider registers the readiness check `platform.identity.key-set:<name>`
  as a required readiness check. It is Unhealthy with no cached set, Degraded with a cached set whose
  last fetch failed, and Healthy with a cached set whose last fetch succeeded. A `SigningKeys` provider
  is always healthy.
- **S21.8** No key material is fetched while a request is being authenticated. A token naming a `kid`
  that the cached set does not hold is `CredentialRejected` and issues no fetch. This is asserted by
  observing outbound fetches, not by inspecting assembly references (I-I8).
- **S21.9** S8.10's assertion that the Identity assembly references no `System.Net.Http` is replaced by
  S21.8's request-path assertion. The contract mandates a startup and refresh fetch, so the old
  assertion cannot hold once discovery exists. Its end-to-end half, no cached key failing as
  `KeyMaterialUnavailable` and never as a server error, is kept.
- **S21.10** The I-I8 and I-I9 records move to `code` citing their tests, and I-I10's record is
  completed, in the same change.

Out of scope: issuer patterns (S22), and any request-path or on-demand fetch, which the contract
forbids.

## S22 — One issuer serves many customer tenants
**Status:** queued

Delivers: an operator trusts a multi-tenant issuer, one that signs each customer tenant's tokens under
that tenant's own issuer address, with a single configuration entry. Users in two different customer
tenants are never mistaken for one another, even when their subjects match.

Touches:
- **`src/SubZeroDev.Platform.Identity/`**: settings for the issuer pattern, issuer matching, and the
  discovery issuer check
- **`tests/`**

Depends on: S21.

Acceptance:
- **S22.1** `IssuerPattern` becomes a recognised key, and S20.5's test is removed. Supplying both
  `Issuer` and `IssuerPattern` is `InconsistentSettings`, and supplying neither is
  `MissingRequiredSetting`, each naming the full key.
- **S22.2** A pattern that does not contain `{tenantid}` exactly once is `InvalidSetting`, naming the
  full key.
- **S22.3** The placeholder matches one or more characters, none of them `/`. Every other character
  matches ordinally. A token whose `iss` matches is claimed, and one that does not match is
  `CredentialNotClaimed`. Table tests cover an empty segment, a segment containing `/`, case
  differences outside the placeholder, and the placeholder at the start, in the middle and at the end.
- **S22.4** The principal's issuer is the concrete validated `iss`, never the pattern. Two tenants of
  one pattern presenting the same subject give two different principals (I-I11).
- **S22.5** For a `Discovery` provider with a pattern, the discovery document's `issuer` must equal the
  pattern string itself. Anything else is a failed fetch (S21.4).
- **S22.6** End to end through an operated host: tokens from two tenants of one configured pattern each
  authenticate as distinct principals, and a token from an issuer outside the pattern is refused as
  unauthenticated.

Out of scope: mapping an issuer tenant to a Platform tenant. Tenant resolution stays S5's, and nothing
here resolves a `TenantId` from a token.

## S23 — A vendor's settings arrive as a configuration source
**Status:** queued

Delivers: a vendor ships its issuer's settings as a small package that the operator adds to the host's
configuration. The host treats those settings exactly as if the operator had typed them, the package
cannot depend on Platform, and an operator has written instructions for configuring any issuer.

Touches:
- **`tests/` fixtures**: a vendor-shaped configuration-source project that references no Platform
  package
- **[`PackageGraphTests.cs`](../tests/SubZeroDev.Platform.Tests/PackageGraphTests.cs)**: the fifth
  build check
- **[`commercial-guide.md`](../docs/docs/commercial-guide.md)**: the operator's guide to the schema
- **[`20-contract.md`](20-contract.md)**: the I-I13 and I-I14 records

Depends on: S22.

Acceptance:
- **S23.1** A fixture project, built the way a vendor configuration package would be, contributes
  `Platform:Identity:Bearer:<name>:*` keys through a standard configuration source and references no
  Platform package.
- **S23.2** The fifth build check fails the build when a vendor configuration package references any
  Platform package. It is shown failing against a deliberately broken fixture before it is shown passing
  (I-I13).
- **S23.3** The same keys and values, supplied once through the fixture's source and once through an
  in-memory settings file, give equal validated settings. The same tokens give identical accept and
  reject results under both (I-I14).
- **S23.4** A key the fixture writes that Identity does not recognise fails startup as
  `InvalidSetting`, naming the full key, exactly as it would from a settings file.
- **S23.5** The commercial guide documents the schema: every key, its default, its constraint, and the
  error a defect produces. It shows a fixed-key, a discovery and a pattern example. It explains where a
  vendor quirk belongs, in that vendor's configuration source and never in Platform. It replaces the
  hand-registered provider example as the recommended path. The documentation build passes with no
  broken link.
- **S23.6** The I-I13 and I-I14 records move to `code` citing their tests, in the same change.

Out of scope: building or publishing any real vendor package, since D5 builds none. Also out of scope
are #94's sign-in hook, its sign-out proof and the hook documentation, which D5 does not deliver
([`90-decisions.md`](90-decisions.md), 2026-09-26).

---

## Landed

- **S1 — The two hosts and the enforced package boundary** — shipped:
  [#169](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/169) via
  [#195](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/195).
- **S2 — The total principal** — shipped:
  [#170](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/170) via
  [#196](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/196).
- **S3 — Audit: the contract, the writer and the log sink** — shipped:
  [#171](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/171) via
  [#197](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/197).
- **S4 — Authorization: names, providers and the evaluator** — shipped:
  [#172](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/172) via
  [#199](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/199).
- **S5 — Tenant resolution at the request boundary** — shipped:
  [#173](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/173) via
  [#200](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/200).
- **S6 — The shareable type and the audited cross-tenant read** — shipped:
  [#174](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/174) via
  [#201](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/201).
- **S7 — The entitlement seam and the Community baseline** — shipped:
  [#175](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/175) via
  [#203](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/203).
- **S8 — The fixed request order and the composition profile's startup rules** — shipped:
  [#176](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/176) via
  [#204](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/204),
  [#210](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/210),
  [#211](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/211),
  [#213](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/213) and
  [#215](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/215).
- **S9 — Identity: authenticating a principal at the transport** — shipped:
  [#177](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/177) via
  [#214](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/214).
- **S10 — Organizations** — shipped:
  [#178](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/178) via
  [#216](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/216).
- **S11 — Billing** — shipped:
  [#179](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/179) via
  [#217](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/217).
- **S12 — Licensing** — shipped:
  [#180](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/180) via
  [#218](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/218).
- **S13 — The durable audit store** — shipped:
  [#181](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/181) via
  [#219](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/219).
- **S14 — Mcp: the frozen catalogue and its startup checks** — shipped:
  [#182](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/182) via
  [#220](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/220).
- **S15 — Mcp: the transport, the connection principal and invocation** — shipped:
  [#183](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/183) via
  [#221](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/221).
- **S16 — The administration shell** — shipped:
  [#184](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/184) via
  [#223](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/223).
- **S17 — The proof: two databases, two hosts, contention and offline** — shipped:
  [#185](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/185) via
  [#226](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/226), [#227](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/227), [#228](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/228), [#231](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/231).
- **S18 — Packages, documentation and the corrected plan** — shipped:
  [#186](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/186) via
  [#233](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/233).
