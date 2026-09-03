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

## How this document is kept

A slice's body is its specification while it is outstanding. **A re-run of `/slices` appends new slices
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

---

## Outstanding

## S6 — The shareable type and the audited cross-tenant read
**Status:** in progress

Delivers: a tenant can publish one of its own records for other tenants to read, and every crossing of
that line is written down. Nothing a tenant did not publish is reachable from outside it, and nothing
another tenant does can write into it at all.

Touches:
- **`SubZeroDev.Platform.Persistence`** — `IShareable`, the model-build query filter,
  `ISharedReadScopeFactory`
- **[`Results.cs`](../src/SubZeroDev.Platform.Abstractions/Results.cs)** —
  `ContractViolation.WriteInsideSharedReadScope`
- **`PlatformPermissions.ShareResource`**, **`PlatformAuditActions.SharedReadScopeOpened`** and
  **`PlatformAuditActions.ResourceShared`**

Depends on: S5.

Acceptance:
- **S6.1** Two tenants create rows carrying the same logical id on the same entity type without
  collision, and each reads only its own.
- **S6.2** Outside a shared-read scope, a query over a shareable type returns only rows whose tenant
  equals the current tenant — whether or not those rows are published.
- **S6.3** Inside a shared-read scope opened for one entity type, a query over that type returns the
  current tenant's rows plus other tenants' published rows, while a query over any other type in the same
  scope still returns only the current tenant's rows.
- **S6.4** Opening the scope writes exactly one audit record, action `platform.tenancy.shared-read`,
  class `Required` — a scope whose queries return four hundred rows still writes one.
- **S6.5** A write attempted while a shared-read scope is open throws
  `PlatformContractViolationException` carrying `ContractViolation.WriteInsideSharedReadScope`, and no
  row is changed.
- **S6.6** Publishing a row requires `PlatformPermissions.ShareResource`, is an ordinary tenant-scoped
  write by the owning tenant, and writes an audit record with action `platform.tenancy.resource-shared`.
- **S6.7** A scope the caller failed to dispose does not survive the request: the next request over the
  same entity type reads only its own tenant's rows.
- **S6.8** The scope-opening member declares no tenant parameter — the caller states that it is crossing,
  never whose rows it wants.
- **S6.9** No write path reaches another tenant's row: every write entry point on the persistence surface
  takes or derives the current tenant and no other.

Out of scope: unpublishing — a published row never returns to private through a Platform path. Any
cross-tenant write, which has no path at all and gains none here.

## S7 — The entitlement seam and the Community baseline
**Status:** queued

Delivers: a product can gate a feature by name and never learn why it was allowed. Whether the deployment
pays a subscription or holds a licence stops being something the calling code can find out, so the same
feature check works in both shapes.

Touches:
- **`SubZeroDev.Platform.Abstractions`** — `FeatureName`, `EntitlementContributorName`,
  `EntitlementDecision`, `IEntitlementContributor`, `IEntitlementEvaluator`, `EntitlementError`
- **`SubZeroDev.Platform.Core`** — the entitlement-contributor registry, the evaluator, and the Community
  baseline contributor

Depends on: S5.

Acceptance:
- **S7.1** The evaluator's `EvaluateAsync` declares no tenant parameter; the tenant comes from the
  ambient scope and appears on the decision.
- **S7.2** Two contributors granting the same feature produce one granted decision naming both sources; a
  decision that is not granted carries an empty source set.
- **S7.3** One contributor granting and another declining produces a granted decision — the union admits
  no veto.
- **S7.4** A contributor returning `EntitlementError.ContributorUnavailable` contributes nothing and does
  not fail the evaluation: another contributor's grant still produces a granted decision.
- **S7.5** A decision round-trips through storage beside a work item and reads back carrying the instant
  it was decided at; no framework package declares an entitlement table.
- **S7.6** With only the Community baseline registered, a feature the baseline names is granted and one it
  does not name is not, and neither answer is an error.
- **S7.7** No caller can resolve a contributor: attempting to obtain one from the container fails, and the
  evaluator is the only public entry.
- **S7.8** Refusing an operation on a decision that was not granted names no contributor to the caller.

Out of scope: Billing's and Licensing's contributors (S11, S12). The
`Local`-forbids-a-non-baseline-contributor rule (I-C3) and the fingerprint input (I-C4), which land in
S8.

## S8 — The fixed request order and the composition profile's startup rules
**Status:** queued

Delivers: both deployment shapes run the same steps in the same order, so a rule proved in one holds in
the other. A host whose installed packages and declared shape disagree refuses to start, instead of
serving something nobody meant to run.

Touches:
- **`SubZeroDev.Platform.Abstractions`** — `IAuthenticationProvider`, `IAuthenticationRequest`,
  `AuthenticationError`
- **`SubZeroDev.Platform.Core`** — the authentication-provider registry, the freeze of all five
  registries, the profile validation, and the settings-fingerprint input per Contract Unresolved 2
- **`SubZeroDev.Platform.Hosting`** — the fixed request pipeline, and
  [`StartupFailure.cs`](../src/SubZeroDev.Platform.Hosting/StartupFailure.cs)'s remaining variants

Depends on: S7.

Acceptance:
- **S8.1** A single request through the operated host produces an ordered trace of exactly: authenticate,
  resolve tenant, open scope, authorize, check entitlement, do the work, audit.
- **S8.2** The same request through the local host produces the same seven steps in the same order, with
  no step skipped and no branch taken.
- **S8.3** A request that is both unauthorized and unentitled is refused as unauthorized, and the trace
  shows no entitlement evaluation ran.
- **S8.4** An endpoint that only reads runs no entitlement evaluation at all, even for data produced under
  a gated feature.
- **S8.5** `Operated` with no authentication provider registered fails startup with
  `HostStartupError.AuthenticationProviderRequired`, naming the profile and the missing registration.
- **S8.6** `Operated` with no sink declaring `IsDurable` fails startup with
  `HostStartupError.DurableAuditSinkRequired`; the default log sink does not satisfy it.
- **S8.7** `Local` with an authentication provider, with a tenant resolver, or with an entitlement
  contributor other than the Community baseline fails startup with
  `HostStartupError.RegistrationForbiddenByProfile`, naming the offending registration and which of the
  two it disagrees with.
- **S8.8** Two hosts differing only in their frozen contributor set compute different settings
  fingerprints and `platform.settings-fingerprint` reports the disagreement; `SettingsFingerprint`'s
  format version changes in the same commit.
- **S8.9** Presenting no credential succeeds carrying an `Anonymous` principal; presenting an invalid one
  fails with `AuthenticationError.CredentialRejected` and does not fall back to `Anonymous`.
- **S8.10** With no signing key cached, a request fails with `AuthenticationError.KeyMaterialUnavailable`,
  surfaces as unauthenticated rather than as a server error, and issues no outbound call on the request
  path.
- **S8.11** `IAuthenticationRequest` exposes headers and nothing else — asserted over its members, with no
  route to a request body.
- **S8.12** Every startup check in this slice fails the host and names the registration that caused it;
  none degrades the host into serving.

Out of scope: a concrete authentication provider, which is S9 — this slice ships the seam, its registry
and its profile rule. The Organizations resolver (S10).

## S9 — Identity: authenticating a principal at the transport
**Status:** queued

Delivers: an operated deployment can put a real sign-in in front of Platform without Platform choosing
the sign-in service, holding a list of users, or knowing anything at all about the account behind the
person using it.

Touches:
- **`SubZeroDev.Platform.Identity`** — new module: authentication providers and the mapping from an
  authentication result to a principal. No entity type, no context, no migration
- **The operated sample host** — registers the module and a test issuer

Depends on: S8.

Acceptance:
- **S9.1** The operated host authenticates a bearer credential at the transport, and the request observes
  a principal of kind `Account` whose id carries the issuer and the subject as two opaque halves.
- **S9.2** A credential from a second issuer carrying the same subject produces a different principal id
  and is not treated as the same principal.
- **S9.3** An invalid credential is refused with `AuthenticationError.CredentialRejected` and the request
  does not proceed as anonymous.
- **S9.4** A principal established from an upstream-proxy assertion observes kind `Delegated` with no
  claims, and carries a membership and an audit actor identically to an `Account` one.
- **S9.5** An architecture test asserts the module declares no entity type, no `DbContext` and no
  migration.
- **S9.6** The local host's resolved dependency graph contains no reference to the module, and the local
  host starts and serves every one of its scenarios with the package absent.
- **S9.7** Two credentials differing only in the case of the subject produce two different principals —
  nothing is trimmed, folded or normalised.

Out of scope: choosing or operating an identity provider — the sample's issuer is a test double.
Federation, account linking and any shared user directory, which are brief non-goals.

## S10 — Organizations
**Status:** queued

Delivers: someone can create an organization, invite another person into it, and have that person accept.
Afterwards each of them can work in the organizations they belong to, and neither can see, enter or
administer one they do not — they are simply told it is not there.

Touches:
- **`SubZeroDev.Platform.Organizations`** — new module: `OrganizationId`, `InvitationId`,
  `OrganizationRole`, `MembershipState`, `Organization`, `Membership`, `Invitation`, `OrganizationError`,
  the organization API, an `ITenantResolver`, an `IPermissionProvider`, and three tables

Depends on: S9.

Acceptance:
- **S10.1** Creating an organization mints a tenant, writes the organization, writes the owner's
  membership and writes the audit row in one transaction; forcing any one of the four to fail leaves none
  of them.
- **S10.2** Two organizations created concurrently never share a tenant: the losing create answers
  `OrganizationError.TenantAlreadyAssigned`, is retryable, and the retry mints a fresh tenant.
- **S10.3** Minting an invitation returns its token exactly once. No API reads it back, and the stored row
  holds only its hash.
- **S10.4** Redeeming a valid token creates exactly one active membership; the same token presented a
  second time creates no second membership and answers `OrganizationError.InvitationNotRedeemable`.
- **S10.5** An expired token, an already-redeemed token and a token that never existed produce the
  identical caller-facing answer; the distinction between them appears only in the log.
- **S10.6** A principal switches its active organization to one it belongs to and the request's tenant
  becomes that organization's tenant. Switching to one it does not belong to answers
  `OrganizationError.OrganizationNotFound` — never forbidden.
- **S10.7** A non-member attempting to administer the organization is told not found.
- **S10.8** With no active organization selected the module's resolver defers and the request proceeds in
  `TenantId.Implicit`.
- **S10.9** The module's permission provider grants `Platform.Organizations.Administer` to `Owner` and
  `Administrator` and not to `Member`, and the resulting decision names the provider as its source.
- **S10.10** Membership is keyed by issuer and subject as two columns with no reference to a user row, and
  a `Delegated` principal holds a membership that behaves identically to an `Account` one.
- **S10.11** The module's schema contains no role-assignment table — the role is the closed enum on the
  membership.
- **S10.12** Membership and ownership changes write audit records of class `Required`.

Out of scope: teams, nested organizations and richer organization administration. Invitation delivery —
D5 mints and redeems; delivery is Notifications, which is D4.

## S11 — Billing
**Status:** queued

Delivers: a deployment can put a customer on a plan, move them to another, and have what that customer
may do change accordingly — while the product's own code never learns that a subscription exists at all.

Touches:
- **`SubZeroDev.Platform.Billing`** — new module: `PlanKey`, `SubscriptionId`, `SubscriptionState`,
  `Plan`, `Subscription`, `ProviderEventReceipt`, `BillingError`, an `IEntitlementContributor`, the
  administration API, the inbound provider event seam, and three tables
- **`tests/SubZeroDev.Platform.Tests`** — the I-C8 architecture check, on S1's mechanism

Depends on: S8.

Acceptance:
- **S11.1** A tenant subscribed to a plan granting a feature resolves that feature as granted; the same
  tenant with no subscription does not.
- **S11.2** A transition from a plan granting a feature to one that does not changes the resolved
  entitlement by writing one row, with no per-feature fan-out and no entitlement stored anywhere in the
  module's schema.
- **S11.3** An architecture test fails the build when any package other than the Billing module references
  `SubscriptionState` or any subscription type, and it fails against a deliberately broken fixture before
  it counts.
- **S11.4** A subscription whose period has ended against the injected clock stops granting its plan's
  features with no row written.
- **S11.5** The same inbound provider event delivered twice updates the subscription once, answers success
  both times, and records one receipt.
- **S11.6** An uninterpretable inbound event answers `BillingError.ProviderEventMalformed` and records no
  receipt.
- **S11.7** A transition naming an unregistered plan answers `BillingError.PlanNotFound`; a transition to
  a state unreachable from the current one answers `BillingError.InvalidTransition`.
- **S11.8** One tenant holds at most one subscription, enforced by a unique index rather than by a check
  in code.
- **S11.9** With outbound network unavailable the host starts, serves and reports ready: no provider is
  contacted on the request path, at startup or on readiness.
- **S11.10** An entitlement transition writes an audit record with action
  `platform.billing.entitlement-changed` and class `Required`.

Out of scope: real payment-provider integration — checkout, invoices, tax handling, webhooks and live
credentials. Metering, quotas and any usage-based enforcement.

## S12 — Licensing
**Status:** queued

Delivers: a deployment with no internet connection can prove from a signed file what it is entitled to,
keep working when that file goes missing, and — once the licence has been expired for a month — stop
being able to start new paid work while everything already running finishes and all of its data stays
readable and exportable.

Touches:
- **`SubZeroDev.Platform.Licensing`** — new module: `LicenceTier`, `LicenceVerificationOutcome`,
  `LicenceClaims`, `LicenceSigningKey`, `LicensingError`, an `IEntitlementContributor`, the revocation
  extension point, and the one-row verified-licence table

Depends on: S8.

Acceptance:
- **S12.1** A validly signed document verifies with outbound network unavailable and writes one row
  carrying the tier, the granted features, the issue instant, the expiry and grace-end instants as
  computed at verification, the verification instant, a fingerprint of the document, and the id of the key
  that verified it.
- **S12.2** Those claims survive a restart: the host restarted with the document deleted resolves the same
  features from the stored row.
- **S12.3** Fifty consecutive `Unavailable` or `ClockUnusable` verifications leave every column of the row
  unchanged, compared before and after.
- **S12.4** A tampered or wrongly signed document grants no tier: a host holding one and no prior row
  resolves the Community tier.
- **S12.5** A fresh installation with no row resolves the Community tier and grants only the Community
  baseline's features.
- **S12.6** `Invalid` and `Unavailable` are separately named in the log and in the audit record, though
  both fall back identically.
- **S12.7** A clock reading earlier than the stored verification instant is evaluated at the stored
  verification instant: grace is neither extended nor expired early.
- **S12.8** Two hosts verifying different documents converge on the one with the later verification
  instant; the host holding the older document answers `LicensingError.SupersededByNewerVerification`,
  which its caller treats as success.
- **S12.9** After the grace end passes on the injected clock, an endpoint admitting new paid-feature work
  is refused while work already accepted, running or scheduled completes, and reads, lists and exports of
  existing data continue to succeed.
- **S12.10** Grace defaults to thirty days when the document names none, and the module's options type
  exposes no member by which a deployment could set it.
- **S12.11** Accepted signing keys are a required ordered option supplied by the host; no key is compiled
  into the module, and a document signed by the second key in the set verifies.
- **S12.12** Verification never fails startup and never fails a request: a host whose document is
  unreadable starts, reports ready and serves.
- **S12.13** With the revocation seam absent, and again with one registered, the host makes no outbound
  call at startup, on readiness or on any request.
- **S12.14** A licence state change writes an audit record with action `platform.licensing.state-changed`
  once per detection: a thousand requests against one expired licence write one record.

Out of scope: machine activation, seat enforcement, trial issuance and an online revocation service.
Resisting an operator who controls the machine's clock — D5 detects and logs, and claims no more.

## S13 — The durable audit store
**Status:** queued

Delivers: an operated deployment keeps a record of who did what, in which tenant, and how it turned
out — one that survives restarts, that an operator can search, and that nothing in the system can quietly
edit or delete.

Touches:
- **`SubZeroDev.Platform.Audit`** — new module: the audit table, an `IAuditSink` declaring
  `IsDurable == true`, and the read API scoped by tenant, instant range, actor and correlation

Depends on: S8.

Acceptance:
- **S13.1** Allowed, denied and failed actions each persist actor issuer, actor subject, actor kind,
  tenant, action, resource, outcome, correlation and instant, and every one is present after a restart.
- **S13.2** The actor is stored as two columns, and no code path splits the rendered pair to recover them.
- **S13.3** The read API answers by tenant with an instant range, by correlation, and by actor subject
  with an instant range; the schema carries indexes for those three reads and no others.
- **S13.4** The module exposes no update and no delete — asserted over its public surface and over the
  operations its schema permits.
- **S13.5** The primary key is the event id and carries no tenant prefix, so an operator's cross-tenant
  query is one indexed read rather than a scan.
- **S13.6** The sink declares `IsDurable == true`, and an `Operated` host with this module present starts
  where the same host without it fails with `HostStartupError.DurableAuditSinkRequired`.
- **S13.7** A thousand concurrent appends from two hosts against PostgreSQL all land, and no unique
  constraint spans hosts.
- **S13.8** Representative secrets pushed through every audited input surface reach neither a stored row
  nor a log line.

Out of scope: retention, pruning, archival, export formats and shipping to an external audit system. D5
selects none, and the table grows without bound deliberately — recorded under
[`90-decisions.md`](90-decisions.md) § *Open*.

## S14 — Mcp: the frozen catalogue and its startup checks
**Status:** queued

Delivers: a product can offer tools to an AI client from two independent sources — a manifest it ships
and its own code — with neither privileged over the other. Nothing is offered to anyone until someone
explicitly says so, and a tool that could ask for a password never gets as far as running.

Touches:
- **`SubZeroDev.Platform.Mcp`** — new module: `ToolName`, `ToolProducerName`, `ToolDefinition`,
  `ToolRegistration`, `IToolProducer`, `IToolCatalogue`, exposure configuration, and the startup
  validation
- **`tests/SubZeroDev.Platform.Tests`** — the I-M9 containment check, on S1's mechanism

Depends on: S8.

Acceptance:
- **S14.1** A manifest-projecting producer and a product-owned fixed-table producer both register through
  the same interface, and the catalogue treats them identically — no ordering, capability or
  schema-derivation difference between them.
- **S14.2** `ToolDefinition` declares no exposure member — asserted over the type. Exposure comes only
  from configuration.
- **S14.3** A registered tool absent from the exposure configuration is not in the catalogue's exposed
  set, and looking it up answers exactly what looking up a name that was never registered answers.
- **S14.4** `IToolCatalogue` declares no member reaching an unexposed registration — no enumeration of all
  registrations, no exposure-ignoring lookup.
- **S14.5** A registered tool whose parameter schema names a parameter matching the redaction marker set
  fails startup with `HostStartupError.SensitiveToolParameter`, naming the tool and the parameter.
- **S14.6** A tool requiring a permission no catalog declares fails startup with
  `HostStartupError.UnregisteredPermission`.
- **S14.7** Each producer's production runs once at startup and never again, and the catalogue exposes no
  registration, unregistration or re-exposure member.
- **S14.8** An architecture test asserts `ModelContextProtocol.*` is referenced by the Mcp module and by
  no other package, and that no Platform public type exposes, returns, accepts or derives from an SDK
  type.

Out of scope: the transport, the connection principal and invocation, which are S15.

## S15 — Mcp: the transport, the connection principal and invocation
**Status:** queued

Delivers: an AI client connects once, proves who it is at that moment, and can then use the tools it is
allowed to use. Every call is recorded, a tool it may not use looks exactly like a tool that does not
exist, and no password ever travels through a tool's arguments.

Touches:
- **`SubZeroDev.Platform.Mcp`** — the SDK transport and session, Platform's own list and call filters
  installed ahead of the SDK's, `McpError`, the fixed invocation order, and the invocation audit

Depends on: S14.

Acceptance:
- **S15.1** The connection authenticates once and its principal is fixed for the connection's lifetime; a
  session request carrying a different principal answers `McpError.ConnectionUnauthenticated` and the
  exchange ends.
- **S15.2** No member of the invocation surface accepts a credential, and the audit record of a call
  contains no argument.
- **S15.3** One invocation produces an ordered trace of exactly: catalogue lookup, tenant resolution,
  argument parse, authorization, entitlement, invocation inside its own operation scope, audit.
- **S15.4** Authorization runs before any producer code: a producer that records having run is never
  reached on a denied call.
- **S15.5** A tool whose schema names a resource parameter is authorized scoped to the parsed resource id;
  a tool whose schema names none is authorized at the tool level.
- **S15.6** An unregistered tool and a registered-but-unexposed tool produce the identical answer on both
  list and call, and that answer is Platform's rather than the SDK's default, which discloses existence.
- **S15.7** Listing returns exposed tools only.
- **S15.8** `McpError.InvalidArguments` names no argument value.
- **S15.9** Every invocation writes exactly one audit record with the tool as the action and class
  `Required`.
- **S15.10** Two calls on one long-lived connection carry different correlations.
- **S15.11** A connection dropped mid-invocation cancels the call through the existing cancellation
  plumbing and leaves no half-applied write.

Out of scope: runtime registration, unregistration or re-exposure of a tool. The SDK's
authorization-metadata path, which is not used at all — Platform's evaluator is the only authority on
this surface.

## S16 — The administration shell
**Status:** queued

Delivers: an operator can see who they are signed in as, move between the organizations they belong to,
read what the deployment is entitled to and what its licence says, and look through the record of what
has happened — in a screen a consumer can throw away and replace with their own.

Touches:
- **The shell** — a front-end build in the delivery shape settled for design Open question 2, with no
  .NET package a backend could reference
- **`tests/SubZeroDev.Platform.Tests`** — the I-W1 assertions over the package graph and the shell's call
  list

Depends on: S13.

Acceptance:
- **S16.1** The shell displays the current principal's kind and display name, and shows an anonymous state
  distinctly from an authenticated account.
- **S16.2** The shell switches the active organization through the same HTTP endpoint an ordinary caller
  uses; a test performs the identical switch with an HTTP client and no UI, and both succeed.
- **S16.3** Every endpoint the shell calls is callable without it: enumerating the shell's calls against
  the public API surface finds no admin-only or UI-only endpoint.
- **S16.4** The shell displays the resolved entitlement, the licence tier and the grace state, reading all
  three through the public API.
- **S16.5** The shell reads the audit record with tenant and instant-range filters and offers no update
  and no delete.
- **S16.6** No backend package references the shell, and the backend builds and runs with the shell's
  assets absent.
- **S16.7** The shell holds no server-side state: it renders an error when the backend is unavailable, and
  nothing is lost when it does.

Out of scope: settings, notifications, API-key administration, payment UI, plugin administration and
feature-flag management — a complete shared administration product is a brief non-goal.

## S17 — The proof: two databases, two hosts, contention and offline
**Status:** queued

Delivers: anyone deciding whether to adopt Platform can look at one CI run and see each of the nine
capabilities pass or fail on its own — on both databases, with the offline deployment and the
several-instances deployment actually exercised rather than assumed.

Touches:
- **The sample solution's scenarios** — one focused set per capability, positive and negative
- **The CI workflow** — the database matrix, the offline run, the multi-instance run, and the
  per-capability reporting

Depends on: S16.

Acceptance:
- **S17.1** CI runs the operated scenarios against SQLite and against PostgreSQL, and reports each of the
  nine capabilities as a separately named result. Starting successfully is not one of them.
- **S17.2** The contention scenarios — concurrent licence verification, concurrent organization creation,
  one invitation redeemed twice, and concurrent audit appends — run against PostgreSQL with at least two
  host instances.
- **S17.3** The local host's scenarios run with outbound network unavailable, and the run fails if any
  outbound connection is attempted.
- **S17.4** The operated proof uses at least two principals, two organizations and two tenants.
- **S17.5** Nine mutation fixtures, one per capability, each violating a negative criterion from the
  brief's capability table, and CI fails against every one of them.
- **S17.6** The local host's scenarios assert it has no package or project reference to Identity,
  Organizations, Billing or Licensing, requires no configuration or storage for them, and uses the
  `system:local` audit actor.

Out of scope: deploying or operating the sample — it is a CI proof, not an environment. Performance,
throughput, latency and capacity measurement, and any comparison between the two database providers.

## S18 — Packages, documentation and the corrected plan
**Status:** queued

Delivers: someone outside this repository can take Platform's commercial surface as published packages,
follow written instructions to build either deployment shape, and find the effort's own plan describing
what was actually built rather than what was once intended.

Touches:
- **The CI packaging steps** and the sample solution's package references
- **[`Fakes.cs`](../src/SubZeroDev.Platform.Testing/Fakes.cs)** — the D5 fakes and the audit inspector
- **`docs/`** — the human-facing guide
- **`implementation-plan.md`** and [`90-decisions.md`](90-decisions.md)

Depends on: S17.

Acceptance:
- **S18.1** Every D5 package is packed in CI as a versioned 0.x artifact, and the sample solution restores
  them from those artifacts rather than through project references.
- **S18.2** `SubZeroDev.Platform.Testing` exposes a fake principal of each of the four kinds, a fake tenant
  resolver, a fake entitlement contributor, a fake permission provider, an audit inspector and the
  composition profile on the test host — and no fake organization, subscription or licence.
- **S18.3** The audit inspector reads records and exposes no write and no clear.
- **S18.4** The human-facing documentation covers registration, optional composition, both deployment
  shapes, security defaults, failure semantics and the sample's asserted scenarios, and the documentation
  build passes with no broken link.
- **S18.5** `implementation-plan.md`'s D5 capability list includes `Platform.Mcp`, and its done-when
  matches [`00-brief.md`](00-brief.md).
- **S18.6** The retained consumer-evidence objection for Authorization, Licensing, Audit and the shared web
  UI is recorded in [`90-decisions.md`](90-decisions.md) and is not written as resolved.

Out of scope: publishing to a public registry, and onboarding any named consumer — external adoption is
separate work.
