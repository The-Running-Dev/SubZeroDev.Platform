# Contract — the minimal package set (D3)

**Document status:** Contract. Derived from [`10-design.md`](10-design.md). Authoritative for the
packages it describes; [`platform-identity.md`](../docs/docs/platform-identity.md) stays
authoritative for what this repository is.

C# with nullable reference types enabled. Types and signatures only. Package grouping is by heading
rather than namespace declaration — package naming belongs to
[ADR-003](../docs/docs/adr/ADR-003-package-scopes-and-registries.md), not here.

**Re-derived in full** from the design as it stands after its **fifth** adversarial review, not
patched from the previous derivation, per the standing decision in
[`90-decisions.md`](90-decisions.md) that a contract is re-derived and diffed rather than patched.
The previous derivation contradicted that revision in six places the decision log named — the
correlation column, the redrive semantics, the capability's migration lock, the cut converter
extension point, the tick-shaped background-work contract, and the operation scope's fourth
member — and each is corrected here in the section it touches. Anything the design still does not
determine is in **[Unresolved](#unresolved)** rather than invented. **Nothing there blocks
implementation.**

**Amended before S8** to make telemetry implementable without inventing dependencies, public
configuration or unsupported drop accounting. The provider, queue, redaction, sampling,
instrumentation and cardinality decisions are recorded in [`90-decisions.md`](90-decisions.md).

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
    public string TraceId { get; }
    public bool Sampled { get; }
    public static bool TryParse(string traceParent, string? traceState, out TraceContext result);
}

public readonly record struct CultureTag
{
    public CultureTag(string value);
    public string Value { get; }
    public static CultureTag Invariant { get; }
    public static bool TryParse(string candidate, out CultureTag result);
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
which `Sampled` and `TraceId` are read, and `TraceState` is the W3C `tracestate` when the origin
carried one; the design requires both to travel with the row so the sampling decision and any vendor
sampler state cross the boundary. `ModuleName`, `EventTypeName`, `HealthCheckName` and
`BackgroundWorkName` are non-empty, trimmed, and case-sensitively unique within their registry.

**`CultureTag` is deliberately non-positional so its all-zero representation can be invariant.**
The default representation has no backing string, but `Value` projects it as `string.Empty` and
never returns null; `Invariant` returns that same representation, and constructing from
`string.Empty` normalizes to it. Therefore `default(CultureTag) == CultureTag.Invariant`, rather
than merely meaning the same thing by convention. That is what lets culture join the scope as an
optional parameter without every existing call site changing, and it means no code path can hold a
`CultureTag` that means nothing. A non-empty value is a BCP-47 language tag, and `TryParse` accepts
exactly what `CultureInfo.GetCultureInfo` will later resolve — Platform stores the tag and never the
`CultureInfo`, because the tag is what a column can hold and what survives a process boundary.

**`TraceContext` exposes its own `TraceId` and no `Correlation` member.** The previous derivation
derived the correlation from the stored trace context, and the design has since falsified that: it
is right for exactly one hop, because in a dispatched handler the ambient trace is the new linked
trace, not the origin's. Correlation is its own persisted value — see the outbox row — and the two
part company only across outbox dispatch.

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

**Accessing `Value` on a failure, or `Error` on a success, throws.** That is one of exactly three
places an exception is correct: it is a defect in the caller, not a runtime condition.

**`PlatformContractViolationException` is the second**, and it exists because the design requires
enqueue to *throw* a named error when there is no ambient transaction, no ambient operation scope,
or no registration binding the event type to a stable name. It carries a `PlatformError` so the code
is stable and enumerable rather than a message string.

**`PlatformStartupException` is the third, and it is a different kind of thing from the other two.**
Both of those are defects at a call site; this one is a fatal condition at host build time. The
design says "aborts startup with a named error" of nine separate conditions — a missing setting, an
inconsistent pair, a cyclic module graph, an external dependency in a liveness check, background
work declaring no role, a probe port already bound — and every one of them produces a
`PlatformError` value. A value is not throwable, so until this existed there was nothing for a host
to abort *with*, and the brief's second CI assertion asserts exactly that abort. Startup is the one
place a `Result` cannot serve: `AddPlatformWebHost` returns the builder, the failure surfaces at
build or start, and the runtime's own contract there is an exception. See *Hosting — startup
failure*.

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
    TraceContext Trace { get; }
    CultureTag Culture { get; }
}

public interface IOperationScopeFactory
{
    IOperationScope Begin(
        TenantId tenant,
        ClaimsPrincipal? principal,
        CultureTag culture = default);

    IOperationScope Begin(
        TraceContext established,
        CorrelationId correlation,
        TenantId tenant,
        ClaimsPrincipal? principal,
        CultureTag culture = default);
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

public interface ICurrentCulture
{
    CultureTag Current { get; }
}
```

**The scope carries five members, and trace context is the fourth.** The row demands a traceparent,
and the sanctioned explicit-scope path — a seeder or migrate-mode utility opening its scope in one
line — had no stated source for one until the design made the scope primitive establish it.

**The two `Begin` overloads are the two establishment cases.** The first is **origination**: a scope
opened with nothing inbound starts a real root trace through the trace-context contract, and the
correlation *is* that root's trace-id — the true statement that this scope is the origin, the same
claim an inbound request with no traceparent makes. The second takes both values explicitly, because
its two callers already hold them: Hosting passes the adopted-or-minted request context with the
correlation equal to its trace-id, and Persistence's dispatcher passes the **new linked trace** with
the correlation **from the row's correlation column** — the one boundary where the two values are
permitted to differ. What stays rejected is the *implicit* version: a scope nobody visibly opened,
minting an origin nobody chose.

**`IOperationScopeAccessor.Current` is the only member that can be null**, and it is what makes
"there is no ambient scope" detectable. The four accessors are meaningful only inside a scope:
outside one they throw `PlatformContractViolationException` with `NoAmbientOperationScope`, which is
what keeps "correlation is always present, tenant is always present" true as written rather than
quietly returning a default.

**`ICurrentCorrelation.Current` is a `CorrelationId`, not a `TraceContext`.** They are the same value
everywhere except across outbox dispatch, where the trace changes and the correlation does not — so
the accessor returns the value that does not change, and it propagates unchanged through any depth
of derived events.

**`ICurrentTenant.Current` is non-nullable and in D3 always returns `TenantId.Implicit`.** Nothing
resolves a tenant from host, header or claim — the brief's binding non-goal. The interface exists so
the column has a supplier, not so tenancy can be turned on.

**`ICurrentPrincipal.Current` is nullable and frequently null.** Identity is D5; a worker dispatching
a message has no principal and must not be given a fabricated anonymous one.

**`ICurrentCulture.Current` is non-nullable and defaults to `CultureTag.Invariant`, and in D3
Platform never resolves it.** Nothing reads `Accept-Language`, a header, a claim or a stored
preference — that is D4's, with Notifications. The interface exists so the outbox column has a
supplier, exactly as `ICurrentTenant` exists so the tenant column has one. **The difference from
tenancy, and the reason this is worth carrying now rather than later:** a product *may* set culture
explicitly when it opens a scope, whereas the tenant is pinned to `TenantId.Implicit` by a binding
non-goal. So the value is useful the day a consumer has two languages, without D3 having built
localization.

**Why culture is a scope member at all, when the runtime already has `CultureInfo.CurrentCulture`.**
That ambient is a thread and async-flow property, and dispatch crosses neither — the row is written
in the web host and dispatched by the worker, in a different process, minutes later, under whatever
culture that process was started with. A value that has to survive that boundary has to be *on the
row*, and a value on the row needs a supplier at enqueue that is not a thread static. The runtime's
ambient stays the right thing to *render* with; it is the wrong thing to *carry* with.

**`IClock.UtcNow` always has `Offset == TimeSpan.Zero`.** Every persisted instant originates here,
and so does every instant bound as a SQL comparand, so a fake clock controls every timestamp in the
system and no evaluation of eligibility, claim expiry or lease expiry reaches the database clock.

### Abstractions — trace-context contract

```csharp
public interface ITraceContextCodec
{
    bool TryParse(string traceParent, string? traceState, out TraceContext result);

    ITraceHandle StartRoot(string activityName);

    ITraceHandle StartLinked(TraceContext origin, string activityName);
}

public interface ITraceHandle : IDisposable
{
    TraceContext Context { get; }
}
```

**Parse, root-start and link are Observability's operations declared in Abstractions**,
because two packages with no edge to Observability perform them: Persistence stamps the row, reads
it back, and starts the linked trace per dispatched message, and the operation-scope primitive's
origination path starts a root. `StartLinked` starts a **new** trace linked to the stored one and
honours the origin's sampling flags; it never continues the origin's trace. `StartRoot` is
origination, not fabrication — the scope that calls it *is* the origin.

**There is no format-current operation, and the reason is structural rather than an omission.** A
previous derivation carried `FormatCurrent()`, on the reasoning that stamping a row needs the
current context as a `traceparent` string. It does not: both handles above return the
`TraceContext` they established, the scope stores it as its fourth member, and every stamping site
reads it from there. So no caller can exist that holds an ambient trace it cannot already read
back — which is what the fourth member bought. Adding the member later is additive if a consumer
ever wants to format a context this contract did not establish.

**Both handles expose the established `TraceContext`** because the caller needs it: the dispatcher
populates the scope's fourth member from it, which is what makes a follow-up row's stored
traceparent the link's — while the correlation column keeps the origin's value.

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

**A module is composed before the container exists**, so it must be registered as a type or an
instance ahead of the standard registration call, and a type registration needs a public
parameterless constructor — nothing can be injected into one. A factory registration or a
constructor requiring arguments aborts startup with `HostStartupError.Registration`.

### Abstractions — event and handler contracts

```csharp
public interface IIntegrationEvent;

public interface IIntegrationEventHandler<in TEvent> where TEvent : IIntegrationEvent
{
    Task<Result<HandlerError>> HandleAsync(TEvent @event, CancellationToken cancellationToken);
}
```

**`IIntegrationEvent` is a marker and carries no `TypeName`.** The stable name is supplied by an
explicit registration call, because dispatch must get from a stored string to a CLR type in order to
deserialize and has no instance to ask — the instance is what deserialization produces. An instance
member could not answer the question that matters.

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

**A report enumerates every registered check, at either detail level.** That is what keeps the
persistence-less host honest: split detection, backlog age, poison visibility and the migration
comparison are all contributed by Persistence, a host composed without it has none of them, and an
absent check must read as absent in the body rather than being indistinguishable from a passing one.

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

    Task TickAsync(CancellationToken cancellationToken);
}
```

**The contract is tick-shaped, and that is what makes Testing's determinism providable.** One
invocation of `TickAsync` is one tick — a dispatch pass under its budget, one prune batch, one
heartbeat — and **Hosting owns the timers** that invoke ticks on the declared interval. A loop that
hid its schedule inside itself would be a loop Hosting could not run in the role it declares, and no
fake clock drives a real timer — determinism needs the schedule and the clock separated, so the test
host replaces the one and controls the other.

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
    public static HealthCheckName OutboxPendingCount { get; }
    public static HealthCheckName OutboxPoisonCount { get; }
    public static HealthCheckName PendingMigrations { get; }
}

public static class PlatformTelemetry
{
    public const string ActivitySourceName = "SubZeroDev.Platform";
    public const string MeterName = "SubZeroDev.Platform";
}
```

**These names are public surface, not implementation detail.** They appear in the probe body an
operator reads and are the handles `RunBackgroundWorkOnceAsync` takes, so leaving them to the first
implementer would set them by accident. `Prune` is one registration covering all three retention
windows — processed rows, poisoned rows and dead host registrations. `OutboxPendingCount` is the
condition the fifth review added: pending rows are unbounded by decision, and the count on the
always-on surface is the bound that exists.

**The telemetry source names are the provider-neutral seam.** Persistence creates a child activity
around every `IUnitOfWork.ExecuteAsync` transaction through `PlatformTelemetry.ActivitySourceName`,
for both providers, with database provider and operation only — never SQL text, parameter values or
a connection string. Observability subscribes to that source and to the meter; Persistence does not
reference an OpenTelemetry or Serilog package.

**`MeterName` is a reserved name with no publisher in D3, and that is stated rather than left to be
discovered.** No Platform code constructs a `Meter` or an instrument; Observability subscribes to the
name so that publishing to it later is additive and needs no consumer change. The metrics D3 actually
exports come from the official ASP.NET Core, HTTP and runtime instrumentation. Nothing in this
contract's operational surface depends on a Platform metric — every condition an operator acts on is
a readiness check, which is what makes the reservation honest rather than a promise. `MeterName` is
public for the same reason `ActivitySourceName` is: a consumer wiring its own exporter needs the
name, whether or not Platform has published to it yet.

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
    public required CorrelationId Correlation { get; init; }
    public required CultureTag Culture { get; init; }
    public required int Attempts { get; init; }
    public DateTimeOffset? NextAttemptAt { get; init; }
    public DateTimeOffset? FirstDeferredAt { get; init; }
    public InstanceId? ClaimedBy { get; init; }
    public DateTimeOffset? ClaimedAt { get; init; }
    public DateTimeOffset? ProcessedAt { get; init; }
    public DateTimeOffset? PoisonedAt { get; init; }
    public string? LastError { get; init; }

    public OutboxMessageState State { get; }
    public DateTimeOffset DueAt { get; }
}
```

**`Id` is the identity — a version-7 UUID minted app-side at enqueue from `IClock`.** It exists
before the insert, survives a database restore, sorts in mint order on both providers, and is the
dedupe key at-least-once delivery offers handlers. **Mint order means millisecond order, and the tie
is unspecified** — version 7 carries its time at millisecond resolution, the runtime generator keeps
no counter within a tick, and anything paging by the id must tolerate ties.

**`Correlation` is a column of its own, because the traceparent stops carrying it after one hop.** A
handler that enqueues a follow-up event is the ordinary case, and at that moment the ambient trace is
the dispatch's new linked trace — so the follow-up row's stored traceparent carries the link's
trace-id, not the origin's. The column is stamped from the ambient correlation at enqueue and
propagates unchanged through any depth of derived events, while the stored trace context keeps the
one job it can still do: the link.

**`Culture` is a column for the same reason `Correlation` is, and propagates the same way.** It is
stamped from the ambient scope at enqueue and travels unchanged through any depth of derived events —
a follow-up raised by a handler still knows which language the originating actor was using, because
that fact is not recoverable anywhere else once the request is over. **It is the originating culture,
never the recipient's.** A recipient's preferred language is a preference lookup at render time and
belongs to Notifications in D4; the two are different values and collapsing them loses the case that
forced the column, which is a recipient with no user record at all — a shared inbox, an operations
channel, a printer. `CultureTag.Invariant` is a legal and common value and means "the actor expressed
no preference", not "unknown".

**`Sequence` is claim order and nothing else.** It is provider-allocated, and on SQLite its values
are reused after a drain and prune. Nothing downstream may treat it as durable or as an identity;
anything needing a cursor across time uses `Id`.

**`State` is derived, never stored.** The four states are predicates over the two mark columns, and
a discriminator column was rejected as a second source of truth:

| State | Predicate | Prunes on | Counts toward |
|---|---|---|---|
| `Pending` | `ProcessedAt` null, `PoisonedAt` null | never | backlog age, pending count |
| `Processed` | `ProcessedAt` set, `PoisonedAt` null | processed window | nothing |
| `Poisoned` | `PoisonedAt` set, `ProcessedAt` null | poison window | poison count |
| `Discarded` | both set | poison window | nothing |

**`DueAt` is the due predicate made a member**: `NextAttemptAt` when set, `OccurredAt` while it is
null. A row is **eligible** when it is `Pending`, due, and either unclaimed or holding a claim older
than the claim window. Expired claims are eligible by that predicate alone, which is why there is no
separate reclaim pass. The claim query, the deferral re-check and redrive all derive from this one
statement — the instant-format rules exist to make exactly this comparison correct in SQL.

**Backlog age measures time past due, never time since occurred.** Age-since-occurred manufactures
"worker down" out of three routine states — a deferred row during an upgrade, a backing-off row
behind a failing handler, and a bulk-redriven row whose `OccurredAt` is days old. A dispatcher that
is working keeps its backlog young *by acting on it*; a dead or mispointed one lets due rows age.

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
    public TelemetryOptions Telemetry { get; init; } = new();
}
```

**`ServiceName` and `ServiceVersion` are derived from the entry assembly when unset**, which is why
neither is `required`.

**`Environment` and `Role` have no setter and are not bindable from configuration.** Environment is
derived from the host — a service must not be able to declare itself production in a file that
shipped from a developer's machine — and the role is fixed by which form of the registration call
the host made.

**`PersistenceProvider` is Core's, because `PlatformOptions` is Core's.** It names which provider a
host is configured for, which makes it a setting rather than part of the provider abstraction.
Persistence depends on Core, so `IProviderCapability.Provider` and `WithProvider` reach it freely;
grouping it under Persistence would have put a required member of a Core record in a package Core
may not reference, and Core → Persistence is an edge the dependency graph forbids.

```csharp
public enum PersistenceProvider { PostgreSql, Sqlite }

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
    public TimeSpan PeerAbsenceGrace { get; init; }

    public TimeSpan PeerLivenessThreshold { get; }
}

public sealed record HealthOptions
{
    public TimeSpan BacklogAgeThreshold { get; init; }
    public long PendingCountThreshold { get; init; }
}

public sealed record HostingOptions
{
    public TimeSpan GracefulShutdownDrainWindow { get; init; }
    public int WorkerProbePort { get; init; }
    public bool WorkerProbeLoopbackOnly { get; init; }
}

public sealed record TelemetryOptions
{
    public string LogDirectory { get; init; } = "logs";
    public Uri? OtlpEndpoint { get; init; }
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
| `Outbox.RetryBackoffCap` | 6 h | positive; **jointly, `>= RetryBackoffBase`** |
| `Outbox.DeferralAge` | 24 h | positive |
| `Outbox.DeferralRetryInterval` | 1 min, fixed — no backoff | positive |
| `Outbox.DispatchTickBudget` | 20 | `>= 1` |
| `Outbox.PruneBatchSize` | 500 | `>= 1` |
| `Outbox.DispatchInterval` | 5 s | positive |
| `Lease.Duration` | 5 min | positive |
| `HostRegistration.HeartbeatInterval` | 15 s | positive |
| `HostRegistration.RetentionWindow` | 7 days | positive |
| `HostRegistration.PeerAbsenceGrace` | 60 s, **rolling** | non-negative; **jointly, at least `HeartbeatInterval`** |
| `HostRegistration.PeerLivenessThreshold` | **derived**, `3 × HeartbeatInterval` | no setter, so the two cannot disagree |
| `Health.BacklogAgeThreshold` | 5 min | positive |
| `Health.PendingCountThreshold` | 100 000 | `>= 1` |
| `Hosting.GracefulShutdownDrainWindow` | 30 s | positive; less than `Outbox.ClaimWindow` |
| `Hosting.WorkerProbePort` | 5100 | valid port |
| `Hosting.WorkerProbeLoopbackOnly` | `true` | — |
| `Persistence.SqliteBusyWaitBound` | 5 s | positive |
| `Persistence.ConnectionString` | **required, no default** | present; parseable by the selected provider |
| `Telemetry.LogDirectory` | `logs`, resolved beneath the content root | present and non-empty; relative paths resolve beneath the content root |
| `Telemetry.OtlpEndpoint` | null | when set, an absolute HTTP or HTTPS URI |

**Telemetry has deliberately little configuration surface.** `LogDirectory` changes the directory,
not the role-specific `<service>-<role>-.jsonl` filename or its UTF-8 JSON Lines format. Daily and
100 MB rolling, 14-day and 31-file retention, the 10 000-event non-blocking local-output buffer, fixed 10%
root sampling, OTLP HTTP/protobuf and the standard signal paths are D3 policy rather than tunable
properties. A null endpoint starts no exporter and makes no outbound connection. The typed
`Platform:Telemetry` section is the sole D3 source; `OTEL_EXPORTER_OTLP_*` is not also consumed.

**`PeerAbsenceGrace` is a rolling measure on the observing host's clock from the absence first being
seen, never a startup-scoped exemption.** A startup grace cannot cover the case the setting exists
for: the surviving web host watching a routine worker restart is long past its own startup.

**It has a floor of one `HeartbeatInterval`, which "non-negative" alone did not give it.** A grace
shorter than a heartbeat degrades `PeerHost` on a host that is working perfectly: the grace elapses
before the peer's next beat can possibly land, so the surface reports a split that a single interval
would have resolved. Zero is the worst case and was legal under the previous wording — it turns a
rolling grace into no grace at all, on the one surface this design elected as always-on. Validated
jointly, and named as `InconsistentSettings` because the constraint belongs to the pair rather than
to either value.

**The prune interval is not a setting either, and it is the only cadence that is not.** One hour,
fixed: the three windows prune runs against are hours to days wide, and no latency depends on it the
way it depends on `Outbox:DispatchInterval`. A tick issues one bounded delete per target, so
`Outbox:PruneBatchSize` and that interval together fix the drain rate — see *Settings inventory* in
[`10-design.md`](10-design.md), where both the value and its consequence are recorded.

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

### Hosting — startup failure

```csharp
public sealed class PlatformStartupException : Exception
{
    public PlatformError Error { get; }
}
```

**Every "aborts startup with a named error" in this contract means this exception carrying that
error.** It is thrown at host build or start, never from a request. `HostStartupError` wraps the
cause — a `ConfigurationError`, a `ModuleGraphError`, a registry's rejection — so the inner error's
name and constraint survive to the operator, which is the property that makes a startup message a
feature rather than a nicety for the brief's stated audience.

### Hosting — error envelope

```csharp
public sealed record ErrorEnvelope(string Code, CorrelationId Correlation);
```

**Two fields, and the design determines exactly these two.** The envelope carries a stable error code
and the correlation identity, **never exception text and never payload content**. The correlation is
what ties it to the log line that does carry the detail — which is the whole reason the design
insisted on a single greppable value. The wire format is resolved at [Unresolved 3](#unresolved).

---

## Persisted schemas

Logical column types map per provider exactly as the design's table states. Names below are logical.

**Two encoding rules bind every table here and every product table, on both providers.** Identifier
columns store as a 16-byte blob in **RFC 4122 network byte order**, never the platform `Guid` byte
order, so bytewise blob comparison equals mint order. Instant columns store as **fixed-width
ISO-8601 UTC text, `Z`-suffixed, exactly seven fractional digits, zero-padded and never trimmed**,
and **every instant bound as a SQL parameter is written by the same formatter as the column** — the
platform's default SQLite parameter binding violates all three properties, so pinning only the write
side moves the defect to the other side of the comparison.

### `platform_outbox`

| Column | Logical type | Null | Constraint |
|---|---|---|---|
| `id` | identifier | no | **Primary key.** Version-7 UUID minted at enqueue |
| `sequence` | sequence | no | **Unique.** App-allocated as `MAX(sequence) + 1` on SQLite (the primary key is `id`, so no rowid alias is available), a `BIGINT` identity on PostgreSQL; claim order only, values reusable after prune on SQLite |
| `occurred_at` | instant | no | |
| `type` | text | no | Non-empty |
| `payload` | payload | no | |
| `tenant` | tenant | no | Defaults to the all-zero sentinel |
| `trace_parent` | text | no | Complete `traceparent` including trace flags |
| `trace_state` | text | yes | W3C `tracestate` when the origin carried one |
| `correlation` | text | no | The origin's trace-id at any depth; stamped from the ambient correlation at enqueue |
| `culture` | text | no | The originating BCP-47 tag; empty is invariant; stamped from the ambient culture at enqueue |
| `attempts` | integer | no | Default 0, `>= 0` |
| `next_attempt_at` | instant | yes | Null means due at `occurred_at` |
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
are legal and each names a state — and **discard alone may produce the both-set state**, which is
what lets it keep meaning an operator decision. The dispatch-state writes that could otherwise race
their way into it are conditional on the live claim; see *Public signatures*.

**Migration story.** New table, no existing data. Created empty by the Persistence module's first
migration.

**Payload shapes change additively or not at all.** New optional fields only — never a rename, a
removal or a change of meaning. A breaking change is a new event under a new stable `type`, with the
old handler retained until the old rows drain. A backlog days deep is this design's normal shape, so
an upgrade that changes what a `type` means is dispatching against history.

**Pending rows are unbounded, by decision rather than oversight.** No retention window applies —
pruning an undispatched row is dropping a committed write — and no backpressure applies either:
enqueue is inside the caller's transaction, so refusing it fails the domain write with it. The bound
that exists is the operator, acting on the pending count and backlog age readiness conditions.

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
schemas, nothing in the mechanism enforces it, so the provider contract tests assert it directly,
on both providers, against the applied schema.

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

**`ISettingsFingerprint.Compute` is specified to the byte, because agreement between two processes
is the whole of its value.** A prose description that two implementations could follow differently
would reintroduce exactly the permanent false mismatch it exists to prevent, and this interface is
public surface a third party may reimplement.

Input to the digest, in order:

1. The literal ASCII bytes `szdfp1`, the format version. It is inside the hashed input, so a future
   change to this encoding is a visible break rather than a silent one.
2. Every `[Fingerprinted]` property reachable from `PlatformOptions`, as an entry, **ordered by
   ordinal comparison of the path's UTF-8 bytes** — never by reflection order, which
   `Type.GetProperties()` does not guarantee.

Each entry is exactly:

```text
uint32BE(byteLength(pathUtf8)) ‖ pathUtf8 ‖ presenceTag ‖ [ uint32BE(byteLength(valueUtf8)) ‖ valueUtf8 ]
```

- `path` is the **configuration path** — `Outbox:ProcessedRetention` — the same string a startup
  error names, so a fingerprint and an error message speak one language.
- `presenceTag` is one byte: `0x00` for a null value, after which **no length and no value follow**;
  `0x01` for a present value, after which both do. This is what keeps null distinguishable from the
  empty string, which a length of zero alone would not.
- Lengths are **byte counts of the UTF-8 encoding**, not character counts, as unsigned 32-bit
  big-endian.
- Values render culture-invariantly: `TimeSpan` as `"c"`, `double` as `"R"`, integers in decimal with
  no separators or sign for non-negative values, `bool` as `true` or `false`, an enum as its declared
  name with its declared casing, a string as itself.

The digest is **SHA-256** over that byte sequence, rendered as **64 lowercase hex characters**. The
length prefixes are what make the encoding injective: without them `a=1,b=23` and `a=12,b=3` could
hash identically, and a fingerprint that can collide on distinct settings silently reports agreement
that does not exist.

Why it is stated here rather than left to the implementation is in
[`90-decisions.md`](90-decisions.md), with the three traps it is built to defeat.

### Persistence — transaction boundary

```csharp
public enum TransactionIntent { ReadOnly, Write }

public interface IUnitOfWork
{
    Task<Result<TransactionError>> ExecuteAsync(
        TransactionIntent intent,
        Func<CancellationToken, Task> work,
        CancellationToken cancellationToken);

    Task<Result<T, TransactionError>> ExecuteAsync<T>(
        TransactionIntent intent,
        Func<CancellationToken, Task<T>> work,
        CancellationToken cancellationToken);
}

public interface IAmbientTransaction
{
    TransactionIntent Intent { get; }
    DbConnection Connection { get; }
    DbTransaction Transaction { get; }
}

public interface IAmbientTransactionAccessor
{
    IAmbientTransaction? Current { get; }
}
```

**The ambient transaction is one connection, mechanically.** Per-module contexts made the phrase
ambiguous — "the caller's transaction" spans contexts that would each open a connection by default,
and two connections is two transactions: the domain write and its outbox rows committing separately
is the partial write the outbox exists to make impossible. The unit of work therefore owns the
connection and the transaction, and **every participant — the product module's context and
Platform's stores alike — enlists through `IAmbientTransactionAccessor` against that one
connection**. The outbox store never opens its own. Enqueue's required ambient transaction is this
pair, and nothing else satisfies it.

**The unit of work owns the lifetime; a participant borrows it.** `Connection` and `Transaction` are
exposed because §2 has Persistence refuse to impose a repository pattern — a product using Dapper or
raw ADO for its own tables cannot join the ambient transaction without both in hand, and
encapsulating enlistment would quietly restrict transactional product writes to one data-access
library. What that exposure costs is a live handle a participant could commit, roll back or dispose,
so the rule is stated rather than assumed: **a participant enlists and does nothing else with the
lifetime.** Commit and rollback happen exactly once, in `ExecuteAsync`, which is what makes the
domain write and its outbox rows atomic.

**`TransactionIntent` is a parameter because no implementation can infer it.** "A transaction that
will write begins immediate" is only actionable if the caller says which kind it is opening, and the
deferred-then-upgrade shape is the one case the rule exists to prevent. Treating every transaction
as a writer would be safe and would make the rule unfalsifiable.

### Persistence — outbox

```csharp
public interface IOutboxWriter
{
    OutboxMessageId Enqueue<TEvent>(TEvent @event) where TEvent : IIntegrationEvent;
}

public sealed record EventHandlerRegistration(
    EventTypeName Type,
    Type EventType,
    Type HandlerType);

public interface IEventHandlerRegistry
{
    Result<EventHandlerRegistrationError> Register<TEvent, THandler>(EventTypeName type)
        where TEvent : IIntegrationEvent
        where THandler : class, IIntegrationEventHandler<TEvent>;

    bool TryResolve(EventTypeName type, out EventHandlerRegistration registration);

    bool TryResolve(Type eventType, out EventHandlerRegistration registration);

    IReadOnlyList<EventHandlerRegistration> Registered { get; }

    void Freeze();
}

public static class PlatformEventHandlerExtensions
{
    public static IServiceCollection AddPlatformEventHandler<TEvent, THandler>(
        this IServiceCollection services,
        EventTypeName type)
        where TEvent : IIntegrationEvent
        where THandler : class, IIntegrationEventHandler<TEvent>;
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

public interface IModuleMigration
{
    string Name { get; }

    Task ApplyAsync(DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken);
}

public interface IModuleMigrationSource
{
    ModuleName Module { get; }

    IReadOnlyList<IModuleMigration> Migrations { get; }
}

public interface IMigrationRunner
{
    Task<Result<IReadOnlyList<ModuleMigrationStatus>, MigrationError>> GetStatusAsync(
        CancellationToken cancellationToken);

    Task<Result<MigrationError>> ApplyAsync(CancellationToken cancellationToken);
}
```

**`AddPlatformEventHandler` is the module-composition form of a handler registration.** A module
only receives `IServiceCollection` while it composes; the runtime `IEventHandlerRegistry` does not
exist until the host starts. The extension therefore records the same name–event–handler triple and
registers the handler type for dependency injection. Startup applies the recorded triples to the
registry and freezes it; only the worker constructs handlers, preserving the web role's declarative
registration without importing worker-only constructor dependencies.

**A module's migrations reach the runner by the same route background work and health checks
do** — plain dependency-injection registration, collected as `IEnumerable<IModuleMigrationSource>`.
Neither `IMigrationRunner.ApplyAsync` nor `RunPlatformMigrateModeAsync` takes a migration list as a
parameter, so without a discoverable contribution point a module would have no way to state what its
history contains; `IPlatformModule` itself carries no migration member, the same way it carries no
health-check or background-work member; the check and work contracts already solved this exact
problem, and this is that solution applied a third time. **`Name` is the ordering key within one
module's history** — applied in ordinal string order, which is what makes "either order across
modules, one order within a module" a property of the mechanism rather than of discipline. A module
registers one `IModuleMigrationSource` naming every migration it owns; `ApplyAsync` receives the
connection and transaction the runner already opened, because the runner — not the migration —
owns the migration-history bookkeeping and the provider-native lock.

**The registration is declarative, and each role validates the half it runs.** Both hosts register
the triple — the web host must, in order to enqueue — but a registration is a statement, not a
resolution: the web host records the handler *type* without ever constructing it, and the handler's
constructor dependencies resolve and validate only in the role that dispatches. Name uniqueness and
one-handler-per-Type check identically in both roles, off the declaration alone; a handler that
cannot be constructed is a named **worker** startup failure and no failure at all in the web role.
The registry legitimately holds two names for two CLR types that mean successive versions of one
event — the shape the additive-payload rule assumes.

**The serialiser is `System.Text.Json` with a Platform-pinned options instance that is not
injectable, and there is no extension point.** Four properties are the durable format, not
preferences: unmapped members are ignored in both directions, enums serialize as strings, property
naming and null handling are Platform's, and number handling is fixed. The converter escape hatch an
earlier derivation carried is **cut** — a converter is a dependency-injection registration the
settings fingerprint cannot see, so converter drift between the two hosts of a half-upgraded
installation is exactly the silent format divergence pinning exists to remove. A payload is what
`System.Text.Json` handles natively under the pinned options, or it is a different payload.

**`Enqueue` returns the id and is synchronous** because it does not write — it looks up the stable
`EventTypeName` for `TEvent`, mints a version-7 UUID from the clock, stamps tenant, trace context,
correlation and culture from the ambient scope, enlists in the ambient transaction, and the write
happens on commit. The id is loggable and returnable before the insert, which is what makes it a
usable dedupe key.

**`Enqueue` throws `PlatformContractViolationException` on three conditions**: no ambient
transaction, no ambient operation scope, and an event type that was never registered. The provider
design requires the contract tests to assert the first two, and the third is listed with them
because the same call site produces it. The alternatives are worse than a throw: a nullable
trace context admits rows whose correlation appears nowhere upstream, and an implicitly minted scope
fabricates a traceparent that dispatch will faithfully rebuild — a fiction indistinguishable at read
time from a real origin. A call site that genuinely has only the enqueue opens a transaction and a
scope around it, two explicit lines that state the intent. The third condition has nothing to write
without the name.

**Redrive is a conditional update that resets the dispatch state whole**: it clears the poison mark,
`attempts`, `first_deferred_at` and the claim columns, **and sets `next_attempt_at` to now** — a
poisoned row still carries whatever next attempt the final backoff wrote, hours ahead at the cap,
and a redrive that left it would report success and deliver nothing for hours. Now rather than null
keeps the redriven row's past-due age measured from the recovery, not from an `occurred_at` days
old. It applies **only while the row is still in the poisoned state as the predicate table defines
it** — so racing the prune pass returns `NotFound` rather than silent nothing, and a row someone
already discarded returns `NotPoisoned` rather than being resurrected into one that can never
deliver.

**Per-id redrive and discard return an outcome per id, not a count.** "One of the forty rows you
named was pruned" is a result, not a failure of the operation.

**Both operations exist per row and in bulk by Type**, because a violated payload rule poisons in
bulk and the recovery must not be a thousand hand-invocations. **No endpoint or console ships in D3
to invoke them**; the sample demonstrates calling them.

**`ILeaseHandle.RenewAsync` returning a failure obliges the holder to abort.** The lease is an
optimisation against duplicate work, not a mutual-exclusion primitive: a holder can stall past its
expiry while its work continues, and nothing fences it. Leased work must be idempotent, and
non-idempotent work does not belong under a lease at all.

**`ApplyAsync` is migrate mode's operation and its exclusion is the provider-native migration lock,
never the lease.** An earlier derivation guarded it with the lease, which cannot do this job: the
lease table is created by the very migration migrate mode is about to apply, so the guard is absent
on exactly the run competing deploy scripts are most likely to race — and a lease expires, so a
stalled migrator is unfenced while its DDL still lands. The native lock is connection-scoped, which
closes both holes at once: no table, so no bootstrap ordering; released by the provider when the
holding process dies, so no expiry window. A second concurrent invocation **fails fast** with
`MigrationError.Locked` — on SQLite that means at `Persistence:SqliteBusyWaitBound`, the same setting
that bounds every other write's wait for the single write lock, because acquiring this lock *is* that
write. It cannot be zero: `Microsoft.Data.Sqlite` reads a zero timeout as *wait forever* rather than
as SQLite's own *fail immediately*, so zero would turn a fail-fast lock into one that never fails.

**One run is one transaction, so a failure rolls the whole run back.** This is a consequence of the
lock rather than a separate choice: on SQLite the exclusion *is* the transaction, so committing each
migration as it applied would release the lock between migrations and let a second invocation
interleave — the race the lock exists to prevent. PostgreSQL could commit per migration and does not,
because two migrate-mode behaviours that differ by provider is the duplication this seam exists to
refuse. The operator-visible consequence is stated rather than discovered: **a failed run leaves the
store exactly as it found it**, and a partially-migrated database is not a state migrate mode can
produce. A migration is applied within a savepoint so its own failure is isolated for reporting; the
savepoint never survives the run's rollback.

### Persistence — the provider seam

```csharp
public enum PruneTarget { ProcessedOutboxRows, PoisonedOutboxRows, DeadHostRegistrations }

public interface IMigrationLock : IAsyncDisposable
{
    DbConnection Connection { get; }
    DbTransaction Transaction { get; }
}

public interface IProviderCapability
{
    PersistenceProvider Provider { get; }

    string FormatInstant(DateTimeOffset instant);
    bool TryParseInstant(string stored, out DateTimeOffset instant);

    byte[] EncodeIdentifier(Guid value);
    bool TryDecodeIdentifier(ReadOnlySpan<byte> encoded, out Guid value);

    string MigrationHistoryTable(ModuleName module);

    Task<Result<IAmbientTransaction, TransactionError>> BeginAsync(
        TransactionIntent intent,
        CancellationToken cancellationToken);

    TransactionError Classify(Exception exception);

    Task<Result<OutboxMessageId?, TransactionError>> StampClaimAsync(
        InstanceId holder,
        DateTimeOffset now,
        TimeSpan claimWindow,
        CancellationToken cancellationToken);

    Task<Result<int, TransactionError>> DeleteBoundedAsync(
        PruneTarget target,
        DateTimeOffset olderThan,
        int batchSize,
        CancellationToken cancellationToken);

    Task<Result<IMigrationLock, MigrationError>> AcquireMigrationLockAsync(
        CancellationToken cancellationToken);

    Task<Result<ConfigurationError>> AssertStartupPreconditionsAsync(
        CancellationToken cancellationToken);
}
```

**The membership rule, so the capability's growth is checkable rather than a matter of taste: a
member belongs here when the two providers must do something *different* to produce the same
observable result.** Everything the providers do identically belongs in a store. That admits the
instant formatter, the identifier encoder, the claim and bounded-delete statements, transaction-begin
mode, the migration history name, **the migration lock** — an advisory lock on PostgreSQL,
an immediate transaction on SQLite — and the startup preconditions — WAL and the busy-wait bound —
and nothing else. `StampClaimAsync` is the statement only, portable by default with PostgreSQL free
to use its locking read underneath; which row to dispatch and what to do with the outcome is policy
and stays in the store. **`Classify` is admitted by the same rule**: what counts as busy, as a
concurrency conflict, or as unreachable is a different exception type and code on each provider,
while what the unit of work does with each is identical.

**`BeginAsync` returns the pair it opened, and `IMigrationLock` exposes the pair it holds.** Both
were previously write-only — success or failure, with no way to read the connection and transaction
back — which made the capability implementable only from inside Persistence, where the internal
casts that recovered them live. That contradicts the seam's stated purpose: this log priced the
capability as expensive precisely because *a third party implementing a provider of their own
compiles against it*, and an extension point that type-checks and then fails at the first cast is
not one. The unit of work owns the returned pair's lifetime and is what makes it **ambient**; the
capability only opens it, which is why `IAmbientTransaction` serves as the return type rather than a
second interface of identical shape.

```csharp
public enum ClaimedWriteOutcome { Applied, ClaimLost }

public enum PoisonAttemptMode { Increment, Preserve }

public interface IOutboxStore
{
    Task<Result<TransactionError>> InsertAsync(
        OutboxMessage message, CancellationToken cancellationToken);

    Task<Result<OutboxMessage?, TransactionError>> ClaimNextAsync(
        InstanceId holder, CancellationToken cancellationToken);

    Task<Result<ClaimedWriteOutcome, TransactionError>> MarkProcessedAsync(
        OutboxMessageId id, InstanceId holder, CancellationToken cancellationToken);

    Task<Result<ClaimedWriteOutcome, TransactionError>> RecordFailureAsync(
        OutboxMessageId id, InstanceId holder, string error, DateTimeOffset nextAttemptAt,
        CancellationToken cancellationToken);

    Task<Result<ClaimedWriteOutcome, TransactionError>> PoisonAsync(
        OutboxMessageId id, InstanceId holder, string error, PoisonAttemptMode attemptMode,
        CancellationToken cancellationToken);

    Task<Result<ClaimedWriteOutcome, TransactionError>> DeferAsync(
        OutboxMessageId id, InstanceId holder, DateTimeOffset nextAttemptAt,
        CancellationToken cancellationToken);

    Task<Result<ClaimedWriteOutcome, TransactionError>> ReleaseClaimAsync(
        OutboxMessageId id, InstanceId holder, CancellationToken cancellationToken);

    Task<Result<IReadOnlyList<OutboxAdministrationResult>, TransactionError>> RedriveAsync(
        IReadOnlyCollection<OutboxMessageId> ids, CancellationToken cancellationToken);

    Task<Result<int, TransactionError>> RedriveByTypeAsync(
        EventTypeName type, CancellationToken cancellationToken);

    Task<Result<IReadOnlyList<OutboxAdministrationResult>, TransactionError>> DiscardAsync(
        IReadOnlyCollection<OutboxMessageId> ids, string reason,
        CancellationToken cancellationToken);

    Task<Result<int, TransactionError>> DiscardByTypeAsync(
        EventTypeName type, string reason, CancellationToken cancellationToken);

    Task<Result<IReadOnlyList<OutboxMessage>, TransactionError>> ListPoisonedAsync(
        int limit, CancellationToken cancellationToken);

    Task<Result<DateTimeOffset?, TransactionError>> OldestPendingDueAsync(
        CancellationToken cancellationToken);

    Task<Result<long, TransactionError>> PendingCountAsync(CancellationToken cancellationToken);

    Task<Result<long, TransactionError>> PoisonedCountAsync(CancellationToken cancellationToken);

    Task<Result<int, TransactionError>> PruneAsync(
        PruneTarget target, DateTimeOffset olderThan, int batchSize,
        CancellationToken cancellationToken);
}

public interface ILeaseStore
{
    Task<Result<bool, TransactionError>> TryAcquireAsync(
        BackgroundWorkName name, InstanceId holder, DateTimeOffset expiresAt,
        CancellationToken cancellationToken);

    Task<Result<bool, TransactionError>> TryRenewAsync(
        BackgroundWorkName name, InstanceId holder, DateTimeOffset expiresAt,
        CancellationToken cancellationToken);

    Task<Result<TransactionError>> ReleaseAsync(
        BackgroundWorkName name, InstanceId holder, CancellationToken cancellationToken);
}

public interface IHostRegistrationStore
{
    Task<Result<TransactionError>> UpsertAsync(
        HostRegistration registration, CancellationToken cancellationToken);

    Task<Result<IReadOnlyList<HostRegistration>, TransactionError>> ListLiveAsync(
        DateTimeOffset heartbeatSince, CancellationToken cancellationToken);

    Task<Result<TransactionError>> DeleteAsync(
        HostRole role, InstanceId instance, CancellationToken cancellationToken);
}
```

**One store per Platform-owned table, and one implementation of each** — parameterised by
`IProviderCapability`, not written twice. §2 has Persistence refuse to impose a repository pattern,
so these cover the three tables Platform both defines and stores and never product data.

**The policy is what is not duplicated.** Which row to claim, whether a failure consumes an attempt,
when a row is poisoned rather than deferred — two copies of that is the objection this design
already raised against a dialect-specific claim, applied to the surrounding logic rather than the
statement.

**`PoisonAttemptMode` makes the two poison paths explicit at the store boundary.** `Increment` is
used when a `HandlerError` reaches poison — a permanent failure or the final transient attempt — and
increments `attempts` exactly once. `Preserve` is used when a `DispatchError` ages past its deferral
window and leaves `attempts` unchanged. The mode is named rather than inferred from the `error`
string: the stored error is diagnostic data, not a control protocol, and renaming an error code must
not change the row transition.

**Every dispatch-state write is conditional on the live claim, which is why each takes the holder
and returns `ClaimedWriteOutcome`.** `MarkProcessedAsync`, `RecordFailureAsync`, `PoisonAsync`,
`DeferAsync` and `ReleaseClaimAsync` apply only while `holder` still holds an unexpired claim on the
row. `ClaimLost` means the write was a **no-op** — counted as evidence of duplicate delivery, never
escalated, because losing a claim mid-flight is the at-least-once window working as priced. Without
this, a stalled dispatcher completing after a reclaim-and-poison manufactures the both-set state —
an operator disposition nobody made. Discard alone produces that state. `DeferAsync` additionally
stamps `first_deferred_at` when it is unset.

**The outcome is a named type rather than a boolean, for the reason this log already gave once.**
Folding a correctness property into a bool puts it on a value a caller can get wrong instead of on
the type the dispatcher switches over, and the obvious misreading here — *the row wasn't there* —
turns a lost claim into an apparent success and stops the duplicate-delivery evidence being counted.
Two variants are exhaustive: a claimed row is always pending, and pending rows are never pruned, so
the row cannot vanish underneath its writer. This mirrors `OutboxAdministrationOutcome`, which
already names this class of result — a well-formed operation that did not apply.

**`OldestPendingDueAsync`, `PendingCountAsync` and `PoisonedCountAsync` exist because readiness
needs them and the predicate table decides what they count.** The oldest-due query considers pending
rows only and returns the due instant — `next_attempt_at`, or `occurred_at` while it is null — so
readiness measures **time past due**, never time since occurred; the pending count considers pending
rows only; the poison count excludes discarded rows, because the decision a discarded row was
demanding has been made.

**Every store method self-guards on an absent schema**, returning `TransactionError.Unavailable`
rather than throwing, so a first production run reports degraded with the schema named instead of
turning a known condition into an unhealthy-by-exception.

### Persistence — registration and startup failure

```csharp
public static class PlatformPersistenceExtensions
{
    public static IServiceCollection AddPlatformPersistence(this IServiceCollection services);
}

public sealed class PersistenceStartupException : Exception
{
    public PlatformError Error { get; }
}
```

**Persistence registers itself, and Hosting does not do it.** The dependency graph has no
Hosting → Persistence edge and a host composed without Persistence is a supported shape, so a
product that wants a store makes this call alongside the standard registration call. It is the same
arrangement `AddPlatformObservability` has, minus Hosting also invoking it: one explicit line, not
the bespoke wiring the brief forbids, because nothing about health, readiness, correlation or
migrations requires the consumer to configure anything beyond naming the package it wants.

**`PersistenceStartupException` is a fourth exception, and the graph is why.** Every "aborts startup
with a named error" elsewhere means `PlatformStartupException` — which lives in Hosting, a package
Persistence may not reference. Persistence has its own startup abort to raise (a SQLite file in any
journal mode but WAL), so it needs a type it is allowed to throw. It carries a `PlatformError` on
the same terms as the other three, so the code stays stable and enumerable. A consumer catching
startup failures by type catches both; that cost is the price of the acyclic graph, and it is
cheaper than the edge.

### Hosting

```csharp
public static class PlatformHostExtensions
{
    public static IHostApplicationBuilder AddPlatformWebHost(this IHostApplicationBuilder builder);

    public static IHostApplicationBuilder AddPlatformWorkerHost(this IHostApplicationBuilder builder);

    public static IEndpointRouteBuilder MapPlatformProbes(this IEndpointRouteBuilder endpoints);
}

public static class PlatformMigrationExtensions
{
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

**Hosting runs registered background work on timers it owns**, invoking each registration's
`TickAsync` on its declared interval, in the role each registration declares, without knowing what
any of it is. Hosting does not reference Persistence; a host composed without Persistence is a
supported shape with a smaller readiness surface, and the probe body's enumeration of registered
checks is what keeps that scoping visible.

**Probes are served by Platform's own middleware, in both roles, without either host calling
`MapPlatformProbes`.** The worker binds its own loopback port through the same endpoint code as the
web role, and that middleware answers on it — the standard registration call has to be sufficient
alone. `MapPlatformProbes` exists on the public surface for a host that places the probes within its
own route table instead; calling it stands the middleware down for that host, rather than being what
serves the probes in the first place. A port collision fails startup with a named bind error citing
the setting rather than falling back silently.

**`RunPlatformMigrateModeAsync` returns a process exit status.** It is a one-shot command, not a
third host role.

**It is grouped under this heading and does not ship in Hosting.** Migrate mode needs the migration
runner, which is Persistence's, and Hosting has no edge to Persistence — so the method is declared in
the Persistence package, in a static class of its own — `PlatformMigrationExtensions`, named in the
block above rather than folded into `PlatformHostExtensions`, which an earlier derivation did and
which contradicted this very paragraph — sharing this namespace. That is the same idiom
`Microsoft.EntityFrameworkCore` uses for `AddDbContext`, which extends
`Microsoft.Extensions.DependencyInjection`'s type from a different assembly than the one declaring
it. The call site is unchanged and the grouping above is by capability rather than by assembly, which
is what this document's own preamble says package grouping means. A reader diffing types to files
should expect this one to move.

### Observability

```csharp
public static class PlatformObservabilityExtensions
{
    public static IHostApplicationBuilder AddPlatformObservability(this IHostApplicationBuilder builder);
}
```

Called by both forms of the standard registration call. Exposed separately because Observability is
usable by a consumer that wants telemetry wiring without a Platform host.

The call installs local Serilog and optional OTLP branches behind the standard `ILogger` surface.
Serilog writes mandatory UTF-8 JSON Lines to console and to a file named
`<service>-<role>-.jsonl`, sharing the file safely between instances of one role. Both local sinks
use the same formatter and one 10 000-event asynchronous buffer with `blockWhenFull` disabled. The
file rolls daily and at 100 MB and retains no file older than 14 days and no more than 31 files. The
supported async-sink inspector maintains the exact dropped-event count. File creation, write and
buffer failure cannot fail startup or application work; an emergency console diagnostic is emitted
once on entry to failure or dropping and once on recovery.

When `Telemetry.OtlpEndpoint` is present, the official OpenTelemetry SDK also exports logs, traces
and metrics over OTLP HTTP/protobuf to the base URI's standard `v1/logs`, `v1/traces` and
`v1/metrics` paths. It uses the SDK's bounded batch processors and experimental in-memory retry as
provided by the pinned 1.17.0 packages, never a disk queue. A package upgrade must explicitly
revalidate that experimental feature. Authentication headers, client certificates, per-signal
endpoints and alternate protocols are outside this contract.

Every OTLP resource and every JSONL record carries `service.name`, `service.version`,
`deployment.environment.name` and bounded `subzerodev.host.role`. A log also carries ambient
correlation, tenant, culture and actor when present. `service.instance.id` is not a global resource
attribute. Request correlation is the trace id; dispatch correlation is represented by its span
link and structured logs rather than by a duplicate unbounded span attribute.

Incoming traces honour their upstream sampled flag. A new root HTTP trace uses deterministic 10%
trace-id head sampling. `StartLinked` copies the stored origin's sampled decision into the new linked
dispatch trace through Platform's sampler; all other traces use the official parent-based ratio
sampler. Error- and latency-based retention is collector-side tail sampling and is not promised by
the host.

The fixed, non-injectable redaction processor runs before Serilog's console/file sinks and the OTLP
branch. Non-empty configuration values whose
case-insensitive key segments include `authorization`, `cookie`, `password`, `secret`, `token`,
`api-key`, `connection-string` or `client-certificate` become `[REDACTED]` in structured log
properties and rendered messages, exceptions and nested text, and span attributes and events.
Platform captures no HTTP headers or bodies, event payloads, SQL parameter values or
connection strings.

**Metric labels are not redacted; they are allowlisted, which is the stronger of the two.** Every
exported metric's labels come from a closed set: host role, HTTP method, route
template, status, database provider, and closed outcome or signal enums. Raw path and query,
tenant, correlation, instance, message, event and user identifiers, and arbitrary tag pass-through
are forbidden. A closed set has nowhere for a secret to arrive, so a redaction pass over it would
have nothing to find, and naming redaction as the mechanism here would misdescribe which half
carries the guarantee. In D3 the allowlist governs the instrumentation packages' instruments, since
Platform publishes none of its own.

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

public sealed class FakeCurrentCulture : ICurrentCulture
{
    public CultureTag Current { get; set; }
}

public sealed record CapturedEvent(
    OutboxMessageId Id,
    EventTypeName Type,
    TenantId Tenant,
    CorrelationId Correlation,
    CultureTag Culture,
    DateTimeOffset At);

public interface IEventCapture
{
    IReadOnlyList<CapturedEvent> Enqueued { get; }
    IReadOnlyList<CapturedEvent> Dispatched { get; }
    void Clear();
}

public static class PlatformTestHost
{
    public static IPlatformTestHostBuilder CreateBuilder();
}

public interface IPlatformTestHostBuilder
{
    IPlatformTestHostBuilder WithRole(HostRole role);
    IPlatformTestHostBuilder WithProvider(PersistenceProvider provider);
    IPlatformTestHostBuilder WithSetting(string key, string value);
    IPlatformTestHostBuilder WithServices(Action<IServiceCollection> configure);
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

**`WithServices` is how a test contributes a module, a health check or a background work**, through
the same plain dependency-injection registration the real host collects them by — so a test
exercises the production collection path rather than a parallel one built for tests. Without it the
test host could only run what Platform itself registers, and every criterion needing a
test-owned check or loop would have to abandon `IPlatformTestHost` for a hand-built host — losing
`Clock` and `RunBackgroundWorkOnceAsync`, which are the two members that make those criteria
checkable at all.

**`PlatformTestHost.CreateBuilder` exists because nothing else produced a builder.** A test cannot
`new` an interface, and leaving the entry point unstated would have each test assembly inventing its
own.

**`RunBackgroundWorkOnceAsync` invokes one tick, which is what makes background work deterministic
in tests.** The tick-shaped contract is what makes this possible: the test host owns the schedule,
the fake clock supplies the instants the tick compares against, and no timing-dependent test
contains a wall-clock wait.

**The provider contract tests must assert at least the following**, which the design names
individually. Their invocation surface is resolved at [Unresolved 7](#unresolved); what they assert
is fixed here.

| Assertion | What it catches |
|---|---|
| Identifier blob sort order equals mint order, across a run minted at **distinct clock instants** — the fake clock advances between mints, and no test asserts order within one millisecond | The SQLite `Guid` byte order scrambling a version-7 UUID's time ordering — without a frozen clock making the assertion false while the encoding is right |
| Instant comparison is correct across a sub-second boundary, column **and** bound comparand | A trimming or variable-width writer making due messages ineligible |
| `Id` is unique across a drain, prune-to-empty, insert cycle | SQLite rowid reuse, which is why the sequence is not the identity |
| `Enqueue` throws without an ambient transaction | An outbox row committing apart from its domain write |
| `Enqueue` throws without an ambient operation scope | A row whose correlation appears nowhere upstream |
| `Enqueue` throws for an event type no registration bound to a name | A row stamped with a name nothing can resolve back to a type |
| No foreign key crosses a module boundary, in the applied schema, on both providers | The either-order migration guarantee, which nothing else enforces |
| A claim is granted to exactly one of two concurrent claimants | The portable conditional-update claim, on both providers |
| A dispatch-state write whose claim has been lost returns `ClaimLost` and changes no column | The race that would otherwise manufacture the discarded state without an operator |
| A product write and its outbox rows commit and roll back together when the product enlists against the ambient transaction rather than opening its own connection | The partial write the outbox exists to prevent, reintroduced by the seam between per-module contexts |
| A payload written under one provider deserializes under the other | The format is the serialiser's, not the provider's |
| The suite goes red against a deliberately broken `IProviderCapability` | A suite that has never failed is not evidence, and the capability is the only place a difference is permitted to live |

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
| `NoAmbientOperationScope` | `Enqueue`, or an ambient accessor, is reached with no scope open | No | Fix the call site — a seeder or migrate-mode utility opens a scope explicitly, which starts a real root trace |
| `UnregisteredEventType` | `Enqueue` is called with an event type no registration bound to a stable name | No | Fix the call site — register the type. There is nothing to stamp on the row without the name |
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
| `InconsistentSettings` | Two settings are individually valid and jointly not. Four pairs: poison retention not longer than processed; drain window not shorter than the claim window; **retry backoff cap shorter than its base**; **peer-absence grace shorter than the heartbeat interval** | No | Fails startup, naming both settings |
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
| `DuplicateNameForEventType` | A second `EventTypeName` registers for a CLR event type already bound | No | Fails startup — enqueue could not choose which name to stamp |
| `HandlerNotConstructible` | The handler's constructor dependencies fail to resolve, **checked only in the dispatching role** | No | Fails **worker** startup, naming the handler and the missing dependency. No failure in the web role, whose container never constructs it |
| `RegistryFrozen` | Registration attempted after the host is built | No | Fails startup |

**Name and handler uniqueness are one verdict, checked identically in both roles off the declaration
alone.** Enforcement is at startup only. Enforcing at dispatch as well is the more rigorous reading —
a container can be populated directly and bypass the registry — and was declined for one error path
rather than two. Revisitable if the bypass ever happens.

### Persistence — `TransactionError`

| Variant | Raised when | Retryable | Caller does |
|---|---|---|---|
| `Unavailable` | The database cannot be reached, or its schema is absent. **Includes a connect or command timeout**, which both providers surface as a cancellation rather than as a provider exception | **Yes**, by the caller's own policy — never by Platform | Surfaces an error envelope carrying the correlation identity; readiness checks report degraded citing the cause |
| `Conflict` | A concurrency conflict aborts the transaction | **Yes** | May retry the whole unit of work; outbox rows roll back with the domain write |
| `Busy` | SQLite's busy-wait bound elapsed without acquiring the write lock | **Yes** | Fails the operation normally; under contention this is the visible symptom |
| `Faulted` | Any other failure inside the transaction | No | Surfaces; the rollback is complete |

**No variant's `Detail` carries an exception message.** Every one is a fixed operator-facing string,
because a readiness body renders `Detail` at full detail and invariant 46 admits no exception text
into a probe body. The exception goes to the log, where the correlation ties the two together — the
same division the error envelope already makes.

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
a failure of the operation. A lost claim on a dispatch-state write is likewise an outcome — `ClaimLost` —
counted as duplicate-delivery evidence, never escalated.

### Abstractions — `HandlerError`

Returned by a handler. Both variants consume an attempt.

**It is Abstractions', not Persistence', and the dependency graph leaves no choice.**
`IIntegrationEventHandler<TEvent>.HandleAsync` returns `Result<HandlerError>` and that interface is in
Abstractions, which has no edge to Persistence — so a product writes a handler against Abstractions
alone, which is the property that makes Abstractions a separate package. `DispatchError` below is
genuinely Persistence', because only the dispatcher raises it.

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
| `Failed` | A migration failed to apply | No | Stops, and does not continue to the next module. **The whole run rolls back** — every migration that run had applied, across every module — so the database is left exactly as the run found it |
| `Locked` | Another invocation holds the provider-native migration lock | **Yes**, once the other run finishes | **Fails fast**, exiting non-zero without applying anything. The lock is connection-scoped: it exists on a fresh store and dies with its holder, so there is no expiry window and no bootstrap ordering |
| `Unavailable` | The database cannot be reached | **Yes** | Exits non-zero; the operator retries |
| `HistoryTableCollision` | Two modules' history tables resolve to one name — module names are unique case-sensitively, so `Orders` and `orders` are two legal modules sharing one table | No | **Fails before acquiring the lock and before applying anything**, naming both modules and the table. Sharing a history is silent corruption of what per-module histories provide: each module reads the other's applied list and skips its own migrations as already applied |

### Hosting — `HostStartupError`

| Variant | Raised when | Retryable | Caller does |
|---|---|---|---|
| `Configuration` | A `ConfigurationError` was raised during binding or validation | No | Aborts startup, surfacing the inner error's name and constraint |
| `ModuleGraph` | A `ModuleGraphError` was raised during resolution | No | Aborts startup, surfacing the inner error |
| `Registration` | Any registry rejected a registration | No | Aborts startup, surfacing the inner error |
| `ProbeBindFailed` | The worker probe port cannot be bound | No | Aborts startup, **naming the setting** — the design's own environment puts two products on one server, and a silent fallback port would make the probe surface unfindable |

**Startup aborts; it never degrades.** *Unavailability* with valid configuration is the opposite
case and is not an error here at all — the host starts and reports not ready, because on a
self-hosted box a database thirty seconds behind the application should not need a human.

### Observability

**No error type crosses this boundary.** File failures are absorbed by the non-blocking Serilog
queue, with its supported inspector supplying exact drop counts and one emergency console
diagnostic on failure-or-dropping entry and recovery. OTLP failures are absorbed by the official
bounded batch processors and in-memory retry, then dropped; the pinned SDK exposes no supported
exact dropped-signal counter or queue-transition hook, so Platform promises neither and does not
manufacture one through a custom processor or parsed internal diagnostics. A malformed inbound
`traceparent` yields `false` from `TryParse` and a fresh root, never a rejected request. Collection
never becomes a path by which a caller can fail, so there is nothing for a caller to handle.

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
| 3 | An identifier's SQLite blob encoding is RFC 4122 network byte order, so bytewise blob order equals mint order at millisecond resolution, the tie unspecified | Persistence |
| 4 | Every outbox row is in exactly one of the four states, and every consumer — readiness, prune, redrive — derives its state from the predicate table rather than from a column of its own | Persistence |
| 5 | `claimed_by` is null if and only if `claimed_at` is null | Persistence |
| 6 | `poisoned_at` set implies `last_error` non-null | Persistence |
| 7 | An outbox row is inserted only inside an ambient transaction that also carries its domain write, and only inside an ambient operation scope | Persistence |
| 8 | The ambient transaction is one connection owned by the unit of work; every participant enlists against it, and the outbox store never opens its own on the enqueue path — claim, marks, redrive, discard, the three readiness queries and prune each correctly open their own connection through `capability.BeginAsync`, none running inside a caller's transaction | Persistence |
| 9 | A participant enlists against the ambient transaction and never commits, rolls back or disposes it; commit and rollback happen exactly once, in `ExecuteAsync` | Persistence |
| 10 | Every product table row has a non-null tenant; the value is `TenantId.Implicit` throughout D3 | Persistence |
| 11 | No foreign key crosses a module boundary | Persistence |
| 12 | A dispatched message's ambient context is rebuilt from its row — correlation from the row's correlation column, tenant and culture from the row, principal null — never inherited from the worker | Persistence |
| 13 | Dispatch starts a new trace linked to the stored one, honouring its stored sampling flags; it never continues the origin trace | Persistence |
| 14 | The correlation and culture columns are stamped from their ambient values at enqueue and each propagates unchanged through derived events at any depth | Persistence |
| 15 | `attempts` increases only on a `HandlerError`; no `DispatchError` variant increments it | Persistence |
| 16 | `attempts` never decreases except through an explicit redrive | Persistence |
| 17 | Every dispatch-state write — mark processed, record failure, defer, poison, release — applies only while the writer holds the live claim; a write that lost its claim returns `ClaimLost`, changes nothing, and is counted as duplicate-delivery evidence rather than escalated | Persistence |
| 18 | Discard alone produces the both-marks-set state | Persistence |
| 19 | Redrive applies only in the poisoned state; it clears the poison mark, `attempts`, `first_deferred_at` and the claim columns, and sets `next_attempt_at` to now | Persistence |
| 20 | A claim covers exactly one row, and is granted to exactly one of any two concurrent claimants | Persistence |
| 21 | The dispatcher claims nothing while this host has unapplied migrations | Persistence |
| 22 | A pending row is never pruned; processed rows prune on the processed window, poisoned and discarded rows on the poison window | Persistence |
| 23 | Every background write is bounded — one row per claim and per mark, `PruneBatchSize` rows per prune statement | Persistence |
| 24 | A transaction that will write begins immediate; the SQLite file is in WAL mode or the host does not start | Persistence |
| 25 | Every persistence readiness check self-guards on an absent schema, reporting degraded with the schema named rather than throwing | Persistence |
| 26 | Exactly one handler is registered per `EventTypeName`, and exactly one `EventTypeName` per CLR event type; handler constructor graphs validate only in the dispatching role, and the registry accepts no registration after `Freeze` | Persistence |
| 27 | A stored `type` resolves to a CLR type through the registry alone — never through a runtime type name | Persistence |
| 28 | Payload serialisation is the pinned `System.Text.Json` options — unmapped members ignored in both directions, enums as strings, fixed naming, null and number handling — with no reachable extension point | Persistence |
| 29 | Backlog age measures pending rows only and time past due only; the pending count counts pending rows only; the poison count excludes discarded rows | Persistence |
| 30 | Module order is a topological sort of declared dependencies, ties broken by name, identical across runs on identical input | Core |
| 31 | The health and background-work registries and the module graph accept no registration after `Freeze` | Core |
| 32 | No check declaring `TouchesExternalDependency` is registered as `Liveness` | Core |
| 33 | Both retention settings and the connection string are present, or the host does not start | Core |
| 34 | The settings fingerprint covers exactly the properties marked `[Fingerprinted]`, and two hosts on identical settings compute identical values | Core |
| 35 | `PeerLivenessThreshold` equals three times `HeartbeatInterval` and cannot be set independently | Core |
| 36 | Liveness never evaluates an external dependency | Hosting |
| 37 | Readiness returns success for `Healthy` and `Degraded`, failure only for `Unhealthy` | Hosting |
| 38 | Every host writes its registration to the store it is using; the first successful heartbeat is the registration, and the host deletes its own row on graceful shutdown | Hosting |
| 39 | Background work runs only in a host whose role the registration's `Roles` includes; no product work and no outbox dispatch runs in the web role | Hosting |
| 40 | Hosting owns every background-work timer; a registration exposes a tick and no schedule of its own | Hosting |
| 41 | A request never blocks on telemetry export, a probe, dispatch, or prune | Hosting |
| 42 | A malformed inbound `traceparent` never fails the request | Hosting |
| 43 | Graceful shutdown stops claiming immediately and releases claims it has not started | Hosting |
| 44 | The worker probe binds loopback unless explicitly configured otherwise, and a port collision fails startup naming the setting | Hosting |
| 45 | The probe body is `Full` only on loopback or in the development environment; the status is identical at either detail level, and the body enumerates every registered check | Hosting |
| 46 | `last_error` never crosses a wire; no probe body and no error envelope carries exception text or payload content | Hosting |
| 47 | Peer absence is informational in the development environment; elsewhere it degrades only once it has persisted for the rolling grace window, measured on the observing host's clock from the absence first being seen | Hosting |
| 48 | Telemetry export never propagates a failure to a caller | Observability |
| 49 | No secret appears in any exported log or span attribute — by redaction; and none can appear in a metric label, because labels are allowlisted rather than filtered | Observability |
| 50 | No metric is labelled with an unbounded value. Platform publishes no instrument in D3, so this is asserted against the instrumentation packages' instruments | Observability |
| 51 | Every host writes mandatory role-specific UTF-8 JSON Lines logs to console and file, and a file failure never prevents startup or blocks application work | Observability |
| 52 | With no OTLP endpoint, no exporter starts and no outbound connection is attempted | Observability |
| 53 | Every telemetry resource and JSONL record carries the same service name, service version, deployment environment and bounded host role | Observability |
| 54 | Incoming traces retain the upstream sampling decision; new root HTTP traces use deterministic 10% trace-id sampling; linked dispatch traces retain the stored origin decision | Observability |
| 55 | Platform captures no HTTP headers or bodies, event payloads, SQL parameter values or connection strings as telemetry | Observability |
| 56 | Every unit of work creates one provider-neutral child activity carrying provider and operation only | Persistence |

---

## Unresolved

Values the design did not determine, each of which sets something a future reader would ask "why?"
about. **All seven are now resolved** — 2, 3 and 4 in S1, 6 and 7 in S2, and 1 and 5 ahead of S3, so
that slice implements against a contract with nothing left to invent. Every one has its reasoning,
its rejected alternatives and its cost in [`90-decisions.md`](90-decisions.md); none of it is
restated here.

**A new entry belongs here whenever the design names a concept without naming its construction.**
The section is not finished because it is currently empty of open items — it is the place that
question goes, and an empty list is what it looks like between them.

**Resolved items keep their number and are struck through rather than removed**, because
[`30-slices.md`](30-slices.md) and [`90-decisions.md`](90-decisions.md) both cite these by number and
renumbering would silently break every reference.

1. ~~**The settings fingerprint's canonical form and hash algorithm.**~~ **Resolved ahead of S3:** each
   `[Fingerprinted]` value is keyed by its **configuration path** — the same string an error message
   names, so the two speak one language — then the pairs are **ordinal-sorted by path**, each path
   and value **length-prefixed** so no two different inputs can concatenate to the same bytes, the
   whole preceded by a format version, hashed with **SHA-256** and rendered as lowercase hex.
   Values format invariantly: `TimeSpan` as `"c"`, `double` as `"R"`, enums by name, and a null
   distinctly from an empty string. Sorting by path rather than by reflection order is the load-
   bearing part — `Type.GetProperties()` guarantees no order. **The byte-exact encoding is specified
   beside `ISettingsFingerprint` itself**, which is what an implementer reads; this entry summarises
   the decision and is not the normative form. See [`90-decisions.md`](90-decisions.md).

2. ~~**Upper bounds for `DispatchTickBudget` and `PruneBatchSize`.**~~ **Resolved in S1:**
   `DispatchTickBudget` at 1 000 and `PruneBatchSize` at 5 000, each an order above its default. The
   prune bound is the one that matters — a prune delete is a single statement holding SQLite's write
   lock, where a tick's budget is only serial duration. Both are enforced by the hand-written binder.
   See [`90-decisions.md`](90-decisions.md).

3. ~~**The wire format of the error envelope and the probe body.**~~ **Resolved in S1:** plain
   `application/json`, camel-cased member names, enums as their string names, nulls omitted. The
   envelope is `{code, correlation}` and nothing else, and does **not** reuse problem details. The
   probe body is
   `{status, checks[]}` with each entry `{name, status, detail?, data?}`, the last two present only
   at full detail. See [`90-decisions.md`](90-decisions.md).

4. ~~**The per-check default timeout, and the probe endpoint's overall timeout.**~~ **Resolved.** The
   endpoint's overall timeout was set in S1 at 15 s; the per-check timeouts were set in S2, with the
   first two checks that needed them — `Database` at 5 s, `PendingMigrations` at 10 s. Both are in
   [`90-decisions.md`](90-decisions.md).

5. ~~**How `InstanceId` is derived.**~~ **Resolved ahead of S3:** `Environment.MachineName`, a slash, and
   eight hex characters from `RandomNumberGenerator`, minted once at startup — `homelab-01/7f3a9c2e`.
   Uniqueness and restart-freshness come from the random suffix alone, so neither process-id reuse
   nor a clock adjustment can break either. The role is deliberately **not** encoded:
   `HostRegistration` carries a `role` column, and two homes for one fact is two things that can
   disagree. See [`90-decisions.md`](90-decisions.md).

6. ~~**The naming convention for a module's migration history table.**~~ **Resolved in S2:**
   `platform_migrations_{module}`, the module name in lower snake case. **Two distinct modules can
   collide** — names are unique case-sensitively, so `Orders` and `orders` are both legal and both
   resolve to one table — so `ApplyAsync` rejects a collision with `HistoryTableCollision` before
   applying anything, rather than letting two modules share one history and skip each other's
   migrations. See [`90-decisions.md`](90-decisions.md).

7. ~~**The provider contract tests' invocation surface.**~~ **Resolved in S2:** an abstract base
   class holding every assertion, with one subclass per provider supplying a connection string and
   the few provider-specific schema queries an assertion needs. PostgreSQL's subclass sources its
   store from a container per test class and a database per test; SQLite's from a temp file. A third
   party runs the suite against a provider of their own by adding a subclass. See
   [`90-decisions.md`](90-decisions.md).
