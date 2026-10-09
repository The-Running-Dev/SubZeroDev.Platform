# Contract — commercial (D5)

Derived from [`10-design.md`](10-design.md), which is where every "why" in this document lives. This
is the artifact the implementing agent is constrained by: everything downstream is checked against it.

**What this document is for.** Semantics. The tree carries shape. Where a declaration does not exist
yet there is nowhere else for its shape to live, so it is written here **as a scaffold** — full
declarations in C#, types and signatures only, no bodies. **The slice that materialises a declaration
into code replaces its scaffold here with a pointer to the file that now declares it, in the same
commit.** That is descriptive drift corrected where it is found (`AGENTS.md`, *Hard rules*), not a
contract amendment, and it needs no approval. What survives that replacement — the invariants, the
error semantics, the constraints a parameter list cannot express — is the point of this document, and
every section below is written as though its scaffolds were already gone.

**What this document does not restate.** `TenantId`, `CorrelationId`, `CultureTag`, `IClock`,
`IOperationScope`, `IOperationScopeFactory`, `ICurrentTenant`, `PlatformError`, `Result<T, TError>`,
`ContractViolation`, `PlatformContractViolationException`, `IPlatformModule`, `IModuleRegistry`,
`ModuleGraphError`, `ConfigurationError`, `HostStartupError`, `PlatformStartupException`,
`ErrorEnvelope`, `IHealthCheck`, `ITenantOwned`, `IAuditable`, `ISoftDeletable`, `IUnitOfWork`,
`IAmbientTransaction`, `IAmbientTransactionAccessor`, `ISettingsFingerprint`,
`FingerprintedAttribute` and `CompositionProfile` are declared in the tree and are cited, never
re-specified. Where D5 changes one, the change is stated against the file that declares it.

---

## Types

### 1. Identity — `SubZeroDev.Platform.Abstractions`

`PrincipalId`, `PrincipalKind` and `Principal` are declared in the tree:
[`Principal.cs`](../src/SubZeroDev.Platform.Abstractions/Principal.cs).

**What the declarations cannot say.**

- **`PrincipalId` is a pair because a single string makes a later reversal a data migration.** Brief
  decision 5 keeps the Automator's and the Game Engine's identity stores separate; the pair is what
  keeps two stores minting the same subject from being a collision. It is the one part of the identity
  model that is expensive to add later, which is why it is here rather than deferred.
- **Neither half may acquire parsing, normalisation, trimming or case folding.** The moment Platform
  knows what a subject looks like it has an opinion about which providers are legal. `Issuer` and
  `Subject` are non-empty; that is the only constraint either carries.
- **`ToString()` is a display and trace form, and nothing parses it back.** It renders `LocalSystem`
  as `system:local`, which is the actor the brief's local host writes. Because both halves are opaque
  and may contain any character, the rendering is not injective and **must never be split to recover
  the pair.** Anywhere the pair must survive storage it is stored as two columns — *Persisted
  schemas*, § 1.
- **`Principal` is total; there is no null principal.** `Anonymous` is a kind of principal, not the
  absence of one, exactly as `TenantId.Implicit` is a tenant rather than the absence of one. A
  nullable actor is indistinguishable from an actor that was never resolved, and the brief requires
  allowed, denied and failed actions each to name one.
- **`Claims` is the raw authentication result and is not the identity.** It is null for `Anonymous`,
  for `System`, and for any `Delegated` principal whose asserting boundary produced no claims. **No
  Platform decision may read it** — authorization reads permissions, not claims.
- **`Delegated` is not a degraded `Account`.** No account will ever exist for BarStrad's customer-side
  principal ([`application-modules.md`](../docs/docs/application-modules.md) §2). A consumer must not
  treat a `Delegated` principal as one with missing fields.
- **Nothing here is a user entity, and none may be added.** Platform owns no directory and no federation.
  The one account concept is Identity's optional, per-host account store (§ 16). It lives in a module, it is
  keyed on this pair, and nothing in this section or any framework package names it (I-C6).

### 2. Authorization — `SubZeroDev.Platform.Abstractions`

`PermissionName`, `PermissionProviderName`, `AuthorizationOutcome`, `AuthorizationDecision` and
`PlatformPermissions` are declared in the tree (S4; `AuthorizationDecision.ProviderFailure`, S24):
[`Authorization.cs`](../src/SubZeroDev.Platform.Abstractions/Authorization.cs). `ResourceRef` is
declared in the same project, materialised by S3 because `AuditEvent` needs it ahead of the rest of
this section: [`Audit.cs`](../src/SubZeroDev.Platform.Abstractions/Audit.cs).

**What the declarations cannot say.**

- **`Sources` is non-empty when and only when `Outcome` is `Allowed`.** The evaluator takes a union
  across providers, so a wrong grant is a wrong *provider*; a decision naming one source when two
  contributed misattributes the other's grant to the next reader. A denial has no source and carries
  an empty set — the reason for a denial is that nothing granted, which is not a provider fact.
- **`Sources` is a set, and its order carries no meaning.** A caller reading the first entry has
  invented a precedence the evaluator does not have.
- **`AuditFailure` is set only on a denial whose `Required` audit record could not be written**, and
  the caller then answers a retryable failure carrying it rather than forbidden — *Error semantics*,
  § 4. The HTTP pipeline answers 503 with the `AuditError` code; Mcp answers a tool result saying the
  call could not be audited and may be retried. A denial that cannot be recorded is not answered as
  though it were.
- **`ProviderFailure` is set when and only when the outcome is `Denied` and at least one registered
  provider could not answer** — returned an error, or is a mirror past `MirrorMaximumAge` (*Public
  surface* § 3, I-A13) (I-A11). A denial reached that way is not the policy's answer, only the
  absence of one. An allowed decision never carries it: the union means any provider's grant allows, and
  one provider's outage does not make another's grant less valid.
- **`ProviderFailure` is always `ProviderUnavailable`, naming the provider as the registry knows it**,
  whatever variant the provider returned. The fact the caller acts on is that a provider could not
  answer, not what it said about itself — the same reason *Error semantics* § 4 has the class, not the
  sink, decide the response. A provider cannot name another provider in a decision.
- **Where more than one provider could not answer, `ProviderFailure` names the first in the provider
  registry's order.** It says the denial may not be the policy's answer. It is not a list of the
  providers that failed, and a caller must not read it as one.
- **A decision carrying `ProviderFailure` is answered as a retryable failure, never as forbidden or
  not found** (I-A12, *Error semantics* § 2). The HTTP pipeline answers 503 with the error's code. Mcp
  answers a tool result saying the permission could not be checked and may be retried. The denial still
  stands for this request: the caller does not proceed, so the check still fails closed.
- **Where a decision carries both, `AuditFailure` decides the answer.** Both answers are retryable, so
  the caller's next move is the same either way. Answering with the provider's code would drop a
  `Required` write's failure from the response, which *Error semantics* § 4 forbids.
- **A `PermissionName` reaching the evaluator unregistered is a startup-detectable defect, never a
  runtime denial.** A typo that silently denies is indistinguishable from a policy that denies — I-A3.
  Startup catches it on every endpoint and tool requirement. A caller that reaches the evaluator
  directly with one gets `PlatformContractViolationException` carrying
  `PermissionCatalogError.UnregisteredPermission`, before any provider is asked and with nothing
  audited (S26).
- **`PermissionName.Value` must not acquire a parser, a wildcard, a hierarchy or a prefix match.** It
  is a stable id compared ordinally. Platform's own names take the `Platform.` prefix by convention;
  the convention is not enforced by the type, because a consumer's names are its own.
- **`ResourceRef.Type` and `.Id` are opaque to Platform.** Never resolved to an entity, never used to
  fetch a row, never compared against anything but themselves. They exist to be recorded and to scope
  a check.
- **D5 has no role-assignment store, and none may be added.** Roles are a closed set on a
  membership — owner, administrator, member — because teams and richer organization administration are
  brief non-goals. A consumer needing custom roles registers a third permission provider; that is the
  extension point and it needs no Platform table.

### 3. Entitlement — `SubZeroDev.Platform.Abstractions`

`FeatureName`, `EntitlementContributorName` and `EntitlementDecision` are declared in the tree (S7):
[`Entitlement.cs`](../src/SubZeroDev.Platform.Abstractions/Entitlement.cs).

**What the declarations cannot say.**

- **There is exactly one entitlement question, and Billing and Licensing both answer it.** No caller
  may ask "is the subscription active" or "does the licence grant this tier". Two queries put the
  commercial-model branch back into product code under a different name and make the self-hosted and
  operated shapes structurally different at every call site rather than at composition.
- **Contribution is a union: any contributor granting a feature grants it.** Intersection is
  forbidden — installing Licensing into an operated deployment would silently revoke subscription
  entitlements, which presents as a working system denying a feature the customer paid for.
- **`Sources` is non-empty when and only when `Granted` is true**, on the same terms as
  `AuthorizationDecision.Sources` and for the same reason: the union's risk is one wrong contributor
  granting everything, and the decision naming its source is one of the three things that bound it.
- **`EntitlementDecision` must remain a value a consumer can persist beside a work item.** It carries
  `DecidedAt` for exactly that: a decision read back from a stored work item is the decision that
  admitted the work, not a fresh one. **Platform stores no entitlement**; a consumer persisting one
  owns its own column shape.
- **`Granted == false` is not an error.** It is a decision. The error is what a caller raises when it
  refuses the operation — *Error semantics*, § 3.

### 4. Audit — `SubZeroDev.Platform.Abstractions`

`AuditEventId`, `AuditAction`, `AuditOutcome`, `AuditClass`, `AuditEvent`, `PlatformAuditActions`,
`IAuditWriter`, `IAuditSink` and `AuditError` are declared in the tree:
[`Audit.cs`](../src/SubZeroDev.Platform.Abstractions/Audit.cs). `ResourceRef` (*Types*, § 2) is
declared in the same file, materialised ahead of the rest of Authorization because `AuditEvent`
needs it.

**What the declarations cannot say.**

- **`AuditEvent` has no payload field, no changed-field list and no free-form detail string, and none
  may be added.** [`platform-specification.md`](../docs/docs/platform-specification.md) lists "changed
  fields where appropriate"; D5 refuses it. The brief requires tests that push representative secrets
  through every audited input surface and assert nothing reaches the record or the logs. With a place
  to put a value, that test asserts a property of the redaction rules and of every future caller's
  discipline; without one it asserts a property of the type. **Adding a payload later is cheap and is
  the whole thing this decision exists to prevent.** A consumer wanting field-level change history
  builds it in its own tables, where the secret question is theirs.
- **The three caller-controlled strings — `Action.Value`, `Resource.Type`, `Resource.Id` — pass
  through the redaction boundary before storage and before logging.** That is not the sink's choice.
- **`Class` is a property of the action, decided by the writer, not by the sink.** The sink does not
  reclassify. The classification is I-U2.
- **Audit rows are append-only at the contract.** No update and no delete, and no sink may expose one.
  The one delete is the Audit store's own retention prune, which removes whole rows by age and only when
  an operator has set a retention (*Public surface* § 6). It is not a surface.
- **Ordering across hosts is deliberately weak and nothing may rely on the opposite.** `OccurredAt`
  comes from `IClock`; two hosts with skewed clocks can write rows whose stored order disagrees with
  real time. `Correlation` is what makes a trail reconstructible; a global ordering is not guaranteed,
  and building one would put a contended sequence on the path every security-sensitive action takes.
- **`AuditEventId` is minted by the writer, not the store**, so the id exists before the row does and
  a `Required` write that fails can be reported by id.

### 5. Composition — `SubZeroDev.Platform.Abstractions`

`CompositionProfile` is declared in the tree:
[`Composition.cs`](../src/SubZeroDev.Platform.Abstractions/Composition.cs). `PlatformOptions.CompositionProfile`,
`[Fingerprinted]`, is declared beside the rest of `PlatformOptions`:
[`PlatformOptions.cs`](../src/SubZeroDev.Platform.Core/PlatformOptions.cs).

**What the declaration cannot say.**

- **The profile does not decide what is present; the package graph does.** The profile is what lets
  startup say the graph out loud and fail when the two disagree — I-C1 to I-C4. It is not a feature
  switch, and **no runtime behaviour branches on it outside startup validation.**
- **The `Local` seam defaults are named providers that exist, not stubs standing in for something
  missing.** The brief's non-goal is explicit that Identity, Organizations, Billing and Licensing are
  *absent* from the local composition, "not registered checks that always pass".

### 6. Tenancy — `SubZeroDev.Platform.Persistence`

`TenantId`, `ITenantOwned` and the implicit constant are settled and untouched:
[`Identity.cs`](../src/SubZeroDev.Platform.Abstractions/Identity.cs),
[`Columns.cs`](../src/SubZeroDev.Platform.Persistence/Columns.cs). D5 adds the shareable declaration
and nothing else to the storage shape — `IShareable` is declared in
[`Columns.cs`](../src/SubZeroDev.Platform.Persistence/Columns.cs).

**What the declaration cannot say.**

- **Shareability is a property of the type, and the declaration is visible in the model.** A per-row
  flag on an ordinary type is refused: it makes the isolation boundary writable by every code path
  that can write the row, which is the failure mode inverted, and a filter that always honours the
  flag means no caller ever states that it intends a cross-tenant read — so there is nothing to audit
  and nothing to grep.
- **`SharedAt` is written only by the publication path**, which is an ordinary tenant-scoped write by
  the owning tenant, requires `PlatformPermissions.ShareResource`, and is audited. Publication is
  something a tenant does to its own row, so nothing about it crosses a boundary.
- **`SharedAt` never returns to null through a Platform path.** Unpublishing is not in D5's surface; a
  consumer needing it states its own semantics for rows another tenant has already read.
- **Declaring `IShareable` changes nothing about a query outside a shared-read scope.** The filter
  stays `tenant equals current`, unconditionally, for shareable and non-shareable types alike.

### 6a. Versioned rows — `SubZeroDev.Platform.Persistence`

`IVersioned` is an opt-in column marker beside `ISoftDeletable` in
[`Columns.cs`](../src/SubZeroDev.Platform.Persistence/Columns.cs), on the same terms: a declaration, not
a mechanism — the consumer adds the column in its own migration and writes its own SQL. Its one member,
`long Version`, is 1 on insert and advanced by exactly 1 by each guarded write that lands.

- **The version is the store's.** It is never supplied by a caller, never chosen by the engine or
  domain code, and never carried in a Platform response — engine-hosting-contract § 6.1.
- **It is not a timestamp and not a token.** Ordering and "advanced by exactly the guarded write" are
  both checkable on a counter and neither is on the alternatives.

### 7. Organizations — `SubZeroDev.Platform.Organizations`

`OrganizationId`, `InvitationId`, `OrganizationRole`, `MembershipState`, `Organization`, `Membership`
and `Invitation` are declared in the tree:
[`Organizations.cs`](../src/SubZeroDev.Platform.Organizations/Organizations.cs).

**What the declarations cannot say.**

- **An organization holds a tenant; it is not one.** One organization has exactly one tenant, minted
  when the organization is created, and **the framework never learns that a tenant has an owner.**
  Making `OrganizationId` and `TenantId` the same value is one fewer column and is refused: it puts a
  module's identity into the framework's most load-bearing type, so `TenantId` would then mean "an
  organization", and a consumer wanting more than one tenant per organization would face a migration
  on every table rather than an added row.
- **`Membership.Principal` is a `PrincipalId`, which is a framework type, and it must never become a
  reference to a user row.** This is what keeps Organizations free of any reference to Identity
  (ADR-006 rule 2) and what lets a `Delegated` principal hold a membership — a membership referencing
  a user row excludes BarStrad's table and SkyNet HR's proxy-asserted operator by construction.
- **`Invitation.TokenHash` is a hash and the token is never stored.** The token is returned once at
  mint and is never readable again, so a database read cannot be replayed into a membership.
- **D5 mints and redeems an invitation; it does not deliver one.** Delivery is Notifications, which is
  D4 and is not in this effort. Nothing in the brief's Organizations criterion requires an email.
- **The active organization is a per-request selection, never a stored preference**, and there is no
  column for one.

### 8. Billing — `SubZeroDev.Platform.Billing`

`PlanKey`, `SubscriptionId`, `SubscriptionState`, `Plan`, `Subscription` and `ProviderEventReceipt`
are declared in the tree: [`Billing.cs`](../src/SubZeroDev.Platform.Billing/Billing.cs).

**What the declarations cannot say.**

- **The inbound event's own shape is not contract-fixed.** `ProviderEvent`, declared in the same
  file, is Billing's, because D5 integrates no real provider and a shape fixed here before one exists
  would be fixed against nothing. What this document fixes about the seam is the paragraph below and
  `ProviderEventReceipt`'s idempotency, not the payload.

- **`SubscriptionState` must never be read outside this module**, and an architecture check enforces
  it (I-C8). Product code consumes `EntitlementDecision`; it does not learn that a subscription exists.
- **Entitlement is derived from the plan and the subscription's state against `IClock`, never
  stored.** A plan transition takes effect by changing one row rather than by a fan-out that can be
  interrupted halfway.
- **`ProviderReference` is opaque and is never parsed.** It identifies the subscription to whichever
  provider owns it, and Platform has no opinion about which one that is.
- **The provider seam is asynchronous now, though D5 integrates no provider.** A provider's
  authoritative state arrives as an inbound event that updates the stored subscription; entitlement
  always reads stored state. A synchronous seam is a rewrite when the first real provider arrives and
  a network call on the request path in the meantime. **No provider is contacted on the request path,
  at startup, or on readiness.**
- **`ProviderEventReceipt` exists so a redelivered event is a no-op**, which every provider requires
  and none guarantees. A duplicate is idempotent success, not an error.

### 9. Licensing — `SubZeroDev.Platform.Licensing`

`LicenceTier` (with `Community`), `LicenceVerificationOutcome`, `LicenceClaims` and
`LicenceSigningKey` are declared in the tree:
[`Licensing.cs`](../src/SubZeroDev.Platform.Licensing/Licensing.cs), which also declares what a
deployment supplies (`LicensingOptions`), the revocation extension point (`IRevocationCheck`) and the
read of the claims in force that *Public surface*, § 10 gives Licensing (`ILicenceState`).

**What the declarations cannot say.**

- **`LicensingOptions` has no grace member and none may be added** (I-L6) — the bullet below says why,
  and a declaration can only be silent about a member that is absent.

- **The licence document is an input file and is never persisted by Platform.** What is persisted is
  one record for the installation — *Persisted schemas*, § 4.
- **The installation is the database, not the machine.** Machine activation and seat enforcement are
  brief non-goals, and a shared store with several hosts is the operated shape; one row keyed by the
  store is the only reading that makes both work.
- **`ExpiresAt` and `GraceEndsAt` are decided at verification and stored, not derived from when an
  error happened.** This is what makes the brief's "an operational verification error uses the stored
  claims without extending their recorded expiry or grace" true rather than aspirational: a deployment
  that errors on every verification cannot renew its own grace, because **nothing on any error path
  writes those instants.**
- **Grace comes from the document, defaulting to thirty days when the document does not name one**
  ([`tenancy-billing-licensing.md`](../docs/docs/tenancy-billing-licensing.md), *Enforcement rules*).
  It is deliberately **not** a deployment setting: a grace window an operator can set is a grace
  window with no end.
- **The accepted public keys are supplied by the consuming product, not compiled into Platform.**
  Platform is not a licensor. A deployment accepts an *ordered set* so key rotation does not need a
  flag day, and the stored record names which key verified the document. This narrows
  [`tenancy-billing-licensing.md`](../docs/docs/tenancy-billing-licensing.md)'s "public key compiled
  into the build", which is the Automator's arrangement and not Platform's.
- **`Invalid` never grants a tier.** A tampered or invalidly signed document is ignored entirely and
  falls back to previously verified claims, or to `Community` when none exist. It is not "the claims
  it would have granted, unverified".
- **The revocation seam is registered or absent, and it is consulted on no path a deployment without
  outbound network takes** — never on the request path, never at startup, never on readiness (I-L5).

### 10. Mcp — `SubZeroDev.Platform.Mcp`

`Platform.Mcp` adopts the official MCP C# SDK for the transport, the session and the filter pipeline,
and **projects to it at the boundary**: no SDK type appears anywhere in Platform's public surface. The
types below are Platform's own, and the module maps them to the SDK's `Tool` and `McpServerTool` on
the way out. The evaluation and the alternatives rejected are in
[`90-decisions.md`](90-decisions.md), 2026-08-29.

`ToolName`, `ToolProducerName`, `ToolDefinition`, `ToolRegistration`, `IToolProducer` and
`IToolCatalogue` are declared in the tree: [`Tool.cs`](../src/SubZeroDev.Platform.Mcp/Tool.cs). The
catalogue's only implementation is internal to the module
([`ToolCatalogue.cs`](../src/SubZeroDev.Platform.Mcp/ToolCatalogue.cs)), so a consumer reaches the
frozen catalogue through the interface and has no other way in.

**What the declarations cannot say.**

- **The SDK is a dependency of the implementation, never of the contract.** `ModelContextProtocol.Core`
  and `ModelContextProtocol.AspNetCore` are referenced by `SubZeroDev.Platform.Mcp` and by nothing
  else, and no Platform type exposes, returns, accepts or derives from an SDK type. The SDK went from
  v1.0 to v2.0 in four months; the projection is what keeps that churn from being a Platform breaking
  change, and it is the entire reason the adoption is shaped this way.
- **`ParameterSchema` is a `JsonElement` because one of the two producers has no .NET method to infer
  a schema from.** A manifest supplies the schema as data. A shape that could only derive a schema by
  reflecting over a method signature would privilege the fixed-table producer and defeat `I-M7`, which
  is the requirement that put Mcp in this brief.
- **Exposure is not on `ToolDefinition`, and that split is what makes default-closed structural.** A
  producer supplies a definition and cannot state that it is exposed; the catalogue combines
  definitions with configuration and produces the registration. A single type with an exposure field a
  producer fills in makes default-closed a rule every producer has to remember, which is the automatic
  exposure on install that
  [`second-consumer-packages.md`](../docs/docs/second-consumer-packages.md) §4 refuses outright.
- **`IToolCatalogue` exposes no way to reach an unexposed registration**, so "neither listed nor
  callable" is a property of the interface rather than of its callers. There is no `All`, no
  `TryGet` that ignores exposure, and none may be added — a diagnostic that can enumerate hidden tools
  is the disclosure `I-M4` exists to prevent.
- **`ProduceAsync` runs once, at startup, and nothing calls it again.** A manifest read is I/O, which
  is why it is asynchronous; the catalogue it feeds is frozen afterwards.
- **`RequiredPermission` is not optional.** Every tool declares one. An unauthenticated or
  unauthorized tool is expressed as a permission the composition grants, never as an absent check —
  the same rule the composition provider follows for an anonymous endpoint (*Public surface*, § 3).
- **`RequiredFeature` is optional, and null means the tool admits no new paid-feature work**, on the
  same terms as an endpoint that only reads (*Public surface*, § 11).

### 11. Shared web UI

**No .NET type, and no package a backend could reference.** The web shell consumes the public HTTP API
over the network ([`10-design.md`](10-design.md) § *Module boundaries* 1 and 4), which is what makes
the brief's "backend packages build and run with no reference to the UI package" provable by the build
rather than asserted. Its delivery shape is [`10-design.md`](10-design.md) § *Open questions* 2, resolved
2026-09-21 as a separate front-end build with no .NET package, and does not affect this document: it
introduces no .NET declaration.

The shell's contract is therefore a constraint rather than a type, and it is I-W1.

### 12. The generic bearer path — `SubZeroDev.Platform.Identity`

**No public .NET type, and none may be added.** The generic path's public contract is its
configuration schema ([`10-design.md`](10-design.md) § *Data model* 10). A vendor configuration package
writes these keys without referencing a Platform package, so a C# settings type would be a second
contract that no vendor package could compile against. The keys and their meanings are the contract.
The values they bind to are internal to Identity. The choices below that go past the design are in
[`90-decisions.md`](90-decisions.md), 2026-09-30.

**One provider per child section of `Platform:Identity:Bearer`.** When the host registers the Identity
module, every child section `Platform:Identity:Bearer:<name>` becomes one generic-path provider.
`<name>` is its `IAuthenticationProvider.Name`. With no child section there is no generic-path provider
and no key fetch. The provider is registered like any other authentication provider, so two providers
sharing a name raise `DuplicateProviderName` (*Error semantics* § 9). Configuration keys compare
case-insensitively, so two sections whose names differ only in case are one section.

| Key under `<name>` | Required | Meaning and constraint |
|---|---|---|
| `Issuer` | exactly one of `Issuer` and `IssuerPattern` | The expected `iss`, compared ordinally. An absolute URI. |
| `IssuerPattern` | exactly one of `Issuer` and `IssuerPattern` | An issuer string containing the placeholder `{tenantid}` exactly once. The placeholder matches one or more characters, none of them `/`. Every other character matches itself ordinally. For an issuer that mints one issuer string per customer tenant. |
| `Discovery` | exactly one of `Discovery` and `SigningKeys` | The issuer's OpenID Connect discovery address: an absolute `https` URI. The key set is found through it. |
| `SigningKeys` | exactly one of `Discovery` and `SigningKeys` | A fixed JSON Web Key Set document, given inline as one string, for an issuer with no discovery. Only public keys. A key with private members is malformed. |
| `Audiences` | yes | A non-empty array of non-empty strings. A token must name at least one of them in `aud`. |
| `Algorithms` | no | An array drawn from `RS256`, `RS384`, `RS512`, `PS256`, `PS384`, `PS512`, `ES256`, `ES384` and `ES512`. Default `[RS256]`. Any other value is malformed, including every `HS*` value and `none`. A shared secret stays the test-grade provider's (I-I7). |
| `ClockTolerance` | no | The skew allowed on `exp` and `nbf`: a non-negative duration of at most `00:05:00`. Default `00:01:00`. |
| `SubjectClaim` | no | The claim whose string value becomes `PrincipalId.Subject`. Default `sub`. |
| `DisplayNameClaim` | no | The claim whose string value becomes `Principal.DisplayName`. When absent, there is no display name. |
| `KeyRefreshInterval` | no | Only valid with `Discovery`. At least `00:00:30` and at most `1.00:00:00`. Default `00:05:00`. |

**What the table cannot say.**

- **The key set is the whole contract, and a key outside it fails startup.** Any key under
  `Platform:Identity:Bearer:<name>` that the table does not name is unrecognised. The same holds for a
  value where the table expects a section, and a section where it expects a value. The host fails
  startup (*Error semantics* § 9). An unrecognised key is most often a vendor dialect the generic path
  does not yet express. Ignoring it would validate tokens against settings the operator did not write.
  The fix is a generic-path capability, never a workaround in a vendor package.
- **Keys are added and never withdrawn or redefined.** Once a vendor package writes a key, the key is
  public contract. A key's meaning never narrows. Its default never changes in a way that accepts a
  token the old default rejected.
- **Identity does not know which configuration source wrote a key.** A key written by a vendor
  configuration package and the same key in a settings file are the same setting. Where both write it,
  the host's configuration precedence decides, as it does for any other key. Identity adds no
  precedence of its own (I-I14).
- **The principal is `Account`.** Its `PrincipalId` is the validated `iss`, as the token carries it,
  paired with the value of the configured subject claim, also as the token carries it. For an
  `IssuerPattern` provider the validated `iss` is the concrete string the token carried, never the
  pattern, so two customer tenants of one issuer are two issuers (I-I11). Neither half is trimmed or
  case-folded (I-I2). A missing, empty or non-string subject claim rejects the credential.
- **The display-name claim is the only other claim the provider reads into a named field.** The rest
  stay in `Principal.Claims`, which no Platform decision reads (I-I6).
- **With `Issuer` and `Discovery`, the discovery document's `issuer` must equal `Issuer` ordinally.**
  With `IssuerPattern`, it must equal the pattern string ordinally. A document that names another
  issuer is a failed fetch. OpenID Connect Discovery requires the match, and the check is what stops a
  misdirected discovery address from supplying keys for an issuer it does not speak for.
- **`SigningKeys` is never fetched and never refreshed.** Its provider has key material from startup,
  and its readiness check always reports healthy. Rotation for such an issuer is a configuration change
  and a restart.
- **A `Discovery` provider fetches, holds and refreshes its own key set**, per instance and never on
  the request path (*Public surface* § 10).
- **No `Microsoft.IdentityModel.*` type appears in Platform's public surface** (I-I12). The libraries
  are referenced by `SubZeroDev.Platform.Identity` and by nothing else, on the same terms, and for the
  same reason, as the MCP SDK (*Types* § 10).

### 13. The sign-in module — `SubZeroDev.Platform.SignIn`

Settings, one set per sign-in method, read from configuration under `SubZeroDev:SignIn:<Name>`. The
schema is public contract, like § 12's.

| Key | Required | Rule |
|---|---|---|
| `Issuer` | yes | names a configured generic-path provider's issuer; a name that matches none fails startup |
| `ClientId` | yes | non-empty |
| `RedirectPath` | yes | a relative path beginning with `/` |
| `Scopes` | no | defaults to `openid`; must include `openid` |
| `EndSessionTemplate` | no | placeholders limited to `{client_id}`, `{return_to}`, `{id_token_hint}`; any other fails startup. `{id_token_hint}` expands to an empty string, as no id token is kept (decision 2026-10-08, #94: id token) |
| `ExtraAuthorizeParameters` | no | name and value pairs. A name that is one of `client_id`, `redirect_uri`, `response_type`, `scope`, `state`, `code_challenge`, `code_challenge_method` fails startup |
| `PostSignOutPath` | no | a relative path, `/` by default |

There is no client-secret key. An unrecognised key fails startup naming the full key (I-I10).

```csharp
public sealed record AuthorizeRequestContext(string Method, Uri Address, IReadOnlyDictionary<string, string> Parameters);
public sealed record EndSessionContext(string Method, Uri Address);
```

---

### 14. The edge's streamed routes — `SubZeroDev.Platform.GameEdge`

The edge's contract is G1's (`g1/20-contract.md`, *Edge — options, forwarding, and readiness*), as
amended by G2. This section declares only what #110 adds. It **amends** G1's `GameEdgeOptions` with
one optional property, and adds two types.

Settings, read from configuration under `GameEdge:StreamingRoutes`, a list. The schema is public
contract.

| Key | Required | Rule |
|---|---|---|
| `StreamingRoutes` | no | absent or empty means no route streams, which is G1's edge unchanged |
| `StreamingRoutes:<n>:PathPrefix` | yes, per entry | begins with `/`; is not `/` alone; has no trailing `/`, `?` or `#`; is not equal to, or a segment-prefix of, another entry's prefix (ordinal) |
| `StreamingRoutes:<n>:FirstByteTimeout` | yes, per entry | greater than zero and no greater than `ForwardTimeout`; no default |

A breach fails startup with the edge's existing configuration failure, naming the full key (for
example `GameEdge:StreamingRoutes:0:FirstByteTimeout`). An overlap names the later entry's key only.

`GameEdgeOptions.StreamingRoutes` and `StreamingRoute` (`PathPrefix`, `FirstByteTimeout`, both
`required`) are declared in
[`Options.cs`](../workloads/game-edge/SubZeroDev.Platform.GameEdge/Options.cs); G1's and G2's members
of `GameEdgeOptions` are unchanged. `StreamedResponse(int StatusCode, string? ContentType, Stream Body)`,
an `IAsyncDisposable` record, is declared in
[`Forwarding.cs`](../workloads/game-edge/SubZeroDev.Platform.GameEdge/Forwarding.cs).

`StreamedResponse.Body` owns the upstream response. Disposing it releases the workload connection,
which is how a caller's disconnect reaches the workload.

---

### 15. Runtime settings — `SubZeroDev.Platform.RuntimeSettings`

```csharp
public readonly record struct SettingName(string Value);   // non-empty; ordinal; never parsed; Platform. prefix reserved

public enum SettingLayer { Global, Tenant, User }

public abstract class SettingDefinition
{
    private protected SettingDefinition(SettingName name, IReadOnlySet<SettingLayer> layers);
    public SettingName Name { get; }
    public IReadOnlySet<SettingLayer> Layers { get; }

    public static SettingDefinition<bool> Boolean(SettingName name, bool defaultValue, IReadOnlySet<SettingLayer> layers);
    public static SettingDefinition<long> Integer(SettingName name, long defaultValue, IReadOnlySet<SettingLayer> layers, Func<long, bool>? isValid = null);
    public static SettingDefinition<string> Text(SettingName name, string defaultValue, IReadOnlySet<SettingLayer> layers, Func<string, bool>? isValid = null);
}

public sealed class SettingDefinition<T> : SettingDefinition where T : notnull
{
    public T Default { get; }
    public bool IsValid(T value);
}

public interface ISettingCatalog
{
    IReadOnlyCollection<SettingDefinition> Declares { get; }
}

public sealed record ResolvedSetting<T>(T Value, SettingLayer? From) where T : notnull;   // From null: the declared default

public static class RuntimeSettingsPermissions
{
    public static PermissionName WriteGlobal { get; }   // "Platform.RuntimeSettings.WriteGlobal"
    public static PermissionName WriteTenant { get; }   // "Platform.RuntimeSettings.WriteTenant"
}

public static class RuntimeSettingsAuditActions
{
    public static AuditAction SettingSet { get; }       // "platform.runtime-settings.set"
    public static AuditAction SettingCleared { get; }   // "platform.runtime-settings.cleared"
}
```

**What the declarations cannot say.**

- **A setting of any type other than `bool`, `long` or `string`.** The base constructor is
  `private protected`, and only the three factories construct a definition.
- **A user value for a principal that is not an `Account`.** The layer does not exist for it (I-ST1).
- **A secret.** A name matching the redaction marker set fails startup (I-ST2). Secrets are startup
  configuration.
- **A grant.** No type here is a permission provider or an entitlement contributor (I-ST6).

---

### 16. Accounts — `SubZeroDev.Platform.Identity`

Present only when the host registers `IdentityAccountsModule` (*Public surface* § 19).

```csharp
public readonly record struct AccountId(string Value)
{
    public const string PrincipalIssuer = "platform.account";
    public PrincipalId ToPrincipalId();
}

public sealed record LinkedIdentity(PrincipalId Identity, DateTimeOffset LinkedAt);

public sealed record Account(AccountId Id, DateTimeOffset CreatedAt, IReadOnlyList<LinkedIdentity> Identities);
```

**What the declarations cannot say.**

- **`AccountId.Value` is opaque.** Identity mints it as 32 lowercase hexadecimal characters from 128 random
  bits. Nothing parses it, and nothing derives it from a claim. `ToPrincipalId()` is
  `new PrincipalId(PrincipalIssuer, Value)`. Identity finds an account from a principal by ordinal equality on
  both halves, never by parsing (I-I2).
- **An account principal is kind `Account` with issuer `platform.account`.** Both conditions are needed. A
  `Delegated` principal never has that issuer (I-I21). A provider-established principal carrying that issuer is
  rejected, so no issuer can impersonate the account namespace.
- **`Account` carries no email, no display name, no profile and no claim, and none may be added.** The
  display name on an account principal comes from the credential presented on the request (I-I17).
- **`Identities` is ordered by `LinkedAt`, then by issuer and subject ordinally, and is never empty** (I-I19).

---

## Persisted schemas

Nothing in the framework's D5 additions is persisted except audit, whose storage is a module, and Persistence's inbox record (§ 8). A
host with no Identity, Organizations, Billing or Licensing package has no table those capabilities
would own and no migration to skip, and a host that takes Identity without `IdentityAccountsModule` has no Identity table — that property is what the brief's local shape forces, and it is
the most consequential thing in this section.

**The migration story for every table below is the same and is stated once: none.** Each is created by
its module's first migration. No consumer runs on Platform today (brief, *Environment and operating
assumptions*), so there is no existing data in any of them and no backfill. Where "none" is a
deliberate constraint rather than an absence, it is called out.

### 1. Audit record — `SubZeroDev.Platform.Audit`

One table. Columns are `AuditEvent`'s members, with the actor stored as **two columns** — issuer and
subject — because `PrincipalId.ToString()` is not injective and must never be split to recover the
pair (*Types*, § 1).

- **Primary key:** the event id. It carries no tenant prefix, unlike every product table: an audit row
  is written *about* a tenant and is not owned by one, and a tenant-keyed audit table would turn the
  cross-tenant queries an operator needs into a cross-partition scan.
- **Indexes:** tenant with instant, correlation, and actor subject with instant. Those are the three
  reads the brief's Audit criterion and the shell's audit view perform. A fourth, **instant alone**,
  serves the retention prune, which otherwise scans the table on every tick. Nothing else is indexed,
  because every index is a write cost on the path every security-sensitive action takes.
- **Constraints:** every column non-null except the two resource columns, which are null together or
  present together. **No unique constraint spans hosts** — appends must not contend.
- **Append-only, enforced by the module's surface having no update or delete path.** The retention
  prune (*Public surface* § 6) deletes whole rows by age. It is background work, not a surface.
- **Migration story: one additive migration, `0002_index_audit_event_occurred_at`**, creating the
  instant-only index on both providers. It is unconditional: the schema does not depend on whether
  retention is set. **Retention itself is not a migration.** It is a setting read at startup, so turning
  it on, changing it or turning it off needs no schema change. Rows already pruned are not recoverable.

### 2. Organizations — `SubZeroDev.Platform.Organizations`

Three tables: organization, membership, invitation.

- **Keys:** organization by id; membership by organization and principal (issuer and subject, two
  columns, same reason as above); invitation by id.
- **`Organization.Tenant` is unique.** This is the constraint that makes "two organizations created
  concurrently cannot share a tenant" a property of the store rather than of a check — I-O2.
- **`Invitation` stores the token hash and never the token**, and redemption is a conditional update
  keyed by that hash, so the answers for "expired", "already redeemed" and "never existed" are
  indistinguishable to the caller (*Error semantics*, § 5).
- **Redemption is a single conditional update against the unredeemed state**, so a token presented
  twice creates at most one membership — I-O3.
- **Migration story: none.**

### 3. Billing — `SubZeroDev.Platform.Billing`

Three tables: plan, subscription, provider event receipt.

- **Keys:** plan by key; subscription by id, with a unique index on tenant so one tenant has at most
  one subscription; receipt by provider and provider event id, which is what makes a redelivered event
  idempotent by the store rather than by a check.
- **A plan's feature set is stored as its own rows**, not as a serialised blob, so a feature can be
  found without parsing every plan.
- **Nothing here stores an entitlement.** Entitlement is derived. A table of resolved entitlements
  would be a fan-out a plan transition can interrupt halfway, which is the thing deriving avoids.
- **Migration story: none.**

### 4. Verified licence — `SubZeroDev.Platform.Licensing`

**One row for the installation.** Not one per host, not one per tenant.

- **Columns:** tier, the granted features, the issue instant, the expiry and grace-end instants **as
  computed at verification**, the verification instant, a fingerprint of the document verified, and
  the id of the key that verified it.
- **Key:** a fixed single-row key. Several hosts share the row; the row is the installation.
- **Written only when the incoming verification instant is later than the stored one.** That monotonic
  guard is what stops a host still holding a replaced document from undoing a newer verification —
  I-L3.
- **No error path writes any column.** `Invalid`, `Unavailable` and `ClockUnusable` leave the row
  untouched, which is what makes stored grace unextendable.
- **Migration story: none.** A fresh installation has no row, and no row means `Community` — that is
  the brief's "a fresh installation with no verified claims continues at Community tier", and it is
  the same rule as the fallback rather than a separate one.

### 5. The shareable marker — `SubZeroDev.Platform.Persistence`

Not a Platform table. A **consumer's** entity type declaring `IShareable` acquires a `SharedAt` column
in that consumer's own migration.

- **`SharedAt` is nullable**, and null is the only representation of "private". There is no separate
  boolean, because two columns that can disagree about the same fact will.
- **The tenant column, the primary keys and the implicit constant are unchanged.** D5 redesigns none
  of them — brief, *Tenancy inherited from D3 and G2*, and a binding non-goal.
- **Migration story for an existing consumer table adopting `IShareable`: an added nullable column, no
  backfill.** Every existing row is private, which is the correct starting state.

### 6. The existing audit columns — `SubZeroDev.Platform.Persistence`

[`IAuditable`](../src/SubZeroDev.Platform.Persistence/Columns.cs) declared `CreatedBy`, `ModifiedBy`
and `DeletedBy` as `string?` in D3, because D3 had no actor. S2 resolved Unresolved item 1:
**`CreatedBy` becomes non-null (`string`)**, matching the ambient principal being total and making
"every row names an actor" a property of the type rather than of the writer — no consumer runs on
Platform today, so the migration cost was zero at the time this was decided
([`90-decisions.md`](90-decisions.md), 2026-08-31).

- **The value written is `PrincipalId.ToString()`**, and it is never split to recover the pair.
- **`ModifiedBy` and `DeletedBy` stay nullable**, because their null means "not yet modified" and
  "not deleted", which is a different fact from "no actor".

### 7. The version column — `SubZeroDev.Platform.Persistence`

Not a Platform table. A **consumer's** entity type declaring `IVersioned` acquires a `version` column in
that consumer's own migration.

| Column | SQLite | PostgreSQL | Constraint |
|---|---|---|---|
| `version` | `INTEGER NOT NULL DEFAULT 1` | `BIGINT NOT NULL DEFAULT 1` | ≥ 1; written only by a guarded write |

- **Migration story for an existing consumer table adopting `IVersioned`: an added non-null column
  defaulting to `1`, no backfill**, in a **new** migration — no existing migration is modified (I-T7).
  Every existing row starts at version 1, which is correct: no reader holds an older one.
- **Platform ships no migration for it.** No migration crosses a module boundary.

### 8. `platform_inbox` — `SubZeroDev.Platform.Persistence`

Platform's own table, beside `platform_outbox`, created by the Platform module's migration
**`0004_create_inbox`** appended to `PlatformMigrationSource` after `0003_create_background_work_lease`.
`0001`–`0003` are not modified (I-T7).

| Column | SQLite | PostgreSQL | Meaning |
|---|---|---|---|
| `message_id` | `BLOB NOT NULL` | `BYTEA NOT NULL` | the outbox message's `OutboxMessageId`, 16 bytes, network byte order — the outbox's identifier encoding |
| `consumer` | `TEXT NOT NULL` | `TEXT NOT NULL` | the handling registration's `EventTypeName` |
| `tenant` | `TEXT NOT NULL` | `TEXT NOT NULL` | the message's tenant, in `platform_outbox.tenant`'s encoding; the implicit tenant's sentinel when the message carries it |
| `processed_at` | `TEXT NOT NULL` | `TEXT NOT NULL` | the commit-side instant, fixed-width ISO-8601 UTC, written by `IProviderCapability.FormatInstant` |
|  | `PRIMARY KEY (message_id, consumer)` | `PRIMARY KEY (message_id, consumer)` | |

- **No payload, no handler output, no error text.** The record is a fact that a commit happened.
- **The tenant is recorded, not keyed.** The message id is unique without it; it is there so a
  tenant's records can be found and erased with the tenant's other rows.
- **Not `ITenantOwned`.** No consumer query reads it; it is Platform-internal, like `platform_outbox`.
- **No foreign key to `platform_outbox`.** Orphans are pruned instead (*Public surface* § 14).
- **Migration story: none.** A new table; the dispatcher already holds while any module has pending
  migrations, so no dispatch runs against a schema without it.

---

### 9. Runtime settings — `SubZeroDev.Platform.RuntimeSettings`

One table, created by the module's migration source (module `RuntimeSettings`, migration
`0001_create_runtime_settings`), with the same text on SQLite and PostgreSQL. **No existing migration
changes** (I-T7).

| Column | Type | Rule |
|---|---|---|
| `tenant` | TEXT NOT NULL | `TenantId.ToString()`. A global row holds `TenantId.Implicit`'s, the all-zero tenant |
| `layer` | TEXT NOT NULL | one of `global`, `tenant`, `user` |
| `name` | TEXT NOT NULL | `SettingName.Value` |
| `principal_issuer` | TEXT NOT NULL | the account's issuer on a user row; `''` otherwise |
| `principal_subject` | TEXT NOT NULL | the account's subject on a user row; `''` otherwise |
| `value` | TEXT NOT NULL | `true`/`false`; an invariant-culture integer; or the text as given |

```sql
PRIMARY KEY (tenant, layer, name, principal_issuer, principal_subject),
CHECK (layer IN ('global', 'tenant', 'user')),
CHECK ((layer = 'user') = (principal_issuer <> '')),
CHECK ((layer = 'user') = (principal_subject <> '')),
CHECK (layer <> 'global' OR tenant = '00000000-0000-0000-0000-000000000000')
```

- **`''` is unambiguous** because neither half of a `PrincipalId` is ever empty, and the pair is stored
  as two columns and never as `ToString()`.
- **A global row is the installation's**, like the verified-licence row (§ 4). Tenant and user rows are
  read and written only with the current tenant (I-ST5).
- **A read is one statement**: `name = @name AND ((layer = 'global') OR (layer = 'tenant' AND tenant =
  @current) OR (layer = 'user' AND tenant = @current AND principal_issuer = @issuer AND
  principal_subject = @subject))`. The branches for layers that do not exist are omitted.
- **A write is `INSERT … ON CONFLICT (…) DO UPDATE SET value = excluded.value`** on both providers. A
  clear is a `DELETE` of the one row.
- **No column records who or when.** The audit record is that history.

---

### 10. Accounts — `SubZeroDev.Platform.Identity`

Two tables, `identity_account` and `identity_account_link`, created by the `IdentityAccounts` module's migration
`0001_create_accounts`. That migration is in the module's own sequence, and only a host that registers the
module runs it.

- **Keys:** `identity_account` by `id`, text. `identity_account_link` by **(`issuer`, `subject`)**: two text
  columns, the pair as the token carried it, never `ToString()` (I-I3), with `account` referencing
  `identity_account(id)` and indexed.
- **The link's primary key is what makes one identity belong to at most one account** — I-I16. Linking an
  identity that is already linked fails at the store. Two concurrent creates or links of the same identity cannot
  both commit.
- **Columns are exactly** `identity_account(id, created_at)` and
  `identity_account_link(issuer, subject, account, linked_at)`, all non-null. **No email, profile, credential,
  token or claim column, and none may be added** — I-I4, I-I17.
- **No tenant column.** An account is per host and not tenant-owned. Neither table is `ITenantOwned`, and no
  tenant filter applies.
- **Unlink takes the account row's write lock before it counts the account's links**, so two concurrent unlinks
  cannot leave an account with none — I-I19.
- **Migration story: none, and enabling the store later is not free.** A host that runs without the store and adds
  it later keeps every existing principal as its raw pair until each identity creates or joins an account. Nothing
  re-keys rows already written under the raw pair.

---

## Public surface

### 1. Ambient identity — `SubZeroDev.Platform.Abstractions`

D5 changes three declarations that exist in the tree. Each is breaking against a published 0.x
surface, and the brief describes those packages as explicitly unstable for exactly this. In
[`OperationContext.cs`](../src/SubZeroDev.Platform.Abstractions/OperationContext.cs):

- **`IOperationScope.Principal` becomes `Principal`, non-null.** The raw authentication result stays
  reachable as `Principal.Claims`.
- **`ICurrentPrincipal.Current` becomes `Principal`, non-null.** It still throws
  `PlatformContractViolationException` carrying `ContractViolation.NoAmbientOperationScope` when no
  scope is open — that is the one thing this member may fail for, and it is a caller defect, not an
  absent actor.
- **`IOperationScopeFactory.Begin`'s `ClaimsPrincipal? principal` parameter becomes
  `Principal principal`, non-optional in both overloads.** It must not acquire a default. A default
  would be `Anonymous`, and a call site that meant to pass an authenticated principal and passed
  nothing would silently downgrade the actor on every row it writes — precisely the unfalsifiable
  audit the total principal exists to remove.

**Doing this now costs a breaking change to packages the brief already calls unstable; doing it after
a consumer ships costs the consumer.**

### 2. Authentication seam — `SubZeroDev.Platform.Abstractions`, registered in `SubZeroDev.Platform.Core`

`IAuthenticationProvider`, `IAuthenticationRequest` and `AuthenticationError` are declared in the tree
(S8): [`Authentication.cs`](../src/SubZeroDev.Platform.Abstractions/Authentication.cs). The provider
registry and the chain that runs registered providers in registration order are declared in
[`Authentication.cs`](../src/SubZeroDev.Platform.Core/Authentication.cs).

**What the declaration cannot say.**

- **`AuthenticateAsync` distinguishes "no credential presented" from "a credential was presented and
  failed to validate".** No credential is success carrying `Principal.Anonymous`; a bad credential is
  a failure. Collapsing them makes an absent token indistinguishable from a forged one.
- **A provider that sees a credential of its kind it does not claim answers `CredentialNotClaimed`,
  never `Principal.Anonymous`.** A bearer token naming an issuer the provider does not trust is a
  presented credential, not an absent one. The chain moves to the next provider, exactly as it does
  on `Principal.Anonymous`, so one provider per trusted issuer still chains.
- **A chain that ends with a credential no provider claimed answers `CredentialRejected`, never
  `Principal.Anonymous`** (I-I15). The chain ends there only when no provider established a principal
  and none rejected. The rejection names the first provider, in registry order, that answered
  `CredentialNotClaimed`. **`CredentialNotClaimed` never leaves the chain**: the chain consumes it, and
  no caller ever receives it. A provider that rejects still ends the chain at once, and a chain in
  which every provider saw no credential of its kind still answers `Principal.Anonymous`.
- **It must never block on a fetch from an issuer.** The one store read on this path is Identity's account lookup
  (§ 19), and it exists only when the host registers the account store. Key material is fetched at startup and cached, and the
  generic path refreshes it off the request path (*Types* § 12). A request arriving when no key is
  cached fails with `AuthenticationError.KeyMaterialUnavailable`, which is an authentication failure
  and **never a server error**.
- **Platform retries nothing here.** `PlatformError.IsRetryable` is the caller's signal, not an
  instruction Platform follows itself.
- **`IAuthenticationRequest` is the transport's credential surface, and it exposes headers and nothing
  else.** It must never expose a request body: a credential in a body is a credential in a log.

### 3. Authorization — `SubZeroDev.Platform.Abstractions`, registered in `SubZeroDev.Platform.Core`

`IPermissionProvider`, `IPermissionCatalog` and `IAuthorizationEvaluator` are declared in the tree
(S4): [`Authorization.cs`](../src/SubZeroDev.Platform.Abstractions/Authorization.cs). The
permission-provider registry, the permission-catalog registry, the evaluator and the composition
provider are declared in
[`Authorization.cs`](../src/SubZeroDev.Platform.Core/Authorization.cs).

`IMirroredPermissionProvider` is declared beside `IPermissionProvider` in
[`Authorization.cs`](../src/SubZeroDev.Platform.Abstractions/Authorization.cs) (S39).
`AuthorizationOptions` and `PlatformOptions.Authorization` are declared in
[`PlatformOptions.cs`](../src/SubZeroDev.Platform.Core/PlatformOptions.cs) and bound from
`Platform:Authorization` by
[`PlatformOptionsBinder.cs`](../src/SubZeroDev.Platform.Core/PlatformOptionsBinder.cs).
`MirrorMaximumAge` is `[Fingerprinted]`.

| Key | Required | Constraint |
|---|---|---|
| `Platform:Authorization:MirrorMaximumAge` | no | At least `00:00:30` and at most `1.00:00:00`. Default `00:15:00`. **Unset means the default; no value means unbounded.** |

- **`EvaluateAsync` takes no principal and no tenant.** Both come from the ambient operation scope,
  which fixed them for the request's lifetime. A parameter for either would let a call site evaluate
  in a tenant the request did not resolve, which is the whole of tenant-aware authorization defeated
  at one call.
- **`EvaluateAsync` returns a decision, never a failure result.** A denial is a decision. A provider
  that could not answer returns `AuthorizationError` to the evaluator, which turns it into a denial
  carrying `ProviderFailure` (*Types* § 2) that the caller may retry — *Error semantics*, § 2.
- **`GrantsAsync` returning an error denies; it never grants.** An unreachable store fails closed.
- **A provider returns an error only when it could not answer.** A provider that answered and grants
  nothing returns an empty set. `PermissionDenied` and `ResourceNotVisible` are how a refusing caller
  answers a decision; a provider never returns them. If one does, the evaluator records it as
  `ProviderUnavailable` (*Types* § 2), and the caller answers with a retryable failure, not the answer
  that variant names.
- **A provider must not audit.** The evaluator audits a denial once, so a union across three
  providers does not write three records. An allowed decision is not itself an audited fact: the
  writer performing the action audits it, inside that action's transaction.
- **`IPermissionProvider.Name` must be unique**, and two providers sharing a name is a startup
  failure: a decision naming its source is worthless if two sources share a name.
- **A provider whose grants are a copy of a source someone else revokes implements
  `IMirroredPermissionProvider`.** Roles mirrored from an issuer — Entra app roles, Keycloak realm roles —
  are the case. A provider over a store the operator writes directly does not, and neither of D5's two
  providers does. Not declaring a mirror is a breach of the grant-source rule, undetectable at runtime,
  as reading the token is.
- **`LastSyncedAsync` returns the instant the tenant's most recent successful sync began reading the
  issuer**, taken from `IClock`, or null before the first. The consumer's sync writes it in the same
  transaction as the rows that read produced; a sync whose issuer read failed or was partial writes
  neither (I-A14). It reads the provider's own store and never the issuer: like key material, it is
  never a network fetch on the request path. An error means the provider could not answer.
- **The evaluator reads a mirrored provider's `LastSyncedAsync` before its `GrantsAsync`, and skips
  `GrantsAsync` when the mirror is stale** (I-A13). Stale means null, or older than
  `MirrorMaximumAge` by the evaluating host's `IClock` — strictly older; at exactly the maximum it still
  answers. A stale mirror or a `LastSyncedAsync` error is a provider that could not answer: it
  contributes nothing and is recorded as `ProviderUnavailable` naming it, under every rule *Types* § 2
  states for `ProviderFailure`, registry order included. Reading the stamp first means a sync landing
  between the two reads only makes the grants fresher than the stamp says.
- **The revocation promise for a mirrored role is "within `MirrorMaximumAge` of its removal at the
  issuer"**, plus any clock skew between the host that wrote the stamp and the host evaluating, and any
  lag in the issuer's own reads. Every other grant keeps I-A10's next-request promise. There is one
  maximum per host, it is `[Fingerprinted]`, and **no setting makes it unbounded.**
- **D5 ships exactly two providers, and neither is a role-assignment table:** the composition provider
  and the Organizations provider. The composition provider grants **every permission to the `System`
  kind in the `Local` profile, nothing to `Anonymous` in either profile, and nothing at all in
  `Operated`.** The grant is keyed to the principal *kind*, never to the absence of an authentication
  provider: an endpoint meant for an unauthenticated caller authorizes that read through its own
  registered permission the ordinary way, and **never inherits the local operator's trust.**

### 4. Entitlement — `SubZeroDev.Platform.Abstractions`, registered in `SubZeroDev.Platform.Core`

`IEntitlementContributor` and `IEntitlementEvaluator` are declared in the tree (S7):
[`Entitlement.cs`](../src/SubZeroDev.Platform.Abstractions/Entitlement.cs). The contributor registry,
the evaluator and the Community baseline contributor are declared in
[`Entitlement.cs`](../src/SubZeroDev.Platform.Core/Entitlement.cs).

- **No caller may reach a contributor directly.** Billing and Licensing are not queryable; the
  evaluator is the only surface. This is what keeps the commercial model out of product code.
- **`EvaluateAsync` takes no tenant**, for the same reason authorization's does not.
- **A contributor returning an error contributes nothing and does not fail the evaluation.** The rule
  is *closed for new grants, open for stored claims*: Billing's contributor cannot answer from an
  unreachable store and contributes nothing; Licensing's answers from the record it read at startup
  and holds in memory, so it is unaffected by the store being down.
- **The contributor set is registered explicitly, enumerated at startup, and joins the settings
  fingerprint.** Those three, plus the decision naming its source, are what bound the union's risk of
  one wrong contributor granting everything. The set reaches the fingerprint input as a second
  parameter on [`ISettingsFingerprint.Compute`](../src/SubZeroDev.Platform.Core/SettingsFingerprint.cs),
  settled in S8 — a registration is not a setting, and projecting it onto `PlatformOptions` would have
  made an operator-facing options object carry a value no operator configured.

### 5. Tenant resolution — `SubZeroDev.Platform.Abstractions`, registered in `SubZeroDev.Platform.Core`

`ITenantResolver` is declared in the tree:
[`TenantResolution.cs`](../src/SubZeroDev.Platform.Abstractions/TenantResolution.cs). The registry and
the resolution chain are declared in
[`TenantResolution.cs`](../src/SubZeroDev.Platform.Core/TenantResolution.cs); resolution at the request
boundary, before the scope opens, is in
[`Pipeline.cs`](../src/SubZeroDev.Platform.Hosting/Pipeline.cs).

**What the declaration cannot say.**

- **Resolvers run in registration order and the first that answers wins.** Order is registration
  order, not priority: a priority number is a second ordering that will disagree with the first.
- **A resolver returning null defers; it does not deny.** A resolver that means "this principal may
  not use that tenant" answers null, and the request proceeds in the implicit tenant to be denied by
  authorization — never by a resolver, which has no decision type and must not acquire one.
- **`ICurrentTenant.Current` is unchanged** and still throws on no ambient scope.

### 6. Audit — `SubZeroDev.Platform.Abstractions`, registered in `SubZeroDev.Platform.Core`

`IAuditWriter` and `IAuditSink` are declared in the tree:
[`Audit.cs`](../src/SubZeroDev.Platform.Abstractions/Audit.cs). The default writer, the default log
sink, the sink registry and the redaction boundary are declared in
[`Audit.cs`](../src/SubZeroDev.Platform.Core/Audit.cs) and
[`Redaction.cs`](../src/SubZeroDev.Platform.Core/Redaction.cs); the transaction-aware writer
Persistence installs in place of Core's default is
[`AuditEnlistment.cs`](../src/SubZeroDev.Platform.Persistence/AuditEnlistment.cs).

- **`WriteAsync` on the writer takes no actor, no tenant and no correlation.** All three come from the
  ambient scope. A parameter for any of them is a way to write a record about somebody else.
- **There is no overload taking a payload, a detail string or a changed-field list, and none may be
  added** — *Types*, § 4.
- **`IsDurable` is declared, not inferred**, on the same precedent as
  `IHealthCheck.TouchesExternalDependency`: a property startup must reject on cannot be discovered by
  trying it.
- **The default sink writes to the log and declares `IsDurable == false`.** It is what makes local
  mode work with no audit package installed. **It is never an `Operated` fallback** — I-C2.
- **A successful action that wrote state writes its audit row in the same transaction as the state
  change**, through the existing ambient transaction. A denial, a read, or a failure that wrote
  nothing writes in its own transaction, after the outcome is known.
- **Readiness degrades on an audit-write failure of either class.** A new check
  `platform.audit.sink` joins
  [`PlatformHealthChecks`](../src/SubZeroDev.Platform.Abstractions/WellKnownNames.cs).

**Retention — `SubZeroDev.Platform.Audit`, setting bound in `SubZeroDev.Platform.Core`.** Off by default.
One setting, one piece of background work, and no new public type in the Audit package.

```csharp
namespace SubZeroDev.Platform.Core;

public sealed record PlatformOptions
{
    // existing members unchanged
    public AuditOptions Audit { get; init; } = new();
}

public sealed record AuditOptions
{
    /// Platform:Audit:RetentionDays. Null: rows are kept forever and no prune is registered.
    [Fingerprinted]
    public int? RetentionDays { get; init; }
}
```

```csharp
namespace SubZeroDev.Platform.Audit;

// internal: registered by AuditModule.Register only when PlatformOptions.Audit.RetentionDays has a value
internal sealed class AuditPruneWork(
    IProviderCapability capability,
    ILeaseManager leaseManager,
    PlatformOptions options,
    IClock clock,
    ILogger<AuditPruneWork> logger) : IBackgroundWork
{
    public BackgroundWorkName Name { get; }   // "platform.audit.prune"
    public HostRoles Roles { get; }           // HostRoles.Worker
    public TimeSpan Interval { get; }         // 1 hour, not a setting
    public bool RequiresLease { get; }        // true
    public Task TickAsync(CancellationToken cancellationToken);
}
```

| Setting | Default | Validation |
|---|---|---|
| `Platform:Audit:RetentionDays` | absent: keep forever | absent, empty or whitespace is absent; otherwise a whole number from 1 to 36500 inclusive. Anything else is `ConfigurationError.InvalidSetting` naming the key (*Error semantics* § 9) |

- **Absent means today's behaviour, exactly.** No work is registered, no lease row is written, and nothing
  deletes an audit row (I-U11).
- **Set to *N*, the Audit store module registers `platform.audit.prune`.** It runs in the Worker role,
  hourly, under the lease of that name. Each tick computes one cutoff, `IClock.UtcNow − N days`, from the
  worker's clock. It then deletes rows with `OccurredAt` earlier than the cutoff, oldest first, **at most
  500 rows per statement**, each statement in its own write transaction and never in an ambient one. It
  stops when a statement deletes fewer than 500 rows, after 100 statements, or on cancellation (I-U12).
  Neither number is a setting.
- **A held lease ends the tick silently**, and any other lease error ends it with a warning. A failed
  statement ends the tick with a warning, and the next tick is the retry. **Readiness is not affected.**
- **Pruning is not audited.** A tick that deletes rows logs the count and the cutoff at Information. It
  writes no `AuditEvent`.
- **The read API is unchanged.** A read over a range older than the cutoff returns the rows that remain
  and raises no error. A read that overlaps a running prune may see a range whose oldest rows are already
  gone.
- **Scope.** Installation-wide, across every tenant and both classes. It governs only the Audit store's
  table. A consumer's own durable sink keeps its own policy. Setting the value in a host that does not
  compose the Audit module is not an error, and nothing is pruned. **Archival and export are not provided.**
- **Consumers.** All four get the same setting, and none of it depends on the principal kind. The
  Automator's self-hosted operator chooses a value or leaves it unset. Game Engine as a Service and
  BarStrad, both hosted, can bound a table that grows with every player action or table session.
  SkyNet HR keeps rows forever unless its operator sets a value.

### 7. Shared-read scope — `SubZeroDev.Platform.Persistence`

`ISharedReadScopeFactory` is declared in
[`SharedRead.cs`](../src/SubZeroDev.Platform.Persistence/SharedRead.cs). It is a seam a consumer's own
query code consults rather than a query Platform builds, because Persistence imposes no repository or
ORM ([`d3/90-decisions.md`](d3/90-decisions.md), 2026-08-03).

- **The scope names one type and widens the filter for that type only.** Every other query on the
  request, shareable type or not, stays `tenant equals current`. `IsOpenFor<TEntity>()` is what a
  consumer's own query code checks to apply that widening — Platform does not build the query, it
  only exposes what the scope's state is (I-T2).
- **A write attempted inside the scope throws `PlatformContractViolationException`** carrying a new
  `ContractViolation` variant, rather than returning an error: it is a defect in the caller, not a
  runtime condition — the distinction
  [`Results.cs`](../src/SubZeroDev.Platform.Abstractions/Results.cs) already draws.
- **One audit record per scope, not per row.** A listing that returns four hundred published rows is
  one escape; four hundred records would make the audit trail unreadable at precisely the point it
  matters.
- **A scope left undisposed is bounded by the operation scope's lifetime.** The request ends, the
  ambient context is restored, and the next request does not inherit it.
- **`Open<TEntity>` returns `Result<IDisposable, AuditError>`**: the scope on success, the audit
  write's failure otherwise. The escape's record is `Required`, so a scope whose record could not be
  written is never opened and the filter is left untouched — *Error semantics*, § 4. It stays
  synchronous: the scope's state is ambient, and ambient state set inside an asynchronous method does
  not flow back to its caller.
- **`Open<TEntity>` must not acquire a tenant parameter.** Naming the tenant to read from would turn
  the modelled escape into a cross-tenant fetch, and the point of the scope is that the caller states
  *that* it is crossing, not *whose* rows it wants.

### 8. Composition and startup — `SubZeroDev.Platform.Core`

- **`PlatformOptions` gains the composition profile, marked `[Fingerprinted]`, and an `Audit` record
  whose `RetentionDays` is `[Fingerprinted]`** (§ 6), **and an `Authorization` record whose
  `MirrorMaximumAge` is `[Fingerprinted]`** (§ 3). Retention is fingerprinted because two workers
  disagreeing on it change which rows they share survive; the mirror maximum is, because two hosts
  disagreeing on it answer the same request differently. Adding the members changes the fingerprint's
  format version, `szdfp3` → `szdfp4`, so a rolling deploy reports a mismatch until every host runs the
  new build.
- **Five registries close at startup**, on the shape
  [`Registries.cs`](../src/SubZeroDev.Platform.Core/Registries.cs) already establishes — `Register`
  returning a `Result`, `Registered` in registration order, a one-way `Freeze`: permission providers,
  entitlement contributors, tenant resolvers, audit sinks, authentication providers. All five close in
  [`HostedServices.cs`](../src/SubZeroDev.Platform.Hosting/HostedServices.cs), and the profile is
  validated only after they do — a rule about what is registered cannot be checked while registration
  is still open.
- **The redaction boundary moved from `SubZeroDev.Platform.Observability` to
  [`SubZeroDev.Platform.Core`](../src/SubZeroDev.Platform.Core/Redaction.cs) and became public**,
  because the Audit store module and Mcp both need the same one and neither may reference
  Observability or the other. **It stays non-injectable**: the
  D3 decision that made it fixed rather than configurable is unchanged, and a redaction boundary a
  consumer could replace is not a boundary. Abstractions is not the destination — it exposes contracts
  only and acquires no implementation.
- **Startup validation is stated as I-C1 to I-C4, I-A3 to I-A4 and I-I10**, and every one fails the
  host with a named error rather than degrading it. **A host that cannot state its own composition
  does not serve**, because every guarantee in this document is stated relative to a composition.

### 9. Testing — `SubZeroDev.Platform.Testing`

Fakes for the framework seams only, beside the existing ones in
[`Fakes.cs`](../src/SubZeroDev.Platform.Testing/Fakes.cs): a fake principal of each of the four kinds,
a fake tenant resolver, a fake entitlement contributor, a fake permission provider, an audit
inspector, the composition profile on the test host, and `PermissionProviderHarness`.

**`PermissionProviderHarness`** ([`PermissionProviderHarness.cs`](../src/SubZeroDev.Platform.Testing/PermissionProviderHarness.cs))
is the grant-source rule of *Public surface* § 3 as a test a consumer runs against its own
`IPermissionProvider` (#92). `AssertRevokedGrantDeniesNextRequestAsync` grants, checks the provider
answers with the permission, revokes, and checks the next answer for the same principal object no
longer includes it. `AssertTokenClaimsGrantNothingAsync` asks about a principal whose token asserts
roles and the permission itself, with nothing granted in the provider's own source, and expects
nothing. Each throws `InvalidOperationException` naming the breach, so any test framework reports it.

`AssertIssuerRevocationDeniesWithinBoundAsync` is the same rule for an `IMirroredPermissionProvider`,
revoking where the role lives — at the issuer — rather than in the mirror (#264). It is declared in
[`PermissionProviderHarness.cs`](../src/SubZeroDev.Platform.Testing/PermissionProviderHarness.cs) and
takes the started `IPlatformTestHost`, the provider, the principal, tenant, optional resource and
permission, and three callbacks: grant at the issuer, revoke at the issuer, and sync.

It evaluates through the host's own evaluator in an operation scope for `principal` and `tenant`, and
moves only `host.Clock`. `provider` is the instance registered in `host` — any other instance is
refused with `ArgumentException` before a callback runs — and `sync` runs the consumer's
real sync against the fake issuer the two other callbacks change, stamping from the host's `IClock`. It
grants at the issuer, syncs, and expects `Allowed` with `provider` among the sources. It revokes at the
issuer **without syncing**, advances the clock past the host's `MirrorMaximumAge` by one tick, and
expects `Denied` with `ProviderFailure` naming `provider`. It syncs again and expects `Denied` with no
`ProviderFailure`: the sync carried the revocation and advanced the stamp. It throws
`InvalidOperationException` naming the breach — no grant after grant and sync, a grant surviving past the
maximum, a stamp that advanced without a sync, a revocation the sync did not carry — and names any other
provider that granted the permission, since a grant from elsewhere would prove nothing. A mirrored
provider runs both this and `AssertRevokedGrantDeniesNextRequestAsync`.

The helper declarations are implemented in [`Fakes.cs`](../src/SubZeroDev.Platform.Testing/Fakes.cs).
The getter-only effective composition profile is declared on `IPlatformTestHost` in
[`PlatformTestHost.cs`](../src/SubZeroDev.Platform.Testing/PlatformTestHost.cs).

**Semantics of the helpers.**

- The resolver initially defers (`Tenant == null`). The entitlement contributor initially
  returns successful `false`; the permission provider initially returns a successful empty set.
  Tests can explicitly supply either grants or seam errors through `Response`. The helpers
  return their configured answer, without consulting a store or another module.
- Default registration names are `Platform.Testing.TenantResolver`,
  `Platform.Testing.EntitlementContributor` and `Platform.Testing.PermissionProvider` respectively.
  Tests configure distinct names before registering multiple instances; startup's duplicate-name
  checks are unchanged. These helpers are not automatically registered by the test host.
- The inspector observes only the supplied in-memory sink. Each read returns a read-only snapshot
  in arrival order; neither modifying a returned collection nor later writes can alter an earlier
  snapshot. It is not a durable-store query API. A null sink is rejected at construction.
- The host property reports the effective, startup-validated composition profile. It cannot change
  the running host's profile and introduces no new builder setting or runtime profile branch.
- The new seam methods honour cancellation before returning their configured answer.

- **`FakeCurrentPrincipal.Current` becomes `Principal`, non-null**, following the interface.
- **No fake organization, subscription or licence.** Those are module knowledge, and a framework
  package faking one is ADR-006 rule 1 violated by the test helpers, which is where it is least likely
  to be noticed. **Each module carries its own fakes.**
- **The audit inspector reads records; it does not write or clear them.** A test helper that can
  delete an audit row is a test helper that can be used to prove the wrong thing.

### 10. Modules

**Identity** exposes authentication providers and the generic bearer path's configuration schema
(*Types* § 12). A host registers the module and writes configuration. No registration call exists per
issuer, and no method chains off the module for a vendor. It owns no rows unless the host also registers `IdentityAccountsModule` (§ 19). Its semantics:

- **Settings are validated before any key is fetched, and before the host serves** — path 2, step 8.
  A settings defect fails startup, names the full key, and leaves no state behind (I-I10). No fetch has
  been attempted and no provider has been registered, so the host never served.
- **The key fetch never fails startup** (I-I10). Each `Discovery` provider attempts its first fetch at
  step 8. A provider whose first fetch fails starts with no keys, answers every credential it claims
  with `KeyMaterialUnavailable`, reports not-ready, and keeps trying on its refresh schedule.
- **Every fetch is bounded by a fixed 30-second timeout, which is not a setting.** A fetch that times
  out is a failed fetch. This applies to the first fetch, which startup waits on, and to every refresh.
- **Key refresh is background work. D5 adds one other, the Audit store's retention prune, and only when
  it is configured (§ 6).** Each `Discovery` provider
  registers one `IBackgroundWork` named `platform.identity.key-refresh:<name>`. It runs at the
  provider's `KeyRefreshInterval` in both host roles, with `RequiresLease` false. It writes nothing
  durable. Each instance validates against its own cache, so each must refresh its own, and a lease
  would leave every other instance's cache stale (I-I9).
- **A successful refresh replaces the whole cached set in one atomic swap.** A request reading
  concurrently sees the whole old set or the whole new one, never a mix (I-I9). Instances may hold
  different sets for up to one interval.
- **A failed refresh keeps the previous set, logs, and degrades readiness. It never empties the
  cache** (I-I9). An issuer outage shorter than its own key lifetime is invisible to callers.
- **The interval is a promise the operator makes about the issuer.** A token signed with a newly
  published key is rejected until the next refresh, so the interval must be shorter than the lead time
  the issuer gives between publishing a key and signing with it. A key the issuer withdraws stays
  accepted until the next successful refresh. The interval bounds how long.
- **Readiness is one check per `Discovery` provider**, `platform.identity.key-set:<name>`, of kind
  `Readiness` and criticality `Required`. It reads the cache and the latest fetch outcome, and never
  fetches:

  | Condition | Status |
  |---|---|
  | no key set cached yet | `Unhealthy` |
  | a set is cached, and the latest fetch failed | `Degraded` |
  | a set is cached, and the latest fetch succeeded | `Healthy` |
- **A provider passes on a credential it does not claim.** A bearer token whose `iss` does not match
  the provider's `Issuer` or `IssuerPattern` is `CredentialNotClaimed`, so the chain moves to the next
  provider. A token no registered provider claims ends the chain `CredentialRejected` (*Public
  surface* § 2, I-I15). The test-grade provider answers the same way. A token the provider claims is
  validated in full and answered by *Error semantics* § 1.

**A vendor configuration package** sits outside both tiers and outside the package graph
([`10-design.md`](10-design.md) § *Module boundaries* 3 and 4). It is not a Platform package. This
contract binds its shape because Identity is what reads its output:

- **It is a configuration source**, the same kind of thing a settings file is. It depends on the host's
  configuration abstraction (`Microsoft.Extensions.Configuration.Abstractions`) and on no Platform
  package. The fifth build check fails the build if it references one (I-I13). A package that cannot
  see `IAuthenticationProvider` cannot implement one, so "sugar over the generic path, never a parallel
  implementation" is a property of what the package can reach.
- **It exposes one method per protocol surface the vendor offers.** Each method is an extension on the
  host's configuration builder. It takes the provider name the caller chooses, and writes keys under
  `Platform:Identity:Bearer:<that name>`. A caller can therefore trust two issuers of one vendor. A
  hosted and a self-hosted deployment are one method only when the same keys, with different values,
  describe both. Otherwise they are two methods, and each states in its documentation what the issuer
  must have switched on (self-hosted Supabase's `GOTRUE_OAUTH_SERVER_ENABLED`, for example).
- **It writes only keys *Types* § 12 names.** A key the table does not name fails startup exactly as
  it would from a settings file. A quirk configuration cannot express becomes a generic-path
  capability first: a new row in that table, and a new decision.
- **None is built in D5.** The mechanism is proven without one. A configuration source built the way a
  vendor package would build it lives in a project of its own that references no Platform package, and
  the fifth build check runs over it. The check must fail against a deliberately broken reference
  before it counts. The same keys and values, supplied once by that source and once by an in-memory
  settings file, must yield equal validated settings, and providers that accept and reject the same
  tokens (I-I14).

**Organizations** exposes the organization API — create, invite, redeem, revoke, switch active
organization, list an organization's memberships — plus an `ITenantResolver` and an
`IPermissionProvider`. It depends on the framework's principal and tenant contracts and **has no
knowledge of Identity and none of Billing.**

- **Creating an organization mints the tenant, writes the organization and writes the owner's
  membership in one transaction, with the audit row joining it.** There is no window in which an
  organization exists without an owner, and none in which a tenant is minted for an organization that
  failed to be created.
- **Minting an invitation returns the token exactly once.** No API reads it back.
- **Switching to an organization the principal is not a member of returns not found, never
  forbidden.**

**Billing** exposes an `IEntitlementContributor`, an administration API for plans, subscriptions and
plan transitions, and the inbound provider event seam. **It is scoped by tenant and needs nothing from
Organizations.**

**Licensing** exposes an `IEntitlementContributor` and the revocation extension point, plus a read of
the current `LicenceClaims` for the shell's licence view. **It exposes no verify-now call on the
request path.**

**Audit store** exposes an `IAuditSink` with `IsDurable == true` and a read API scoped by tenant,
instant range, actor and correlation. **It exposes no update and no delete.** When
`Platform:Audit:RetentionDays` is set, it registers the retention prune as internal background work
(§ 6). That work is not a surface, and no caller can invoke it or choose its rows.

**Mcp** exposes tool producer registration and exposure configuration, and consumes the principal,
permission, entitlement and audit seams. It adopts the official MCP C# SDK for the transport and
projects to it at the boundary (*Types*, § 10); its semantics are:

- **Registration and exposure are two facts.** A tool is registered by a producer and exposed by
  configuration, default closed. Automatic exposure on install is refused outright
  ([`second-consumer-packages.md`](../docs/docs/second-consumer-packages.md) §4).
- **Two producers, and neither is privileged**: manifest projection and a product-owned fixed table.
  **A shape that assumes it owns tool definitions cannot serve this**, and that is the criterion the
  ADR-004 evaluation turns on.
- **The catalogue is in-memory and frozen after startup.** Nothing registers, unregisters or
  re-exposes at runtime.
- **The connection authenticates; a call never does.** A `Principal` is established for the
  connection's lifetime and does not change; re-authenticating means a new connection. **Every
  credential exchange happens at the connection and nowhere else** — an argument reaching a tool has
  already entered the model's context, and a credential a model has seen is disclosed.
- **The invocation order is fixed**: look up in the frozen catalogue, resolve the tenant, parse
  arguments against the declared schema, authorize the declared permission scoped to the parsed
  resource when the schema names one, check entitlement when the tool declares a feature, invoke
  inside an operation scope of its own, audit. **Authorization runs before any producer code is
  reached.**
- **The invocation is audited with the tool as the action and no arguments**, which the audit schema
  makes structural rather than a rule this module has to remember.

**RuntimeSettings** exposes a setting reader and writer and nothing over HTTP (*Public surface* § 18).
It consumes the principal, tenant, permission and audit seams, and **contributes no permission
provider and no entitlement contributor.**

**Web shell** exposes nothing on the server — I-W1.

### 11. Request order — `SubZeroDev.Platform.Hosting`

Hosting gains the fixed order, and **it is the most expensive thing in this contract to change later,
because every capability's semantics are stated relative to it**: authenticate at the transport,
resolve the tenant, open the operation scope, authorize, check entitlement if the endpoint admits new
paid-feature work, do the work inside a transaction when it writes. Auditing is not a step of its own:
the evaluator audits a denial at step 4, and the writer performing an action audits it inside that
action's transaction at step 6.

- **Authorization precedes entitlement, and both precede any side effect.** A principal who may not
  perform an action must not learn from the response whether the deployment is entitled to the
  feature, and entitlement resolution may read the store while an authorization denial must not.
  **Reversing them turns every entitlement into an unauthenticated probe.**
- **Tenant resolution precedes authorization**, because permissions are tenant-aware and a decision
  taken before the tenant is known has been taken in the wrong tenant.
- **The scope's tenant and principal are fixed for the request's lifetime.** A membership revoked
  while a request is in flight does not change the request that already resolved. **It denies the
  next request, with no re-authentication in between** — no grant outlives the request that derived
  it (I-A10).
- **The local host takes the same path, with no step skipped and no branch taken.** The absence of the
  four commercial packages is visible in the package graph and invisible in the flow.
- **Only an endpoint that admits new paid-feature work is entitlement-gated, and the endpoint says so
  by declaring no feature.** An endpoint that reads, lists or exports data a tenant already has
  declares none, even when that data was produced under the same feature — entitlement was checked at
  the admission that produced it, and a lapsed licence does not re-ask a question access already
  answered.

**The endpoint's declaration.** Steps 4 and 5 need to know which permission an endpoint requires and
whether it admits new paid-feature work, and the pipeline is where the order is enforced, so the
declaration is metadata Hosting reads rather than a call the handler makes. `EndpointRequirement`,
`EndpointRequirementExemption` and the two conventions that attach them
(`PlatformEndpointConventions`) are declared in the tree:
[`EndpointRequirement.cs`](../src/SubZeroDev.Platform.Hosting/EndpointRequirement.cs).

**What the declarations cannot say.**

- **`RequiredPermission` is not optional, and an endpoint reachable by an unauthenticated caller
  declares one too** — a permission the composition provider grants, never an absent check. This is
  the endpoint half of the rule *Types*, § 10 states for a tool and § 3 states for the composition
  provider, and it is why `Anonymous` earns no blanket grant from either profile.
- **`RequiredFeature` is optional and must not acquire a default.** Null means the endpoint admits no
  new paid-feature work, on the same terms as a tool that declares no feature. A default would make
  "not gated" the outcome of an omission, and the whole point of the null is that it was written down
  by somebody who decided.
- **Every endpoint reachable through the pipeline carries one or the other, checked at startup.**
  Hosting enumerates the mapped endpoints once routing is configured and fails the host with
  `HostStartupError.UndeclaredEndpointRequirement`, naming the route, when an endpoint carries neither
  a requirement nor an exemption. **This is what makes I-R5's premise checkable**: code cannot know
  whether an endpoint admits new paid-feature work, but it can refuse to serve one that never said.
  The cost is that an endpoint Platform did not map — a third-party middleware's, a documentation
  UI's, the probes' own two — needs annotating, and the ones whose mapping returns no convention
  builder cannot be annotated at all and must be exempted where they are added to the route table.
- **An exemption states a reason, and the reason is not optional.** An exemption list nobody can read
  is an ungated surface with an extra step, and the reason is what makes it reviewable — the same
  argument that makes a decision name its source rather than merely being a decision.
- **Platform takes exactly two exemptions: the probes, and the Mcp transport.** The probes must
  answer before a principal can be granted anything: the composition provider grants nothing at all
  in `Operated`, so a probe declaring a permission is a probe denied in every operated host, which
  fails the deployment the probes exist to keep alive. The Mcp transport's permission varies per
  call, so it authorizes and checks entitlement per tool call, inside its own fixed order
  (*Types* § 10), rather than once at the endpoint — its exemption moves the check, it does not
  remove it.
- **The pipeline's authorization check is never resource-scoped.** It passes no `ResourceRef`, and an
  endpoint whose authorization is genuinely per-resource makes a second, explicit `EvaluateAsync` call
  in its handler with the reference it constructed. Mcp can scope its check because the SDK parsed the
  arguments against a declared schema before any producer code ran; HTTP has no equivalent at the
  convention, and a Hosting that read a route value to mint an identifier would be Hosting knowing
  something about the shape of a product's URLs. **The cost, stated: such an endpoint is authorized
  twice, and only the coarse check is enforced by code** — I-R7.
- **The declaration is read by Hosting and by nothing else.** It is not a second surface a handler
  consults to discover what it requires; a handler that branches on its own endpoint's metadata has
  reintroduced the per-handler ordering the fixed order exists to remove.

### 12. Standalone observability startup — `SubZeroDev.Platform.Observability`

`PlatformObservabilityExtensions.AddPlatformObservability` is declared in the tree:
[`PlatformObservabilityExtensions.cs`](../src/SubZeroDev.Platform.Observability/PlatformObservabilityExtensions.cs).
The package cannot use Hosting's startup exception without reversing the dependency edge, so its own
startup-abort wrapper, authorized by
[ADR-008](../docs/docs/adr/ADR-008-observability-startup-failure.md), is declared in the tree:
[`StartupFailure.cs`](../src/SubZeroDev.Platform.Observability/StartupFailure.cs).

- **The standalone path applies the same OTLP endpoint constraint as the Platform-host path.** An
  absent or whitespace `Platform:Telemetry:OtlpEndpoint` means no exporter starts. A present value is
  accepted only when it is an absolute `http` or `https` URI.
- **A present value that violates that constraint aborts registration** with
  `ObservabilityStartupException` carrying `ConfigurationError.InvalidSetting` and naming
  `Platform:Telemetry:OtlpEndpoint`. It is never converted to absence, because a typo must not
  silently disable export.
- **This startup rule does not change exporter-failure semantics.** Once valid configuration has been
  accepted, file and OTLP failures remain behind their bounded non-blocking processors and never
  propagate to application work.


### 13. The sign-in module — `SubZeroDev.Platform.SignIn`

Written against the recommendation of `10-design.md` *Open questions*, 5: the endpoints are mapped on the
operated host that takes Identity.

```csharp
public static IServiceCollection AddPlatformSignIn(this IServiceCollection services);
public static IEndpointRouteBuilder MapPlatformSignIn(this IEndpointRouteBuilder endpoints);
public static IServiceCollection ConfigurePlatformSignIn(this IServiceCollection services, string method, Action<SignInHooks> configure);

public sealed class SignInHooks
{
    public Func<AuthorizeRequestContext, ValueTask<IReadOnlyDictionary<string, string>>>? AdjustAuthorizeRequest { get; set; }
    public Func<EndSessionContext, ValueTask<Uri>>? ReplaceEndSessionAddress { get; set; }
}
```

Endpoints per method: `begin` (GET), `token` (POST, requires the anti-forgery header) and `signout` (POST)
under `/signin/<method>`, and `callback` (GET) at the method's `RedirectPath`; two methods may not share a
`RedirectPath` (decision 2026-10-08, #94: callback path).

- **A caller may rely on** the callback creating a session only after the state matched (I-S1), and on
  the session carrying no more than issuer, subject, access token and its expiry.
- **A caller must never** expect sign-out to revoke an issued access token (I-S2), or the hook to change
  what Platform trusts (I-S3). The hooks receive Platform's records and return a parameter set or a
  `Uri`; no ASP.NET or IdentityModel type crosses this surface (I-I12).
- **Hooks run after configuration binds.** `AdjustAuthorizeRequest` may add parameters and not replace
  `client_id`, `redirect_uri`, `response_type`, `scope`, `state` or the proof key; `ReplaceEndSessionAddress`
  is called after the template or discovery value has produced the address.
- **A vendor configuration package** writes `EndSessionTemplate` and `ExtraAuthorizeParameters` and
  nothing else of this module's keys it did not already write for § 12, and references no Platform
  package (I-I13).
- **Proof without a vendor package:** a fixture issuer whose discovery document has no end-session
  endpoint; sign-out sends the person to the template's address built from the fixture's values.

### 14. Guarded writes and the inbox — `SubZeroDev.Platform.Persistence`

#### The version guard

`IVersionGuard` is in [`VersionGuard.cs`](../src/SubZeroDev.Platform.Persistence/VersionGuard.cs):
`ExecuteAsync(DbCommand guardedWrite, CancellationToken)` runs a consumer's compare-and-swap write on
the ambient Write transaction and returns `Result<TransactionError>`. Platform builds no SQL: the
command is the consumer's own, an UPDATE or DELETE whose predicate includes the row's key, the tenant
and `version = @expected`, and whose UPDATE sets `version = @expected + 1`. Registered by Persistence's
composition alongside `IUnitOfWork`.

- **The guard enlists the command** on the ambient connection and transaction; the consumer does not.
- **Matched rows are the judgement**, and both providers report UPDATE and DELETE matched rows the same
  way, so there is no provider branch and nothing on `IProviderCapability`.
- **Zero rows dooms the unit of work.** `IUnitOfWork.ExecuteAsync` checks the doom before flushing
  staged outbox messages and audit events: a doomed unit rolls back everything — the consumer's other
  writes, staged outbox rows, staged audit — and returns `TransactionError.StaleVersion`, **even if the
  work ignored the guard's result and returned normally**. A unit whose work throws after a doom is
  classified as today; the exception is the newer fact.
- **One answer for gone, changed, and another tenant's.** Zero rows cannot tell them apart and must not
  try: distinguishing "changed" from "not yours" would disclose existence across tenants.
- **Provider behaviour, stated once.** PostgreSQL READ COMMITTED: a concurrent guarded write on the same
  row blocks, then re-evaluates its predicate against the committed version and matches zero rows.
  SQLite: write transactions begin immediate and serialize, so the second reads the advanced version.
- **Inserts are not guarded.** A new row is written with `version = 1`.

#### The inbox

The inbox is the outbox dispatcher's guarantee, not a callable seam: there is no public inbox type.
Its one public change is a prune target, added to
[`ProviderCapability.cs`](../src/SubZeroDev.Platform.Persistence/ProviderCapability.cs):

```csharp
public enum PruneTarget
{
    ProcessedOutboxRows,
    PoisonedOutboxRows,
    DeadHostRegistrations,

    /// <summary>Rows in <c>platform_inbox</c> whose <c>platform_outbox</c> row no longer exists,
    /// recorded before the prune's <c>olderThan</c> instant.</summary>
    OrphanedInboxRecords,
}
```

The internal store is `InboxStore` in [`Inbox.cs`](../src/SubZeroDev.Platform.Persistence/Inbox.cs):
`TryRecordAsync` inserts on the ambient transaction and reports whether the record was new.

- **Dispatch order, inside the handler's one write transaction:** record, then — only if the record
  was inserted — invoke the handler. A handler failure rolls the record back with everything else, so
  a retry is not mistaken for a duplicate.
- **A conflicting insert is a duplicate.** The handler is not invoked; the transaction commits having
  written nothing; the row is marked processed as on any success; no attempt is consumed; one
  Information log line names the message id and consumer, never the payload.
- **Concurrency.** PostgreSQL: a second dispatcher's insert blocks on the first's uncommitted key, then
  does nothing if the first commits, or inserts and runs if the first rolled back. SQLite: the second
  cannot begin until the first commits.
- **Statement text is identical on both providers** (SQLite ≥ 3.24 upsert), so it is a store's, not a
  capability member. The prune's bounded delete joins `PruneSql`'s identical text:
  `DELETE … WHERE (message_id, consumer) IN (SELECT … FROM platform_inbox i WHERE i.processed_at < @olderThan AND NOT EXISTS (SELECT 1 FROM platform_outbox o WHERE o.id = i.message_id) LIMIT @batch)`.
  `PruneWork` runs it hourly under its existing lease with `olderThan` = now and `PruneBatchSize`; no
  new setting. It runs after the three other targets, so a record whose outbox row was pruned goes in the
  same pass, and repeats until a statement deletes fewer than `PruneBatchSize` rows. A failed statement
  logs the shared `"Prune of {Target} failed: {Code}."` Warning and ends the repetition.
- **Exactly-once is for database effects only.** A handler's external side effect — an e-mail, a call to
  a provider — is not inside the transaction, and the handler stays responsible for its idempotency.
- **The lost-claim warning changes wording**, because a lost claim no longer re-applies database effects:
  `"Outbox state write for {MessageId} lost its claim; the message may be dispatched again and the inbox will skip it."`

### 15. Module lifecycle hooks — `SubZeroDev.Platform.Abstractions`

Two members are added to [`IPlatformModule`](../src/SubZeroDev.Platform.Abstractions/Modules.cs). Both
are default interface members, so a module that declares neither compiles and behaves as before. Hosting
runs them. Nothing else calls them. They are
`InitializeAsync(IServiceProvider services, CancellationToken cancellationToken)` and
`ShutdownAsync(IServiceProvider services, CancellationToken cancellationToken)`, each returning `Task` and
defaulting to `Task.CompletedTask`.

`HostStartupError` ([`StartupFailure.cs`](../src/SubZeroDev.Platform.Hosting/StartupFailure.cs)) gains one
variant, `ModuleInitialization(ModuleName module, string detail)`.

**Semantics.**

- **When the hooks run.** `InitializeAsync` runs once per host start, on both roles and in the test host,
  after every registry has frozen and every `StartingAsync` check has passed, and before the first
  background tick and the first request. It runs in the topological order module composition resolved,
  one module at a time; a module's hook starts only after its dependencies' hooks have completed.
  **Migrate mode never runs either hook.**
- **The provider.** Each call receives the provider of a fresh service scope, disposed when the call
  returns. No ambient operation scope is open. The one exception is a `ShutdownAsync` run by disposal
  after a failed startup: the container has already marked itself disposed by then, so no scope can be
  created and the provider resolves nothing. A module that must release something on that path holds
  it itself. A hook that writes opens one through
  `IOperationScopeFactory.Begin`, stating its tenant, as any originating code does.
- **Registration is closed.** A hook resolves services; it cannot register them, and the registries are
  already frozen.
- **Shutdown.** `ShutdownAsync` runs in exact reverse order, after background work and the listener have
  stopped. Its token is bounded by the host's graceful drain window. It runs **exactly once** for each
  module whose `InitializeAsync` completed, by whichever comes first: the host stopping, or the host
  being disposed after a later startup step failed. It never runs for a module whose `InitializeAsync`
  threw or never ran. A `ShutdownAsync` that throws is logged, and the remaining shutdowns still run.
- **Initialization failure.** An `InitializeAsync` that throws is logged with its exception. The modules
  that already initialized are shut down in reverse, and the host fails with `ModuleInitialization`. Its
  `Detail` names the module and the exception's type, never its message.
- **Concurrency.** Every instance of every role runs every hook, concurrently with the others, including
  an old and a new instance overlapping during a restart. **A hook must not assume it is the only
  process.** Once-per-installation work belongs in a migration or a seeder (§ 16).

### 16. Migration application and seeding — `SubZeroDev.Platform.Persistence`

`IMigrationRunner`, `IModuleMigration`, `IModuleMigrationSource`, `ISeeder` and migrate mode are
declared in the tree, in [`Migrations.cs`](../src/SubZeroDev.Platform.Persistence/Migrations.cs) and
[`MigrateMode.cs`](../src/SubZeroDev.Platform.Persistence/MigrateMode.cs). `MigrationError.SeedFailed` is
in [`Errors.cs`](../src/SubZeroDev.Platform.Persistence/Errors.cs). This section states the seeder rules
the code does not show, and the development-environment application the D3 design promised (D3 contract,
*Unresolved* 8).

**Migrate mode.** `RunPlatformMigrateModeAsync`'s signature is unchanged, and it is still the operator's
explicit operation. In order, it:

1. binds settings, as today;
2. registers the Core ambient defaults a host registers (operation scope, accessors, clock, the
   trace-context codec a scope originates from, and the audit writer a unit of work dispatches to), then
   Persistence;
3. **resolves the module graph and calls each module's `Register` in topological order**. That is what
   `IPlatformModule.Register` already promises. A graph error prints `<code>: <detail>` and exits 1;
4. applies migrations through `IMigrationRunner.ApplyAsync`;
5. **on success, including when nothing was pending, runs every registered `ISeeder`.**

Exit codes are `0` when every migration and seeder succeeded, `2` for `Locked` (no seeder runs), and `1`
for anything else.

**Seeders.**

- **Registration.** A module registers a seeder from `Register`, through
  `TryAddEnumerable(ServiceDescriptor.Singleton<ISeeder, T>())`, the route `IModuleMigrationSource` uses.
  Migrate mode resolves `IEnumerable<ISeeder>` once per run from a service scope it creates. A host
  composes seeders and never invokes them.
- **Order.**
  - Seeders run grouped by `Module`, in the composed modules' topological order.
  - Groups whose `Module` names no composed module run after those groups, ordered by module name,
    ordinally. These are an application's own seeders.
  - Within a group, seeders run in registration order.
  - `Name` is for reporting, and uniqueness is not checked.
- **Tenancy.** Migrate mode opens **no** operation scope. A seeder writes only inside a scope it opened
  through `IOperationScopeFactory.Begin(tenant, Principal.LocalSystem)`, naming the tenant explicitly.
  A write without one is the existing `NoAmbientOperationScope` violation, which migrate mode reports as
  `SeedFailed`.
  - A seeder covers data that exists when migrate mode runs.
  - Data that a tenant needs when it is created belongs to the handler of that creation, not to a
    seeder.
- **Transactions.** A seeder runs its own transactions through `IUnitOfWork`. Migrate mode wraps none,
  so atomicity is per seeder transaction, not per run.
- **Idempotency.** A seeder runs on every migrate mode run, outside the migration lock, and possibly
  concurrently with another run. **It must converge**: any number of runs, sequential or concurrent,
  leave the rows one run leaves. It writes insert-if-absent against a store constraint, not
  read-then-insert.
- **Failure.** A seeder that throws stops the run. Later seeders do not run, and migrations and earlier
  seeders' commits stay. Migrate mode prints `SeedFailed: <detail>` and exits 1. A seeder the container
  cannot construct stops the run before any seeder runs: migrate mode prints `Registration: <message>`
  and exits 1.

**Development-environment application.**

- **Owner and trigger.** `AddPlatformPersistence` adds an internal lifecycle service, idempotently. In
  `StartingAsync`, after `PersistenceStartupCheck`, it calls `IMigrationRunner.ApplyAsync`, **if and only
  if** `PlatformOptions.Environment` equals `Development`, ordinal and ignoring case.
  - That is the same derived, non-bindable notion `PeerHostHealthCheck` uses.
  - No setting enables or disables it, and it has no public surface.
  - It runs on both roles.
- **Lock.** It takes the provider-native migration lock, because it calls the runner and nothing else.
  **There is one path that writes migration history.**
- **Failure behaviour** is stated in *Error semantics* § 13.
- **Phase.** It completes before any `StartAsync` runs. So it completes before module initialization
  (§ 15), the first background tick and the listener.
- **No seeding.** It applies migrations only.

---

### 17. The edge's streamed routes — `SubZeroDev.Platform.GameEdge`

**Amends** G1's `IGameWorkloadForwarder` (*The edge — .NET*) with one method,
`ForwardStreamingAsync(ForwardedRequest request, TimeSpan firstByteTimeout, CancellationToken
cancellationToken)`, returning `Task<Result<StreamedResponse, EdgeError>>`. It is declared in
[`Forwarding.cs`](../workloads/game-edge/SubZeroDev.Platform.GameEdge/Forwarding.cs). `ForwardAsync`,
`ForwardedRequest` and `ForwardedResponse` are unchanged. `firstByteTimeout` bounds the wait for the
workload's headers only; the body is read under the caller's token alone.

`MapGameWorkloadForwarding` keeps its signature. For a request whose path matches a listed prefix
(*Types* § 14), it calls `ForwardStreamingAsync` with that entry's `FirstByteTimeout`. Every other
request takes `ForwardAsync`.

- **A caller may rely on:**
  - the workload's status and `Content-Type` being sent when the workload's headers arrive, before any
    body byte;
  - each piece of the workload's body being flushed to it as it arrives (I-E2);
  - an incomplete response, never a clean end, when the workload fails mid-stream (I-E3);
  - a route not listed behaving exactly as G1 declares (I-E1).
- **A caller must never:**
  - expect an edge-authored status, envelope or frame after headers;
  - expect `ForwardTimeout` to end a streamed response;
  - expect a retry or a resumed stream (I-E4).
- **Matching** is ordinal, case-sensitive and on segment boundaries, against the request path only. The
  method and the query string do not take part. The matcher and the configuration check are `internal`
  and are not surface.
- **Observability.** Event `1101` `EdgeStreamAborted`, at `Warning`, in category
  `SubZeroDev.Platform.GameEdge`. It carries the matched prefix, the correlation id, the code, bytes
  forwarded, milliseconds since headers and the exception's type name. It never carries the request
  path, query, body or the exception's message. Counter `subzerodev.edge.stream.aborts` (`{abort}`) on
  meter `PlatformTelemetry.MeterName`, tagged `route` (the prefix) and `code`. A caller disconnect is
  neither logged above `Debug` nor counted (I-E5).
- **Proof without SkyNet HR:** the test fake workload emits a body incrementally on a route the test
  host lists, then destroys its socket.

---

### 18. Runtime settings — `SubZeroDev.Platform.RuntimeSettings`

```csharp
public interface ISettingReader
{
    Task<Result<ResolvedSetting<T>, SettingError>> GetAsync<T>(
        SettingDefinition<T> setting, CancellationToken cancellationToken) where T : notnull;
}

public interface ISettingWriter
{
    Task<Result<SettingError>> SetAsync<T>(
        SettingDefinition<T> setting, SettingLayer layer, T value, CancellationToken cancellationToken) where T : notnull;

    Task<Result<SettingError>> ClearAsync(
        SettingDefinition setting, SettingLayer layer, CancellationToken cancellationToken);
}

public sealed class RuntimeSettingsModule : IPlatformModule;   // Name "RuntimeSettings"; depends on no module
```

A host composes the module with `services.AddSingleton<IPlatformModule, RuntimeSettingsModule>()`, as
it composes Organizations, and registers each catalogue with
`services.AddSingleton<ISettingCatalog, TCatalog>()`. Registration adds the reader and writer, the
module's `IPermissionCatalog` declaring the two names, the migration source, and the startup
registration that builds the catalogue, which the platform's own lifecycle service runs (see
`90-decisions.md`, 2026-10-09, S43).

- **The catalogue is built in `StartingAsync` and frozen** before any hosted service's `StartAsync` and
  before the host serves (I-ST2). Its defects are in *Error semantics* § 9.
- **The definition passed is looked up by name in the frozen catalogue**, and the catalogue's default,
  layers and rule are the ones applied. A name the catalogue lacks, or declares with another type, is
  `NotDeclared`.
- **`GetAsync` resolves user, then tenant, then global, then the default**, over the layers that both
  exist (*10-design* *Data model* § 13) and are admitted by the declaration (I-ST1). `From` names the
  layer that answered, or is null for the default. It reads through every time (I-ST7).
- **`SetAsync` and `ClearAsync` check in this order**, and stop at the first failure: declared; layer
  admitted; layer exists; value valid (set only); permission (global and tenant only, evaluated by
  `IAuthorizationEvaluator` with `ResourceRef("RuntimeSetting", <name>)`); then one transaction holding
  the upsert or delete and its audit record (I-ST3, I-ST4).
- **A user-layer write needs no permission** and reaches only the ambient `Account`'s own row.
- **The audit record is `Required`**, with action `SettingSet` or `SettingCleared` and resource
  `ResourceRef("RuntimeSetting", "<layer>/<name>")`, where the layer is lowercase. It carries no value.
  A clear that found no row succeeds and writes no record.
- **A caller may rely on** a committed write being seen by the next read on any instance (I-ST7), and
  on no setting changing a permission, an entitlement or a startup option (I-ST6, I-ST8).
- **A caller must never** store a secret in a setting, expect a setting to appear in the fingerprint,
  or expect an HTTP endpoint from this module. The product maps its own, under its own requirements.

---

### 19. Accounts — `SubZeroDev.Platform.Identity`

```csharp
public sealed class IdentityAccountsModule : IPlatformModule
{
    public ModuleName Name { get; }                              // "IdentityAccounts"
    public IReadOnlyCollection<ModuleName> DependsOn { get; }    // ["Identity"]
    public void Register(IServiceCollection services);
}

public interface IAccountApi
{
    Task<Result<Account, AccountError>> CreateAccountAsync(CancellationToken cancellationToken);
    Task<Result<Account, AccountError>> GetCurrentAccountAsync(CancellationToken cancellationToken);
    Task<Result<Account, AccountError>> LinkAsync(string secondCredential, CancellationToken cancellationToken);
    Task<Result<Account, AccountError>> UnlinkAsync(PrincipalId identity, CancellationToken cancellationToken);
}

public static class IdentityAuditActions
{
    public static AuditAction AccountCreated { get; }    // "platform.identity.account-created"
    public static AuditAction IdentityLinked { get; }    // "platform.identity.identity-linked"
    public static AuditAction IdentityUnlinked { get; }  // "platform.identity.identity-unlinked"
}
```

Registering `IdentityAccountsModule` without `IdentityModule` fails startup through the module graph. No setting is
added: `Platform:Identity` gains no key, and Identity still exposes no settings type.

- **Mapping.** With the store registered, every principal the generic path or the test-grade provider establishes
  is looked up by its (issuer, subject) after validation. A linked identity's principal is its account's
  (*Types* § 16). An unlinked identity's principal is unchanged. The upstream-proxy provider and any provider
  the consumer registers are never mapped. The lookup is not cached. A store failure answers
  `AuthenticationError.ProviderFailed`, **never the unlinked pair** (I-I20).
- **`CreateAccountAsync`** is for an ambient `Account` principal that an Identity provider established and that no
  account links. It creates an account whose one identity is that pair. The account row, the link row and the
  `Required` audit row share one transaction. **The caller's principal changes from the next request on.** Rows
  the identity wrote under its raw pair are not re-keyed, so a consumer creates the account before writing
  anything it wants the account to own.
- **`LinkAsync`** requires the ambient principal to be an account principal. `secondCredential` is a compact
  bearer token. The host reads it from a request header, never from a body, on the terms of § 2. Identity
  validates it with its own `Account`-producing providers, with mapping off, and **also requires its `iat` to be
  no earlier than five minutes before the call** (the provider's `ClockTolerance` applies either side). A token
  without `iat` is refused. The five minutes is fixed and is not a setting. The validated pair is linked to the
  caller's account in one transaction with its `Required` audit row. Linking a pair the account already holds
  succeeds and writes nothing. **This is the only way a second identity joins an account** (I-I18).
- **`UnlinkAsync`** removes one pair from the caller's account, unless it is the last (I-I19). It may remove the
  pair the caller is presenting, and that credential is its raw pair from the next request on.
- **`GetCurrentAccountAsync`** answers the caller's account and its identities.
- **The sign-in module is unchanged.** It is how a person usually obtains `secondCredential`: they begin method B
  while signed in to the host through method A, and take B's token from B's token endpoint (`10-design.md`
  *Data model* § 11, § 14). The module never references Identity (I-C7).
- **A caller may rely on** two identities that share every claim but their subject pair being two principals until
  `LinkAsync` joins them (I-I17).
- **A caller must never** create, find or link an account by email or any other claim, or expect an account to
  span hosts.
- **No permission is declared.** The API acts only on the caller's own account. The host's endpoint declares the
  permission its composition grants an authenticated caller.

---

## Error semantics

Every error below is a `PlatformError` subtype: a stable enumerable code, never a string message, with
`IsRetryable` stated per variant. **No bare exceptions and no string errors cross a module boundary.**
This section never becomes a pointer — a variant's name will be in the tree, but when it fires and
what the caller does about it will not.

### 1. `AuthenticationError` — `SubZeroDev.Platform.Abstractions`

| Variant | Raised when | Retryable | The caller is expected to |
|---|---|---|---|
| `CredentialRejected` | a credential was presented and failed to validate | no | return unauthenticated; **do not fall back to `Anonymous`** |
| `KeyMaterialUnavailable` | no signing key is cached and none may be fetched on the request path | no | return **unauthenticated**, never a server error, and never block on a fetch |
| `ProviderFailed` | the provider itself faulted | no | return unauthenticated and degrade readiness |
| `CredentialNotClaimed` | a provider saw a credential of its kind that it does not claim, such as a bearer token whose `iss` it does not trust | no | **never received**: the chain consumes it, and if no provider claims the credential the chain answers `CredentialRejected` (I-I15) |

**No credential presented is not in this table.** It is success carrying `Principal.Anonymous`. **A
credential nobody claims is not "no credential"**: it reaches the caller as `CredentialRejected`, and
the caller does not fall back to `Anonymous` for it either.

**On the generic bearer path** (*Types* § 12), a provider claims a bearer token whose `iss` matches its
`Issuer` or `IssuerPattern`, and answers any other `CredentialNotClaimed` (*Public surface* § 10). A
token whose `iss` cannot be read at all is `CredentialRejected`. For a token the provider claims, the
variants are fixed as follows:

| Condition | Variant |
|---|---|
| no key set is cached yet | `KeyMaterialUnavailable` |
| the header's `alg` is not in the provider's `Algorithms`, including `none` and every `HS*` value | `CredentialRejected` |
| the token names a key id the cached set does not hold | `CredentialRejected` — **never a fetch** (I-I8) |
| the token names no key id, and no cached key of an accepted algorithm verifies it | `CredentialRejected` — never a fetch |
| the signature does not verify | `CredentialRejected` |
| `exp` or `nbf` is outside the provider's `ClockTolerance`, or `exp` is absent | `CredentialRejected` |
| `aud` names none of the provider's `Audiences` | `CredentialRejected` |
| the subject claim is missing, empty or not a string | `CredentialRejected` |
| the validation library faults on anything other than the token's own content | `ProviderFailed` |

**`KeyMaterialUnavailable` is decided before the token is examined further.** With no keys there is
nothing to validate against, and answering `CredentialRejected` would tell the caller its token was bad
when the provider could not check it. **An unknown key id is never a reason to fetch.** A request-path
fetch keyed on an attacker-chosen key id is an amplifier pointed at the issuer, and the cost of
refusing it is the rotation bound stated in *Public surface* § 10.

### 2. `AuthorizationError` — `SubZeroDev.Platform.Abstractions`

| Variant | Raised when | Retryable | The caller is expected to |
|---|---|---|---|
| `PermissionDenied` | no provider granted, **every provider answered**, and the principal can see the resource | no | return **forbidden** |
| `ResourceNotVisible` | the resource is in another tenant, or the principal may not know it exists | no | return **not found** |
| `ProviderUnavailable` | a provider could not answer — typically an unreachable store, or a mirrored provider never synced or whose last sync is older than `Platform:Authorization:MirrorMaximumAge` (I-A13). Carried on `AuthorizationDecision.ProviderFailure`, never returned by `EvaluateAsync` | **yes** | return a retryable failure — 503 on HTTP, a retryable tool result on Mcp; the denial stands for this request |

**`PermissionDenied` and `ResourceNotVisible` are not interchangeable, and the difference is a
security property rather than a style choice.** *Forbidden* confirms the resource exists. A
cross-tenant read returns not found; switching to an organization the principal is not a member of
returns not found; a permission denial on a resource the principal *can* see returns forbidden,
because there the existence is already known and pretending otherwise only obscures the fix.

**`ProviderUnavailable` is retryable and still denies.** Failing closed and being retryable are not in
tension: the request is denied now, and a caller who retries after the store returns is right to.
**A denial with a provider unable to answer is never answered as `PermissionDenied`, and never as
`ResourceNotVisible`.** Either would tell the client an outage is a missing permission or a missing
resource, and would record as a policy denial one that no policy made.

### 3. `EntitlementError` — `SubZeroDev.Platform.Abstractions`

| Variant | Raised when | Retryable | The caller is expected to |
|---|---|---|---|
| `ContributorUnavailable` | a contributor could not answer | **yes** | contribute nothing; **the evaluation continues** |
| `FeatureNotEntitled` | an endpoint or tool refuses admission on a decision with `Granted == false` | no | return the refusal; **do not name which contributor declined** |

**`ContributorUnavailable` never fails an evaluation.** It is how *closed for new grants, open for
stored claims* is expressed: an unreachable store removes Billing's contribution and leaves Licensing's
in-memory claims standing.

**`FeatureNotEntitled` is raised by the caller, not by the evaluator.** The evaluator returns a
decision; refusing is the endpoint's act. It must not disclose which contributor declined — a
self-hosted deployment's licence state is not an operated caller's business, and the reverse holds too.

### 4. `AuditError` — `SubZeroDev.Platform.Abstractions`

| Variant | Raised when | Retryable | The caller is expected to |
|---|---|---|---|
| `SinkUnavailable` | a sink could not write | **yes** | apply the class rule below |
| `SinkRejected` | a sink refused the record as malformed | no | log, degrade readiness; **still apply the class rule** — under `Required` the caller receives `SinkUnavailable` naming the sink, because the class, not the sink, decides that the response is retryable |

**The class decides the consequence, and the sink does not choose it:**

- **`Required`** — the response becomes a retryable failure and readiness degrades. Applies to
  authorization denials, shared-resource escapes, membership and ownership changes, entitlement and
  licence transitions, and MCP invocations.
- **`Recorded`** — logged, readiness degrades, the response is unaffected. Everything else.

**No `Required` write's result may be discarded.** An authorization denial carries it on
`AuthorizationDecision.AuditFailure` (*Types* § 2), a shared-read escape returns it from
`Open<TEntity>` (*Types* § 7), and an MCP invocation or refusal whose record cannot be written answers
a retryable tool result rather than its outcome.

**A single class was rejected in both directions.** All-`Required` makes an audit outage a total
outage, which is the self-inflicted outage
[`tenancy-billing-licensing.md`](../docs/docs/tenancy-billing-licensing.md) refuses elsewhere.
All-`Recorded` makes the brief's "allowed, denied and failed actions persist" true only when nothing is
wrong, which is not what a security control is for.

**An audit write inside the action's transaction needs no class handling**: the action rolls back,
which is atomic in both directions — no committed change without its audit row, and no audit row for a
change that rolled back.

### 5. `OrganizationError` — `SubZeroDev.Platform.Organizations`

| Variant | Raised when | Retryable | The caller is expected to |
|---|---|---|---|
| `OrganizationNotFound` | the organization does not exist, **or the principal is not a member** | no | return not found |
| `NotAMember` | the principal an administrative action names has no active membership | no | return forbidden |
| `InvitationNotRedeemable` | the token is expired, already redeemed, **or never existed** | no | return the same answer for all three |
| `TenantAlreadyAssigned` | the unique tenant constraint rejected a concurrent create | **yes** | retry the create; it mints a fresh tenant |
| `StoreUnavailable` | the organization store could not complete the operation | **yes** | retry |

**`OrganizationNotFound` deliberately covers "exists but you may not see it".** Existence is not
confirmed to a caller who may not see it. **A caller who is not an active member gets
`OrganizationNotFound`, never `NotAMember`**: `NotAMember` describes the principal an action names,
and only a caller already confirmed as an active member can receive it.

**`StoreUnavailable` is an infrastructure failure, not an answer about any organization**, and it
confirms nothing about existence: every organization answers it alike while the store is down.
`OrganizationNotFound` is kept for a membership check that failed, and no other variant stands in for
a store failure.

**`InvitationNotRedeemable` deliberately collapses three causes into one answer.** An invitation token
is a capability, and a probe that tells the prober which guesses were close is a capability oracle.
**This variant must never be split for diagnosability** — the operator-facing distinction goes to the
log, which the prober cannot read.

### 6. `BillingError` — `SubZeroDev.Platform.Billing`

| Variant | Raised when | Retryable | The caller is expected to |
|---|---|---|---|
| `PlanNotFound` | a transition names a plan that is not registered | no | reject the transition |
| `SubscriptionNotFound` | administration acts on a tenant with no subscription | no | reject |
| `InvalidTransition` | the target state is unreachable from the current one | no | reject |
| `ProviderEventMalformed` | an inbound event cannot be interpreted | no | reject and log; **do not record a receipt** |
| `StoreUnavailable` | the billing store could not complete the operation | **yes** | retry; a provider's redelivery of the same event is idempotent |

**A redelivered provider event is not an error.** The recorded event identity makes it idempotent
success, and returning an error would make a provider's ordinary retry look like a fault.

### 7. Licensing — `SubZeroDev.Platform.Licensing`

`LicenceVerificationOutcome` is the *outcome* of a verification, recorded and logged; it is not an
error returned to a caller, because **verification never fails a host and never fails a request**.

| Outcome | Meaning | Effect |
|---|---|---|
| `Verified` | signature valid against an accepted key, payload well formed | writes the record, subject to the monotonic guard |
| `Invalid` | signature fails, wrong key, malformed payload | **the document is ignored entirely and never grants a tier**; logged loudly; audited once |
| `Unavailable` | absent, unreadable, I/O error | stored claims stand; logged; audited once |
| `ClockUnusable` | the clock reads earlier than the stored verification instant | evaluated at the stored verification instant; logged; audited once |

**`Invalid` and `Unavailable` fall back identically and must stay separately named.** An operator
seeing "licence unavailable" reaches for the file; one seeing "licence invalid" reaches for the key; a
single code sends both to the wrong place.

**Fallback in every case is the stored record if one exists, `Community` if none does.** The brief's
"a fresh installation with no verified claims continues at Community tier" is the second half of that
sentence, not a separate rule.

**`ClockUnusable` is evaluated at the stored verification instant**, which neither extends grace nor
expires early — the two ways to be wrong. D5 detects and logs a backwards clock; it does not claim to
resist an operator who controls the machine.

**Each outcome is audited once per detection, not once per check.** An expired licence checked on every
request would write an audit record per request, which is a denial-of-service against the audit trail
delivered by the audit trail.

The one returned error, `LicensingError`:

| Variant | Raised when | Retryable | The caller is expected to |
|---|---|---|---|
| `SupersededByNewerVerification` | the monotonic guard rejected a stale write | no | **treat as success**: a newer verification already stands, which is the intended outcome |

### 8. `McpError` — `SubZeroDev.Platform.Mcp`

| Variant | Raised when | Retryable | The caller is expected to |
|---|---|---|---|
| `UnknownTool` | the name is unregistered, **or registered and not exposed** | no | return unknown tool for both |
| `InvalidArguments` | arguments fail the tool's declared schema | no | return the failure; **name no argument value** |
Authorization and entitlement refusals are not `McpError` variants: they are § 2's and § 3's, raised
by the same evaluators the HTTP path uses, so a denial means the same thing on both surfaces.

**A principal change on an established session is not an `McpError` either.** The adopted SDK binds a
session to the principal that established it and answers a later request carrying a different one
with its own 403 at the transport, before any tool is reached; a new principal means a new connection.
A connection that arrives with no principal is `Anonymous`, and each of its tool calls is authorized
like any other.

| Condition | Answer | Retryable |
|---|---|---|
| tool not registered | **unknown tool** | no |
| tool registered but not exposed | **unknown tool — the same answer** | no |
| arguments fail the declared schema | invalid arguments | no |
| authorization denies | forbidden, or not found where the resource is not visible, per § 2 | no |
| authorization denies, and a permission provider could not answer | a tool result saying the permission could not be checked, per § 2 | **yes** |
| entitlement refuses | not entitled, per § 3 | no |
| the connection drops mid-invocation | cancelled through the existing cancellation plumbing | n/a |

**Unregistered and registered-but-unexposed are the same answer and must never be distinguished.**
"Forbidden" would confirm the tool exists.

**This overrides the adopted SDK's default, which discloses existence.** The SDK's own authorization
filter answers an unauthorized call with `"Access forbidden: This tool requires authorization"`, which
confirms the tool is there. `SubZeroDev.Platform.Mcp` installs its own filters ahead of it — the SDK's
`ListToolsFilters` and `CallToolFilters` are ordered lists, so this is composition rather than a fork
— and **the SDK's authorization metadata path is not used at all**: Platform's permission evaluator is
the only authority, so there is one authorization model rather than two that can disagree.

**`InvalidArguments` names no argument value**, for the same reason the audit record has no payload:
an argument that failed validation is as likely to be a secret as one that passed.

### 9. Startup — `SubZeroDev.Platform.Core`, surfaced as `HostStartupError`

Each condition surfaces as a [`HostStartupError`](../src/SubZeroDev.Platform.Hosting/StartupFailure.cs),
on the shape `ModuleGraphError` already establishes. Most are `HostStartupError` codes of their own;
the duplicate-name conditions are the registries' own errors, carried as the inner error of
`HostStartupError.Registration`. **Every variant fails the host; none degrades it. None is
retryable — a misconfigured installation does not resolve itself.**

| Variant | Raised when |
|---|---|
| `AuthenticationProviderRequired` | `Operated` with no authentication provider registered |
| `DurableAuditSinkRequired` | `Operated` with no sink declaring `IsDurable` |
| `RegistrationForbiddenByProfile` | `Local` with an authentication provider, a tenant resolver, or an entitlement contributor other than the Community baseline |
| `DuplicatePermissionName` | two modules declare the same `PermissionName` — `PermissionCatalogError`, inside `Registration` |
| `DuplicateProviderName` | two permission providers, authentication providers, tenant resolvers or audit sinks share a name — each registry's own error, inside `Registration`; two entitlement contributors sharing a name raise `DuplicateContributorName` there instead |
| `UnregisteredPermission` | a tool, an endpoint, or any registration requires a `PermissionName` no catalog declares — its own code, carrying `PermissionCatalogError.UnregisteredPermission` as the inner error |
| `DuplicateSettingName` | two setting catalogues declare the same `SettingName` — `SettingCatalogError`, inside `Registration` |
| `InvalidSettingDefault` | a declared default fails the setting's own validation rule — `SettingCatalogError`, inside `Registration` |
| `SettingWithoutLayers` | a setting declares no layer — `SettingCatalogError`, inside `Registration` |
| `SensitiveSettingName` | a setting name matches the redaction marker set — `SettingCatalogError`, inside `Registration` |
| `SensitiveToolParameter` | a registered tool's schema names a parameter matching the redaction marker set |
| `UndeclaredEndpointRequirement` | a mapped endpoint carries neither a requirement nor an exemption |
| `Configuration` | a generic-path provider's settings are defective (*Types* § 12), or `Platform:Audit:RetentionDays` is present and is not a whole number from 1 to 36500 (*Public surface* § 6). The inner error is `ConfigurationError`, and the variant depends on the defect — see below |
| `ModuleInitialization` | a module's `InitializeAsync` threw (*Public surface* § 15). Names the module and the exception's type; the modules already initialized were shut down first |

**A generic-path settings defect is a `ConfigurationError`, not a new code.** The error names the full
key, `Platform:Identity:Bearer:<name>:<setting>`, so it names the provider and the setting in one
string, on the `Detail` convention `ConfigurationError` already follows:

| Defect | `ConfigurationError` variant |
|---|---|
| `Audiences` is absent; neither `Issuer` nor `IssuerPattern` is present; neither `Discovery` nor `SigningKeys` is present | `MissingRequiredSetting` |
| `Issuer` and `IssuerPattern` are both present; `Discovery` and `SigningKeys` are both present; `KeyRefreshInterval` is present with `SigningKeys` | `InconsistentSettings` |
| a value breaks its row's constraint; a key is one the table does not name; a value stands where a section belongs, or a section where a value belongs | `InvalidSetting` |

**A defect fails startup before any fetch** (I-I10). A key-fetch failure is not in this section. It
never fails startup, and it degrades readiness instead (*Public surface* § 10).

**`Platform:Authorization:MirrorMaximumAge` outside its row's constraint is `InvalidSetting`** naming that
full key, raised by the options binder as every `Platform:` setting is, before any provider is consulted.
Zero, a negative value and an infinite value are outside it. An absent key is not a defect: it binds the
default (*Public surface* § 3).

**An invalid retention value is `ConfigurationError.InvalidSetting("Platform:Audit:RetentionDays", …)`**,
with the constraint `must be a whole number` or `must be between 1 and 36500`. It is raised when options
are bound, so in both roles and before any module composes (I-U13). An absent, empty or whitespace value
is not a defect: it means keep forever.

**Each names the profile, the offending registration and which of the two it disagrees with**, on the
`Detail` convention `ModuleGraphError` and `ConfigurationError` already follow. Each describes a
deployment that would otherwise run and be wrong quietly.

### 10. `ContractViolation` — `SubZeroDev.Platform.Abstractions`

Three variants are added to the existing set in
[`Results.cs`](../src/SubZeroDev.Platform.Abstractions/Results.cs):

| Variant | Raised when |
|---|---|
| `WriteInsideSharedReadScope` | a write was attempted while a shared-read scope was open |
| `GuardedWriteOutsideWriteTransaction` | `IVersionGuard.ExecuteAsync` was called with no ambient transaction, or inside one opened `ReadOnly` |
| `GuardedWriteNotSingleRow` | a guarded write matched more than one row — its key predicate is wrong |

It **throws** rather than returning, on the same terms as `NoAmbientTransaction`: a defect in the
caller, not a runtime condition.

### 11. Standalone observability startup — `SubZeroDev.Platform.Observability`

`ObservabilityStartupException` is the package-local startup-abort wrapper from *Public surface*,
§ 12. It always carries a `PlatformError`; no bare exception or string error crosses the package
boundary.

| Error | Raised when | Retryable | The caller is expected to |
|---|---|---|---|
| `ConfigurationError.InvalidSetting` | standalone `AddPlatformObservability` receives a present `Platform:Telemetry:OtlpEndpoint` that is not an absolute `http` or `https` URI | no | fix the named setting and rebuild the host |

The Platform-host path continues to wrap the same configuration error in `PlatformStartupException`.
The wrapper type differs because the package graph forbids Observability from referencing Hosting;
the setting, constraint and non-retryable meaning are identical.

### 12. `TransactionError.StaleVersion` — `SubZeroDev.Platform.Persistence`

One variant, `StaleVersion`, is added to `TransactionError` in
[`Errors.cs`](../src/SubZeroDev.Platform.Persistence/Errors.cs). Its detail is "The guarded write
matched no row: the row changed or is gone since it was read." and names no version.

| Variant | Raised when | Retryable | The caller is expected to |
|---|---|---|---|
| `StaleVersion` | a guarded write matched zero rows | **no** | re-read the row and decide; **never merge**, never re-run with the same expected version |
| `Conflict` *(existing)* | the provider aborted for serialization or deadlock | yes | re-run the unit unchanged |

**`StaleVersion` cannot reach Hosting or Mcp as a type** — neither package references Persistence.
The product code that called the unit of work owns the answer, and it is fixed here:

| Surface | Answer | Discloses |
|---|---|---|
| HTTP endpoint | **409**, `ErrorEnvelope("StaleVersion", correlation)` | nothing but the code and correlation |
| MCP tool, via the product's `IToolInvoker` | `ToolInvocationResult.Failure("The resource changed since it was read; read it again before repeating the call.")` | nothing |
| the product lets it escape as an exception | today's 500 `UnhandledRequestFailure` or the adapter's "The tool failed to complete." | nothing — and it is a defect in the product |

**No answer carries a version, current or expected** (engine-hosting-contract § 6.1). Whether a
product exposes a version on its own wire is the product's decision, outside this contract.

**A duplicate inbox delivery is not an error.** It is a success that invoked nothing (*Public surface*,
§ 14), and no `DispatchError` variant is added.

---

### 13. Migrate mode and development application — `SubZeroDev.Platform.Persistence`, surfaced as `MigrationError`

| Variant | Migrate mode | Development host start |
|---|---|---|
| `Failed` | exit 1; the whole run rolled back; no seeder runs | fails startup with `PersistenceStartupException` |
| `HistoryTableCollision` | exit 1; nothing applied; no seeder runs | fails startup with `PersistenceStartupException` |
| `Locked` | exit 2; no seeder runs | waits one second and retries, logging each wait, until the result is not `Locked` or startup is cancelled |
| `Unavailable` | exit 1 | starts with a warning; readiness reports it through the existing database and pending-migrations checks; no retry in this process |
| `SeedFailed` | exit 1; committed migrations and earlier seeders' writes stay; later seeders do not run | never raised: a host does not seed |

**`SeedFailed` is not retryable.** A seeder that throws on one run throws on the next unless something
changed. Its `Detail` names the module, the seeder's `Name` and the exception's type and message, on the
`Failed` convention. `PersistenceStartupException`'s message carries the `MigrationError`'s `Detail`, as it
already does for `ConfigurationError`.

---

### 14. `EdgeError` on a streamed route — `SubZeroDev.Platform.GameEdge`

G1's `EdgeError` table (*The edge — `EdgeError`*) stands, with two rows **amended**. #106's
after-headers clause is scoped to buffered routes, and the timeout row names the first-byte budget. No
variant is added.

| Error | HTTP | Raised when | Retryable | The caller is expected to |
|---|---|---|---|---|
| `EdgeError.WorkloadUnreachable` | `503` | **Buffered route:** the forward fails before or after response headers arrive. **Streamed route:** the forward fails before response headers arrive. Either: the readiness probe cannot reach the workload | no | treat the workload as down; retry at the caller's discretion |
| `EdgeError.WorkloadTimeout` | `504` | **Buffered route:** the forward exceeds `ForwardTimeout`. **Streamed route:** the workload's response headers have not arrived within the route's `FirstByteTimeout` | no | treat the workload as slow; retry at the caller's discretion |

G1's sentence *"The edge produces no other error"* holds, with one addition. **On a streamed route,
after the workload's headers are sent, a workload failure is not an error the caller receives.** It is
an aborted response:
- HTTP/1.1: no terminating chunk, and the connection closes;
- HTTP/2: `RST_STREAM`.

No `PlatformError` crosses to the caller, and `ErrorEnvelopeMiddleware` never sees it. The edge records
it as code `workload_unreachable` in its log and counter only.

---

### 15. `SettingError` — `SubZeroDev.Platform.RuntimeSettings`

`SettingError` and `SettingCatalogError` are `PlatformError` subtypes on `OrganizationError`'s shape:
a private constructor, a static factory per variant, and a `Detail` that names the setting and the
layer and **never a value**.

| Variant | Raised when | Retryable | The caller is expected to |
|---|---|---|---|
| `NotDeclared` | the name is not in the catalogue, or is declared with another type | no | fix the code; this is a defect |
| `LayerNotAdmitted` | the declaration does not admit the layer written | no | write a layer the setting admits |
| `LayerUnavailable` | a user-layer write by a principal that is not an `Account`, or a tenant- or user-layer write under the implicit tenant | no | write the global layer, or act as an account in a tenant |
| `InvalidValue` | the value fails the setting's rule | no | supply a valid value |
| `PermissionDenied` | the evaluator denied `WriteGlobal` or `WriteTenant`; the denial is already audited | no | not retry; obtain the grant |
| `AuthorizationUnavailable` | the decision carried a provider or audit failure | yes | retry |
| `StoredValueInvalid` | a stored value fails to parse or validate under the current declaration (read only) | no | set or clear that layer |
| `StoreUnavailable` | the store, or the audit write inside the change's transaction, failed | yes | retry |

**`StoredValueInvalid` is never replaced by a lower layer or the default** (I-ST9), and
`StoreUnavailable` is never replaced by the default.

---

### 16. `AccountError` — `SubZeroDev.Platform.Identity`

A `PlatformError`, declared as `OrganizationError` is: private constructor, one static factory per variant, `Detail`.

| Variant | Raised when | Retryable | The caller is expected to |
|---|---|---|---|
| `NotEligible` | `CreateAccountAsync` from a principal that is not an Identity-established, unlinked `Account`: `Anonymous`, `Delegated`, `System`, a consumer provider's principal, or an account principal | no | return forbidden |
| `NotAnAccount` | `LinkAsync`, `UnlinkAsync` or `GetCurrentAccountAsync` from a principal that is not an account principal | no | return forbidden, or offer to create one |
| `SecondCredentialRejected` | `secondCredential` is not claimed, fails validation, has no `iat`, or was issued more than five minutes before the call | no | have the person sign in with the second method again |
| `IdentityAlreadyLinked` | the pair to link, or to create an account for, is linked to another account, **including by a concurrent call that won the primary key** | no | tell the person that sign-in belongs to another account; **never merge** |
| `LastIdentity` | `UnlinkAsync` names the account's only identity | no | refuse; there is no account deletion |
| `IdentityNotLinked` | `UnlinkAsync` names a pair this account does not hold, **whether or not another account holds it** | no | return not found |
| `StoreUnavailable` | the account store could not complete the operation | **yes** | retry |

**`IdentityNotLinked` deliberately covers "linked elsewhere".** An unlink request is not a way to learn which pairs
other accounts hold. **`SecondCredentialRejected` collapses its causes**, and the specific cause goes to the log.
**At authentication, a store failure is not an `AccountError`.** It is `AuthenticationError.ProviderFailed`
(§ 1), and never the unlinked pair.

## Invariants

**This table is generated** from `design/state/invariants/*.md` — the table below is the whole
section, a single projected region, and it is regenerated, never hand-edited. Adding an invariant
means writing its record first.

**The `Held by` column renders the derived `BoundBy`, not a written field.** An invariant nothing
binds renders `—`, the *enforced by nothing* case, rather than hiding it — this is currently every
row, because no module has a unit record yet.

Each is written so it could become an assertion. **"Code" means a build check, a startup check, a
type, or a store constraint — the only ones a reader may trust without checking.** "Instruction"
means this document is the only thing holding it, and a reviewer is the enforcement.

<!-- invariants:start -->
| | Statement | Held by | Enforcement | Evidence |
|---|---|---|---|---|
| **I-A1** | `AuthorizationDecision.Sources` is non-empty **iff** `Outcome == Allowed` (Owner: Core.) Enforced by code — evaluator construction. | — | code | tests/SubZeroDev.Platform.Tests/AuthorizationTests.cs |
| **I-A2** | The evaluator takes the union of every registered provider and consults no other source (Owner: Core.) | — | instruction | — |
| **I-A3** | Every `PermissionName` reaching the evaluator is declared by some `IPermissionCatalog`; an undeclared one fails **startup**, never a request (Owner: Core.) | — | code | tests/SubZeroDev.Platform.Tests/RequestOrderTests.cs, tests/SubZeroDev.Platform.Tests/McpTests.cs, tests/SubZeroDev.Platform.Tests/AuthorizationTests.cs |
| **I-A4** | Two modules never declare the same `PermissionName` (Owner: Core.) Enforced by code — startup validation. | — | code | tests/SubZeroDev.Platform.Tests/AuthorizationTests.cs |
| **I-A5** | A provider returning an error denies and never grants (Owner: Core.) Enforced by code — the evaluator. | — | code | tests/SubZeroDev.Platform.Tests/AuthorizationTests.cs |
| **I-A6** | The composition provider grants nothing to `Anonymous` in either profile (Owner: Core.) Enforced by code — the provider. | — | code | tests/SubZeroDev.Platform.Tests/AuthorizationTests.cs |
| **I-A7** | The composition provider grants nothing at all in `Operated` (Owner: Core.) | — | code | tests/SubZeroDev.Platform.Tests/AuthorizationTests.cs |
| **I-A8** | No provider writes an audit record; the evaluator audits a denial once, and an allowed action is audited by the writer performing it (Owner: Core.) | — | instruction | — |
| **I-A9** | D5 has no role-assignment store (Owner: Organizations.) Enforced by code — schema. | — | code | tests/SubZeroDev.Platform.Tests/OrganizationsTests.cs |
| **I-A10** | Neither the evaluator nor a Platform permission provider carries a grant from one request to the next, so a membership revoked between two requests is denied on the second without re-authentication (Owner: Core, Organizations.) | — | code | tests/SubZeroDev.Platform.Tests/OrganizationsTests.cs |
| **I-A11** | `AuthorizationDecision.ProviderFailure` is non-null **iff** `Outcome == Denied` and at least one registered provider could not answer — returned an error, or is a mirror past `MirrorMaximumAge` (I-A13) — and when non-null it is `ProviderUnavailable` naming a registered provider (Owner: Core.) | — | code | tests/SubZeroDev.Platform.Tests/AuthorizationTests.cs |
| **I-A12** | A decision carrying `ProviderFailure` is answered as a retryable failure and never as forbidden or not found, on every surface that refuses on a decision (Owner: Hosting, Mcp, and each caller refusing on a decision.) | — | code | tests/SubZeroDev.Platform.Tests/ProviderFailureHttpTests.cs, tests/SubZeroDev.Platform.Tests/McpInvocationTests.cs |
| **I-A13** | A registered `IMirroredPermissionProvider` whose `LastSyncedAsync` is null, errs, or is older than `Platform:Authorization:MirrorMaximumAge` by the evaluating host's clock contributes no grant, is not asked for grants, and is recorded as `ProviderUnavailable` naming it; no configuration makes the maximum unbounded (Owner: Core.) | — | code | tests/SubZeroDev.Platform.Tests/MirroredProviderTests.cs |
| **I-A14** | A mirror's last-synced instant is the instant its most recent successful sync began reading the issuer, written with the rows that read produced; a failed or partial issuer read advances neither (Owner: each consumer's mirror sync. Checked in part by `PermissionProviderHarness.AssertIssuerRevocationDeniesWithinBoundAsync`.) | — | instruction | — |
| **I-B1** | Product code asks `FeatureName` and never subscription state or licence tier (Owner: all.) Enforced by code — for subscription state (I-C8); enforced by instruction for licence tier. | — | code, instruction | — |
| **I-B2** | Contribution is a union; no contributor can veto another (Owner: Core.) Enforced by code — the evaluator. | — | code | tests/SubZeroDev.Platform.Tests/EntitlementTests.cs |
| **I-B3** | `EntitlementDecision.Sources` is non-empty **iff** `Granted` (Owner: Core.) | — | code | tests/SubZeroDev.Platform.Tests/EntitlementTests.cs |
| **I-B4** | Entitlement is never stored by Billing; it is derived from plan, state and `IClock` (Owner: Billing.) Enforced by code — no entitlement table exists. | — | code | tests/SubZeroDev.Platform.Tests/BillingTests.cs |
| **I-B5** | A unit of work carries the decision that admitted it; nothing re-evaluates during execution (Owner: consumers.) Enforced by code — in the sample's scenario; enforced by instruction otherwise. | — | code, instruction | — |
| **I-B6** | No billing provider is contacted on the request path, at startup, or on readiness (Owner: Billing.) | — | code | tests/SubZeroDev.Platform.Tests/BillingTests.cs |
| **I-B7** | A redelivered provider event is idempotent (Owner: Billing.) Enforced by code — a unique receipt key. | — | code | tests/SubZeroDev.Platform.Tests/BillingTests.cs |
| **I-C1** | `Operated` with no authentication provider fails startup (Owner: Core.) | — | code | tests/SubZeroDev.Platform.Tests/CompositionProfileTests.cs |
| **I-C2** | `Operated` with no sink declaring `IsDurable` fails startup; the log sink is never an `Operated` fallback (Owner: Core.) | — | code | tests/SubZeroDev.Platform.Tests/CompositionProfileTests.cs |
| **I-C3** | `Local` with an authentication provider, a tenant resolver, or a non-baseline entitlement contributor fails startup (Owner: Core.) | — | code | tests/SubZeroDev.Platform.Tests/CompositionProfileTests.cs |
| **I-C4** | The composition profile and the contributor set are inside the settings-fingerprint input (Owner: Core.) Enforced by code — see `SettingsFingerprint.cs`. | — | code | tests/SubZeroDev.Platform.Tests/SettingsFingerprintTests.cs |
| **I-C5** | The local host has no package or project reference to Identity, Organizations, Billing or Licensing (Owner: the sample.) Enforced by code — a dependency-graph assertion. | — | code | tests/SubZeroDev.Platform.Tests/PackageGraphTests.cs, tests/SubZeroDev.Platform.Tests/LocalHostProofTests.cs |
| **I-C6** | No framework package references a module (Owner: all.) Enforced by code — an architecture test over the resolved package graph, which must fail against a deliberately broken graph before it counts. | — | code | tests/SubZeroDev.Platform.Tests/PackageGraphTests.cs |
| **I-C7** | No module references another module (Owner: all.) Enforced by code — the same test, second direction. | — | code | tests/SubZeroDev.Platform.Tests/PackageGraphTests.cs |
| **I-C8** | Nothing outside Billing references `SubscriptionState` or any subscription type (Owner: all.) Enforced by code — architecture test. | — | code | tests/SubZeroDev.Platform.Tests/PackageGraphTests.cs |
| **I-C9** | Every startup check fails the host and names the registration that caused it; none degrades (Owner: Core, Hosting.) | — | code | tests/SubZeroDev.Platform.Tests/CompositionProfileTests.cs |
| **I-C10** | Module `InitializeAsync` hooks run once per host start, in topological order, one at a time, after every registry has frozen and every `StartingAsync` check has passed, and complete before the first background tick or request; `ShutdownAsync` hooks run in reverse after background work and the listener stop (Owner: Hosting.) | — | instruction | tests/SubZeroDev.Platform.Tests/ModuleLifecycleTests.cs |
| **I-C11** | `ShutdownAsync` runs exactly once for each module whose `InitializeAsync` completed — on stop, or on disposal after a later startup step failed — and never for one whose `InitializeAsync` threw or never ran (Owner: Hosting.) | — | code | tests/SubZeroDev.Platform.Tests/ModuleLifecycleTests.cs |
| **I-C12** | A throwing `InitializeAsync` fails the host with `ModuleInitialization` naming the module, never degrades it, and its `Detail` carries no exception message (Owner: Hosting.) | — | code | tests/SubZeroDev.Platform.Tests/ModuleLifecycleTests.cs |
| **I-C13** | No lifecycle hook assumes it is the only process: every instance of every role runs every hook, and migrate mode runs none (Owner: Hosting; each module for its own hooks.) | — | instruction | tests/SubZeroDev.Platform.Tests/ModuleLifecycleTests.cs |
| **I-E1** | A request whose path matches no `GameEdge:StreamingRoutes` prefix is forwarded buffered, under one `ForwardTimeout`, with G1's answers including #106's after-headers `503`; with no route listed the edge is G1's edge unchanged (Owner: GameEdge.) | — | code | workloads/game-edge/SubZeroDev.Platform.GameEdge.Tests/StreamingTests.cs, workloads/game-edge/SubZeroDev.Platform.GameEdge.Tests/ForwardingTests.cs |
| **I-E2** | On a streamed route the edge commits the workload's status and `Content-Type` when the workload's headers arrive and flushes each piece of the body as it is read, never coalescing; `ForwardTimeout` does not apply and no edge deadline follows the headers (Owner: GameEdge.) | — | code | workloads/game-edge/SubZeroDev.Platform.GameEdge.Tests/StreamingTests.cs |
| **I-E3** | After a streamed route's headers are sent, a workload failure aborts the caller's response — no terminating chunk on HTTP/1.1, a reset stream on HTTP/2 — and the edge writes no byte of its own after headers: no status, envelope, frame or trailer (Owner: GameEdge.) | — | code | workloads/game-edge/SubZeroDev.Platform.GameEdge.Tests/StreamingTests.cs |
| **I-E4** | A streamed forward is exactly one attempt to the workload: the edge never retries it, before or after headers, and never resumes a stream (Owner: GameEdge.) | — | code | workloads/game-edge/SubZeroDev.Platform.GameEdge.Tests/StreamingTests.cs |
| **I-E5** | Each aborted stream is logged once as `EdgeStreamAborted` at `Warning` and counted once on `subzerodev.edge.stream.aborts`, carrying the configured prefix and never the request path, query, body or exception message; a caller disconnect is neither counted nor logged above `Debug` (Owner: GameEdge.) | — | code | workloads/game-edge/SubZeroDev.Platform.GameEdge.Tests/StreamingTests.cs |
| **I-I1** | The ambient principal is never null while an operation scope is open (Owner: Abstractions.) | — | code | tests/SubZeroDev.Platform.Tests/OperationScopeTests.cs, tests/SubZeroDev.Platform.Tests/PrincipalTests.cs |
| **I-I2** | `PrincipalId.Issuer` and `.Subject` are never parsed, normalised, trimmed or case-folded by Platform (Owner: Abstractions.) | — | instruction | — |
| **I-I3** | `PrincipalId.ToString()` is never split to recover the pair; anywhere the pair is stored it is two columns (Owner: Abstractions, Audit store, Organizations.) Enforced by code — in each schema; enforced by instruction otherwise. | — | code, instruction | — |
| **I-I4** | Platform declares no user entity and no directory. The optional account store holds an account id, its creation instant and its linked (issuer, subject) pairs, and nothing else, and exists only in a host that registers it (Owner: Identity.) Enforced by code — an architecture check over the module's types and its migration's columns. | — | code, instruction | tests/SubZeroDev.Platform.Tests/IdentityTests.cs, tests/SubZeroDev.Platform.Tests/AccountTests.cs |
| **I-I5** | A `Delegated` principal is never treated as an `Account` with missing fields (Owner: every consumer.) | — | instruction | — |
| **I-I6** | No Platform decision reads `Principal.Claims` (Owner: all.) | — | code | tests/SubZeroDev.Platform.Tests/PermissionGrantSourceTests.cs |
| **I-I7** | The generic bearer path accepts asymmetric signature algorithms only: a shared-secret algorithm or `none` in its settings fails startup, and a token whose header names an algorithm outside the provider's set is rejected (Owner: Identity.) | — | code | tests/SubZeroDev.Platform.Tests/ConfiguredBearerTests.cs |
| **I-I8** | No key material is fetched on the request path; a token naming a key id the cached set does not hold is `CredentialRejected` and never triggers a fetch (Owner: Identity.) | — | code | tests/SubZeroDev.Platform.Tests/KeyDiscoveryTests.cs |
| **I-I9** | A key-set refresh takes no lease, writes nothing durable, and replaces the cached set in one atomic swap; a failed refresh keeps the previous set and degrades readiness, and never empties the cache (Owner: Identity.) | — | code | tests/SubZeroDev.Platform.Tests/KeyDiscoveryTests.cs |
| **I-I10** | A generic-path settings defect — missing, inconsistent, malformed or unrecognised key — fails startup naming the full key, before any key fetch; a key-fetch failure never fails startup (Owner: Identity.) Enforced by code — the settings half, before any provider is registered, and the fetch half, where a failed fetch starts the host with no keys. | — | code | tests/SubZeroDev.Platform.Tests/ConfiguredBearerTests.cs, tests/SubZeroDev.Platform.Tests/KeyDiscoveryTests.cs |
| **I-I11** | A generic-path principal is `Account`, and its `PrincipalId` is the concrete validated `iss` paired with the configured subject claim's value, both exactly as the token carries them — never an issuer pattern (Owner: Identity.) | — | code | tests/SubZeroDev.Platform.Tests/ConfiguredBearerTests.cs, tests/SubZeroDev.Platform.Tests/IssuerPatternTests.cs |
| **I-I12** | No `Microsoft.IdentityModel.*` type appears in Platform's public surface; those libraries are referenced by `SubZeroDev.Platform.Identity` and by nothing else (Owner: Identity.) | — | code | tests/SubZeroDev.Platform.Tests/PackageGraphTests.cs |
| **I-I13** | A vendor configuration package references no Platform package (Owner: each vendor configuration package.) | — | code | tests/SubZeroDev.Platform.Tests/PackageGraphTests.cs |
| **I-I14** | Identity reads the generic path's settings without knowing which configuration source wrote them: the same keys and values from a vendor configuration source and from a settings file yield equal validated settings (Owner: Identity.) | — | code | tests/SubZeroDev.Platform.Tests/VendorConfigurationSourceTests.cs |
| **I-I15** | A request presenting a credential that no registered provider claims ends the authentication chain `CredentialRejected`, never `Principal.Anonymous`; `CredentialNotClaimed` is consumed by the chain and never reaches a caller (Owner: Core, every authentication provider.) | — | code | tests/SubZeroDev.Platform.Tests/AuthenticationTests.cs, tests/SubZeroDev.Platform.Tests/IdentityTests.cs |
| **I-I16** | One (issuer, subject) is linked to at most one account (Owner: Identity.) Enforced by code — the link table's primary key, on SQLite and PostgreSQL. | — | code | tests/SubZeroDev.Platform.Tests/AccountTests.cs |
| **I-I17** | No account is created, found or linked from an email or any claim other than the subject pair; two identities sharing every other claim are two principals until linked (Owner: Identity.) | — | code | tests/SubZeroDev.Platform.Tests/AccountTests.cs |
| **I-I18** | A pair joins an existing account only through `LinkAsync`, called by that account's principal, with a second credential an Identity provider validates in the same call and that was issued within five minutes (Owner: Identity.) | — | code | tests/SubZeroDev.Platform.Tests/AccountTests.cs |
| **I-I19** | An account always has at least one linked identity; unlinking the last is refused, including under concurrent unlinks (Owner: Identity.) | — | code | tests/SubZeroDev.Platform.Tests/AccountTests.cs |
| **I-I20** | With the account store registered, a store failure while mapping a principal fails authentication and never yields the unlinked pair (Owner: Identity.) | — | code | tests/SubZeroDev.Platform.Tests/AccountTests.cs |
| **I-I21** | A `Delegated`, `Anonymous` or `System` principal is never mapped to or made into an account, and a host without `IdentityAccountsModule` has no account table and unchanged principals (Owner: Identity.) | — | code | tests/SubZeroDev.Platform.Tests/AccountTests.cs, tests/SubZeroDev.Platform.Tests/IdentityTests.cs |
| **I-L1** | Exactly one verified-licence row exists per installation (Owner: Licensing.) Enforced by code — a single-row key. | — | code | tests/SubZeroDev.Platform.Tests/LicensingTests.cs |
| **I-L2** | No verification error path writes any column of that row (Owner: Licensing.) Enforced by code, plus a test that errors repeatedly and asserts the instants unchanged. | — | code | tests/SubZeroDev.Platform.Tests/LicensingTests.cs |
| **I-L3** | A verification writes only when its instant is later than the stored one (Owner: Licensing.) Enforced by code — conditional update. | — | code | tests/SubZeroDev.Platform.Tests/LicensingTests.cs |
| **I-L4** | An `Invalid` document never grants a tier (Owner: Licensing.) | — | code | tests/SubZeroDev.Platform.Tests/LicensingTests.cs |
| **I-L5** | Revocation is consulted on no path — not the request path, not startup, not readiness (Owner: Licensing.) Enforced by code — no path calls a registered revocation check. | — | code | tests/SubZeroDev.Platform.Tests/LicensingTests.cs |
| **I-L6** | Grace comes from the document, defaulting to 30 days; it is never a deployment setting (Owner: Licensing.) Enforced by code — no such option exists. | — | code | tests/SubZeroDev.Platform.Tests/LicensingTests.cs |
| **I-L7** | Accepted signing keys are supplied by the consumer as an ordered set; none is compiled into Platform (Owner: Licensing.) Enforced by code — a required option. | — | code | tests/SubZeroDev.Platform.Tests/LicensingTests.cs |
| **I-L8** | After grace, new paid-feature work is denied while accepted, running and scheduled work continues and existing data stays readable and exportable (Owner: Licensing, consumers.) | — | code | tests/SubZeroDev.Platform.Tests/LicensingTests.cs |
| **I-L9** | Verification never fails startup and never fails a request (Owner: Licensing.) | — | code | tests/SubZeroDev.Platform.Tests/LicensingTests.cs |
| **I-M1** | The tool catalogue is frozen after startup; nothing registers, unregisters or re-exposes at runtime (Owner: Mcp.) | — | code | tests/SubZeroDev.Platform.Tests/McpTests.cs |
| **I-M2** | No registered tool's schema names a parameter matching the redaction marker set; a match fails **startup** (Owner: Mcp.) Enforced by code — startup validation. | — | code | tests/SubZeroDev.Platform.Tests/McpTests.cs |
| **I-M3** | Exposure is default closed; a registered but unexposed tool is neither listed nor callable (Owner: Mcp.) | — | code | tests/SubZeroDev.Platform.Tests/McpTests.cs, tests/SubZeroDev.Platform.Tests/McpInvocationTests.cs, tests/SubZeroDev.Platform.Tests/OperatedScenarioTests.cs |
| **I-M4** | Unregistered and unexposed produce the identical answer (Owner: Mcp.) | — | code | tests/SubZeroDev.Platform.Tests/McpTests.cs, tests/SubZeroDev.Platform.Tests/McpInvocationTests.cs |
| **I-M5** | Authentication happens at the connection and never at a call (Owner: Mcp.) | — | code | tests/SubZeroDev.Platform.Tests/McpInvocationTests.cs |
| **I-M6** | Authorization runs before any producer code is reached (Owner: Mcp.) Enforced by code — invocation order. | — | code | tests/SubZeroDev.Platform.Tests/McpInvocationTests.cs, tests/SubZeroDev.Platform.Tests/OperatedScenarioTests.cs |
| **I-M7** | Both producers — manifest projection and a product-owned fixed table — register through the same surface, and neither is privileged (Owner: Mcp.) | — | code | tests/SubZeroDev.Platform.Tests/McpInvocationTests.cs |
| **I-M8** | An invocation is audited with no arguments (Owner: Mcp.) Enforced by code — see I-U1. | — | code | tests/SubZeroDev.Platform.Tests/McpInvocationTests.cs, tests/SubZeroDev.Platform.Tests/AuditTests.cs |
| **I-M9** | No SDK type appears in Platform's public surface; `ModelContextProtocol.*` is referenced by `SubZeroDev.Platform.Mcp` and by nothing else (Owner: Mcp.) Enforced by code — an architecture test over the resolved package graph, alongside I-C6 and I-C7. | — | code | tests/SubZeroDev.Platform.Tests/PackageGraphTests.cs |
| **I-M10** | `IToolCatalogue` offers no route to an unexposed registration — no `All`, no exposure-ignoring lookup (Owner: Mcp.) Enforced by code — the interface. | — | code | tests/SubZeroDev.Platform.Tests/McpTests.cs |
| **I-M11** | Platform's permission evaluator is the only authorization authority on this surface; the SDK's authorization-metadata path is not used (Owner: Mcp.) Enforced by code — the sample's unknown-tool scenario; enforced by instruction otherwise. | — | code, instruction | — |
| **I-O1** | Creating an organization mints the tenant, writes the organization and writes the owner's membership in one transaction with its audit row (Owner: Organizations.) | — | code | tests/SubZeroDev.Platform.Tests/OrganizationsTests.cs |
| **I-O2** | Two organizations never share a tenant (Owner: Organizations.) Enforced by code — a unique constraint. | — | code | tests/SubZeroDev.Platform.Tests/OrganizationsTests.cs, tests/SubZeroDev.Platform.Tests/OperatedProofTests.cs |
| **I-O3** | One invitation token creates at most one membership (Owner: Organizations.) Enforced by code — a conditional update. | — | code | tests/SubZeroDev.Platform.Tests/OrganizationsTests.cs, tests/SubZeroDev.Platform.Tests/OperatedProofTests.cs |
| **I-O4** | The invitation token is stored only as a hash and is readable exactly once, at mint (Owner: Organizations.) | — | code | tests/SubZeroDev.Platform.Tests/OrganizationsTests.cs |
| **I-O5** | Expired, already-redeemed and never-existed are indistinguishable to a caller (Owner: Organizations.) Enforced by code — one error variant. | — | code | tests/SubZeroDev.Platform.Tests/OrganizationsTests.cs |
| **I-O6** | Membership is keyed by `PrincipalId` and never by a user row (Owner: Organizations.) Enforced by code — schema. | — | code | tests/SubZeroDev.Platform.Tests/OrganizationsTests.cs |
| **I-O7** | A non-member cannot switch into or administer an organization, and is told not found (Owner: Organizations.) Enforced by code — sample scenario. | — | code | tests/SubZeroDev.Platform.Tests/OrganizationsTests.cs, tests/SubZeroDev.Platform.Tests/OperatedScenarioTests.cs, tests/SubZeroDev.Platform.Tests/AdministrationShellTests.cs |
| **I-O8** | The framework never learns that a tenant has an owner (Owner: all.) | — | code | tests/SubZeroDev.Platform.Tests/PackageGraphTests.cs |
| **I-OB1** | An absent OTLP endpoint starts no exporter; a present invalid endpoint aborts both registration paths with the same `ConfigurationError.InvalidSetting`; a validly configured exporter failure never propagates to application work (Owner: Observability, Hosting.) | — | code | tests/SubZeroDev.Platform.Tests/TelemetryOptionsTests.cs, tests/SubZeroDev.Platform.Tests/TelemetryExportTests.cs |
| **I-P1** | A guarded write commits only when it matched exactly one row; zero rows rolls back the whole unit of work — consumer writes, staged outbox rows and staged audit — and returns `StaleVersion`, whether or not the work read the guard's result (Owner: Persistence.) | — | code | tests/SubZeroDev.Platform.Tests/PersistenceIntegrationTests.cs, tests/SubZeroDev.Platform.Tests/PostgresPersistenceContractTests.cs |
| **I-P2** | Every guarded write predicates on `version = @expected` and the tenant, and an UPDATE sets `version = @expected + 1`; no other statement writes `version` (Owner: each consumer declaring `IVersioned`.) | — | instruction | tests/SubZeroDev.Platform.Tests/PersistenceIntegrationTests.cs |
| **I-P3** | No answer to a stale version — HTTP, MCP, log or audit — carries a version value, and gone, changed and another tenant's row get the same answer (Owner: Persistence, each consumer.) | — | instruction | tests/SubZeroDev.Platform.Tests/PersistenceIntegrationTests.cs, tests/SubZeroDev.Platform.Tests/StaleVersionAnswerTests.cs |
| **I-P4** | A handler is never invoked for a (message id, consumer) whose earlier invocation committed (Owner: Persistence.) | — | code | tests/SubZeroDev.Platform.Tests/OutboxDispatchTests.cs, tests/SubZeroDev.Platform.Tests/PersistenceIntegrationTests.cs |
| **I-P5** | The inbox record commits in the same transaction as the handler's effects, and a handler failure leaves no record (Owner: Persistence.) | — | code | tests/SubZeroDev.Platform.Tests/OutboxDispatchTests.cs, tests/SubZeroDev.Platform.Tests/PersistenceIntegrationTests.cs |
| **I-P6** | An inbox record is pruned only once its outbox row no longer exists (Owner: Persistence.) | — | code | tests/SubZeroDev.Platform.Tests/PersistenceIntegrationTests.cs, tests/SubZeroDev.Platform.Tests/PruneLoggingTests.cs |
| **I-P7** | Migrations are applied automatically only when the derived environment is `Development`; no setting can enable it (Owner: Persistence.) | — | code | tests/SubZeroDev.Platform.Tests/DevelopmentMigrationTests.cs |
| **I-P8** | `IMigrationRunner.ApplyAsync` is the only path that writes migration history; development application and migrate mode both take the provider-native lock through it (Owner: Persistence.) | — | instruction | tests/SubZeroDev.Platform.Tests/DevelopmentMigrationTests.cs |
| **I-P9** | Seeders run only in migrate mode, only after `ApplyAsync` succeeded, grouped in composed-module topological order and registration order within a module (Owner: Persistence.) | — | code | tests/SubZeroDev.Platform.Tests/SeedingTests.cs |
| **I-P10** | A seeder converges: any number of sequential or concurrent runs leave the rows one run leaves (Owner: each seeder's module.) | — | instruction | tests/SubZeroDev.Platform.Tests/SeedingTests.cs |
| **I-P11** | Migrate mode opens no operation scope for a seeder; a seeder writes only inside a scope it opened naming its tenant, as `Principal.LocalSystem` (Owner: Persistence for the first half; each seeder's module for the second.) | — | instruction | tests/SubZeroDev.Platform.Tests/SeedingTests.cs |
| **I-R1** | Authorization precedes entitlement, and both precede any side effect (Owner: Hosting, Mcp.) Enforced by code — pipeline order. | — | code | tests/SubZeroDev.Platform.Tests/RequestOrderTests.cs |
| **I-R2** | Tenant resolution precedes authorization (Owner: Hosting, Mcp.) | — | code | tests/SubZeroDev.Platform.Tests/RequestOrderTests.cs |
| **I-R3** | The scope's tenant and principal do not change for the request's lifetime (Owner: Core.) | — | code | tests/SubZeroDev.Platform.Tests/TenancyTests.cs |
| **I-R4** | The local host takes the same path with no step skipped and no branch taken (Owner: Hosting.) Enforced by code — sample scenario. | — | code | tests/SubZeroDev.Platform.Tests/RequestOrderTests.cs |
| **I-R5** | Only an endpoint admitting new paid-feature work is entitlement-gated (Owner: consumers.) | — | instruction | — |
| **I-R6** | Every mapped endpoint carries a requirement declaration or a named exemption (Owner: Hosting.) Enforced by code — startup check over the endpoint data source. | — | code | tests/SubZeroDev.Platform.Tests/RequestOrderTests.cs, tests/SubZeroDev.Platform.Tests/AdministrationShellTests.cs |
| **I-R7** | The pipeline's authorization check is never resource-scoped; a per-resource check is the handler's own second call (Owner: Hosting, consumers.) | — | instruction | — |
| **I-S1** | The sign-in module creates a session only after the callback's state equals the state it stored for that begin, and the session holds no more than the issuer, the subject, the access token and its expiry; the module owns no row and does not change the authentication seam (Owner: SignIn.) | — | instruction | — |
| **I-S2** | Sign-out clears the session and redirects to the issuer, and never claims to revoke an access token already issued; such a token stays valid at Platform until it expires (Owner: SignIn.) | — | instruction | — |
| **I-S3** | A host-registered sign-in hook runs after configuration binds and cannot change which issuer, key source, audience or algorithm Platform trusts; the trust root is the generic path's settings alone (Owner: SignIn.) | — | instruction | — |
| **I-ST1** | A runtime setting resolves user, then tenant, then global, then its declared default, over the layers its declaration admits; the user layer exists only for an `Account` principal in a non-implicit tenant, and the tenant layer only for a non-implicit tenant (Owner: RuntimeSettings.) | — | code | tests/SubZeroDev.Platform.Tests/RuntimeSettingsTests.cs |
| **I-ST2** | Every runtime setting is declared once in a catalogue frozen before the host serves; a duplicate name, a default its own rule rejects, an empty layer set, or a name matching the redaction marker set fails startup (Owner: RuntimeSettings.) | — | code | tests/SubZeroDev.Platform.Tests/RuntimeSettingsTests.cs |
| **I-ST3** | A global or tenant write is authorized against `Platform.RuntimeSettings.WriteGlobal` or `WriteTenant`, scoped to the setting, before the store is touched; a user write reaches only the ambient account's own row (Owner: RuntimeSettings.) | — | code | tests/SubZeroDev.Platform.Tests/RuntimeSettingsTests.cs |
| **I-ST4** | Every change to a setting writes exactly one `Required` audit record in the change's transaction, naming the layer and setting and never the value; if the record cannot be written the change does not commit (Owner: RuntimeSettings.) | — | code | tests/SubZeroDev.Platform.Tests/RuntimeSettingsTests.cs, tests/SubZeroDev.Platform.Tests/AuditInputSurfaceTests.cs, tests/SubZeroDev.Platform.Tests/RuntimeSettingsPostgresTests.cs |
| **I-ST5** | Tenant and user setting rows are read and written only with the current tenant; a global row belongs to the installation and is stored under the implicit tenant with layer `global` (Owner: RuntimeSettings.) | — | code | tests/SubZeroDev.Platform.Tests/RuntimeSettingsTests.cs |
| **I-ST6** | No runtime setting grants a permission or a feature: the module registers no permission provider and no entitlement contributor, and no authorization or entitlement decision reads a setting (Owner: RuntimeSettings.) | — | code | tests/SubZeroDev.Platform.Tests/PackageGraphTests.cs |
| **I-ST7** | The module holds no in-process copy of a stored value, so a committed write is seen by the next read on every instance of every role (Owner: RuntimeSettings.) | — | code | tests/SubZeroDev.Platform.Tests/RuntimeSettingsTests.cs, tests/SubZeroDev.Platform.Tests/RuntimeSettingsPostgresTests.cs |
| **I-ST8** | Runtime settings and their catalogue are not inputs to the settings fingerprint, and no runtime setting overrides a startup option (Owner: RuntimeSettings.) | — | code | tests/SubZeroDev.Platform.Tests/SettingsFingerprintTests.cs |
| **I-ST9** | A stored value that fails to parse or validate under the current declaration is answered `StoredValueInvalid`, never replaced by a lower layer or the default (Owner: RuntimeSettings.) | — | code | tests/SubZeroDev.Platform.Tests/RuntimeSettingsTests.cs |
| **I-T1** | **There is no code path in Platform by which a write reaches another tenant's row.** Isolation is asymmetric on purpose: reads have one modelled audited escape, writes have none (Owner: Persistence.) | — | code | tests/SubZeroDev.Platform.Tests/PersistenceIntegrationTests.cs, tests/SubZeroDev.Platform.Tests/SharedReadTests.cs, tests/SubZeroDev.Platform.Tests/AuditStoreTests.cs |
| **I-T2** | Outside a shared-read scope the query filter is `tenant equals current`, unconditionally, for shareable and non-shareable types alike (Owner: Persistence.) | — | code | tests/SubZeroDev.Platform.Tests/SharedReadTests.cs |
| **I-T3** | A shared-read scope widens the filter for the one declared type only (Owner: Persistence.) Enforced by code — the generic parameter. | — | code | tests/SubZeroDev.Platform.Tests/SharedReadTests.cs |
| **I-T4** | Opening a shared-read scope emits exactly one audit record, never one per row (Owner: Persistence.) Enforced by code — scope construction. | — | code | tests/SubZeroDev.Platform.Tests/SharedReadTests.cs |
| **I-T5** | `SharedAt` is written only by a permissioned, audited, tenant-scoped write by the owning tenant (Owner: Persistence.) Enforced by code — for the permission check; enforced by instruction otherwise. | — | code, instruction | — |
| **I-T6** | With no resolver registered, `ICurrentTenant.Current` is `TenantId.Implicit` (Owner: Core.) Enforced by code — resolver chain. | — | code | tests/SubZeroDev.Platform.Tests/TenancyTests.cs |
| **I-T7** | The tenant identifier, primary keys and implicit-tenant representation are unchanged from D3 and G2 (Owner: Persistence.) | — | code | tests/SubZeroDev.Platform.Tests/PersistenceIntegrationTests.cs, tests/SubZeroDev.Platform.Tests/TenancyTests.cs |
| **I-T8** | A resolver never denies; it answers or defers (Owner: Core.) Enforced by code — the return type carries no decision. | — | code | tests/SubZeroDev.Platform.Tests/TenancyTests.cs |
| **I-U1** | `AuditEvent` has no payload, changed-field list or free-form detail field (Owner: Abstractions.) Enforced by code — the type. | — | code | tests/SubZeroDev.Platform.Tests/AuditTests.cs |
| **I-U2** | Authorization denials, shared-resource escapes, membership and ownership changes, entitlement and licence transitions and MCP invocations are `Required`; everything else is `Recorded` (Owner: each writer.) | — | instruction | — |
| **I-U3** | A successful action that wrote state writes its audit row in the same transaction (Owner: each writer.) Enforced by code — the ambient transaction. | — | code | tests/SubZeroDev.Platform.Tests/AuditTests.cs, tests/SubZeroDev.Platform.Tests/OrganizationsTests.cs |
| **I-U4** | A denial, a read, or a failure that wrote nothing writes its row in its own transaction after the outcome is known (Owner: each writer.) | — | code | tests/SubZeroDev.Platform.Tests/AuditTests.cs, tests/SubZeroDev.Platform.Tests/AuditStoreTests.cs, tests/SubZeroDev.Platform.Tests/McpInvocationTests.cs |
| **I-U5** | `Action`, `Resource.Type` and `Resource.Id` pass through the redaction boundary before storage and before logging (Owner: Core.) Enforced by code — the writer, not the sink. | — | code | tests/SubZeroDev.Platform.Tests/AuditTests.cs |
| **I-U6** | No secret value or payload reaches a stored record or a log line, through **any** audited input surface (Owner: all.) | — | code | tests/SubZeroDev.Platform.Tests/AuditInputSurfaceTests.cs, tests/SubZeroDev.Platform.Tests/AuditTests.cs, tests/SubZeroDev.Platform.Tests/AuditStoreTests.cs, tests/SubZeroDev.Platform.Tests/McpInvocationTests.cs, tests/SubZeroDev.Platform.Tests/TelemetryRedactionTests.cs, tests/SubZeroDev.Platform.Tests/OperatedScenarioTests.cs |
| **I-U7** | Audit records are never updated, and no surface exposes an update or a delete; the only delete is the retention prune, which removes whole rows by age (Owner: Audit store.) | — | instruction | tests/SubZeroDev.Platform.Tests/AuditStoreTests.cs, tests/SubZeroDev.Platform.Tests/AuditRetentionTests.cs |
| **I-U8** | Audit rows are not totally ordered across hosts and nothing relies on the opposite (Owner: all.) | — | instruction | — |
| **I-U9** | An error condition is audited once per detection, not once per check (Owner: Licensing, Core.) | — | code | tests/SubZeroDev.Platform.Tests/AuditDetectionTests.cs, tests/SubZeroDev.Platform.Tests/LicensingTests.cs |
| **I-U10** | Audit-write failure degrades readiness in both classes (Owner: Core.) Enforced by code — `platform.audit.sink`. | — | code | tests/SubZeroDev.Platform.Tests/AuditTests.cs |
| **I-U11** | With `Platform:Audit:RetentionDays` absent, no audit-prune work is registered and nothing deletes an audit row (Owner: Audit store.) | — | instruction | tests/SubZeroDev.Platform.Tests/AuditRetentionTests.cs |
| **I-U12** | With `Platform:Audit:RetentionDays` set to N, the retention prune runs only in the Worker role under its lease, deletes only rows whose `OccurredAt` is earlier than the worker's now minus N days, and deletes at most 500 rows per statement, each in its own transaction (Owner: Audit store.) | — | instruction | tests/SubZeroDev.Platform.Tests/AuditRetentionTests.cs |
| **I-U13** | A present `Platform:Audit:RetentionDays` that is not a whole number from 1 to 36500 fails startup in both roles with `HostStartupError.Configuration` carrying `ConfigurationError.InvalidSetting` naming the key (Owner: Core.) | — | instruction | tests/SubZeroDev.Platform.Tests/AuditRetentionTests.cs |
| **I-U14** | Two hosts whose `Platform:Audit:RetentionDays` differ, including set against absent, compute different settings fingerprints (Owner: Core.) | — | instruction | tests/SubZeroDev.Platform.Tests/SettingsFingerprintTests.cs |
| **I-W1** | The shell holds no server-side state and reaches the system only over the public HTTP API; **no backend package references it, and it has no privileged endpoint of its own** (Owner: the shell.) | — | code | tests/SubZeroDev.Platform.Tests/PackageGraphTests.cs, tests/SubZeroDev.Platform.Tests/AdministrationShellTests.cs |
<!-- invariants:end -->

---

## Unresolved

Item 1 (the nullability of `IAuditable.CreatedBy` under a total principal) was resolved for S2 —
**`CreatedBy` becomes non-null** — and is recorded under *Persisted schemas*, § 6 and
[`90-decisions.md`](90-decisions.md), 2026-08-31.

Item 2 (how the registered entitlement-contributor set reaches the settings-fingerprint input) was
resolved for S8 — **`Compute` gains a second parameter** carrying the frozen contributor names,
rather than projecting them onto a `[Fingerprinted]` property of `PlatformOptions` — and is recorded
at [`SettingsFingerprint.cs`](../src/SubZeroDev.Platform.Core/SettingsFingerprint.cs) and
[`90-decisions.md`](90-decisions.md), 2026-09-03. The format version inside `SettingsFingerprint`
changed in the same commit, per what the item determined either way.

Item 3 (may a consumer-registered permission provider derive a grant from token claims?,
[#92](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/92)) was resolved by the grant-source
rule in [`10-design.md`](10-design.md) *Data model* § 3 — claims and the raw authentication result are never
a grant source for any provider — recorded at [`90-decisions.md`](90-decisions.md), 2026-09-25. The rule is
structural for Platform's providers (I-I6, I-A2, I-A10) and a contract obligation for a consumer's, with the
revoke-then-deny test shipped in `Platform.Testing` as a harness. `Principal.Claims` stays public, and
#92's criteria were amended to match, per the owner's ruling of 2026-10-07 ([#263](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/263),
[`90-decisions.md`](90-decisions.md), 2026-10-07). **Its revocation half was reopened for roles mirrored
from an issuer** by red-team F3 ([#264](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/264)):
the next-request promise did not hold for them, and the mirror's delay was unbounded. It is resolved by
the owner's ruling of 2026-10-08 and the `10-design.md` correction landing in the same change: a mirror
older than `Platform:Authorization:MirrorMaximumAge` cannot answer, a mirrored role's revocation takes
effect within that maximum (*Public surface* § 3; I-A13, I-A14), and a second harness proves it by
revoking at the issuer ([`90-decisions.md`](90-decisions.md), 2026-10-08).

**Item 4 was reopened and resolved by the 2026-10-08 design, and the paragraph below is superseded for
a host that takes the sign-in module.** #94's first, third and fourth criteria now have a sign-in method
to attach to (*Types* § 13, *Public surface* § 13), and 2026-09-26's accepted risk no longer applies to
such a host. The host that serves the endpoints is `10-design.md` *Open questions*, 5, and the
declarations are written against its recommendation. What follows is kept as the record of the
validation half:

Item 4 (a per-vendor escape hatch for sign-in providers that depart from the standard,
[#94](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/94)) was resolved **for the
validation half only**. The generic bearer path, its configuration schema and the vendor configuration
package's shape are in *Types* § 12 and *Public surface* § 10. They follow
[`90-decisions.md`](90-decisions.md), 2026-09-25, which settled the three questions this item was
waiting on, and 2026-09-30, which settled the choices the design left to this document. **#94 is not
satisfied by this contract.** Its first, third and fourth done-when criteria are a hook per configured
sign-in method, sign-out proven against a non-conforming provider, and documentation that reaching for
the hook is expected. They describe a sign-in method Platform does not have, and D5 does not deliver
them ([`90-decisions.md`](90-decisions.md), 2026-09-26, an accepted risk). Its second criterion holds
in validation terms only: a vendor configuration package adjusts the generic path's values without
hand-wiring the rest of it. A vendor's sign-in and sign-out quirks stay with each client
([`10-design.md`](10-design.md) § *Control flow*, path 4).

Item 5 (a public inbox for inbound messages that do not come from the outbox — webhooks, a future
transport) is **deliberately not offered** by #62's design. Billing records its provider-event receipts
itself, and no second consumer has asked; the extraction guard says the second consumer earns the
abstraction. When one does, the `platform_inbox` table's `(message_id, consumer)` key is the shape it
would extend, and the decision must also name stable handler identities if one-handler-per-type is
relaxed.

Item 8 of the D3 contract ([`d3/20-contract.md`](d3/20-contract.md), *Unresolved*: development-environment
automatic migration) was resolved by the owner's ruling of 2026-10-08
([#68](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/68)). It is built, and its four questions
are answered in *Public surface* § 16 and *Error semantics* § 13:

- **Owner:** Persistence.
- **Trigger:** automatic in the derived `Development` environment only.
- **Lock:** the provider-native lock, through the runner.
- **Failure:** `Failed` fails startup, `Locked` waits, `Unavailable` starts not-ready.

Recorded at [`90-decisions.md`](90-decisions.md), 2026-10-08. The seeder that D3's `NoAmbientOperationScope`
remedy names now exists, as `ISeeder` (§ 16,
[#61](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/61)).
