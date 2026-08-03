# Contract — the minimal package set (D3)

**Document status:** Contract. Derived from [`10-design.md`](10-design.md). Authoritative for the
packages it describes; [`platform-identity.md`](../docs/docs/platform-identity.md) stays
authoritative for what this repository is.

C# with nullable reference types enabled. Types and signatures only. Package grouping is by heading
rather than namespace declaration — package naming belongs to
[ADR-003](../docs/docs/adr/ADR-003-package-scopes-and-registries.md), not here.

**Re-derived in full** from the design as it stands after its fourth adversarial review, not patched
from the previous derivation, per the decision of 2026-08-03 in
[`90-decisions.md`](90-decisions.md). Anything the design did not determine is in
**[Unresolved](#unresolved)** rather than invented. Three of those entries block implementation.

---

## Types

### Abstractions — identifiers and constrained values

```csharp
public readonly record struct TenantId(Guid Value)
{
    public static TenantId Implicit { get; }
    public static bool TryParse(string candidate, out TenantId result);
}

public readonly record struct CorrelationId(string TraceId)
{
    public static bool TryParse(string candidate, out CorrelationId result);
}

public readonly record struct TraceContext(string TraceParent, string? TraceState)
{
    public CorrelationId Correlation { get; }
    public bool Sampled { get; }
    public static bool TryParse(string traceParent, string? traceState, out TraceContext result);
}

public readonly record struct InstanceId(string Value);

public readonly record struct ModuleName(string Value);

public readonly record struct EventTypeName(string Value);

public readonly record struct HealthCheckName(string Value);

public readonly record struct BackgroundWorkName(string Value);
```

**Invariants carried by these types, not by their callers.** `TenantId.Implicit` is `Guid.Empty` —
the well-known all-zero sentinel. `CorrelationId.TraceId` is 32 lowercase hex characters and never
all-zero. `TraceContext.TraceParent` is a complete W3C `traceparent` **including trace flags**, from
which `Sampled` is read, and `TraceState` is the W3C `tracestate` when the origin carried one; the
design requires both to travel with the row so the sampling decision and any vendor sampler state
cross the boundary. `ModuleName`, `EventTypeName`, `HealthCheckName` and `BackgroundWorkName` are
non-empty, trimmed, and case-sensitively unique within their registry.

### Abstractions — host role

```csharp
public enum HostRole { Web, Worker }

[Flags]
public enum HostRoles { Web = 1, Worker = 2, Both = Web | Worker }
```

**`HostRole` is what a host *is*; `HostRoles` is what a background-work registration *declares*.**
They are separate because the registration heartbeat declares `Both` while no host is ever both.
Migrate mode is a one-shot command, not a third role.

### Abstractions — result and error

```csharp
public abstract record PlatformError(string Code)
{
    public abstract bool IsRetryable { get; }
}

public readonly struct Result<T, TError> where TError : PlatformError
{
    public bool IsSuccess { get; }
    public T Value { get; }
    public TError Error { get; }

    public static Result<T, TError> Success(T value);
    public static Result<T, TError> Failure(TError error);

    public TOut Match<TOut>(Func<T, TOut> onSuccess, Func<TError, TOut> onFailure);
}

public readonly struct Result<TError> where TError : PlatformError
{
    public bool IsSuccess { get; }
    public TError Error { get; }

    public static Result<TError> Success();
    public static Result<TError> Failure(TError error);
}

public sealed class PlatformContractViolationException : Exception
{
    public PlatformError Error { get; }
}
```

**Accessing `Value` on a failure, or `Error` on a success, throws.** That is one of exactly two
places an exception is correct: it is a defect in the caller, not a runtime condition.

**`PlatformContractViolationException` is the other**, and it exists because the design requires
enqueue to *throw* a named error when there is no ambient transaction or no ambient operation scope.
It carries a `PlatformError` so the code is stable and enumerable rather than a message string.

### Abstractions — ambient operation context

```csharp
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public interface IOperationScope : IDisposable
{
    CorrelationId Correlation { get; }
    TenantId Tenant { get; }
    ClaimsPrincipal? Principal { get; }
}

public interface IOperationScopeFactory
{
    IOperationScope Begin(CorrelationId correlation, TenantId tenant, ClaimsPrincipal? principal);
}

public interface IOperationScopeAccessor
{
    IOperationScope? Current { get; }
}

public interface ICurrentTenant
{
    TenantId Current { get; }
}

public interface ICurrentPrincipal
{
    ClaimsPrincipal? Current { get; }
}

public interface ICurrentCorrelation
{
    CorrelationId Current { get; }
}
```

**The scope is opened by two establishers and read by three accessors, and all five live here.**
Hosting opens a scope on an inbound request; Persistence opens one per dispatched message. Left
unowned, the second establisher invents its own write path.

**`IOperationScopeAccessor.Current` is the only member that can be null**, and it is what makes
"there is no ambient scope" detectable. The three accessors are meaningful only inside a scope:
outside one they throw `PlatformContractViolationException` with `NoAmbientOperationScope`, which is
what keeps "correlation is always present, tenant is always present" true as written rather than
quietly returning a default.

**`ICurrentCorrelation.Current` is a `CorrelationId`, not a `TraceContext`.** They are the same value
everywhere except across outbox dispatch, where the trace changes and the correlation does not — so
the accessor returns the value that does not change, and the trace is the runtime's.

**`ICurrentTenant.Current` is non-nullable and in D3 always returns `TenantId.Implicit`.** Nothing
resolves a tenant from host, header or claim — the brief's binding non-goal. The interface exists so
the column has a supplier, not so tenancy can be turned on.

**`ICurrentPrincipal.Current` is nullable and frequently null.** Identity is D5; a worker dispatching
a message has no principal and must not be given a fabricated anonymous one.

**`IClock.UtcNow` always has `Offset == TimeSpan.Zero`.** Every persisted instant originates here,
and so does every instant bound as a SQL comparand, so a fake clock controls every timestamp in the
system and no evaluation of eligibility, claim expiry or lease expiry reaches the database clock.

### Abstractions — trace-context contract

```csharp
public interface ITraceContextCodec
{
    TraceContext FormatCurrent();

    bool TryParse(string traceParent, string? traceState, out TraceContext result);

    IDisposable StartLinked(TraceContext origin, string activityName);
}
```

**Parse, format and link are Observability's operations declared in Abstractions**, because
Persistence performs all three — stamping the row, reading it back, and starting the linked trace —
while having no edge to Observability. `StartLinked` starts a **new** trace linked to the stored one
and honours the origin's sampling flags; it never continues the origin's trace.

**`TryParse` never throws and never fails a request.** A malformed inbound header yields `false` and
a fresh context.

### Abstractions — module contract

```csharp
public interface IPlatformModule
{
    ModuleName Name { get; }
    IReadOnlyCollection<ModuleName> DependsOn { get; }
    void Register(IServiceCollection services);
}
```

### Abstractions — event and handler contracts

```csharp
public interface IIntegrationEvent
{
    EventTypeName TypeName { get; }
}

public interface IIntegrationEventHandler<in TEvent> where TEvent : IIntegrationEvent
{
    Task<Result<HandlerError>> HandleAsync(TEvent @event, CancellationToken cancellationToken);
}
```

**A handler returns a result rather than throwing.** The dispatcher distinguishes a handled failure
from a defect, and only the former participates in the attempt-and-backoff cycle. An exception that
escapes a handler is treated as `HandlerError.Transient`.

**Exactly one handler may be registered per `EventTypeName`**, enforced at startup by the handler
registry. Every dispatch-state column is per row, so N handlers behind one row would share one retry
budget and one poison verdict.

### Abstractions — health contract

```csharp
public enum HealthStatus { Healthy, Degraded, Unhealthy }

public enum HealthCheckKind { Liveness, Readiness }

public enum HealthCheckCriticality { Required, Optional }

public enum HealthReportDetail { Full, Minimal }

public interface IHealthCheck
{
    HealthCheckName Name { get; }
    HealthCheckKind Kind { get; }
    HealthCheckCriticality Criticality { get; }
    TimeSpan Timeout { get; }
    bool TouchesExternalDependency { get; }

    Task<HealthCheckResult> CheckAsync(CancellationToken cancellationToken);
}

public sealed record HealthCheckResult(
    HealthStatus Status,
    string? Detail,
    IReadOnlyDictionary<string, string> Data);

public sealed record HealthReport(
    HealthStatus Aggregate,
    IReadOnlyList<HealthReportEntry> Entries);

public sealed record HealthReportEntry(
    HealthCheckName Name,
    HealthStatus Status,
    TimeSpan Duration,
    string? Detail,
    IReadOnlyDictionary<string, string> Data);
```

**`TouchesExternalDependency` exists so registration can reject it.** The design enforces the
liveness rule at registration rather than by convention, and a check cannot be interrogated for this
after the fact — it has to declare.

**`HealthReportDetail` is the body-narrowing switch, not a status switch.** `Minimal` renders the
aggregate and each entry's name and status; `Full` adds `Detail` and `Data`. The status is identical
either way, so nothing consuming the probe programmatically changes behaviour.

### Abstractions — background work contract

```csharp
public interface IBackgroundWork
{
    BackgroundWorkName Name { get; }
    HostRoles Roles { get; }
    TimeSpan Interval { get; }
    bool RequiresLease { get; }

    Task RunAsync(CancellationToken cancellationToken);
}
```

**`Roles` is what lets Hosting start Persistence's dispatcher without depending on Persistence, and
lets the web host run Persistence's registration heartbeat through the same channel.** The heartbeat
declares `Both`; the dispatcher and the prune pass declare `Worker`.

**Work declaring `RequiresLease` must be idempotent.** The lease reduces duplicate runs; it does not
prevent them, and nothing here fences a stalled holder.

### Abstractions — well-known names

```csharp
public static class PlatformBackgroundWork
{
    public static BackgroundWorkName OutboxDispatch { get; }
    public static BackgroundWorkName Prune { get; }
    public static BackgroundWorkName HostRegistrationHeartbeat { get; }
}

public static class PlatformHealthChecks
{
    public static HealthCheckName Database { get; }
    public static HealthCheckName PeerHost { get; }
    public static HealthCheckName SettingsFingerprint { get; }
    public static HealthCheckName OutboxBacklogAge { get; }
    public static HealthCheckName OutboxPoisonCount { get; }
    public static HealthCheckName PendingMigrations { get; }
}
```

**These names are public surface, not implementation detail.** They appear in the probe body an
operator reads and are the handles `RunBackgroundWorkOnceAsync` takes, so leaving them to the first
implementer would set them by accident. `Prune` is one registration covering all three retention
windows — processed rows, poisoned rows and dead host registrations.

### Abstractions — settings fingerprint marker

```csharp
[AttributeUsage(AttributeTargets.Property)]
public sealed class FingerprintedAttribute : Attribute;
```

**A setting is fingerprinted when two hosts disagreeing on it changes what happens to rows they
share** — when it decides outcomes, not merely timing. The attribute is what makes that membership
checkable rather than a list maintained in prose next to a hash function.

### Persistence — outbox message

```csharp
public readonly record struct OutboxMessageId(Guid Value)
{
    public static OutboxMessageId Create(DateTimeOffset at);
}

public enum OutboxMessageState { Pending, Processed, Poisoned, Discarded }

public sealed record OutboxMessage
{
    public required OutboxMessageId Id { get; init; }
    public required long Sequence { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public required EventTypeName Type { get; init; }
    public required string Payload { get; init; }
    public required TenantId Tenant { get; init; }
    public required TraceContext TraceContext { get; init; }
    public required int Attempts { get; init; }
    public DateTimeOffset? NextAttemptAt { get; init; }
    public DateTimeOffset? FirstDeferredAt { get; init; }
    public InstanceId? ClaimedBy { get; init; }
    public DateTimeOffset? ClaimedAt { get; init; }
    public DateTimeOffset? ProcessedAt { get; init; }
    public DateTimeOffset? PoisonedAt { get; init; }
    public string? LastError { get; init; }

    public OutboxMessageState State { get; }
}
```

**`Id` is the identity — a version-7 UUID minted app-side at enqueue from `IClock`.** It exists
before the insert, survives a database restore, sorts in mint order on both providers, and is the
dedupe key at-least-once delivery offers handlers.

**`Sequence` is claim order and nothing else.** It is provider-allocated, and on SQLite its values
are reused after a drain and prune. Nothing downstream may treat it as durable or as an identity;
anything needing a cursor across time uses `Id`.

**`State` is derived, never stored.** The four states are predicates over the two mark columns, and
a discriminator column was rejected as a second source of truth:

| State | Predicate | Prunes on | Counts toward |
|---|---|---|---|
| `Pending` | `ProcessedAt` null, `PoisonedAt` null | never | backlog age |
| `Processed` | `ProcessedAt` set, `PoisonedAt` null | processed window | nothing |
| `Poisoned` | `PoisonedAt` set, `ProcessedAt` null | poison window | poison count |
| `Discarded` | both set | poison window | nothing |

A row is **eligible** when it is `Pending`, `NextAttemptAt` is null or not in the future, and
`ClaimedAt` is null or older than the claim window. Expired claims are eligible by that predicate
alone, which is why there is no separate reclaim pass.

### Persistence — background work lease

```csharp
public sealed record BackgroundWorkLease
{
    public required BackgroundWorkName Name { get; init; }
    public required InstanceId Holder { get; init; }
    public required DateTimeOffset AcquiredAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
}
```

### Persistence — host registration

```csharp
public sealed record HostRegistration
{
    public required HostRole Role { get; init; }
    public required InstanceId Instance { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset HeartbeatAt { get; init; }
    public required string SettingsFingerprint { get; init; }
}
```

**Never read by the host that wrote it.** Its only consumer is the other role's readiness check, and
its whole purpose is that a host writing to the wrong database registers itself *there*, so its
absence from the right one is detectable from the side that is positioned to notice.

### Persistence — migration status

```csharp
public sealed record ModuleMigrationStatus(
    ModuleName Module,
    IReadOnlyList<string> Pending,
    IReadOnlyList<string> Surplus);
```

**`Surplus` is migrations applied that this host never registered** — the normal state of a
not-yet-restarted process once migrate mode has run. It reports degraded on the same check as
`Pending`, because the comparison is symmetric and only one direction was previously stated.

### Persistence — columns contributed to product tables

```csharp
public interface ITenantOwned
{
    TenantId Tenant { get; }
}

public interface IAuditable
{
    DateTimeOffset CreatedAt { get; }
    string? CreatedBy { get; }
    DateTimeOffset? ModifiedAt { get; }
    string? ModifiedBy { get; }
}

public interface ISoftDeletable
{
    bool IsDeleted { get; }
    DateTimeOffset? DeletedAt { get; }
    string? DeletedBy { get; }
}
```

**`ITenantOwned` is not optional; `ISoftDeletable` is opt-in per table.** A soft delete nobody asked
for silently changes the meaning of every query against that table.

### Persistence — provider selection

```csharp
public enum PersistenceProvider { PostgreSql, Sqlite }
```

### Core — configuration root

```csharp
public sealed record PlatformOptions
{
    public string? ServiceName { get; init; }
    public string? ServiceVersion { get; init; }
    public string Environment { get; }
    public HostRole Role { get; }

    public required PersistenceOptions Persistence { get; init; }
    public required OutboxOptions Outbox { get; init; }
    public LeaseOptions Lease { get; init; }
    public HostRegistrationOptions HostRegistration { get; init; }
    public HealthOptions Health { get; init; }
    public HostingOptions Hosting { get; init; }
}
```

**`ServiceName` and `ServiceVersion` are derived from the entry assembly when unset**, which is why
neither is `required`.

**`Environment` and `Role` have no setter and are not bindable from configuration.** Environment is
derived from the host — a service must not be able to declare itself production in a file that
shipped from a developer's machine — and the role is fixed by which form of the registration call
the host made.

```csharp
public sealed record PersistenceOptions
{
    public required PersistenceProvider Provider { get; init; }
    public required string ConnectionString { get; init; }
    public TimeSpan SqliteBusyWaitBound { get; init; }
}

public sealed record OutboxOptions
{
    [Fingerprinted] public required TimeSpan ProcessedRetention { get; init; }
    [Fingerprinted] public required TimeSpan PoisonedRetention { get; init; }
    [Fingerprinted] public TimeSpan ClaimWindow { get; init; }
    [Fingerprinted] public int PoisonAttemptCount { get; init; }
    [Fingerprinted] public TimeSpan RetryBackoffBase { get; init; }
    [Fingerprinted] public double RetryBackoffFactor { get; init; }
    [Fingerprinted] public TimeSpan RetryBackoffCap { get; init; }
    [Fingerprinted] public TimeSpan DeferralAge { get; init; }

    public TimeSpan DeferralRetryInterval { get; init; }
    public int DispatchTickBudget { get; init; }
    public int PruneBatchSize { get; init; }
    public TimeSpan DispatchInterval { get; init; }
}

public sealed record LeaseOptions
{
    [Fingerprinted] public TimeSpan Duration { get; init; }
}

public sealed record HostRegistrationOptions
{
    public TimeSpan HeartbeatInterval { get; init; }
    public TimeSpan RetentionWindow { get; init; }
    public TimeSpan PeerAbsenceStartupGrace { get; init; }

    public TimeSpan PeerLivenessThreshold { get; }
}

public sealed record HealthOptions
{
    public TimeSpan BacklogAgeThreshold { get; init; }
}

public sealed record HostingOptions
{
    public TimeSpan GracefulShutdownDrainWindow { get; init; }
    public int WorkerProbePort { get; init; }
    public bool WorkerProbeLoopbackOnly { get; init; }
}
```

**Every value the design commits to, with its default. Only the two retention windows are required**;
a wrong value that degrades gets a default, a missing value that corrupts fails the host.

| Setting | Default | Validation |
|---|---|---|
| `Outbox.ProcessedRetention` | **required, no default** | present; positive |
| `Outbox.PoisonedRetention` | **required, no default** | present; positive; **strictly greater than `ProcessedRetention`** |
| `Outbox.ClaimWindow` | 5 min | positive |
| `Outbox.PoisonAttemptCount` | 12 | `>= 1` |
| `Outbox.RetryBackoffBase` | 30 s | positive |
| `Outbox.RetryBackoffFactor` | 2 | `> 1` |
| `Outbox.RetryBackoffCap` | 6 h | `>= RetryBackoffBase` |
| `Outbox.DeferralAge` | 24 h | positive |
| `Outbox.DeferralRetryInterval` | 1 min, fixed — no backoff | positive |
| `Outbox.DispatchTickBudget` | 20 | `>= 1` |
| `Outbox.PruneBatchSize` | 500 | `>= 1` |
| `Outbox.DispatchInterval` | 5 s | positive |
| `Lease.Duration` | 5 min | positive |
| `HostRegistration.HeartbeatInterval` | 15 s | positive |
| `HostRegistration.RetentionWindow` | 7 days | positive |
| `HostRegistration.PeerAbsenceStartupGrace` | 60 s | non-negative |
| `HostRegistration.PeerLivenessThreshold` | **derived**, `3 × HeartbeatInterval` | no setter, so the two cannot disagree |
| `Health.BacklogAgeThreshold` | 5 min | positive |
| `Hosting.GracefulShutdownDrainWindow` | 30 s | positive; less than `Outbox.ClaimWindow` |
| `Hosting.WorkerProbePort` | 5100 | valid port |
| `Hosting.WorkerProbeLoopbackOnly` | `true` | — |
| `Persistence.SqliteBusyWaitBound` | 5 s | positive |
| `Persistence.ConnectionString` | **required, no default** | present; parseable by the selected provider |

**SQLite's journal mode is not a setting.** WAL is required and is a property of the file rather
than of a host, so two hosts cannot disagree on it. Persistence asserts it on open and fails startup
if the file is in any other mode — the contention analysis in the design is false without it.

### Core — module descriptor

```csharp
public sealed record ModuleDescriptor(
    ModuleName Name,
    IReadOnlyCollection<ModuleName> DependsOn,
    IPlatformModule Module);
```

### Hosting — error envelope

```csharp
public sealed record ErrorEnvelope(string Code, CorrelationId Correlation);
```

**Two fields, and the design determines exactly these two.** The envelope carries a stable error code
and the correlation identity, **never exception text and never payload content**. The correlation is
what ties it to the log line that does carry the detail — which is the whole reason the design
insisted on a single greppable value. The wire format is [Unresolved](#unresolved).

---

## Persisted schemas

Logical column types map per provider exactly as the design's table states. Names below are logical.

**Two encoding rules bind every table here and every product table.** Identifier columns store on
SQLite as a 16-byte blob in **RFC 4122 network byte order**, never the platform `Guid` byte order,
so bytewise blob comparison equals mint order. Instant columns store on SQLite as **fixed-width
ISO-8601 UTC text, `Z`-suffixed, exactly seven fractional digits, zero-padded and never trimmed**,
and **every instant bound as a SQL parameter is written by the same formatter as the column** — the
platform's default SQLite parameter binding violates all three properties, so pinning only the write
side moves the defect to the other side of the comparison.

### `platform_outbox`

| Column | Logical type | Null | Constraint |
|---|---|---|---|
| `id` | identifier | no | **Primary key.** Version-7 UUID minted at enqueue |
| `sequence` | sequence | no | **Unique.** Provider-allocated; claim order only, values reusable after prune |
| `occurred_at` | instant | no | |
| `type` | text | no | Non-empty |
| `payload` | payload | no | |
| `tenant` | tenant | no | Defaults to the all-zero sentinel |
| `trace_parent` | text | no | Complete `traceparent` including trace flags |
| `trace_state` | text | yes | W3C `tracestate` when the origin carried one |
| `attempts` | integer | no | Default 0, `>= 0` |
| `next_attempt_at` | instant | yes | Null means eligible now |
| `first_deferred_at` | instant | yes | Stamped on first deferral; the deferral age measures from it |
| `claimed_by` | text | yes | Null exactly when `claimed_at` is null |
| `claimed_at` | instant | yes | Null exactly when `claimed_by` is null |
| `processed_at` | instant | yes | |
| `poisoned_at` | instant | yes | |
| `last_error` | text | yes | Non-null whenever `poisoned_at` is set |

**Indexes.** One covering the eligibility predicate — `processed_at`, `poisoned_at`,
`next_attempt_at`, `claimed_at`, ordered by `sequence` — because every dispatch poll runs it and it
is the only hot query. One on `processed_at` and one on `poisoned_at`, for the prune passes. One
unique index on `sequence`. The primary key on `id` is append-mostly on PostgreSQL because the UUID
is time-ordered.

**Check constraints, and one that was retracted.** `claimed_by` and `claimed_at` are null together
or set together. `poisoned_at IS NOT NULL` implies `last_error IS NOT NULL`. `attempts >= 0`.

There is **no** constraint asserting `processed_at` and `poisoned_at` are mutually exclusive. An
earlier derivation carried one; it cannot exist, because discard sets both marks by design and the
constraint would reject the operation the design requires. All four combinations of the two columns
are legal and each names a state.

**Migration story.** New table, no existing data. Created empty by the Persistence module's first
migration.

**Payload shapes change additively or not at all.** New optional fields only — never a rename, a
removal or a change of meaning. A breaking change is a new event under a new stable `type`, with the
old handler retained until the old rows drain. A backlog days deep is this design's normal shape, so
an upgrade that changes what a `type` means is dispatching against history.

### `platform_background_work_lease`

| Column | Logical type | Null | Constraint |
|---|---|---|---|
| `name` | text | no | **Primary key** |
| `holder` | text | no | |
| `acquired_at` | instant | no | |
| `expires_at` | instant | no | |

**Primary key on `name` alone** is what makes acquisition a conditional update rather than a
read-then-write: a second acquirer either updates the expired row or does not, atomically.

**Migration story.** New table, no existing data.

### `platform_host_registration`

| Column | Logical type | Null | Constraint |
|---|---|---|---|
| `role` | text | no | **Primary key** with `instance` |
| `instance` | text | no | **Primary key** with `role` |
| `started_at` | instant | no | |
| `heartbeat_at` | instant | no | |
| `settings_fingerprint` | text | no | |

**Index** on `role` and `heartbeat_at` — the peer-presence query, which considers live rows only.

**Migration story.** New table, no existing data. A host **deletes its own row on graceful
shutdown**, and the prune pass removes rows whose heartbeat is older than the registration retention
window. A previous derivation stated these rows are never pruned; that reintroduces the unbounded
growth the second outbox retention window exists to close, in the table that watches for everything
else.

### Columns on product tables

Every product table carries `tenant` (non-null, defaulting to the sentinel) and the four audit
columns. Soft-delete columns appear only where the product opts in.

**No query filter ships in D3.** The column is data and is ruinous to add after products have rows;
a filter is code and is cheap whenever tenancy becomes a feature.

**Migration story, and the reason the column is in D3 at all.** On a fresh installation the column is
present from the first migration and nothing is backfilled. **Adding it to a table that already has
rows requires a backfill under lock on every table at once**, which is exactly the correctness
migration the brief moved this into D3 to avoid. There is no supported path that adds it later.

### Migration history

One history per module, not one shared table — a shared one serialises the ordering it exists to
permit.

**No foreign key may cross a module boundary.** The either-order guarantee holds only for disjoint
schemas, nothing in the mechanism enforces it, so the provider contract tests assert it directly.

**Schema change is expand-then-contract.** A column is added, populated and read before anything
stops writing the one it replaces; a breaking change is two releases rather than one. The
degraded-and-serve answer to a host running behind the schema is honest only if the pending change
is additive, so additivity is a rule here and not a hope.

---

## Public signatures

### Abstractions

The interfaces and value types above constitute the surface. No functions.

### Core

```csharp
public interface IModuleRegistry
{
    Result<IReadOnlyList<ModuleDescriptor>, ModuleGraphError> Resolve(
        IReadOnlyCollection<IPlatformModule> modules);
}

public interface IBackgroundWorkRegistry
{
    Result<BackgroundWorkRegistrationError> Register(IBackgroundWork work);
    IReadOnlyList<IBackgroundWork> Registered { get; }
    IReadOnlyList<IBackgroundWork> ForRole(HostRole role);
    void Freeze();
}

public interface IHealthCheckRegistry
{
    Result<HealthCheckRegistrationError> Register(IHealthCheck check);
    IReadOnlyList<IHealthCheck> Registered { get; }
    void Freeze();
}

public interface ISettingsFingerprint
{
    string Compute(PlatformOptions options);
}
```

**`Resolve` returns the topological order with ties broken by name**, so the order is reproducible
across runs. **`Freeze` is one-way**; registration after it returns a failure rather than mutating a
structure concurrent readers are walking.

**`ForRole` is how Hosting starts work it cannot name.** It returns the registrations whose `Roles`
include the host's role, which for the web host is the registration heartbeat alone.

### Persistence

```csharp
public interface IUnitOfWork
{
    Task<Result<TransactionError>> ExecuteAsync(
        Func<CancellationToken, Task> work,
        CancellationToken cancellationToken);

    Task<Result<T, TransactionError>> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken);
}

public interface IOutboxWriter
{
    OutboxMessageId Enqueue<TEvent>(TEvent @event) where TEvent : IIntegrationEvent;
}

public interface IEventHandlerRegistry
{
    Result<EventHandlerRegistrationError> Register<TEvent>(
        EventTypeName type)
        where TEvent : IIntegrationEvent;

    void Freeze();
}

public interface IOutboxAdministration
{
    Task<Result<IReadOnlyList<OutboxAdministrationResult>, OutboxError>> RedriveAsync(
        IReadOnlyCollection<OutboxMessageId> ids,
        CancellationToken cancellationToken);

    Task<Result<int, OutboxError>> RedriveByTypeAsync(
        EventTypeName type,
        CancellationToken cancellationToken);

    Task<Result<IReadOnlyList<OutboxAdministrationResult>, OutboxError>> DiscardAsync(
        IReadOnlyCollection<OutboxMessageId> ids,
        string reason,
        CancellationToken cancellationToken);

    Task<Result<int, OutboxError>> DiscardByTypeAsync(
        EventTypeName type,
        string reason,
        CancellationToken cancellationToken);

    Task<Result<IReadOnlyList<OutboxMessage>, OutboxError>> ListPoisonedAsync(
        int limit,
        CancellationToken cancellationToken);
}

public enum OutboxAdministrationOutcome { Applied, NotFound, NotPoisoned }

public sealed record OutboxAdministrationResult(
    OutboxMessageId Id,
    OutboxAdministrationOutcome Outcome);

public interface ILeaseManager
{
    Task<Result<ILeaseHandle, LeaseError>> AcquireAsync(
        BackgroundWorkName name,
        CancellationToken cancellationToken);
}

public interface ILeaseHandle : IAsyncDisposable
{
    BackgroundWorkName Name { get; }
    DateTimeOffset ExpiresAt { get; }
    Task<Result<LeaseError>> RenewAsync(CancellationToken cancellationToken);
}

public interface IMigrationRunner
{
    Task<Result<IReadOnlyList<ModuleMigrationStatus>, MigrationError>> GetStatusAsync(
        CancellationToken cancellationToken);

    Task<Result<MigrationError>> ApplyAsync(CancellationToken cancellationToken);
}
```

**`Enqueue` returns the id and is synchronous** because it does not write — it mints a version-7
UUID from the clock, enlists in the caller's transaction, and the write happens on commit. The id is
loggable and returnable before the insert, which is what makes it a usable dedupe key.

**`Enqueue` throws `PlatformContractViolationException` when there is no ambient transaction or no
ambient operation scope**, and the provider contract tests assert both throws. The alternatives are
worse than a throw: a nullable trace context admits rows whose correlation appears nowhere upstream,
and an implicitly minted scope fabricates a traceparent that dispatch will faithfully rebuild — a
fiction indistinguishable at read time from a real origin. A call site that genuinely has only the
enqueue opens a transaction around it, one explicit line that states the intent.

**Per-id redrive and discard return an outcome per id, not a count.** Racing the prune pass yields
`NotFound` and a row someone already discarded yields `NotPoisoned`, both distinguishable from
success — the design requires a clear "already pruned" rather than silent nothing, and requires that
a discarded row is never resurrected into one that can never deliver.

**Both operations exist per row and in bulk by Type**, because a violated payload rule poisons in
bulk and the recovery must not be a thousand hand-invocations. **No endpoint or console ships in D3
to invoke them**; the sample demonstrates calling them.

**`ILeaseHandle.RenewAsync` returning a failure obliges the holder to abort.** The lease is an
optimisation against duplicate work, not a mutual-exclusion primitive: a holder can stall past its
expiry while its work continues, and nothing fences it. Leased work must be idempotent, and
non-idempotent work does not belong under a lease at all.

**`ApplyAsync` is migrate mode's operation and takes an exclusive lease before applying**, returning
`MigrationError.Locked` if one is held. It is the operation with the most destructive potential per
statement, and the ways to invoke it twice at once are entirely ordinary — a unit restarting a run
that appeared to fail, an operator retrying in a second shell, both hosts' deploy scripts each
helpfully migrating. Neither provider serialises this on our behalf.

### Hosting

```csharp
public static class PlatformHostExtensions
{
    public static IHostApplicationBuilder AddPlatformWebHost(this IHostApplicationBuilder builder);

    public static IHostApplicationBuilder AddPlatformWorkerHost(this IHostApplicationBuilder builder);

    public static IEndpointRouteBuilder MapPlatformProbes(this IEndpointRouteBuilder endpoints);

    public static Task<int> RunPlatformMigrateModeAsync(
        this IHostApplicationBuilder builder,
        CancellationToken cancellationToken);
}
```

**Two forms of one registration call, one bootstrap.** The worker is the same startup validation,
module ordering, options binding and health registration with the product HTTP surface omitted and
background work enabled — splitting it into a second package would duplicate exactly the behaviour
that must not diverge between the two processes of one installation.

**There is no `UsePlatform`.** The brief's done-criterion is that health, readiness, correlation,
migrations and telemetry are configured by nothing but the standard registration call; a second
mandatory call is bespoke wiring by the first consumer.

**`MapPlatformProbes` is called by the worker form itself**, on its own loopback port, through the
same endpoint code as the web role. It exists on the public surface so a web host can place the
probes within its own route table.

**`RunPlatformMigrateModeAsync` returns a process exit status.** It is a one-shot command, not a
third host role.

### Observability

```csharp
public static class PlatformObservabilityExtensions
{
    public static IHostApplicationBuilder AddPlatformObservability(this IHostApplicationBuilder builder);
}
```

Called by both forms of the standard registration call. Exposed separately because Observability is
usable by a consumer that wants telemetry wiring without a Platform host.

### Testing

```csharp
public sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; }
    public void Advance(TimeSpan by);
    public void SetTo(DateTimeOffset instant);
}

public sealed class FakeCurrentTenant : ICurrentTenant
{
    public TenantId Current { get; set; }
}

public sealed class FakeCurrentPrincipal : ICurrentPrincipal
{
    public ClaimsPrincipal? Current { get; set; }
}

public sealed record CapturedEvent(
    OutboxMessageId Id,
    EventTypeName Type,
    TenantId Tenant,
    CorrelationId Correlation,
    DateTimeOffset At);

public interface IEventCapture
{
    IReadOnlyList<CapturedEvent> Enqueued { get; }
    IReadOnlyList<CapturedEvent> Dispatched { get; }
    void Clear();
}

public interface IPlatformTestHostBuilder
{
    IPlatformTestHostBuilder WithRole(HostRole role);
    IPlatformTestHostBuilder WithProvider(PersistenceProvider provider);
    IPlatformTestHostBuilder WithSetting(string key, string value);
    Task<IPlatformTestHost> StartAsync(CancellationToken cancellationToken);
}

public interface IPlatformTestHost : IAsyncDisposable
{
    IServiceProvider Services { get; }
    FakeClock Clock { get; }
    IEventCapture Events { get; }

    Task<HealthReport> ProbeAsync(HealthCheckKind kind, CancellationToken cancellationToken);
    Task RunBackgroundWorkOnceAsync(BackgroundWorkName name, CancellationToken cancellationToken);
}
```

**`RunBackgroundWorkOnceAsync` is what makes background work deterministic in tests.** A test that
waits for a timer is flaky by construction.

**The provider contract tests must assert at least the following**, which the design names
individually. Their invocation surface is [Unresolved](#unresolved); what they assert is not.

| Assertion | What it catches |
|---|---|
| Identifier blob sort order equals mint order, across a run minted in sequence | The SQLite `Guid` byte order scrambling a version-7 UUID's time ordering |
| Instant comparison is correct across a sub-second boundary, column **and** bound comparand | A trimming or variable-width writer making due messages ineligible |
| `Id` is unique across a drain, prune-to-empty, insert cycle | SQLite rowid reuse, which is why the sequence is not the identity |
| `Enqueue` throws without an ambient transaction | An outbox row committing apart from its domain write |
| `Enqueue` throws without an ambient operation scope | A row whose correlation appears nowhere upstream |
| No foreign key crosses a module boundary | The either-order migration guarantee, which nothing else enforces |
| A claim is granted to exactly one of two concurrent claimants | The portable conditional-update claim, on both providers |
| The suite goes red against a deliberately broken provider | A suite that has never failed is not evidence |

---

## Error semantics

Every variant is a `PlatformError` with a stable `Code`. No bare exceptions and no string errors
cross a module boundary; the two exceptions that do exist are named above and both signal a caller
defect rather than a runtime condition.

### Abstractions — `ContractViolation`

Carried by `PlatformContractViolationException`. Never returned.

| Variant | Raised when | Retryable | Caller does |
|---|---|---|---|
| `NoAmbientTransaction` | `Enqueue` is called outside a unit of work | No | Fix the call site — open a transaction around the enqueue |
| `NoAmbientOperationScope` | `Enqueue`, or an ambient accessor, is reached with no scope open | No | Fix the call site — a seeder or migrate-mode utility opens a scope explicitly |
| `ResultAccessedIncorrectly` | `Value` read on a failure, or `Error` on a success | No | Fix the call site |

### Core — `ModuleGraphError`

| Variant | Raised when | Retryable | Caller does |
|---|---|---|---|
| `MissingDependency` | A module declares a dependency no registered module provides | No | Fails startup, naming the module and the missing dependency |
| `CyclicDependency` | The dependency graph contains a cycle | No | Fails startup, naming the cycle |
| `DuplicateModuleName` | Two modules register the same name | No | Fails startup |

### Core — `ConfigurationError`

| Variant | Raised when | Retryable | Caller does |
|---|---|---|---|
| `MissingRequiredSetting` | A setting with no default is absent — the two retention windows, the connection string | No | Fails startup, naming the setting **and the configuration source expected to supply it** |
| `InvalidSetting` | A value is present but outside its permitted range, or a connection string is unparseable | No | Fails startup, naming the setting and the constraint |
| `InconsistentSettings` | Two settings are individually valid and jointly not — poison retention not longer than processed, drain window not shorter than the claim window | No | Fails startup, naming both settings |
| `UnsupportedJournalMode` | The SQLite file is open in any mode other than WAL | No | Fails startup. The contention analysis this design rests on is false outside WAL |

### Core — `HealthCheckRegistrationError`

| Variant | Raised when | Retryable | Caller does |
|---|---|---|---|
| `DuplicateName` | The name is already registered | No | Fails startup |
| `RegistryFrozen` | Registration attempted after the host is built | No | Fails startup — a defect, not a condition |
| `ExternalDependencyInLivenessCheck` | A check declaring `TouchesExternalDependency` registers as `Liveness` | No | Fails startup. A database check reachable from liveness produces a restart loop during the outage it was meant to report |

### Core — `BackgroundWorkRegistrationError`

| Variant | Raised when | Retryable | Caller does |
|---|---|---|---|
| `DuplicateName` | The name is already registered | No | Fails startup |
| `RegistryFrozen` | Registration attempted after the host is built | No | Fails startup |
| `NoRoleDeclared` | `Roles` is empty, so no host would ever run the work | No | Fails startup. Silent never-running is the failure this field exists to prevent |

### Persistence — `EventHandlerRegistrationError`

| Variant | Raised when | Retryable | Caller does |
|---|---|---|---|
| `DuplicateHandlerForType` | A second handler registers for an `EventTypeName` already registered | No | Fails startup, naming the type and both handlers. A product that wants two things to happen writes one handler that does two things |
| `RegistryFrozen` | Registration attempted after the host is built | No | Fails startup |

**Enforcement is at startup only.** Enforcing at dispatch as well is the more rigorous reading — a
container can be populated directly and bypass the registry — and was declined for one error path
rather than two. Revisitable if the bypass ever happens.

### Persistence — `TransactionError`

| Variant | Raised when | Retryable | Caller does |
|---|---|---|---|
| `Unavailable` | The database cannot be reached | **Yes**, by the caller's own policy — never by Platform | Surfaces an error envelope carrying the correlation identity |
| `Conflict` | A concurrency conflict aborts the transaction | **Yes** | May retry the whole unit of work; outbox rows roll back with the domain write |
| `Busy` | SQLite's busy-wait bound elapsed without acquiring the write lock | **Yes** | Fails the operation normally; under contention this is the visible symptom |
| `Faulted` | Any other failure inside the transaction | No | Surfaces; the rollback is complete |

**Platform retries nothing on the request path.** A generic retry doubles load on a struggling
database and turns a fast failure into a slow one.

**A transaction that will write begins immediate, never deferred.** A deferred transaction that
upgrades to a write after reading — the shape of both a claim and a mark — can take a busy condition
that waiting cannot resolve, because no amount of waiting makes its read snapshot valid again.

### Persistence — `OutboxError`

| Variant | Raised when | Retryable | Caller does |
|---|---|---|---|
| `Unavailable` | The database cannot be reached | **Yes** | Retries at the caller's discretion |

**Per-row dispositions are outcomes, not errors.** `NotFound` and `NotPoisoned` are returned per id
in `OutboxAdministrationResult`, because "one of the forty rows you named was pruned" is a result, not
a failure of the operation.

### Persistence — `HandlerError`

Returned by a handler. Both variants consume an attempt.

| Variant | Raised when | Retryable | Caller does |
|---|---|---|---|
| `Transient` | The handler failed in a way that may succeed later; also an exception escaping the handler | **Yes** | Dispatcher records the error, increments attempts, sets the next attempt with exponential backoff |
| `Permanent` | The handler failed in a way that will not succeed on retry | No | Dispatcher poisons the row immediately, without burning the remaining attempts to reach a conclusion the handler already had |

### Persistence — `DispatchError`

Raised by the dispatcher, never by a handler. **No variant consumes an attempt** — that is the whole
reason these are separate from `HandlerError`.

| Variant | Raised when | Retryable | Caller does |
|---|---|---|---|
| `HandlerUnresolved` | No handler is registered for the row's `type` | **Yes, without consuming an attempt** | Releases the claim, stamps `first_deferred_at` if unset, sets the next attempt one fixed deferral interval ahead. Raised routinely during an upgrade, when the new web process enqueues a type the old worker has never seen. Poisons only past the deferral age, measured from first deferral |
| `PayloadUndeserializable` | The type resolves and the payload does not deserialize | **Yes, without consuming an attempt** | The same deferral path. Burning attempts here mass-poisons the entire pre-upgrade backlog within minutes of a bad deploy — the exact catastrophe the additive-only payload rule exists to prevent, delivered by the retry machinery itself |
| `MigrationsPending` | This host has registered migrations that are not applied | **Yes, without consuming an attempt** | **Does not claim at all.** Nothing is stamped and nothing ages; the backlog-age readiness condition already reports the wait |

**Measuring the deferral age from first deferral rather than from `occurred_at` is what preserves
the grace after a long outage:** a days-old backlog row gets the full deferral window on its first
attempt instead of poisoning instantly.

### Persistence — `LeaseError`

| Variant | Raised when | Retryable | Caller does |
|---|---|---|---|
| `Held` | Another holder has an unexpired lease | **Yes**, at the next interval | Skips this run entirely |
| `Lost` | Renewal found the lease held by someone else | No | **Aborts the work immediately** |
| `Unavailable` | The database cannot be reached | **Yes** | Skips this run |

### Persistence — `MigrationError`

| Variant | Raised when | Retryable | Caller does |
|---|---|---|---|
| `Failed` | A migration failed to apply | No | Stops, and does not continue to the next module. Both providers apply a migration atomically, so the database is left at a known point |
| `Locked` | Migrate mode found an exclusive lease held | **Yes**, once the other run finishes | Exits non-zero without applying anything |
| `Unavailable` | The database cannot be reached | **Yes** | Exits non-zero; the operator retries |

### Hosting — `HostStartupError`

| Variant | Raised when | Retryable | Caller does |
|---|---|---|---|
| `Configuration` | A `ConfigurationError` was raised during binding or validation | No | Aborts startup, surfacing the inner error's name and constraint |
| `ModuleGraph` | A `ModuleGraphError` was raised during resolution | No | Aborts startup, surfacing the inner error |
| `Registration` | Any registry rejected a registration | No | Aborts startup, surfacing the inner error |

**Startup aborts; it never degrades.** *Unavailability* with valid configuration is the opposite
case and is not an error here at all — the host starts and reports not ready, because on a
self-hosted box a database thirty seconds behind the application should not need a human.

### Observability

**No error type crosses this boundary.** Export failures are absorbed — buffered to a bounded queue,
retried internally, then dropped with a state-transition log rather than one line per failure. A
malformed inbound `traceparent` yields `false` from `TryParse` and a fresh root, never a rejected
request. The design forbids collection from becoming a path by which a caller can fail, so there is
nothing for a caller to handle.

### Testing

**No error type of its own.** Failures surface as the underlying package's error, which is the point
— a test host that translated errors would be testing the translation.

---

## Invariants

Each is written to be assertable, with the module responsible for maintaining it.

| # | Invariant | Owner |
|---|---|---|
| 1 | Every persisted instant has `Offset == TimeSpan.Zero` and originates from `IClock`; no eligibility, claim-expiry or lease-expiry comparison reads a database clock | Persistence |
| 2 | A persisted instant's SQLite text form is fixed-width, `Z`-suffixed, seven fractional digits, never trimmed — and every instant bound as a SQL parameter uses the same formatter as the column | Persistence |
| 3 | An identifier's SQLite blob encoding is RFC 4122 network byte order, so bytewise blob order equals mint order | Persistence |
| 4 | Every outbox row is in exactly one of the four states, and every consumer — readiness, prune, redrive — derives its state from the predicate table rather than from a column of its own | Persistence |
| 5 | `claimed_by` is null if and only if `claimed_at` is null | Persistence |
| 6 | `poisoned_at` set implies `last_error` non-null | Persistence |
| 7 | An outbox row is inserted only inside a transaction that also carries its domain write, and only inside an ambient operation scope | Persistence |
| 8 | Every product table row has a non-null tenant; the value is `TenantId.Implicit` throughout D3 | Persistence |
| 9 | No foreign key crosses a module boundary | Persistence |
| 10 | A dispatched message's ambient context is reconstructed from its row — correlation from the stored traceparent's trace-id, tenant from the row, principal null — never inherited from the worker | Persistence |
| 11 | Dispatch starts a new trace linked to the stored one, honouring its stored sampling flags; it never continues the origin trace | Persistence |
| 12 | `attempts` increases only on a `HandlerError`; no `DispatchError` variant increments it | Persistence |
| 13 | `attempts` never decreases except through an explicit redrive | Persistence |
| 14 | A claim covers exactly one row, and is granted to exactly one of any two concurrent claimants | Persistence |
| 15 | Every background write is bounded — one row per claim and per mark, `PruneBatchSize` rows per prune statement | Persistence |
| 16 | A transaction that will write begins immediate; the SQLite file is in WAL mode or the host does not start | Persistence |
| 17 | Every persistence readiness check self-guards on an absent schema, reporting degraded with the schema named rather than throwing | Persistence |
| 18 | Exactly one handler is registered per `EventTypeName` | Persistence |
| 19 | Module order is a topological sort of declared dependencies, ties broken by name, identical across runs on identical input | Core |
| 20 | The health, background-work and handler registries accept no registration after `Freeze` | Core |
| 21 | No check declaring `TouchesExternalDependency` is registered as `Liveness` | Core |
| 22 | Both retention settings and the connection string are present, or the host does not start | Core |
| 23 | The settings fingerprint covers exactly the properties marked `[Fingerprinted]`, and two hosts on identical settings compute identical values | Core |
| 24 | `PeerLivenessThreshold` equals three times `HeartbeatInterval` and cannot be set independently | Core |
| 25 | Liveness never evaluates an external dependency | Hosting |
| 26 | Readiness returns success for `Healthy` and `Degraded`, failure only for `Unhealthy` | Hosting |
| 27 | Every host writes its registration to the store it is using, and deletes its own row on graceful shutdown | Hosting |
| 28 | Background work runs only in a host whose role the registration's `Roles` includes; no product work and no outbox dispatch runs in the web role | Hosting |
| 29 | A request never blocks on telemetry export, a probe, dispatch, or prune | Hosting |
| 30 | A malformed inbound `traceparent` never fails the request | Hosting |
| 31 | Graceful shutdown stops claiming immediately and releases claims it has not started | Hosting |
| 32 | The worker probe binds loopback unless explicitly configured otherwise | Hosting |
| 33 | The probe body is `Full` only on loopback or in the development environment, and the status is identical at either detail level | Hosting |
| 34 | `last_error` never crosses a wire; no probe body and no error envelope carries exception text or payload content | Hosting |
| 35 | Peer absence is informational in the development environment, and degrades elsewhere only after the startup grace has elapsed | Hosting |
| 36 | Telemetry export never propagates a failure to a caller | Observability |
| 37 | No secret appears in any exported log, span attribute or metric label | Observability |
| 38 | No metric is labelled with an unbounded value | Observability |

---

## Unresolved

The design does not determine these. **The first three block implementation**; the rest have safe
provisional readings but no stated value.

1. **How an event declares its stable `EventTypeName`.** The design requires the persisted `type` to
   survive a class rename and forbids it being a runtime type name, but does not say what supplies
   it. An attribute, a registration-time mapping and an interface member are all viable and fail
   differently when a name collides or is forgotten. `IIntegrationEvent.TypeName` above is the
   *shape* the design implies; **what populates it is undetermined**, and a wrong choice orphans rows
   permanently.

2. **The payload serialisation contract.** `Payload` is `string` because the design says json and
   nothing further. Whether the serialiser is injectable, and what canonical form a payload takes,
   are undetermined. *What has been resolved:* an undeserializable payload is
   `DispatchError.PayloadUndeserializable` and takes the deferral path without consuming an attempt.
   The serialiser's identity is what remains.

3. **The provider abstraction's surface.** The design commits to a real provider abstraction verified
   by contract tests and names none of its members. Everything provider-specific — instant
   formatting, identifier encoding, the claim statement, immediate transactions, prune batching,
   journal-mode assertion — passes through it, so its shape decides whether the two providers are
   genuinely interchangeable or merely both present.

4. **The settings fingerprint's canonical form and hash algorithm.** Membership is now determined —
   the `[Fingerprinted]` marker and the rule behind it. The canonical serialisation of those values
   and the digest over it are not, and two hosts computing them differently would report a permanent
   false mismatch.

5. **Upper bounds for `DispatchTickBudget` and `PruneBatchSize`.** The design makes both
   correctness-adjacent on SQLite and requires them validated; only the lower bound (`>= 1`) follows
   from what it says. What value is too large to hold the single write lock for is not stated.

6. **The wire format of the error envelope and the probe body.** Determined: the envelope's two
   fields, that the probe body narrows by detail level, and that neither carries exception text or
   payload content. Undetermined: media type, member names, and whether the envelope reuses an
   existing problem-details shape.

7. **The per-check default timeout, and the probe endpoint's overall timeout.** The design sizes the
   SQLite busy-wait bound as "shorter than a probe timeout" without stating that timeout.

8. **How `InstanceId` is derived.** Two hosts of the same role on one machine must differ, and a
   restart must produce a new value or a dead row would be indistinguishable from its replacement.
   The design names the concept and not its construction.

9. **The naming convention for a module's migration history table.** One history per module is
   determined; what each is called, and therefore whether two modules can collide, is not.

10. **The provider contract tests' invocation surface.** What they must assert is determined and
    listed above. Whether they are an abstract base class, a shared suite parameterised by a provider
    factory, or something else, is not — and it decides how a third party runs them against a
    provider of their own.
