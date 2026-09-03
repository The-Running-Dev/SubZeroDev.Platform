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
- **Nothing here is a user entity, and none may be added.** Platform owns no directory: federation,
  account linking and a shared user store are brief non-goals.

### 2. Authorization — `SubZeroDev.Platform.Abstractions`

`PermissionName`, `PermissionProviderName`, `AuthorizationOutcome`, `AuthorizationDecision` and
`PlatformPermissions` are declared in the tree (S4):
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
- **A `PermissionName` reaching the evaluator unregistered is a startup-detectable defect, never a
  runtime denial.** A typo that silently denies is indistinguishable from a policy that denies — I-A3.
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
- **Audit rows are append-only at the contract.** No update, no delete, and no sink may expose one.
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
and nothing else to the storage shape.

```csharp
/// An entity type that may publish rows for reading by other tenants. Declared on the type at model
/// build; there is no per-row opt-in on an ordinary type.
public interface IShareable : ITenantOwned
{
    /// When the owning tenant published the row, or null while it is private.
    DateTimeOffset? SharedAt { get; }
}
```

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

### 7. Organizations — `SubZeroDev.Platform.Organizations`

```csharp
public readonly record struct OrganizationId(Guid Value);
public readonly record struct InvitationId(Guid Value);

public enum OrganizationRole
{
    Owner,
    Administrator,
    Member,
}

public enum MembershipState
{
    Active,
    Revoked,
}

public sealed record Organization(
    OrganizationId Id,
    TenantId Tenant,
    string Name,
    PrincipalId Owner,
    DateTimeOffset CreatedAt);

public sealed record Membership(
    OrganizationId Organization,
    PrincipalId Principal,
    OrganizationRole Role,
    MembershipState State,
    DateTimeOffset CreatedAt);

public sealed record Invitation(
    InvitationId Id,
    OrganizationId Organization,
    OrganizationRole Role,
    DateTimeOffset ExpiresAt,
    string TokenHash,
    PrincipalId? RedeemedBy,
    DateTimeOffset? RedeemedAt);
```

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

```csharp
public readonly record struct PlanKey(string Value);
public readonly record struct SubscriptionId(Guid Value);

public enum SubscriptionState
{
    Trialing,
    Active,
    PastDue,
    Cancelled,
    Expired,
}

public sealed record Plan(
    PlanKey Key,
    string DisplayName,
    IReadOnlySet<FeatureName> Features);

public sealed record Subscription(
    SubscriptionId Id,
    TenantId Tenant,
    PlanKey Plan,
    SubscriptionState State,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    DateTimeOffset? TrialEndsAt,
    string ProviderReference);

public sealed record ProviderEventReceipt(
    string Provider,
    string ProviderEventId,
    DateTimeOffset ReceivedAt);
```

**What the declarations cannot say.**

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

```csharp
/// The tier a licence document claims. An opaque stable name: Platform is not a licensor and owns
/// no tier vocabulary beyond the well-known baseline.
public readonly record struct LicenceTier(string Value)
{
    public string Value { get; }

    /// The tier of an installation with no verified claims. Never absent, never null.
    public static LicenceTier Community { get; }

    public override string ToString();
}

public enum LicenceVerificationOutcome
{
    Verified,
    Invalid,
    Unavailable,
    ClockUnusable,
}

/// The claims in force. Derived from the stored record; never from the document.
public sealed record LicenceClaims(
    LicenceTier Tier,
    IReadOnlySet<FeatureName> Features,
    DateTimeOffset IssuedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? GraceEndsAt,
    DateTimeOffset VerifiedAt);

/// One accepted signing key. A deployment supplies an ordered set.
public sealed record LicenceSigningKey(
    string KeyId,
    System.Security.Cryptography.AsymmetricAlgorithm PublicKey);
```

**What the declarations cannot say.**

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

```csharp
/// A tool's name, unique across every producer.
public readonly record struct ToolName(string Value)
{
    public string Value { get; }
    public override string ToString();
}

/// Which producer supplied a definition.
public readonly record struct ToolProducerName(string Value)
{
    public string Value { get; }
    public override string ToString();
}

/// A tool as its producer supplies it. Carries no exposure: a producer cannot expose itself.
public sealed record ToolDefinition(
    ToolName Name,
    string Description,
    System.Text.Json.JsonElement ParameterSchema,
    PermissionName RequiredPermission,
    FeatureName? RequiredFeature);

/// A definition after configuration has decided its exposure. What the frozen catalogue holds.
public sealed record ToolRegistration(
    ToolDefinition Definition,
    ToolProducerName Producer,
    bool IsExposed);

/// Supplies definitions at startup. Manifest projection and a product-owned fixed table each
/// implement this, and neither is privileged.
public interface IToolProducer
{
    ToolProducerName Name { get; }

    ValueTask<IReadOnlyCollection<ToolDefinition>> ProduceAsync(CancellationToken cancellationToken);
}

/// The catalogue, frozen after startup.
public interface IToolCatalogue
{
    /// Every exposed registration. An unexposed one is not here and is not reachable from here.
    IReadOnlyCollection<ToolRegistration> Exposed { get; }

    /// Looks up an exposed tool. Unregistered and unexposed both answer false.
    bool TryGetExposed(ToolName name, out ToolRegistration registration);
}
```

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
rather than asserted. Its delivery shape is [`10-design.md`](10-design.md) § *Open questions* 2 and
does not affect this document: neither answer introduces a .NET declaration, and the recommendation
and its alternative differ only in how the assets are built and served.

The shell's contract is therefore a constraint rather than a type, and it is I-W1.

---

## Persisted schemas

Nothing in the framework's D5 additions is persisted except audit, and audit's storage is a module. A
host with no Identity, Organizations, Billing or Licensing package has no table those capabilities
would own and no migration to skip — that property is what the brief's local shape forces, and it is
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
  reads the brief's Audit criterion and the shell's audit view perform. Nothing else is indexed,
  because every index is a write cost on the path every security-sensitive action takes.
- **Constraints:** every column non-null except the two resource columns, which are null together or
  present together. **No unique constraint spans hosts** — appends must not contend.
- **Append-only, enforced by the schema having no update or delete path in the module's surface.**
- **Migration story: none, and the absence of a retention migration is deliberate.** D5 selects no
  retention (brief, *Environment and operating assumptions*), so **the table grows without bound and
  no pruning job is registered even though [`Prune`](../src/SubZeroDev.Platform.Persistence/Prune.cs)
  exists.** That is a real accepted cost; the first party to notice will be an operator rather than a
  reviewer, and it is tracked under [`90-decisions.md`](90-decisions.md) § *Open*.

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
- **It must never block on a network fetch.** Key material is fetched at startup and cached; a request
  arriving when no key is cached fails with `AuthenticationError.KeyMaterialUnavailable`, which is an
  authentication failure and **never a server error**.
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

- **`EvaluateAsync` takes no principal and no tenant.** Both come from the ambient operation scope,
  which fixed them for the request's lifetime. A parameter for either would let a call site evaluate
  in a tenant the request did not resolve, which is the whole of tenant-aware authorization defeated
  at one call.
- **`EvaluateAsync` returns a decision, never a failure result.** A denial is a decision. A provider
  that could not answer returns `AuthorizationError` to the evaluator, which turns it into a denial
  the caller may retry — *Error semantics*, § 2.
- **`GrantsAsync` returning an error denies; it never grants.** An unreachable store fails closed.
- **A provider must not audit.** The evaluator audits the decision once, so a union across three
  providers does not write three records.
- **`IPermissionProvider.Name` must be unique**, and two providers sharing a name is a startup
  failure: a decision naming its source is worthless if two sources share a name.
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

### 7. Shared-read scope — `SubZeroDev.Platform.Persistence`

```csharp
/// Opens the one modelled cross-tenant read. Read-only, one declared type, audited once per scope.
public interface ISharedReadScopeFactory
{
    /// Widens the query filter to "mine, or shared" for TEntity only, for the scope's lifetime.
    /// Emits one audit record when the scope opens.
    IDisposable Open<TEntity>() where TEntity : class, IShareable;

    /// Whether a shared-read scope is currently open for TEntity. Persistence imposes no
    /// repository or ORM (`design/d3/90-decisions.md`, 2026-08-03), so this is the seam a
    /// consumer's own query code — EF's `HasQueryFilter`, Dapper, raw ADO — consults to decide
    /// whether to widen its own filter, on the same terms `IAmbientTransactionAccessor.Current`
    /// already exposes the ambient transaction to a consumer's own data-access code.
    bool IsOpenFor<TEntity>() where TEntity : class, IShareable;
}
```

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
- **`Open<TEntity>` must not acquire a tenant parameter.** Naming the tenant to read from would turn
  the modelled escape into a cross-tenant fetch, and the point of the scope is that the caller states
  *that* it is crossing, not *whose* rows it wants.

### 8. Composition and startup — `SubZeroDev.Platform.Core`

- **`PlatformOptions` gains the composition profile, marked `[Fingerprinted]`.**
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
- **Startup validation is stated as I-C1 to I-C4 and I-A3 to I-A4**, and every one fails the host with
  a named error rather than degrading it. **A host that cannot state its own composition does not
  serve**, because every guarantee in this document is stated relative to a composition.

### 9. Testing — `SubZeroDev.Platform.Testing`

Fakes for the framework seams only, beside the existing ones in
[`Fakes.cs`](../src/SubZeroDev.Platform.Testing/Fakes.cs): a fake principal of each of the four kinds,
a fake tenant resolver, a fake entitlement contributor, a fake permission provider, an audit
inspector, and the composition profile on the test host.

- **`FakeCurrentPrincipal.Current` becomes `Principal`, non-null**, following the interface.
- **No fake organization, subscription or licence.** Those are module knowledge, and a framework
  package faking one is ADR-006 rule 1 violated by the test helpers, which is where it is least likely
  to be noticed. **Each module carries its own fakes.**
- **The audit inspector reads records; it does not write or clear them.** A test helper that can
  delete an audit row is a test helper that can be used to prove the wrong thing.

### 10. Modules

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
instant range, actor and correlation. **It exposes no update and no delete.**

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

**Web shell** exposes nothing on the server — I-W1.

### 11. Request order — `SubZeroDev.Platform.Hosting`

Hosting gains the fixed order, and **it is the most expensive thing in this contract to change later,
because every capability's semantics are stated relative to it**: authenticate at the transport,
resolve the tenant, open the operation scope, authorize, check entitlement if the endpoint admits new
paid-feature work, do the work inside a transaction when it writes, audit.

**How an endpoint declares its permission and its feature** — resolved for S8, `## Unresolved` item
3 — mirrors `ToolDefinition` (*Types*, § 10)'s split between a mandatory grant and an optional gate,
attached as endpoint metadata rather than as a constructor argument because Hosting composes over
`Microsoft.AspNetCore.Routing.IEndpointConventionBuilder`, not over a registration list Platform owns:

```csharp
namespace SubZeroDev.Platform.Hosting;

public sealed record EndpointRequirement(
    PermissionName RequiredPermission,
    FeatureName? RequiredFeature);

public static class EndpointRequirementExtensions
{
    public static TBuilder RequiresPermission<TBuilder>(
        this TBuilder builder,
        PermissionName permission,
        FeatureName? admitsFeature = null)
        where TBuilder : IEndpointConventionBuilder;
}
```

- **Authorization precedes entitlement, and both precede any side effect.** A principal who may not
  perform an action must not learn from the response whether the deployment is entitled to the
  feature, and entitlement resolution may read the store while an authorization denial must not.
  **Reversing them turns every entitlement into an unauthenticated probe.**
- **Tenant resolution precedes authorization**, because permissions are tenant-aware and a decision
  taken before the tenant is known has been taken in the wrong tenant.
- **The scope's tenant and principal are fixed for the request's lifetime.** A membership revoked
  while a request is in flight does not change the request that already resolved.
- **The local host takes the same path, with no step skipped and no branch taken.** The absence of the
  four commercial packages is visible in the package graph and invisible in the flow.
- **Only an endpoint that admits new paid-feature work is entitlement-gated**, decided by
  `RequiredFeature is not null`, never by the handler. An endpoint that reads, lists or exports data a
  tenant already has is not, even when that data was produced under the same feature — entitlement was
  checked at the admission that produced it, and a lapsed licence does not re-ask a question access
  already answered.
- **`RequiresPermission` is the one way an endpoint attaches this**, on the same terms
  `ToolDefinition.RequiredPermission` is the one way a tool does. An endpoint the pipeline routes with
  no `EndpointRequirement` attached is a startup-detectable defect, never a request the pipeline lets
  through unauthorized — `HostStartupError.EndpointRequirementMissing` (*Error semantics*, § 9).
- **`RequiresPermission` is called at startup, on the route, not per request.** The pipeline reads the
  attached metadata off `HttpContext.GetEndpoint()`; it does not call a handler-supplied delegate,
  which is what keeps step 4 and step 5 something the pipeline runs rather than something a correctly
  written handler happens to have called in order.

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

**No credential presented is not in this table.** It is success carrying `Principal.Anonymous`.

### 2. `AuthorizationError` — `SubZeroDev.Platform.Abstractions`

| Variant | Raised when | Retryable | The caller is expected to |
|---|---|---|---|
| `PermissionDenied` | no provider granted, and the principal can see the resource | no | return **forbidden** |
| `ResourceNotVisible` | the resource is in another tenant, or the principal may not know it exists | no | return **not found** |
| `ProviderUnavailable` | a provider could not answer — typically an unreachable store | **yes** | return a retryable failure; the denial stands for this request |

**`PermissionDenied` and `ResourceNotVisible` are not interchangeable, and the difference is a
security property rather than a style choice.** *Forbidden* confirms the resource exists. A
cross-tenant read returns not found; switching to an organization the principal is not a member of
returns not found; a permission denial on a resource the principal *can* see returns forbidden,
because there the existence is already known and pretending otherwise only obscures the fix.

**`ProviderUnavailable` is retryable and still denies.** Failing closed and being retryable are not in
tension: the request is denied now, and a caller who retries after the store returns is right to.

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
| `SinkRejected` | a sink refused the record as malformed | no | log, degrade readiness; **still apply the class rule** |

**The class decides the consequence, and the sink does not choose it:**

- **`Required`** — the response becomes a retryable failure and readiness degrades. Applies to
  authorization denials, shared-resource escapes, membership and ownership changes, entitlement and
  licence transitions, and MCP invocations.
- **`Recorded`** — logged, readiness degrades, the response is unaffected. Everything else.

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
| `NotAMember` | a member-only action by a principal whose membership is revoked | no | return forbidden |
| `InvitationNotRedeemable` | the token is expired, already redeemed, **or never existed** | no | return the same answer for all three |
| `TenantAlreadyAssigned` | the unique tenant constraint rejected a concurrent create | **yes** | retry the create; it mints a fresh tenant |

**`OrganizationNotFound` deliberately covers "exists but you may not see it".** Existence is not
confirmed to a caller who may not see it.

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
| `ConnectionUnauthenticated` | a session request presents a principal other than the one the session was established with | no | end the exchange; a new principal means a new connection |

Authorization and entitlement refusals are not `McpError` variants: they are § 2's and § 3's, raised
by the same evaluators the HTTP path uses, so a denial means the same thing on both surfaces.

| Condition | Answer | Retryable |
|---|---|---|
| tool not registered | **unknown tool** | no |
| tool registered but not exposed | **unknown tool — the same answer** | no |
| arguments fail the declared schema | invalid arguments | no |
| authorization denies | forbidden, or not found where the resource is not visible, per § 2 | no |
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

A new `PlatformError` subtype, wrapped by
[`HostStartupError.Registration`](../src/SubZeroDev.Platform.Hosting/StartupFailure.cs) on the shape
`ModuleGraphError` already establishes. **Every variant fails the host; none degrades it. None is
retryable — a misconfigured installation does not resolve itself.**

| Variant | Raised when |
|---|---|
| `AuthenticationProviderRequired` | `Operated` with no authentication provider registered |
| `DurableAuditSinkRequired` | `Operated` with no sink declaring `IsDurable` |
| `RegistrationForbiddenByProfile` | `Local` with an authentication provider, a tenant resolver, or an entitlement contributor other than the Community baseline |
| `DuplicatePermissionName` | two modules declare the same `PermissionName` |
| `DuplicateProviderName` | two providers, contributors, resolvers or sinks share a name |
| `UnregisteredPermission` | a tool, or any registration, requires a `PermissionName` no catalog declares |
| `SensitiveToolParameter` | a registered tool's schema names a parameter matching the redaction marker set |
| `EndpointRequirementMissing` | an endpoint the pipeline routes carries no `EndpointRequirement` |

**Each names the profile, the offending registration and which of the two it disagrees with**, on the
`Detail` convention `ModuleGraphError` and `ConfigurationError` already follow. Each describes a
deployment that would otherwise run and be wrong quietly.

### 10. `ContractViolation` — `SubZeroDev.Platform.Abstractions`

One variant is added to the existing set in
[`Results.cs`](../src/SubZeroDev.Platform.Abstractions/Results.cs):

| Variant | Raised when |
|---|---|
| `WriteInsideSharedReadScope` | a write was attempted while a shared-read scope was open |

It **throws** rather than returning, on the same terms as `NoAmbientTransaction`: a defect in the
caller, not a runtime condition.

---

## Invariants

Each is written so it could become an assertion. **"Code" means a build check, a startup check, a
type, or a store constraint — the only ones a reader may trust without checking.** "Instruction" means
this document is the only thing holding it, and a reviewer is the enforcement.

### Identity

| # | Invariant | Owner | Enforced by |
|---|---|---|---|
| I-I1 | The ambient principal is never null while an operation scope is open | Abstractions | **code** — non-nullable type |
| I-I2 | `PrincipalId.Issuer` and `.Subject` are never parsed, normalised, trimmed or case-folded by Platform | Abstractions | instruction |
| I-I3 | `PrincipalId.ToString()` is never split to recover the pair; anywhere the pair is stored it is two columns | Abstractions, Audit store, Organizations | instruction, and **code** in each schema |
| I-I4 | Platform declares no user entity and no directory | Identity | **code** — architecture check over the module's types |
| I-I5 | A `Delegated` principal is never treated as an `Account` with missing fields | every consumer | instruction |
| I-I6 | No Platform decision reads `Principal.Claims` | all | instruction |

### Authorization

| # | Invariant | Owner | Enforced by |
|---|---|---|---|
| I-A1 | `AuthorizationDecision.Sources` is non-empty **iff** `Outcome == Allowed` | Core | **code** — evaluator construction |
| I-A2 | The evaluator takes the union of every registered provider and consults no other source | Core | instruction |
| I-A3 | Every `PermissionName` reaching the evaluator is declared by some `IPermissionCatalog`; an undeclared one fails **startup**, never a request | Core | **code** — startup validation |
| I-A4 | Two modules never declare the same `PermissionName` | Core | **code** — startup validation |
| I-A5 | A provider returning an error denies and never grants | Core | **code** — evaluator |
| I-A6 | The composition provider grants nothing to `Anonymous` in either profile | Core | **code** — the provider, plus a sample scenario |
| I-A7 | The composition provider grants nothing at all in `Operated` | Core | **code** — the provider |
| I-A8 | No provider writes an audit record; the evaluator audits the decision once | Core | instruction |
| I-A9 | D5 has no role-assignment store | Organizations | **code** — schema |

### Tenancy

| # | Invariant | Owner | Enforced by |
|---|---|---|---|
| I-T1 | **There is no code path in Platform by which a write reaches another tenant's row.** Isolation is asymmetric on purpose: reads have one modelled audited escape, writes have none | Persistence | **code** — the scope is read-only and a write inside it throws |
| I-T2 | Outside a shared-read scope the query filter is `tenant equals current`, unconditionally, for shareable and non-shareable types alike | Persistence | **code** — the consumer's own query code, consulting `ISharedReadScopeFactory.IsOpenFor<TEntity>()` at model build |
| I-T3 | A shared-read scope widens the filter for the one declared type only | Persistence | **code** — the generic parameter |
| I-T4 | Opening a shared-read scope emits exactly one audit record, never one per row | Persistence | **code** — scope construction |
| I-T5 | `SharedAt` is written only by a permissioned, audited, tenant-scoped write by the owning tenant | Persistence | instruction, and **code** for the permission check |
| I-T6 | With no resolver registered, `ICurrentTenant.Current` is `TenantId.Implicit` | Core | **code** — resolver chain |
| I-T7 | The tenant identifier, primary keys and implicit-tenant representation are unchanged from D3 and G2 | Persistence | **code** — existing migrations unmodified |
| I-T8 | A resolver never denies; it answers or defers | Core | **code** — the return type carries no decision |

### Composition

| # | Invariant | Owner | Enforced by |
|---|---|---|---|
| I-C1 | `Operated` with no authentication provider fails startup | Core | **code** |
| I-C2 | `Operated` with no sink declaring `IsDurable` fails startup; the log sink is never an `Operated` fallback | Core | **code** |
| I-C3 | `Local` with an authentication provider, a tenant resolver, or a non-baseline entitlement contributor fails startup | Core | **code** |
| I-C4 | The composition profile and the contributor set are inside the settings-fingerprint input | Core | **code** — [`SettingsFingerprint.cs`](../src/SubZeroDev.Platform.Core/SettingsFingerprint.cs) |
| I-C5 | The local host has no package or project reference to Identity, Organizations, Billing or Licensing | the sample | **code** — dependency-graph assertion |
| I-C6 | No framework package references a module | all | **code** — architecture test over the resolved package graph, **which must fail against a deliberately broken graph before it counts** |
| I-C7 | No module references another module | all | **code** — the same test, second direction |
| I-C8 | Nothing outside Billing references `SubscriptionState` or any subscription type | all | **code** — architecture test |
| I-C9 | Every startup check fails the host and names the registration that caused it; none degrades | Core, Hosting | **code** |

### Entitlement, Billing and Licensing

| # | Invariant | Owner | Enforced by |
|---|---|---|---|
| I-B1 | Product code asks `FeatureName` and never subscription state or licence tier | all | **code** for subscription state (I-C8); instruction for tier |
| I-B2 | Contribution is a union; no contributor can veto another | Core | **code** — evaluator |
| I-B3 | `EntitlementDecision.Sources` is non-empty **iff** `Granted` | Core | **code** |
| I-B4 | Entitlement is never stored by Billing; it is derived from plan, state and `IClock` | Billing | **code** — no entitlement table exists |
| I-B5 | A unit of work carries the decision that admitted it; nothing re-evaluates during execution | consumers | instruction, and **code** in the sample's scenario |
| I-B6 | No billing provider is contacted on the request path, at startup, or on readiness | Billing | **code** — the offline CI run |
| I-B7 | A redelivered provider event is idempotent | Billing | **code** — unique receipt key |
| I-L1 | Exactly one verified-licence row exists per installation | Licensing | **code** — single-row key |
| I-L2 | No verification error path writes any column of that row | Licensing | **code**, plus a test that errors repeatedly and asserts the instants unchanged |
| I-L3 | A verification writes only when its instant is later than the stored one | Licensing | **code** — conditional update |
| I-L4 | An `Invalid` document never grants a tier | Licensing | **code** |
| I-L5 | Revocation is consulted on no path — not the request path, not startup, not readiness | Licensing | **code** — the offline CI run with outbound network unavailable |
| I-L6 | Grace comes from the document, defaulting to 30 days; it is never a deployment setting | Licensing | **code** — no such option exists |
| I-L7 | Accepted signing keys are supplied by the consumer as an ordered set; none is compiled into Platform | Licensing | **code** — a required option |
| I-L8 | After grace, new paid-feature work is denied while accepted, running and scheduled work continues and existing data stays readable and exportable | Licensing, consumers | **code** — sample scenario |
| I-L9 | Verification never fails startup and never fails a request | Licensing | **code** |

### Audit

| # | Invariant | Owner | Enforced by |
|---|---|---|---|
| I-U1 | `AuditEvent` has no payload, changed-field list or free-form detail field | Abstractions | **code** — the type |
| I-U2 | Authorization denials, shared-resource escapes, membership and ownership changes, entitlement and licence transitions and MCP invocations are `Required`; everything else is `Recorded` | each writer | instruction |
| I-U3 | A successful action that wrote state writes its audit row in the same transaction | each writer | **code** — the ambient transaction |
| I-U4 | A denial, a read, or a failure that wrote nothing writes its row in its own transaction after the outcome is known | each writer | **code** |
| I-U5 | `Action`, `Resource.Type` and `Resource.Id` pass through the redaction boundary before storage and before logging | Core | **code** — the writer, not the sink |
| I-U6 | No secret value or payload reaches a stored record or a log line, through **any** audited input surface | all | **code** — the brief's representative-secret tests |
| I-U7 | Audit records are append-only: no update, no delete, in any surface | Audit store | **code** — schema and API |
| I-U8 | Audit rows are not totally ordered across hosts and nothing relies on the opposite | all | instruction |
| I-U9 | An error condition is audited once per detection, not once per check | Licensing, Core | **code** — the detection sites |
| I-U10 | Audit-write failure degrades readiness in both classes | Core | **code** — `platform.audit.sink` |

### Organizations

| # | Invariant | Owner | Enforced by |
|---|---|---|---|
| I-O1 | Creating an organization mints the tenant, writes the organization and writes the owner's membership in one transaction with its audit row | Organizations | **code** |
| I-O2 | Two organizations never share a tenant | Organizations | **code** — unique constraint |
| I-O3 | One invitation token creates at most one membership | Organizations | **code** — conditional update |
| I-O4 | The invitation token is stored only as a hash and is readable exactly once, at mint | Organizations | **code** — schema and API |
| I-O5 | Expired, already-redeemed and never-existed are indistinguishable to a caller | Organizations | **code** — one error variant |
| I-O6 | Membership is keyed by `PrincipalId` and never by a user row | Organizations | **code** — schema |
| I-O7 | A non-member cannot switch into or administer an organization, and is told not found | Organizations | **code** — sample scenario |
| I-O8 | The framework never learns that a tenant has an owner | all | **code** — I-C6 |

### Mcp

| # | Invariant | Owner | Enforced by |
|---|---|---|---|
| I-M1 | The tool catalogue is frozen after startup; nothing registers, unregisters or re-exposes at runtime | Mcp | **code** |
| I-M2 | No registered tool's schema names a parameter matching the redaction marker set; a match fails **startup** | Mcp | **code** — startup validation |
| I-M3 | Exposure is default closed; a registered but unexposed tool is neither listed nor callable | Mcp | **code** |
| I-M4 | Unregistered and unexposed produce the identical answer | Mcp | **code** |
| I-M5 | Authentication happens at the connection and never at a call | Mcp | **code** — no per-call credential parameter exists |
| I-M6 | Authorization runs before any producer code is reached | Mcp | **code** — invocation order |
| I-M7 | Both producers — manifest projection and a product-owned fixed table — register through the same surface, and neither is privileged | Mcp | **code** — sample scenario |
| I-M8 | An invocation is audited with no arguments | Mcp | **code** — I-U1 |
| I-M9 | No SDK type appears in Platform's public surface; `ModelContextProtocol.*` is referenced by `SubZeroDev.Platform.Mcp` and by nothing else | Mcp | **code** — architecture test over the resolved package graph, alongside I-C6 and I-C7 |
| I-M10 | `IToolCatalogue` offers no route to an unexposed registration — no `All`, no exposure-ignoring lookup | Mcp | **code** — the interface |
| I-M11 | Platform's permission evaluator is the only authorization authority on this surface; the SDK's authorization-metadata path is not used | Mcp | instruction, and **code** — the sample's unknown-tool scenario |

### Shared web UI

| # | Invariant | Owner | Enforced by |
|---|---|---|---|
| I-W1 | The shell holds no server-side state and reaches the system only over the public HTTP API; **no backend package references it, and it has no privileged endpoint of its own** | the shell | **code** — the package graph, plus an assertion that every endpoint the shell calls is callable without it |

### Request order

| # | Invariant | Owner | Enforced by |
|---|---|---|---|
| I-R1 | Authorization precedes entitlement, and both precede any side effect | Hosting, Mcp | **code** — pipeline order |
| I-R2 | Tenant resolution precedes authorization | Hosting, Mcp | **code** |
| I-R3 | The scope's tenant and principal do not change for the request's lifetime | Core | **code** — the scope |
| I-R4 | The local host takes the same path with no step skipped and no branch taken | Hosting | **code** — sample scenario |
| I-R5 | Only an endpoint admitting new paid-feature work is entitlement-gated | Hosting | **code** — `EndpointRequirement.RequiredFeature is null` |
| I-R6 | Every endpoint the pipeline routes carries an `EndpointRequirement`; one that doesn't fails **startup**, never a request | Hosting | **code** — startup validation, on I-A3's and I-M2's terms |

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

Item 3 (how an endpoint declares its required permission, and whether it admits new paid-feature work)
was resolved for S8 — **a declared metadata surface**, `EndpointRequirement` attached through
`RequiresPermission`, mirroring `ToolDefinition` — and is recorded under *Public surface*, § 11 and
[`90-decisions.md`](90-decisions.md), 2026-09-03. I-R5's `Enforced by` and `Owner` changed in the same
edit, and a new invariant (I-R6) and a new `HostStartupError` variant
(`EndpointRequirementMissing`) were added, per what the item determined.
