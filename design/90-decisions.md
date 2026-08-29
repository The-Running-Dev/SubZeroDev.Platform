# Decision log — D5 effort

Append-only. Newest at the top. The rejected alternatives are the point — without them, every future
session relitigates the same choice.

Completed efforts keep their logs with their design sets:
[`g2/90-decisions.md`](g2/90-decisions.md), [`g1/90-decisions.md`](g1/90-decisions.md),
[`d3/90-decisions.md`](d3/90-decisions.md).

**This log is effort-local.** `AGENTS.md`, *Decision logging*, decides what belongs here and what
belongs in `docs/docs/adr/`.

## Open

- **Audit retention is deferred out of D5 and the table grows without bound.** The brief's operating
  assumptions exclude retention duration, archival, export formats and external shipping from this
  effort, so no pruning job is registered even though `Prune` exists. The first party to notice will be
  an operator, not a reviewer. Someone has to decide what retention means before an operated deployment
  runs for long.
- **`Platform.Mcp`'s protocol implementation has not been evaluated against existing packages.**
  ADR-004 §4 requires the check and requires the reason recorded either way, and it must happen before
  `/contract` because it decides whether tool registration and invocation are Platform's own contracts
  or a projection of somebody else's. `10-design.md` § *Open questions* 1 states the criteria.

---

### 2026-08-29 — Provenance is a named type, and the well-known names are Platform's own public surface

Context: `/contract` for D5. `10-design.md` requires an authorization decision to carry "the non-empty
set of providers that produced the grant" and an entitlement decision to name its contributors, and it
requires the publication of a shared row to be "an explicit, permissioned, audited write" and an
audit-write failure to degrade readiness. None of those is expressible without something to name: the
design states the requirement and not the handle.
Chosen: `PermissionProviderName` and `EntitlementContributorName` as opaque name structs on the shape
`ModuleName` and `HealthCheckName` already establish; `ResourceRef(Type, Id)` as the one type both a
resource-scoped authorization check and an audit record name a resource with; `AuditAction` as a name
struct; and three well-known name classes on the `PlatformHealthChecks` precedent —
`PlatformPermissions` (`Platform.Tenancy.ShareResource`, `Platform.Organizations.Administer`,
`Platform.Audit.Read`), `PlatformAuditActions`, and `PlatformHealthChecks.AuditSink`
(`platform.audit.sink`). All are public surface, because a consumer's policy and an operator's probe
body both refer to them by name.
Rejected: **bare strings for provider and contributor provenance**, which needs no new type; rejected
because the union's entire risk control is that a decision names its source, and a raw string is one
typo away from attributing a grant to a provider that did not make it, with nothing to catch it.
**Two resource types, one for authorization and one for audit**; rejected because the audit record of
a denial must name the same resource the check named, and two types is a mapping that can disagree.
**Leaving the publication permission unnamed for a slice to invent**; rejected because an unnamed
permission is one each consumer names differently, and I-A3 makes an undeclared name a startup
failure — so the name has to exist before the first slice, not after it.
Reversibility: cheap for each name's spelling while packages are 0.x; expensive for the decision that
provenance is typed at all, since it is what every audit and diagnostic reading a decision depends on.

### 2026-08-29 — The licence tier is an opaque name with a well-known Community baseline

Context: `10-design.md` states that Platform is not a licensor and that accepted keys are supplied by
the consuming product, while the brief and `tenancy-billing-licensing.md` both name Community, Pro and
Enterprise and require a fresh installation with no verified claims to continue at Community. The
design does not say which of those two facts decides the type.
Chosen: `LicenceTier` is an opaque stable name struct with one well-known value, `Community`, standing
to the tier vocabulary exactly as `TenantId.Implicit` stands to tenants and `Principal.Anonymous` to
principals — a real value rather than the absence of one. Entitlement is asked by `FeatureName`, never
by tier, so the tier is recorded and displayed and never branched on by Platform.
Rejected: **an enum of Community, Pro and Enterprise**, which is the vocabulary both documents use and
which makes the well-known baseline free; rejected because that vocabulary is the Automator's licence
model rather than Platform's, a framework type carrying a product's price tiers is the same mistake as
a framework type meaning "an organization", and a second consumer with a different ladder would face a
framework change to sell anything.
Reversibility: cheap. Narrowing an opaque name to a closed set later is additive for anyone already
using the three names; widening a shipped enum is not.

### 2026-08-29 — A durable audit sink declares itself, and the audit table is keyed by event id

Context: `10-design.md` makes `Operated` with no durable audit sink a startup failure, but a sink's
durability is not discoverable by trying it, and the audit record's storage shape is not stated.
Chosen: `IAuditSink.IsDurable` is declared on the interface, on the precedent
`IHealthCheck.TouchesExternalDependency` already sets for a property a registry must reject on. The
audit table is keyed by the event id, which the writer mints, and **is not tenant-prefixed** unlike
every product table; it is indexed on tenant with instant, on correlation, and on actor subject with
instant, and on nothing else. The actor is stored as two columns, issuer and subject.
Rejected: **inferring durability from whether the Audit store module is present**; rejected because it
makes a framework check depend on knowing a module's identity, which is ADR-006 rule 1 through the back
door. **A tenant-prefixed audit key**, matching every other table; rejected because an audit row is
written *about* a tenant rather than owned by one, and the cross-tenant queries an operator actually
runs would become cross-partition scans. **Storing the rendered `PrincipalId`**; rejected because the
rendering is not injective over two opaque halves and could not be read back.
Reversibility: cheap for the index set; expensive for the key, which every stored row carries.

### 2026-08-29 — Module packages are `SubZeroDev.Platform.<Capability>`

Context: `10-design.md` names the six module units — Identity, Organizations, Billing, Licensing, the
audit store and Mcp — but fixes a package name for only one of them, `Platform.Mcp`, and the contract
has to name the assembly each declaration lives in.
Chosen: `SubZeroDev.Platform.Identity`, `.Organizations`, `.Billing`, `.Licensing`, `.Audit` and
`.Mcp`, extending the convention `Platform.Mcp` already uses and the ecosystem prefix ADR-003 settles.
The web shell takes no .NET package name, because it has no .NET package.
Rejected: **a distinguishing prefix or suffix for the module tier**, such as `Platform.Modules.*`;
rejected because ADR-006's rule is structural and the architecture checks (I-C6, I-C7) enforce it over
the resolved package graph, so a naming convention adds a second, weaker statement of the same fact
that can disagree with the first. **`SubZeroDev.Platform.AuditStore`** for the audit store; rejected
because the module is the only thing named Audit that ships, and the word "store" is the design's
disambiguator against the framework's audit *contract*, which lives in Abstractions and needs no name
of its own.
Reversibility: cheap while every package is 0.x and unpublished.

---

### 2026-08-24 — The framework owns the questions; modules own the answers

Context: `/design` for D5. Nine capabilities that are coupled in use — Authorization must audit, Mcp
must authorize, Organizations must provision tenants, Billing and Licensing must both answer one
entitlement question — under ADR-006 rule 1 (no framework package references a module) and rule 2 (no
module references another). Those two rules together admit very few arrangements, and most obvious
designs violate one of them on the first coupling.
Chosen: every commercial capability splits into a **decision seam** in the framework and a **policy
store** in an opt-in module. A seam is a question, a contract and a composition point, with a default
answer that is correct with every module absent. Modules contribute answers to seams and never reach
each other, which makes rule 2 vacuous by construction rather than observed by discipline. A seam
enters the framework only when a framework package must consume it on a path that exists with every
module absent, or two independent modules must consume it and cannot reach each other — the
seam-admission test, recorded because the pressure runs one way and every future capability will have a
reason why its contract would be convenient in Abstractions.
Rejected: **capabilities as whole modules**, the extraction guard's usual answer and the reading
ADR-006's own examples support; rejected because Authorization audits from inside a framework package,
which makes Audit-as-a-module a rule 1 build failure, and Mcp audits from a module, which makes it a
rule 2 violation — there is no arrangement of fully-modular capabilities that satisfies both rules while
the framework itself has anything to record, and it does. **A third package tier for optional
infrastructure**, which would let these rules differ per tier; rejected because nothing in D5 needs the
difference and a tier introduced before a rule needs it will acquire one. **Putting the whole of each
capability in the framework**, one cadence and no matrix; rejected because the brief requires Identity,
Organizations, Billing and Licensing to be absent from the local composition — not registered checks
that always pass — and a mandatory package cannot be absent.
Reversibility: expensive. It is the shape every D5 contract is written against.

### 2026-08-24 — The ambient principal is total, and a principal id is issuer plus subject

Context: `IOperationScope.Principal` is `ClaimsPrincipal?` and `IAuditable.CreatedBy` is `string?`
because D3 had no identity. The brief requires allowed, denied and failed actions each to persist an
actor, and admits accountless and delegated principals as first-class rather than degraded.
Chosen: the ambient principal is **non-null**, with `Anonymous` a well-known value exactly as
`TenantId.Implicit` is a well-known tenant, and four kinds — `Anonymous`, `Account`, `Delegated`,
`System` — where the kind states whether the actor is resolvable afterwards. `PrincipalId` is a pair,
issuer and subject, both opaque and compared ordinally; neither half is parsed or normalised by
Platform. `ClaimsPrincipal?` stays alongside as the raw authentication result.
Rejected: **keeping the nullable principal**, no breaking change to a published 0.x surface; rejected
because a null actor is indistinguishable from an actor that was never resolved, so an audit trail
containing them cannot answer the question it exists to answer — the criterion becomes a convention
rather than a property of the type. **A single-string principal id**, simpler and adequate today;
rejected because brief decision 5 keeps the Automator and Game Engine identity stores separate while
preserving a later reversal, and a single string makes that reversal a data migration the first time two
stores mint the same subject. **Modelling accountless principals as `Account` with absent fields**;
rejected because `application-modules.md` §2 states the correction directly — no account will ever exist
for BarStrad's customer-side principal — and every consumer would be defensive about permanently absent
fields.
Reversibility: expensive, which is the argument for taking the breaking change now, while the packages
are explicitly unstable 0.x, rather than after a consumer ships.

### 2026-08-24 — Billing and Licensing contribute to one entitlement seam, resolved as a union

Context: the brief requires product code to consume entitlements and never subscription state, and
`tenancy-billing-licensing.md` forbids any code path outside the billing module branching on it.
Licensing answers the same "may this feature be used" question from a different source, and both may be
absent.
Chosen: one `FeatureName` question. Billing and Licensing register as entitlement contributors and
neither is queryable directly. Resolution is a **union** — any contributor granting a feature grants it
— and the decision records which contributor did. The contributor set joins the settings fingerprint, so
two instances that disagree about who may grant are visible through the existing
`platform.settings-fingerprint` check rather than by their behaviour diverging.
Rejected: **two queries**, one for subscription state and one for licence tier, which is the shape both
capabilities' own documents describe and needs no new concept; rejected because it reintroduces under a
different name exactly the branch the brief forbids — every gated feature would ask which commercial
model is in play — and makes the self-hosted and operated shapes structurally different at every call
site rather than at composition. **Intersection rather than union**, safer-sounding because every
contributor must agree; rejected because installing Licensing into an operated deployment would then
silently revoke subscription entitlements, which presents as a working system denying a feature the
customer paid for and is a failure nobody would look for.
Reversibility: expensive. It is the public shape product code is written against.

### 2026-08-24 — The tenant escape is a declared type plus an audited read-only scope

Context: `second-consumer-packages.md` §3 names the deliberately shared resource as the thing to design,
"because an unmodelled one is how isolation quietly stops holding". The Game Engine's published
campaigns are readable across tenants while sessions and saves never are.
Chosen: shareability is declared on the **entity type** at model build, so it is visible in the model
rather than writable by any path that can write a row. A row becomes shared through an explicit,
permissioned, audited write by its owning tenant — an ordinary tenant-scoped write. A cross-tenant read
happens only inside a named scope that widens the filter for the one declared type it names, emits one
audit event per scope rather than per row, and is **read-only**: a write attempted inside it is a
contract violation and throws. The resulting invariant is that no code path in Platform lets a write
reach another tenant's row.
Rejected: **a per-row `IsShared` flag honoured globally by the filter**, the usual build; rejected
because it makes the isolation boundary writable by every path that can write the row, and because no
caller ever states an intent to cross, so there is nothing to audit and nothing to grep. **A
`ReadAcrossTenants` permission and no scope**, explicit and auditable and consistent with the
authorization model; rejected because a permission is held for a session rather than an operation — the
holder crosses on every query including the unintended ones, and the audit says a principal *could* have
crossed rather than that it did. **Allowing writes inside the scope**, symmetric and it would let a
shared resource be edited by a collaborator; rejected because the asymmetric invariant is worth far more
than the symmetry, and the consumer that forced this design writes published campaigns from their owner
only.
Reversibility: cheap for the read half — widening later is additive. Expensive for the write half:
admitting cross-tenant writes later changes an invariant other code will have come to rely on.

### 2026-08-24 — An organization holds a tenant; it is not one

Context: all three evidence consumers read one-to-one — a team, a studio, a venue — and the brief
requires switching between organizations to isolate. Tenancy is framework and Organizations is a module,
so whichever way this goes decides whether a framework type carries a module's concept.
Chosen: one organization has exactly one tenant, minted when the organization is created and held as a
column on the organization. The framework never learns that a tenant has an owner. Membership is keyed
by the framework's `PrincipalId`, which is what keeps Organizations free of any reference to Identity
and lets a `Delegated` principal hold a membership.
Rejected: **making the organization id the tenant id**, one fewer column and no possible disagreement
between the two; rejected because `TenantId` would then mean "an organization" — a module's identity
inside the framework's most load-bearing value — and a consumer wanting several tenants per organization
would face a migration on every table rather than an added row. **Organizations and tenants as
orthogonal dimensions**, maximum flexibility; rejected because no consumer needs it and generality
invented without a consumer to test it is the failure `second-consumer-packages.md` §1 describes and
ADR-006 rejects by name.
Reversibility: cheap. One-to-many is an added table and no framework change.

### 2026-08-24 — The audit record has no payload, and the redaction boundary moves to Core

Context: `platform-specification.md` lists "changed fields where appropriate" among audit fields. The
brief requires tests that push representative secrets through every audited input surface and assert
that neither values nor payloads reach the stored record or the logs.
Chosen: the audit record carries the brief's seven fields plus the actor kind and the record class. **No
payload, no changed-field list, no free-form detail string.** The three caller-controlled strings —
action, resource type, resource id — pass through the fixed redaction boundary before storage, and that
boundary moves from Observability to Core — the framework package both modules already depend on, and
not Abstractions, which exposes contracts only and acquires no implementation — so the Audit store
module and Mcp reach the same one. It stays non-injectable; the D3 decision that made it fixed rather
than configurable is unchanged.
Rejected: **the specification's fuller list including changed fields**, which is what an auditor usually
asks for and where "we redact it" is the standard answer; rejected because with a payload field the
brief's test asserts a property of the redaction rules and of every future caller's discipline, while
without one it asserts a property of the type — and a changed-field list is the single most likely place
for a credential to arrive by accident, because the field that changed is often the one that matters. A
consumer wanting field-level change history builds it in its own tables, where the secret question is
theirs.
Reversibility: cheap in the direction that should be resisted. Adding a payload later is easy; the whole
value of the decision is in not doing it.

### 2026-08-24 — Audit joins the action's transaction when there is one, and has two classes when there is not

Context: the brief requires allowed, denied and failed actions to persist across restart. A denial has
no transaction to join; a rolled-back action must not leave an audit row claiming it happened; a
committed action must not lack one.
Chosen: a **successful action that wrote state writes its audit row in the same transaction as the state
change** — atomic in both directions, which needs no idempotency and no reconciliation, and which is the
existing outbox pattern applied to a second writer. A **denial, a read, or a failure that wrote nothing
writes its audit row in its own transaction**, after the outcome is known. For the second case only, the
record's class decides what an audit-write failure costs: `Required` converts the response to a retryable
failure and degrades readiness — authorization denials, shared-resource escapes, membership and ownership
changes, entitlement and licence transitions, MCP invocations — while `Recorded` logs, degrades readiness
and leaves the response alone.
Rejected: **audit always in its own transaction**, uniform and simple; rejected because a committed state
change whose audit write then fails needs an idempotency key on every mutating operation to be retried
safely, which is a cost paid on every write to handle a rare failure. **A single class**, in both
directions: all-`Required` makes an audit outage a total outage, which is the self-inflicted outage
`tenancy-billing-licensing.md` refuses elsewhere; all-`Recorded` makes the brief's durability criterion
true only when nothing is wrong, which is not what a security control is for.
Reversibility: cheap. The classes are a per-action declaration and the transaction rule is one code path.

### 2026-08-24 — Entitlement is decided at admission and carried with the work

Context: the brief and `tenancy-billing-licensing.md` both require that expiry degrades features and
never touches running or scheduled work — after the recorded thirty-day grace, new paid-feature work is
denied while accepted, running and scheduled work continues.
Chosen: a unit of work carries the entitlement decision that admitted it, and execution does not
re-check. An entitlement decision is therefore a **value** that can be persisted with a work item, not
only a function call.
Rejected: **re-evaluating on each step**, more current and the naive reading of "gate the feature";
rejected because it makes the guarantee depend on where an expiry lands relative to a step boundary,
which is a race, and it puts an entitlement read on every step of every background job.
Reversibility: expensive. It decides whether an entitlement decision is a value or a call, and the work
item's persisted shape follows from the answer.

### 2026-08-24 — The MCP tool catalogue freezes at startup, and a secret-shaped parameter fails startup

Context: two inherited non-negotiables — authentication belongs to the connection and never to a call,
and exposure is opt-in per tool, default closed. The brief additionally requires that no tool schema or
call accepts a secret parameter, and that a registered-but-unexposed tool is neither listed nor callable.
Chosen: tools register and are exposed at startup, from any number of producers, and the catalogue is
**immutable afterwards**. Registration and exposure are separate facts. Every parameter name in every
registered schema is tested against the same fixed marker set the redaction boundary uses, and a match
fails startup with a named error, in the shape Core already uses for a bad module graph. A tool whose
required permission is not a registered name fails startup too. Unregistered and
registered-but-unexposed both answer "unknown tool", never "forbidden", which would confirm existence.
Rejected: **a mutable catalogue supporting runtime registration**, which the Automator will eventually
want so a plugin install needs no restart; rejected because freezing lets both safety properties be
checked once with a named startup failure rather than on every registration with a runtime error nobody
sees, and because it removes a catalogue-mutation-versus-invocation race from the path every call takes.
The reversal is available in the useful direction only: a mutable catalogue can replace a frozen one
without changing a consumer, and the reverse cannot.
Reversibility: cheap.

### 2026-08-24 — The licensed installation is the database, and expiry and grace are stored as verified

Context: the brief's licensing criteria require that an operational verification error uses stored claims
without extending their recorded expiry or grace, that a tampered document never grants a tier, that a
fresh installation continues at Community, and that a backwards clock is detected without any claim to
resist deliberate manipulation. Machine activation and seat enforcement are non-goals.
Chosen: **one verified-licence row per installation, and the installation is the database**, not the
machine — which is the only reading that works for both a shared store with several operated hosts and a
single self-hosted node. The row stores the tier, the features, the issue instant, **the expiry and
grace-end instants as computed at verification**, the verification instant, a fingerprint of the document
and which accepted key verified it. A verification result replaces the stored row only when its
verification instant is later, so a host still holding a superseded document cannot undo a replacement,
and every host reads the stored row rather than only its own file, so all hosts converge. Verification
has four distinguishable outcomes — `Verified`, `Invalid`, `Unavailable`, `ClockUnusable` — of which the
last three fall back identically to the stored row, or to Community when none exists, and each is audited
once per detection rather than once per check. The grace window comes from the document and defaults to
the recorded thirty days; it is never a deployment setting. Accepted public keys are an **ordered set
supplied by the consuming product**, not compiled into Platform and not one key, so rotation needs no flag
day and Platform is not a licensor. The revocation seam is never consulted on startup, readiness or
feature use.
Rejected: **deriving grace from when the error occurred**, the obvious implementation; rejected because a
deployment that errors on every verification would renew its own grace forever, and the brief's "without
extending their recorded expiry or grace" would be aspirational. **Collapsing `Invalid` and `Unavailable`
into one code** since the fallback is identical; rejected because an operator seeing "unavailable" reaches
for the file and one seeing "invalid" reaches for the key, and one code sends both to the wrong place.
**A configurable grace window**; rejected because a grace window an operator can set is a grace window
with no end. **A background re-verification timer**; rejected because it is a second writer contending on
the one row for a requirement nobody stated — expiry and grace already evolve with the clock from stored
instants, without re-verification.
Reversibility: cheap for the outcome vocabulary and the key set. Expensive for the stored-instant model,
which is what the brief's criteria are stated against.

### 2026-08-24 — The composition profile is declared, validated at startup, and fingerprinted

Context: the brief requires the local host to have no package or project reference to Identity,
Organizations, Billing or Licensing, and requires their absence to be real rather than "registered checks
that always pass". The package graph decides what is present; nothing made the host say so.
Chosen: a host declares `Local` or `Operated`. At startup the provider registries close and are validated
against the declaration — `Operated` with no authentication provider fails; `Local` with an authentication
provider, a non-baseline entitlement contributor or a tenant resolver fails — and the failure names the
profile, the offending registration and which of the two it disagrees with. The profile and the
contributor set join the existing settings fingerprint, so a second instance registering a different set
is visible through `platform.settings-fingerprint` rather than through divergent behaviour. The seam
defaults in `Local` are named local providers that exist and can be pointed at, not stubs standing in for
absent modules.
Rejected: **relying on the package graph alone**, which is already checked by the brief's own architecture
gate; rejected because the graph is a build-time fact and a misconfigured host is a runtime one — an
operated host that started without an authentication provider would serve, and every guarantee in the
design is stated relative to a composition it never announced. **Degrading rather than failing startup**;
rejected because a host that cannot state its own composition should not serve.
Reversibility: cheap.

### 2026-08-24 — A caller who may not see a resource is told it does not exist

Context: two boundaries return refusals — the tenant filter and the authorization evaluator — and using
one answer for both leaks existence across tenants.
Chosen: a cross-tenant read returns **not found**, and so does switching to an organization the principal
is not a member of, because "forbidden" confirms the resource exists. A permission denial on a resource
the principal *can* see returns **forbidden**, because there the existence is already known and pretending
otherwise only obscures the fix. An invitation token that is unknown, expired or already redeemed does not
distinguish a token that never existed from one that did.
Rejected: **one answer for both**, simpler and one fewer distinction for a caller to learn; rejected
because the difference is a security property rather than a style choice — a uniform "forbidden" turns
every identifier into an existence oracle, and a uniform "not found" makes a genuine permission problem
undiagnosable.
Reversibility: cheap in mechanism, expensive in expectation once a consumer's error handling depends on it.

### 2026-08-24 — Placement recorded for all nine capabilities under ADR-006, and no dependency taken

Context: the brief makes it a completion condition that `/design` records framework-versus-module
placement for every capability under ADR-006 with none left `Undecided`, and `AGENTS.md` requires every
gap to be checked against existing packages before it is written, with the reason recorded either way.
Chosen: the placement table in `10-design.md` § *Module boundaries* 1 — framework seams for Identity's
principal contract, Authorization, Tenancy, the shared entitlement question and Audit's contract; modules
for Organizations, the audit store, Billing, Licensing, Mcp and the web shell. **No new dependency is
taken by this design.** Token validation is the in-box ASP.NET Core authentication handlers; licence
signature verification is `System.Security.Cryptography`; the deliberately shared resource, which is the
hard half of tenancy, is not provided by any multi-tenancy library.
Rejected: **Finbuckle.MultiTenant**, which `minimal-platform-packages.md` §3a explicitly recommended
adopting "when tenancy becomes a feature", and D5 is that stage — so this needs a reason rather than a
preference. Two: the brief's non-goals forbid redesigning the settled tenant identifier, keys and
implicit-tenant representation, and Finbuckle brings its own tenant-info model and store abstractions, so
adopting it re-homes a shape D3 settled and G2 built on; and the part that is actually hard — a declared
shareable type, an audited read-only escape, and the no-cross-tenant-write invariant — is not in the
library, so its hard half would be written anyway on top of a model this repository did not choose. Its
resolution strategies remain the design reference, and the resolver seam is shaped so a consumer could
register a Finbuckle-backed resolver without Platform depending on it. **Supabase as an identity
substrate**, recorded there as a live option for D5; not rejected and not chosen — the brief's non-goals
put choosing or operating an identity substrate out of scope, so it becomes a deployment choice behind the
authentication seam, which is what "decide neither now" wanted and what "depend on the protocol, not the
vendor" requires. **A licensing library**; rejected because taking one means adopting its document format
as Platform's public licence format.
Reversibility: cheap for each rejection — every one leaves the seam that would accept the package later.
Expensive for the placement table, which every D5 contract is written against.
