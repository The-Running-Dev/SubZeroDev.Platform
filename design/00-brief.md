# Brief — commercial (D5)

> **Provenance.** This brief was assembled from decisions taken by Ben on 2026-08-22 and from
> constraints already recorded in the tree. It was revised after `/brief-check` on 2026-08-24 to
> make the scope boundary, operating assumptions and acceptance evidence explicit. Where the
> ecosystem documents describe a longer-term capability catalogue, this brief names the subset D5
> actually commits to deliver.

---

## Problem

Platform has hosting, persistence and observability, but a product adopting it still has to invent
its own principal, authorization, organization, tenant-isolation, entitlement, licensing, audit,
administration and MCP-security seams. Those seams are coupled in use: identity without
authorization and audit is incomplete, while billing and licensing cannot gate features safely
without a common entitlement boundary.

D5 establishes the smallest reusable commercial and security surface before the next products
adopt Platform. It replaces product-specific wiring with consumable Platform APIs while preserving
the identity-free, billing-free and licence-free local deployment shape.

---

## Who it is for

D5 serves the common intersection of the Automator and Game Engine as a Service first. BarStrad and
SkyNet HR remain additional evidence that the abstractions must handle accountless and delegated
principals, but neither is the first adopter and neither is migrated by this effort.

The proof consumer is a Platform-owned sample, not a product. No product receives priority over the
shared boundary: when the four shapes diverge, D5 implements only the reusable contract named below
and leaves product policy in the product.

---

## Scope

**Nine capabilities.** The eight D5 capabilities at `implementation-plan.md:161` — Identity,
Authorization, Organizations, Tenancy, Billing, Licensing, Audit and shared web UI — plus
`Platform.Mcp`.

**Mcp is admitted deliberately.** D5's stated done-when at `implementation-plan.md:167` already
requires Mcp to accept tool definitions from a producer other than manifest projection, while D5's
package list omitted it. Scope follows the done-when rather than the list.

The capability lists in `platform-specification.md` describe the intended long-term Platform
surface. **They are context, not a commitment to deliver every bullet in D5.** This effort delivers
the following bounded subset:

| Capability | D5 delivers | Evidence |
|---|---|---|
| Identity | An optional principal contract; authenticated and accountless principal kinds; integration seams for hosted authentication without choosing or owning an identity provider | `platform-specification.md:215`; `second-consumer-packages.md:40`; all four consumers |
| Authorization | Stable named permissions, policy and resource checks, tenant-aware decisions, module-contributed permissions, and audit of security-sensitive decisions | `platform-specification.md:231`; no divergence analysis or canonical consumer row |
| Organizations | Organizations, memberships, invitations, ownership and active-organization switching; teams and richer organization administration remain outside D5 | `platform-specification.md:254`; three consumers through the combined Organizations / Tenancy row |
| Tenancy | Request tenant context, enforced tenant-scoped access, and an explicit auditable model for deliberately shared resources, built over the existing tenant identifier | `second-consumer-packages.md:63`; three consumers |
| Billing | Provider-neutral plans, subscriptions and entitlements sufficient to gate features and apply plan transitions; product code consumes entitlements, never subscription state | `platform-specification.md:275`; `second-consumer-packages.md:121`; BarStrad's model remains undecided |
| Licensing | Signed offline licence documents, tier and feature claims, expiration and grace, durable last-known verified claims, and optional online revocation as an extension point only | `platform-specification.md:299`; no divergence analysis or canonical consumer row |
| Audit | A durable audit event contract and sink carrying actor, tenant, action, resource, timestamp, correlation id and outcome, with sensitive-data exclusion | `platform-specification.md:401`; no divergence analysis or canonical consumer row |
| Shared web UI | A replaceable administration shell proving login/accountless state, organization switching, entitlement/licence state and audit viewing without becoming a backend dependency | `platform-specification.md:455`; no divergence analysis or canonical consumer row |
| Mcp | Transport authentication, authorization, consent, logging and default-closed tool registration from both manifest projection and a product-owned fixed table | `platform-specification.md:449`; `second-consumer-packages.md:87`; three consumers |

**Four capabilities are admitted without consumer evidence:** Authorization, Licensing, Audit and
the shared web UI have no divergence analysis in `second-consumer-packages.md` and no row in the
canonical consumer count at `platform-identity.md:120`. They remain in scope by decision because the
commercial surface cannot be proved safely without them. The objection is retained rather than
declared resolved, following ADR-006 rule 4; the eventual design must keep their placement
reversible.

### Tenancy inherited from D3 and G2

D5 ships request tenancy and isolation **over the existing tenant column**. It does not redesign the
identifier or migrate the settled storage shape:

- The logical tenant type is non-null and opaque, with a well-known all-zero implicit tenant —
  `design/d3/10-design.md:1507`.
- The tenant is part of every existing primary key and every query supplies the implicit constant —
  `design/g2/90-decisions.md:1149`.
- Until D5, the store supplies that constant while no request resolves or carries a tenant and no
  behaviour varies by tenant — `design/g2/90-decisions.md:775`.

D5 replaces the implicit request behaviour with an explicit tenant context where tenancy is enabled,
while identity-free local mode continues to use the implicit tenant without setup.

---

## Non-goals

- **Delivering every capability listed in `platform-specification.md`.** The bounded D5 subset above
  is binding. Omitted bullets require a later brief rather than arriving through `/design`.
- **Migrating or onboarding the Automator, GEaaS, BarStrad or SkyNet HR.** They supply evidence; the
  sample supplies D5's executable proof.
- **Deploying or operating the sample.** It is a CI proof, not a hosted environment or public endpoint.
- **Real payment-provider integration or money movement.** Paddle remains the recorded first provider,
  but D5 delivers the provider-neutral seam and deterministic test provider, not checkout, invoices,
  tax handling, webhooks or live credentials.
- **Metering, quotas or rate limiting.** In particular, execution minutes and playtime remain rejected;
  no usage-based enforcement is introduced by D5.
- **Machine activation, seat enforcement, trial issuance or an online revocation service.** Licensing
  exposes claims and the optional revocation seam but does not build these product policies.
- **Federation, account linking, a shared user directory or shared identity storage.** The Automator
  and GEaaS retain separate identity stores; Platform provides a common principal contract only.
- **Choosing or operating an identity substrate.** D5 defines provider-neutral integration seams;
  an OIDC, proxy, local or other provider belongs to deployment or product policy.
- **A complete shared administration product.** Settings, notifications, API-key administration,
  payment UI, plugin administration and feature-flag management are outside the proof shell.
- **Redesigning the settled tenant identifier, primary keys or implicit-tenant storage representation.**
- **Marketplace, distributed event bus or enterprise tenancy features** already deferred at
  `platform-specification.md:523`.
- **Performance, throughput, latency or capacity optimization.** D5 proves correctness under the
  stated concurrency shapes; it sets no performance SLO or production capacity target.
- **Hardening licensing against determined piracy or a malicious deployment clock.** The recorded
  threat model is casual over-use, not DRM resistant to an operator who controls the machine.
- **Choosing the framework-versus-application-module tier in this brief.** `/design` must settle and
  record the tier for every capability before `/contract`; no capability may remain `Undecided` when
  D5 finishes.
- **Requiring Identity, Organizations, Billing or Licensing in local/community mode.** Their packages,
  configuration and storage are absent from that composition, not registered checks that always pass.

---

## Definition of done

### Executable proof

One sample solution under `samples/` contains an operated host, an identity-free local host and
focused automated scenarios for all nine capabilities. CI executes the scenarios as assertions and
reports each capability separately; starting successfully is not proof. A regression that violates
any positive or negative criterion fails CI.

The operated scenarios run against SQLite and PostgreSQL. The multi-instance contention scenarios run
against PostgreSQL. The local host runs offline with outbound network unavailable and proves that it
has no package or project reference to Identity, Organizations, Billing or Licensing and requires no
configuration or storage for them.

| Capability | The sample and gates must demonstrate |
|---|---|
| Identity | The operated host authenticates a principal at the transport boundary. The separate local host builds and runs with Identity absent from its dependency graph, uses an explicit `system:local` audit actor, and requires no account setup. |
| Authorization | A module-contributed named permission grants an allowed resource action and denies the same action to another principal. The denial is returned as an authorization failure and produces an audit event. Administrative override, if included by `/design`, is explicit and audited rather than an implicit bypass. |
| Organizations | One principal creates an organization, invites a second principal, the second accepts membership, and each can switch only to an organization it belongs to. A non-member cannot switch into or administer the organization. |
| Tenancy | Two tenants can create the same logical resource id without collision; ordinary reads and writes cannot cross tenants; an undeclared cross-tenant read is denied. A resource declared deliberately shared is readable across tenants only through the modelled shared-resource path, and that escape is audited. |
| Billing | A subscription resolves to named entitlements; an entitled operation succeeds and the same operation without the entitlement is denied. A plan transition changes the resolved entitlement without product code reading subscription state. An architecture gate rejects references to subscription state outside the Billing implementation. |
| Licensing | A valid signed document verifies with outbound network unavailable and establishes durable verified claims. Those claims survive restart. An operational verification error uses the stored claims without extending their recorded expiry or grace; a fresh installation with no verified claims continues at Community tier. A tampered or invalidly signed document never grants a tier and falls back only to previously verified claims, or Community when none exist. After the recorded 30-day grace period, new paid-feature work is denied while accepted, running and scheduled work continues and existing data remains readable and exportable. |
| Audit | Allowed, denied and failed actions persist actor, tenant, action, resource, timestamp, correlation id and outcome across restart. The audit contract excludes sensitive fields, and tests pass representative secrets through every audited input surface and assert that neither values nor payloads reach the stored record or logs. |
| Shared web UI | The shell displays the current authenticated or accountless state, permits organization switching through the same backend API used without the UI, displays entitlement/licence state, and reads the audit record. Backend packages build and run with no reference to the UI package. |
| Mcp | A manifest-projected tool and a product-owned fixed-table tool both register, list and execute when explicitly exposed. A registered but unexposed tool is neither listed nor callable. The operated connection authenticates at the transport, authorization is applied before invocation, and no tool schema or call accepts a secret parameter. |

### Public delivery

D5 is not complete until all of the following hold:

- `/design` records framework-versus-module placement for every capability under ADR-006, and the
  dependency-direction architecture checks enforce that placement.
- Public contracts are present in `design/20-contract.md`, implemented in versioned 0.x packages,
  packed in CI, and consumed by the sample from package artifacts rather than project references.
- `Platform.Testing` exposes the minimum test host, fake principal, fake tenant, fake entitlement,
  fake clock and audit inspection support required for a consumer to verify the same boundaries.
- Human-facing documentation covers registration, optional composition, both deployment shapes,
  security defaults, failure semantics and the sample's asserted scenarios.
- `implementation-plan.md` is corrected so its D5 capability list includes `Platform.Mcp` and its
  done-when matches this brief.
- The retained consumer-evidence objection remains recorded for Authorization, Licensing, Audit and
  shared web UI; completion does not pretend that the sample turned into an external consumer.

---

## Environment and operating assumptions

- **Technology:** .NET, with the boundary between Platform and a hosted product remaining a process
  and image boundary — [ADR-002](../docs/docs/adr/ADR-002-implementation-technology.md).
- **Operated SaaS:** multiple concurrent host instances are supported against PostgreSQL. Agent and
  browser clients connect across an untrusted network; confidentiality belongs to the deployment
  transport, while Platform owns authentication and authorization at that transport boundary.
- **Self-hosted / homelab:** one instance is the common case, SQLite and PostgreSQL are supported,
  and the deployment may have no outbound network for its entire lifetime. Optional online
  revocation cannot become a startup, readiness or feature-use dependency.
- **Database matrix:** tenant, authorization, entitlement, licence and audit correctness runs against
  both providers. Provider-specific performance is not compared. Multi-instance behavior is proved on
  PostgreSQL only; SQLite remains a local/single-node provider.
- **Users and tenants:** single-user and implicit-tenant operation is first-class. The operated proof
  uses at least two principals, two organizations and two tenants so isolation and membership are
  exercised rather than inferred.
- **Audit:** audit records are durable across process restart. Retention duration, archival, export
  formats and external audit shipping are not selected by D5.
- **Clock:** expiry and grace decisions use an injectable clock and remain deterministic in tests. The
  deployment controls its system clock; D5 detects and logs unusable clock input but does not claim to
  resist deliberate clock manipulation.
- **Scale:** D5 proves correctness for the CI scenarios and concurrent operated instances. It assumes
  no specific user count, tenant count, event volume, retention volume, throughput or latency target.
- **Consumer state:** no named consumer runs on Platform today. The sample is the executable proof and
  package consumer; external adoption is separate work.

---

## Lifespan

**Long-lived. Full pipeline, stages 0 through 5, including `/redteam`.**

Nine capabilities become public API surface with four named prospective consumers. Five capability
areas currently carry an unsettled framework-versus-module tier that `/design` must resolve under
ADR-006 rule 3 before contracts are written. This is the case the staged pipeline exists for; the
short path at `kit-help.md:91` is not taken.

---

## Decisions taken here that override or narrow earlier recommendations

**1. `Platform.Mcp` is in scope though `implementation-plan.md:161` omits it.** Scope follows D5's
done-when at `:167`, which explicitly names Mcp. The implementation plan is corrected as a D5
completion condition.

**2. Four capabilities remain in scope without consumer evidence.** Authorization, Licensing, Audit
and shared web UI are admitted by decision with the objection retained under ADR-006 rule 4. The
sample supplies executable evidence, not a fictional external consumer.

**3. The long-term capability catalogues are narrowed for D5.** The exact subset in `## Scope` is the
commitment. A bullet elsewhere that is not included there does not silently expand this effort.

**4. The proof is one assertion-driven sample solution, not an external consumer.** It contains two
deployment hosts and focused scenarios, runs across the stated database matrix, and includes negative
paths and architecture gates that a happy-path demonstration cannot prove.

**5. The Automator and GEaaS still do not share identity.** The 2026-08-22 reaffirmation of
`design/g1/90-decisions.md:532` holds for this effort. Federation, account linking and shared identity
storage remain non-goals; opaque stable principal ids preserve a later reversal without requiring it
now.
