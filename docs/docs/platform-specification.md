---
sidebar_position: 2
sidebar_label: Platform Specification
---

:::caution Partly superseded — read with ADR-004

This document compares Platform to ABP Framework and specifies six near-term packages. Since it
was written, [ADR-004](adr/ADR-004-framework-build-not-adopt.md) evaluated ABP and ASP.NET
Boilerplate properly and decided: **no application framework is adopted**, the host framework is
a per-product choice, and existing NuGet packages are preferred over hand-rolled infrastructure.
It also established that .NET already provides much of what the six describe.

The **principles, boundaries and package shapes below stand.** The near-term set is now settled at
six packages by [`design/00-brief.md`](../../design/00-brief.md); each narrow gap must still be
checked against existing packages before anything is written, as ADR-004 requires. The text is
otherwise unchanged, because it is a moved specification and this repository does not fork what
it moves.

:::

> **Moved from the ecosystem specification staging tree**
> (`SubZeroDev/Specs/SubZeroDev.Platform/02-platform-specification.md`), which names this repository as its
> destination. Content is unchanged; only this provenance note and the front matter
> were added. See [Platform Identity](platform-identity.md) for why it moved.

# SubZeroDev.Platform Specification

## Purpose

SubZeroDev.Platform is the reusable application framework for SubZeroDev products.

It should reduce the creation of a new product to:

```csharp
var builder = SubZeroDevApplication.CreateBuilder(args);

builder
    .AddPlatform()
    .AddIdentity()
    .AddNotifications()
    .AddProductModule<MyProductModule>();

await builder.Build().RunAsync();
```

The actual API is subject to review, but the developer experience should remain this simple.

## Goals

- provide production-grade infrastructure once
- standardize project architecture
- allow applications to install only required modules
- support local, self-hosted, and SaaS deployments
- support multi-tenancy without forcing it
- expose consistent extension points
- provide shared defaults without blocking replacement
- reduce duplicated configuration, logging, API, identity, notification, billing, and persistence code

## Principles

1. Opinionated defaults
2. Replaceable providers
3. Modular packages
4. Minimal mandatory dependencies
5. Clear dependency direction
6. Async-first APIs
7. Structured errors
8. Strongly typed configuration
9. Secure-by-default behavior
10. Deterministic startup validation

## Package structure

**Decision: a minimal Platform is built alongside Automator.** Six packages near-term; the rest are
candidates, created when a consumer needs them.

### Near-term

```text
SubZeroDev.Platform.Abstractions
SubZeroDev.Platform.Core
SubZeroDev.Platform.Hosting
SubZeroDev.Platform.Persistence
SubZeroDev.Platform.Observability
SubZeroDev.Platform.Testing
```

### Candidates

Specified below so the shapes are agreed, and deliberately not built yet:

```text
Configuration   Events        Identity       Authorization
Organizations   Tenancy       Notifications  Storage
BackgroundJobs  Scheduling    Plugins        Billing
Licensing       Audit         Api            Mcp
Web             UI            Aspire
```

### Why the split, and the risk it carries

A framework earns its abstractions from its second and third consumer. Designed from zero consumers,
twenty-four packages encode guesses — and the first product then bends around interfaces that were
never tested against anything.

Six is the set that is genuinely hard to retrofit: hosting shape, persistence and transaction
boundaries, observability wiring, and test infrastructure all cost far more to introduce later than
to start with.

**The risk of this middle path is honest and worth naming:** it becomes the twenty-four package plan
by increments, one reasonable-looking addition at a time. The guard is that a candidate becomes a
package when a _second_ consumer needs it, not when the first one does. Until then it lives inside
Automator, where it is cheap to change.

`SubZeroDev.Platform.Plugins` deserves particular care: plugin abstractions belong to the plugin
contract, which has its own repository precisely so a non-.NET plugin need not depend on a .NET
framework. This package, if it ever exists, is a .NET _client_ of that contract, not the contract.

## Application modules

Platform should use a module registration model.

A module declares:

- identity
- dependencies
- configuration
- services
- database contributions
- endpoints
- health checks
- permissions
- UI navigation contributions
- background jobs
- migrations
- optional startup tasks

Example conceptual contract:

```csharp
public interface IPlatformModule
{
    string Id { get; }
    Version Version { get; }
    IReadOnlyCollection<string> Dependencies { get; }

    void ConfigureServices(PlatformModuleContext context);
    void ConfigureApplication(PlatformApplicationContext context);
}
```

Avoid dynamic assembly scanning as the only registration method. Explicit registration must always be supported.

## Hosting

Hosting provides:

- host bootstrap
- environment detection
- configuration loading
- dependency injection
- middleware conventions
- startup validation
- graceful shutdown
- health endpoints
- readiness and liveness
- OpenAPI defaults
- correlation IDs
- request context
- tenant context
- user context
- module initialization ordering

## Configuration

Configuration precedence:

```text
Built-in defaults
→ package defaults
→ appsettings.json
→ environment-specific appsettings
→ user secrets
→ environment variables
→ external secret/config providers
→ command-line overrides
→ runtime overrides
```

Requirements:

- typed options
- validation at startup
- secrets never logged
- configuration source metadata available for diagnostics
- provider replacement
- reload only where safe
- explicit immutable options for security-critical settings

## Persistence

Initial support:

- EF Core
- SQLite for local/single-node
- PostgreSQL for production
- migration ownership by modules
- transaction abstraction
- outbox support for integration events
- audit fields
- soft-delete abstraction where required

Platform should not force repositories over EF Core. Domain modules may use direct DbContext access or domain repositories.

## Identity

Capabilities:

- local users
- JWT bearer tokens
- cookie authentication for web UI
- API keys
- external OAuth/OIDC providers
- refresh tokens
- token revocation
- service accounts
- future passkeys

Identity must remain optional for local-only products.

## Authorization

Capabilities:

- roles
- claims
- named permissions
- policies
- resource authorization
- tenant-aware permissions
- module-contributed permissions
- administrative override policy
- audit of security-sensitive decisions

Permissions should be stable string identifiers:

```text
Automator.Plugins.View
Automator.Plugins.Install
Automator.Executions.Run
Automator.Secrets.Manage
```

## Organizations and tenancy

**Not before Phase 8**, with one exception: carry a tenant identifier in the first schema, defaulted
to a single implicit tenant. Adding the column later is easy; adding tenant _isolation_ to queries,
storage paths, and secret scopes after data exists is a correctness migration on every table at once.

Optional module supporting:

- organizations
- teams
- memberships
- invitations
- ownership
- tenant isolation
- tenant-scoped settings
- tenant-scoped billing
- tenant-scoped secrets
- tenant-scoped plugins

Single-user local deployments should not need artificial tenant setup.

## Billing and subscriptions

**Not before Phase 8.** Specified so the shape is agreed. The provider decision (Paddle, as merchant
of record), the metered dimensions, and the licence model are in `10-tenancy-billing-licensing.md`.

Platform owns shared commercial primitives:

- plans
- prices
- entitlements
- subscriptions
- metered usage
- invoices
- payment provider adapters
- billing events
- trials
- grace periods
- cancellation
- plan transitions

Initial providers may include Stripe or Paddle, but business code must depend on Platform billing abstractions.

Billing must not be required for self-hosted use.

## Licensing

**Not before Phase 8.** Separate from subscription billing.

Capabilities:

- license keys
- signed license documents
- offline validation
- machine activation
- seat limits
- feature entitlements
- expiration
- trial licenses
- revocation lists when online

## Notifications

Unified notification model:

- recipient
- channel
- template
- variables
- priority
- deduplication key
- correlation ID
- tenant
- scheduling metadata

Channels:

- email
- Discord
- Slack
- Teams
- webhooks
- SMS
- push
- in-app

Notification providers must be independently replaceable.

## Events

Event categories:

- domain events
- application events
- integration events

Initial implementation:

- in-process event bus
- durable outbox for integration events
- typed event envelopes
- correlation and causation IDs
- schema version
- timestamp
- tenant and actor metadata

Distributed buses are future providers.

## Background jobs and scheduling

Platform provides abstractions and a default implementation.

Capabilities:

- enqueue
- delay
- retry
- cancellation
- priority
- uniqueness/deduplication
- recurring schedules
- ownership
- distributed locks where required
- execution history

Automator builds richer orchestration on top of these primitives.

## Storage

Abstraction for:

- local filesystem
- S3-compatible storage
- Azure Blob
- MinIO

Capabilities:

- streams
- metadata
- checksums
- signed URLs
- retention policies
- tenant isolation
- content type
- encryption metadata

## Audit

Audit events should record:

- actor
- tenant
- action
- entity/resource
- timestamp
- request/correlation ID
- outcome
- changed fields where appropriate
- source IP where available
- client identity

Secrets and sensitive payloads must never be written to audit logs.

## Observability

Default OpenTelemetry integration:

- logs
- traces
- metrics
- service name/version
- correlation IDs
- tenant labels only where safe
- endpoint metrics
- database metrics
- background-job metrics
- provider health

## API conventions

Platform API defaults:

- problem details
- request validation
- API versioning
- OpenAPI
- pagination
- filtering conventions
- idempotency key support
- rate limiting
- correlation IDs
- consistent error codes
- authentication and authorization integration

## MCP conventions

Platform provides reusable MCP hosting, authentication, logging, and authorization primitives.

Automator decides which plugin commands become MCP tools.

## UI and administration

Potential shared shell:

- login
- user profile
- organization switcher
- navigation
- settings
- notifications
- audit log
- API keys
- billing
- plugin administration
- feature flags

Do not make UI a Phase One dependency for backend packages.

## Testing support

Platform.Testing should provide:

- test host builder
- fake clock
- fake current user
- fake tenant
- in-memory or test-container persistence
- notification capture
- event capture
- deterministic background jobs
- authentication helpers
- contract tests for providers

## Versioning

- semantic versioning
- package-level versioning
- explicit breaking-change policy
- database migration compatibility notes
- module dependency ranges
- shared schema versioning

## Phase scope

Phase numbering is defined once in `SubZeroDev.Ecosystem/18-roadmap.md`. This document does not
maintain its own.

**Phase 2 — minimal Platform, built alongside the Automator MVP:**

- abstractions and core
- hosting
- persistence baseline
- observability
- testing utilities

**Phase 5 — extracted from Automator once a second consumer exists:**

- configuration, events, notifications, storage
- background jobs and scheduling
- API conventions

**Phase 8 — commercial:**

- identity, authorization, organizations, tenancy
- billing, licensing, audit
- shared web UI

Deferred indefinitely: marketplace, distributed event bus, enterprise tenancy.
