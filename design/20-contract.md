# Contract — the minimal package set (D3)

**Document status:** Contract. Derived from [`10-design.md`](10-design.md). Authoritative for the
packages it describes; [`platform-identity.md`](../docs/docs/platform-identity.md) stays
authoritative for what this repository is.

C# with nullable reference types enabled. Types and signatures only. Package grouping is by heading
rather than namespace declaration — package naming belongs to
[ADR-003](../docs/docs/adr/ADR-003-package-scopes-and-registries.md), not here.

Anything the design did not determine is in **[Unresolved](#unresolved)** rather than invented. That
section is not a residue list; three of its entries block implementation.

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

public readonly record struct TraceContext(string TraceParent)
{
    public CorrelationId CorrelationId { get; }
    public bool Sampled { get; }
    public static bool TryParse(string candidate, out TraceContext result);
}

public readonly record struct InstanceId(string Value);

public readonly record struct ModuleName(string Value);

public readonly record struct EventTypeName(string Value);

public readonly record struct HealthCheckName(string Value);

public readonly record struct BackgroundWorkName(string Value);
```

**Invariants carried by these types, not by their callers.** `TenantId.Implicit` is `Guid.Empty` —
the well-known all-zero sentinel. `CorrelationId.TraceId` is 32 lowercase hex characters and never
all-zero. `TraceContext.TraceParent` is a complete W3C `traceparent` including trace flags, from
which `Sampled` is read; the design requires the flags to travel with the row, so a type carrying
only the trace-id would not satisfy it. `ModuleName`, `EventTypeName`, `HealthCheckName` and
`BackgroundWorkName` are non-empty, trimmed, and case-sensitively unique within their registry.

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
```

**Accessing `Value` on a failure, or `Error` on a success, throws.** That is the one place an
exception is correct: it is a defect in the caller, not a runtime condition.

### Abstractions — ambient operation context

```csharp
public interface IClock
{
    DateTimeOffset UtcNow { get; }
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
    TraceContext Current { get; }
}
```

**`ICurrentTenant.Current` is non-nullable and in D3 always returns `TenantId.Implicit`.** Nothing
resolves a tenant from host, header or claim — the brief's binding non-goal. The interface exists so
the column has a supplier, not so tenancy can be turned on.

**`ICurrentPrincipal.Current` is nullable and frequently null.** Identity is D5; a worker dispatching
a message has no principal and must not be given a fabricated anonymous one.

**`IClock.UtcNow` always has `Offset == TimeSpan.Zero`.** Every persisted instant originates here, so
a fake clock controls every timestamp in the system.

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
from a defect, and only the former participates in the attempt-and-backoff cycle.

### Abstractions — health contract

```csharp
public enum HealthStatus { Healthy, Degraded, Unhealthy }

public enum HealthCheckKind { Liveness, Readiness }

public enum HealthCheckCriticality { Required, Optional }

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

### Abstractions — background work contract

```csharp
public interface IBackgroundWork
{
    BackgroundWorkName Name { get; }
    TimeSpan Interval { get; }
    bool RequiresLease { get; }

    Task RunAsync(CancellationToken cancellationToken);
}
```

**Work declaring `RequiresLease` must be idempotent.** The lease reduces duplicate runs; it does not
prevent them, and nothing here fences a stalled holder.

### Persistence — outbox message

```csharp
public sealed record OutboxMessage
{
    public required long Sequence { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public required EventTypeName Type { get; init; }
    public required string Payload { get; init; }
    public required TenantId Tenant { get; init; }
    public required TraceContext TraceContext { get; init; }
    public required int Attempts { get; init; }
    public DateTimeOffset? NextAttemptAt { get; init; }
    public InstanceId? ClaimedBy { get; init; }
    public DateTimeOffset? ClaimedAt { get; init; }
    public DateTimeOffset? ProcessedAt { get; init; }
    public DateTimeOffset? PoisonedAt { get; init; }
    public string? LastError { get; init; }
}
```

**`ProcessedAt` and `PoisonedAt` are mutually exclusive; both null means undispatched.** A row is
eligible when both are null, `ClaimedAt` is null or older than the claim window, and `NextAttemptAt`
is null or in the past.

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
public enum HostRole { Web, Worker }

public sealed record HostRegistration
{
    public required HostRole Role { get; init; }
    public required InstanceId Instance { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset HeartbeatAt { get; init; }
    public required string SettingsFingerprint { get; init; }
}
```

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

### Core — configuration root

```csharp
public sealed record PlatformOptions
{
    public required string ServiceName { get; init; }
    public required string ServiceVersion { get; init; }
    public required string Environment { get; init; }
    public required HostRole Role { get; init; }
    public required OutboxOptions Outbox { get; init; }
    public required HealthOptions Health { get; init; }
}

public sealed record OutboxOptions
{
    public required TimeSpan ProcessedRetention { get; init; }
    public required TimeSpan PoisonedRetention { get; init; }
    public TimeSpan ClaimWindow { get; init; }
    public int DispatchBatchSize { get; init; }
    public int PruneBatchSize { get; init; }
    public int MaxAttempts { get; init; }
}

public sealed record HealthOptions
{
    public TimeSpan BacklogAgeThreshold { get; init; }
    public TimeSpan PeerHeartbeatTimeout { get; init; }
}
```

**`ProcessedRetention` and `PoisonedRetention` have no defaults and are validated as present at
startup.** Every other member has a default. The rule is the design's: a wrong value that *degrades*
gets a default; a missing value that *corrupts* — silently never pruning — fails the host.
`ClaimWindow` defaults to five minutes.

**`Environment` is read from the host and is not settable from configuration.** A service must not be
able to declare itself production in a file that shipped from a developer's machine.

### Core — module descriptor

```csharp
public sealed record ModuleDescriptor(
    ModuleName Name,
    IReadOnlyCollection<ModuleName> DependsOn,
    IPlatformModule Module);
```

---

## Persisted schemas

Logical column types map per provider exactly as the design's table states. Names below are logical.

### `platform_outbox`

| Column | Logical type | Null | Constraint |
|---|---|---|---|
| `sequence` | sequence | no | **Primary key.** Monotonic per database |
| `occurred_at` | instant | no | |
| `type` | text | no | Non-empty |
| `payload` | payload | no | |
| `tenant` | tenant | no | Defaults to the all-zero sentinel |
| `trace_context` | text | no | Complete `traceparent` |
| `attempts` | integer | no | Default 0, `>= 0` |
| `next_attempt_at` | instant | yes | |
| `claimed_by` | text | yes | Null exactly when `claimed_at` is null |
| `claimed_at` | instant | yes | Null exactly when `claimed_by` is null |
| `processed_at` | instant | yes | Null when `poisoned_at` is set |
| `poisoned_at` | instant | yes | Null when `processed_at` is set |
| `last_error` | text | yes | |

**Indexes.** One covering the eligibility predicate — `processed_at`, `poisoned_at`,
`next_attempt_at`, `claimed_at`, ordered by `sequence` — because every dispatch poll runs it and it is
the only hot query. One on `processed_at` and one on `poisoned_at`, for the two prune passes.

**Check constraints.** `processed_at IS NULL OR poisoned_at IS NULL`, and the paired nullability of
`claimed_by`/`claimed_at`. Both are cheap, and both encode an invariant the dispatcher would
otherwise be trusted to maintain alone.

**Migration story.** New table, no existing data. Created empty by the Persistence module's first
migration.

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

**Index** on `role` and `heartbeat_at` — the peer-presence query.

**Migration story.** New table, no existing data. Rows are never pruned: a stale registration is how
the peer check detects absence, and deleting it would erase the signal.

### Columns on product tables

Every product table carries `tenant` (non-null, defaulting to the sentinel) and the four audit
columns. Soft-delete columns appear only where the product opts in.

**Migration story, and the reason the column is in D3 at all.** On a fresh installation the column is
present from the first migration and nothing is backfilled. **Adding it to a table that already has
rows requires a backfill under lock on every table at once**, which is exactly the correctness
migration the brief moved this into D3 to avoid. There is no supported path that adds it later.

### Migration history

One history per module, not one shared table. **No foreign key may cross a module boundary** — the
either-order guarantee holds only for disjoint schemas, nothing in the mechanism enforces it, so the
provider contract tests assert it.

---

## Public signatures

### Abstractions

The interfaces above constitute the surface. No functions.

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
    void Freeze();
}

public interface IHealthCheckRegistry
{
    Result<HealthCheckRegistrationError> Register(IHealthCheck check);
    IReadOnlyList<IHealthCheck> Registered { get; }
    void Freeze();
}
```

**`Resolve` returns the topological order with ties broken by name**, so the order is reproducible
across runs. **`Freeze` is one-way**; registration after it returns a failure rather than mutating a
structure concurrent readers are walking.

### Persistence

```csharp
public interface IUnitOfWork
{
    Task<Result<TransactionError>> ExecuteAsync(
        Func<IOutboxWriter, CancellationToken, Task> work,
        CancellationToken cancellationToken);
}

public interface IOutboxWriter
{
    void Enqueue<TEvent>(TEvent @event) where TEvent : IIntegrationEvent;
}

public interface IOutboxAdministration
{
    Task<Result<int, OutboxError>> RedriveAsync(
        IReadOnlyCollection<long> sequences,
        CancellationToken cancellationToken);

    Task<Result<int, OutboxError>> DiscardAsync(
        IReadOnlyCollection<long> sequences,
        string reason,
        CancellationToken cancellationToken);

    Task<Result<IReadOnlyList<OutboxMessage>, OutboxError>> ListPoisonedAsync(
        int limit,
        CancellationToken cancellationToken);
}

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
    Task<Result<MigrationError>> ApplyAsync(CancellationToken cancellationToken);
    Task<Result<IReadOnlyList<string>, MigrationError>> PendingAsync(
        CancellationToken cancellationToken);
}
```

**`Enqueue` is `void` and synchronous** because it does not write — it enlists in the caller's
transaction and the write happens on commit. That is the atomicity guarantee expressed in the
signature: there is no way to enqueue outside a unit of work.

**`ILeaseHandle.RenewAsync` returning a failure obliges the holder to abort.** The lease is an
optimisation, not a mutual-exclusion primitive, and a holder that ignores a failed renewal is the
case that makes it unsafe.

### Hosting

```csharp
public static class PlatformHostExtensions
{
    public static IServiceCollection AddPlatform(
        this IServiceCollection services,
        IConfiguration configuration);

    public static IApplicationBuilder UsePlatform(this IApplicationBuilder app);

    public static IEndpointRouteBuilder MapPlatformProbes(this IEndpointRouteBuilder endpoints);
}
```

**Both host roles call `AddPlatform`.** The role comes from configuration, not from a different
registration call, so startup validation and module ordering cannot diverge between the two
processes.

### Observability

```csharp
public static class PlatformObservabilityExtensions
{
    public static IServiceCollection AddPlatformObservability(
        this IServiceCollection services,
        IConfiguration configuration);
}
```

### Testing

```csharp
public sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; }
    public void Advance(TimeSpan by);
    public void SetTo(DateTimeOffset instant);
}

public interface IPlatformTestHost : IAsyncDisposable
{
    IServiceProvider Services { get; }
    FakeClock Clock { get; }
    Task<HealthReport> ProbeReadinessAsync(CancellationToken cancellationToken);
    Task RunBackgroundWorkOnceAsync(BackgroundWorkName name, CancellationToken cancellationToken);
}
```

**`RunBackgroundWorkOnceAsync` is what makes background work deterministic in tests.** A test that
waits for a timer is flaky by construction.

---

## Error semantics

Every variant is a `PlatformError` with a stable `Code`. No bare exceptions and no string errors
cross a module boundary.

### Core — `ModuleGraphError`

| Variant | Raised when | Retryable | Caller does |
|---|---|---|---|
| `MissingDependency` | A module declares a dependency no registered module provides | No | Fails startup, naming the module and the missing dependency |
| `CyclicDependency` | The dependency graph contains a cycle | No | Fails startup, naming the cycle |
| `DuplicateModuleName` | Two modules register the same name | No | Fails startup |

### Core — `ConfigurationError`

| Variant | Raised when | Retryable | Caller does |
|---|---|---|---|
| `MissingRequiredSetting` | A setting with no default is absent — the two retention windows | No | Fails startup, naming the setting **and the configuration source expected to supply it** |
| `InvalidSetting` | A value is present but outside its permitted range | No | Fails startup, naming the setting and the constraint |

### Core — `HealthCheckRegistrationError`, `BackgroundWorkRegistrationError`

| Variant | Raised when | Retryable | Caller does |
|---|---|---|---|
| `DuplicateName` | The name is already registered | No | Fails startup |
| `RegistryFrozen` | Registration attempted after the host is built | No | Fails startup — a defect, not a condition |
| `ExternalDependencyInLivenessCheck` | A check declaring `TouchesExternalDependency` registers as `Liveness` | No | Fails startup. A database check reachable from liveness produces a restart loop during the outage it was meant to report |

### Persistence — `TransactionError`

| Variant | Raised when | Retryable | Caller does |
|---|---|---|---|
| `Unavailable` | The database cannot be reached | **Yes**, by the caller's own policy — never by Platform | Surfaces an error envelope carrying the correlation identity |
| `Conflict` | A concurrency conflict aborts the transaction | **Yes** | May retry the whole unit of work; outbox rows roll back with the domain write |
| `Faulted` | Any other failure inside the transaction | No | Surfaces; the rollback is complete |

**Platform retries nothing on the request path.** A generic retry doubles load on a struggling
database and turns a fast failure into a slow one.

### Persistence — `OutboxError`

| Variant | Raised when | Retryable | Caller does |
|---|---|---|---|
| `UnknownSequence` | Redrive or discard names a row that does not exist | No | Reports which sequences were not found |
| `NotPoisoned` | Redrive or discard targets a row that is not poisoned | No | Refuses — redriving a live row would deliberately duplicate delivery |
| `Unavailable` | The database cannot be reached | **Yes** | Retries at the caller's discretion |

### Persistence — `HandlerError`

| Variant | Raised when | Retryable | Caller does |
|---|---|---|---|
| `Transient` | The handler failed in a way that may succeed later | **Yes** | Dispatcher increments attempts and backs off |
| `Permanent` | The handler failed in a way that will not succeed on retry | No | Dispatcher poisons the row immediately, without consuming remaining attempts |
| `Unresolvable` | No handler is registered for the row's type | **Yes, without consuming an attempt** | Dispatcher releases the claim and backs off. Raised routinely during an upgrade, when the new web process enqueues a type the old worker has never seen |

**`Unresolvable` not consuming an attempt is the whole reason it is separate from `Transient`.**
Otherwise a deploy poisons valid messages purely on timing.

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
| `Unavailable` | The database cannot be reached | **Yes** | Exits non-zero; the operator retries |

### Observability

**No error type crosses this boundary.** Export failures are absorbed — buffered, retried internally,
then dropped with a state-transition log. The design forbids collection from becoming a path by which
a caller can fail, so there is nothing for a caller to handle.

---

## Invariants

Each is written to be assertable, with the module responsible for maintaining it.

| # | Invariant | Owner |
|---|---|---|
| 1 | Every persisted instant has `Offset == TimeSpan.Zero`, and every one originates from `IClock` | Persistence |
| 2 | A persisted instant's SQLite text form is fixed-width, `Z`-suffixed, seven fractional digits, never trimmed | Persistence |
| 3 | `processed_at` and `poisoned_at` are never both non-null on one row | Persistence |
| 4 | `claimed_by` is null if and only if `claimed_at` is null | Persistence |
| 5 | An outbox row is inserted only inside a transaction that also carries its domain write | Persistence |
| 6 | Every product table row has a non-null tenant; the value is `TenantId.Implicit` throughout D3 | Persistence |
| 7 | No foreign key crosses a module boundary | Persistence |
| 8 | A dispatched message's ambient context is reconstructed from its row, never inherited from the worker | Persistence |
| 9 | `attempts` never decreases except through an explicit redrive | Persistence |
| 10 | Module order is a topological sort of declared dependencies, ties broken by name, identical across runs on identical input | Core |
| 11 | The health and background-work registries accept no registration after `Freeze` | Core |
| 12 | No check declaring `TouchesExternalDependency` is registered as `Liveness` | Core |
| 13 | Both retention settings are present, or the host does not start | Core |
| 14 | Liveness never evaluates an external dependency | Hosting |
| 15 | Readiness returns success for `Healthy` and `Degraded`, failure only for `Unhealthy` | Hosting |
| 16 | Every host writes its registration to the store it is using, before serving | Hosting |
| 17 | No background work runs in the web role | Hosting |
| 18 | A request never blocks on telemetry export, a probe, dispatch, or prune | Hosting |
| 19 | A malformed inbound `traceparent` never fails the request | Hosting |
| 20 | Telemetry export never propagates a failure to a caller | Observability |
| 21 | No secret appears in any exported log, span attribute or metric label | Observability |
| 22 | No metric is labelled with an unbounded value | Observability |

---

## Unresolved

The design does not determine these. **The first three block implementation**; the rest have safe
provisional readings but no stated value.

1. **How an event declares its stable `EventTypeName`.** The design requires the persisted `Type` to
   survive a class rename and forbids it being a runtime type name, but does not say what supplies
   it. An attribute, a registration-time mapping and an interface member are all viable and fail
   differently when a name collides or is forgotten. `IIntegrationEvent.TypeName` above is the
   *shape* the design implies; **what populates it is undetermined**, and a wrong choice orphans rows
   permanently.

2. **The payload serialisation contract.** `Payload` is `string` above because the design says json
   and nothing further. Whether the serialiser is injectable, what happens when a payload written by
   an older version cannot be deserialised by a newer one, and whether that is `Permanent` or
   `Unresolvable`, are all undetermined — and the second is a routine upgrade condition, not an edge
   case.

3. **The provider abstraction's surface.** The design commits to a real provider abstraction verified
   by contract tests and names none of its members. Everything provider-specific above — instant
   formatting, the claim statement, prune batching — passes through it, so its shape decides whether
   the two providers are genuinely interchangeable or merely both present.

4. **`MaxAttempts` before poisoning.** "A bounded attempt count", no number.

5. **The deferral age at which an `Unresolvable` row is poisoned.** "A bounded age", no number.

6. **`DispatchBatchSize` and `PruneBatchSize` defaults and permitted ranges.** The design makes them
   correctness-adjacent on SQLite and requires them validated, without saying against what.

7. **`BacklogAgeThreshold` and `PeerHeartbeatTimeout`.** Both drive a `Degraded` readiness result;
   neither has a stated value.

8. **The host heartbeat interval**, which must be materially shorter than `PeerHeartbeatTimeout` or
   the peer check reports false absences.

9. **The graceful-shutdown drain window.** "Bounded", no number.

10. **The settings fingerprint's hash algorithm and its exact input set.** The design lists which
    settings must agree; it does not fix the canonical form, and two hosts computing it differently
    would report a permanent false mismatch.

11. **The error envelope's field set.** Determined: derived from the failure and the ambient context,
    and carries the correlation identity. Undetermined: every other field, and the wire format. No
    type is declared above for that reason.
