# Slices — the minimal package set (D3)

**Document status:** Slices. Derived from [`10-design.md`](10-design.md) and
[`20-contract.md`](20-contract.md). The contract is authoritative for every signature named below;
**no slice may introduce one that is absent from it.** Where a slice needs a signature the contract
does not carry, it stops and asks for a contract amendment rather than inventing one.

Each slice is vertical: it runs, and its acceptance criteria are observable from outside the code
that satisfies them. **All six packages land together** per the brief, so no slice releases
anything — the release is [S9](#s9--pack-publish-consume-and-the-api-reference).

**Interfaces arrive with their consumers, not whole.** `IProviderCapability`, `IOutboxStore` and
`IEventHandlerRegistry` each span several slices. A member is declared in the slice that implements
and exercises it; declaring the rest with throwing bodies is the half-wired state `AGENTS.md`
forbids. Nothing is added that the contract does not already carry.

**Testing grows with every slice.** It is not a final slice — the fake clock and the test host land
in S1 because S2 cannot be verified without them, and the provider contract tests accumulate
assertions from S2 onward.

## Where the contract's unresolved items get decided

Each needed a `90-decisions.md` entry from the slice that first set a value. **All seven are now
settled**, and this table is the record of where each was taken.

| [Unresolved](20-contract.md#unresolved) | Decided |
|---|---|
| 2 — upper bounds for `DispatchTickBudget` and `PruneBatchSize` | In S1, with the rest of options validation |
| 3 — wire format of the error envelope and the probe body | In S1 |
| 4 — per-check default timeout and the probe endpoint timeout | In S1 |
| 6 — migration history table naming convention per module | In S2 |
| 7 — the provider contract tests' invocation surface | In S2 |
| 1 — the settings fingerprint's canonical form and hash | **Ahead of** S3 |
| 5 — how `InstanceId` is derived | **Ahead of** S3 |

The last two were taken before S3 started rather than during it, deliberately: both are
architectural, and `AGENTS.md` forbids continuing an implementation while that kind of uncertainty
is unresolved. S3 transcribes them.

---

## S1 — Host boots, scopes the operation, answers its probes

Delivers: a sample web host and a sample worker host start from `AddPlatformWebHost()` and
`AddPlatformWorkerHost()` alone — no second call — serve liveness and readiness, populate an
ambient operation scope on every request, return an error envelope carrying the correlation when a
handler throws, and abort startup with a named error on a bad setting.

Touches:
- **Abstractions** — the value types (`TenantId`, `CorrelationId`, `TraceContext`, `InstanceId`,
  `ModuleName`, `HealthCheckName`, `BackgroundWorkName`), `PlatformError`, `Result<…>`,
  `PlatformContractViolationException`, `IClock`, `IOperationScope`, `IOperationScopeFactory`,
  `IOperationScopeAccessor`, `ICurrentTenant`, `ICurrentPrincipal`, `ICurrentCorrelation`,
  `ITraceContextCodec`, `ITraceHandle`, `IPlatformModule`, the health contract, the background-work
  contract, `HostRole`, `HostRoles`, `PlatformHealthChecks`, `PlatformBackgroundWork`,
  `FingerprintedAttribute`
- **Core** — `PlatformOptions` and every sub-record with its binding and validation,
  `ModuleDescriptor`, `IModuleRegistry`, `IHealthCheckRegistry`, `IBackgroundWorkRegistry`, the
  default `IClock`, scope factory and three accessors, `ModuleGraphError`, `ConfigurationError`,
  `HealthCheckRegistrationError`, `BackgroundWorkRegistrationError`
- **Observability** — `AddPlatformObservability` and `ITraceContextCodec` over
  `System.Diagnostics.Activity`, telemetry wiring excluded
- **Hosting** — `PlatformHostExtensions` web and worker forms, `MapPlatformProbes`, the probe
  endpoints and their wire mapping, request scope establishment, `ErrorEnvelope`, the
  background-work timers, graceful shutdown, `HostStartupError`
- **Testing** — `FakeClock`, `FakeCurrentTenant`, `FakeCurrentPrincipal`,
  `IPlatformTestHostBuilder` and `IPlatformTestHost` less `WithProvider` and `Events`
- **samples/** — a web project with one endpoint and a worker project, both in CI

Depends on: none.

Acceptance:
- `AddPlatformWebHost()` is the only Platform call in the sample's `Program.cs`; adding a second
  mandatory call fails the criterion this slice exists to prove.
- Liveness returns HTTP 200 with a body enumerating every registered check by name and status. With
  no Persistence registered the body contains no `PlatformHealthChecks.Database` entry — an absent
  check reads as absent, not as a passing one.
- A readiness check returning `Degraded` yields HTTP 200; the same check returning `Unhealthy`
  yields HTTP 503. Both bodies enumerate the identical entry list.
- The probe body is `Full` on loopback and in `Development`, `Minimal` elsewhere; the aggregate
  status and every entry's status are identical between the two, and only `Detail` and `Data`
  differ.
- Registering a check with `TouchesExternalDependency = true` and `Kind = Liveness` aborts startup
  with `ExternalDependencyInLivenessCheck` naming the check.
- `IModuleRegistry.Resolve` over modules `B` (depends on `A`), `C`, `A` returns `A, C, B`, and
  returns the same order over the same input presented in a different discovery order.
- Two modules named `Orders` abort startup with `DuplicateModuleName`; a module depending on
  `Invoices` when no module provides it aborts with `MissingDependency` naming both; `A → B → A`
  aborts with `CyclicDependency` naming the cycle.
- Omitting `Outbox:ProcessedRetention` aborts startup with `MissingRequiredSetting` naming the
  setting **and the configuration source expected to supply it**. `PoisonedRetention` equal to
  `ProcessedRetention` aborts with `InconsistentSettings` naming both. A
  `Hosting:GracefulShutdownDrainWindow` of `00:10:00` against a `ClaimWindow` of `00:05:00` aborts
  with `InconsistentSettings`. `Outbox:RetryBackoffFactor = 1` aborts with `InvalidSetting` naming
  the constraint. **This is the brief's second CI assertion.**
- An endpoint that throws returns `ErrorEnvelope` with a stable code and the request's correlation,
  and no exception text, stack trace or payload content anywhere in the response.
- Inside a request, `ICurrentCorrelation.Current.TraceId` equals the trace-id of the request's
  established traceparent, `ICurrentTenant.Current` equals `TenantId.Implicit`, and
  `ICurrentPrincipal.Current` is null. Outside any scope all three throw
  `PlatformContractViolationException` carrying `NoAmbientOperationScope`.
- A request carrying a well-formed `traceparent` adopts its trace-id as the correlation. A request
  carrying `traceparent: not-a-traceparent` returns 200 with a fresh root trace, never 400.
- `IOperationScopeFactory.Begin(TenantId.Implicit, null)` outside any request opens a root trace
  whose `TraceContext.TraceId` equals the scope's `Correlation.TraceId`.
- The worker host binds its probes on `127.0.0.1:5100` and is unreachable on the machine's other
  addresses. With 5100 already bound, startup aborts with `ProbeBindFailed` naming
  `Hosting:WorkerProbePort`.
- An `IBackgroundWork` declaring `Worker` ticks in the worker host and never in the web host; one
  declaring `Both` ticks in both; one declaring no role aborts startup with `NoRoleDeclared`.
  Hosting invokes the tick on the declared interval, and
  `IPlatformTestHost.RunBackgroundWorkOnceAsync` invokes exactly one.
- Registration after `Freeze` returns `RegistryFrozen` from all three registries and mutates
  nothing.
- CI starts the sample in both roles in a non-`Development` environment and fails if either process
  exits non-zero or either probe is unreachable.

Out of scope: any database access — the Persistence package does not exist yet, and
`Persistence:ConnectionString` is validated as present and parseable without anything opening a
connection; telemetry exporters, instrumentation and sampling (S8); events, handlers and the outbox
(S4); the settings fingerprint (S3); anything that would make Persistence a de facto requirement of
Hosting.

---

## S2 — Two providers, one connection, per-module migrations

Delivers: the sample's two product modules each own a table carrying the tenant and audit columns;
migrate mode applies both in either order on PostgreSQL and SQLite; and one request writing to both
modules commits or rolls back as a single transaction over a single connection.

**This slice exercises the design's riskiest bets first** — the SQLite instant and identifier
encodings, and the claim that one connection spans per-module contexts. Each is a correctness
property the design says nothing else in the definition of done would catch.

Touches:
- **Persistence** — `IProviderCapability` for both providers (`FormatInstant`, `TryParseInstant`,
  `EncodeIdentifier`, `TryDecodeIdentifier`, `MigrationHistoryTable`, `BeginAsync`,
  `AcquireMigrationLockAsync`, `AssertStartupPreconditionsAsync`), `IMigrationLock`, `IUnitOfWork`,
  `IAmbientTransaction`, `IAmbientTransactionAccessor`, `TransactionIntent`, `IMigrationRunner`,
  `ModuleMigrationStatus`, `ITenantOwned`, `IAuditable`, `ISoftDeletable`, `TransactionError`,
  `MigrationError`, the `Database` and `PendingMigrations` readiness checks
- **Core** — `ConfigurationError.UnsupportedJournalMode`
- **Hosting** — `RunPlatformMigrateModeAsync`
- **Testing** — `IPlatformTestHostBuilder.WithProvider`, and the provider contract-test suite
- **samples/** — two modules with their own tables and migrations, one of them opting into soft
  delete; an endpoint writing to both

Depends on: S1.

Acceptance:
- `RunPlatformMigrateModeAsync` against an empty database creates both modules' tables and one
  migration history table per module, and returns exit status 0. A second run returns 0 and applies
  nothing.
- Applying module `B` before module `A`, and `A` before `B`, produce identical applied schemas on
  both providers.
- The contract test asserting that no foreign key in the applied schema references a table outside
  its owner's model passes on both providers, and goes red when a cross-module foreign key is added
  to a sample module.
- Two concurrent `RunPlatformMigrateModeAsync` invocations against one store: one applies, the other
  exits non-zero with `MigrationError.Locked` having applied nothing — including against a store
  whose schema does not yet exist.
- 100 identifiers minted at distinct clock instants — the fake clock advanced at least one
  millisecond between mints — inserted and read back ordered by the identifier column, return in
  mint order on both providers. The same assertion goes red when the SQLite encoder is switched to
  `Guid.ToByteArray()`. No assertion is made about two identifiers minted within one millisecond.
- Rows stamped `2026-08-03T12:00:00.1000000Z` and `2026-08-03T12:00:00.1500000Z` sort in that order
  on both providers, and `WHERE created_at <= @now` with `@now` bound as
  `2026-08-03T12:00:00.1200000Z` returns exactly the first — the comparand written by the same
  capability formatter as the column. The assertion goes red against a formatter that trims trailing
  zeros.
- One `ExecuteAsync(TransactionIntent.Write, …)` writing a row in each module leaves both rows on
  success and neither on a thrown failure, on both providers — **including when the second module
  writes through a raw `DbCommand` enlisted via `IAmbientTransactionAccessor`** rather than opening
  its own connection. Two connections would leave one row.
- Every product row carries `tenant = TenantId.Implicit`, `created_at` from `IClock` with
  `Offset == TimeSpan.Zero`, and `created_by` null when there is no principal. The soft-delete
  columns exist on the opted-in table and on no other.
- A SQLite file in `journal_mode=delete` aborts startup with `UnsupportedJournalMode`; the same file
  in WAL starts.
- Against a store whose schema is absent, readiness reports `Degraded` with `PendingMigrations`
  naming the absent schema — never `Unhealthy`, and no exception escapes a check. **`Database`
  answers reachability only and stays healthy here**, because a reachable store with no tables is
  reachable, and in D3 Platform owns no table of its own until S3 — so there is nothing for
  `Database` to find missing that `PendingMigrations` does not already report. Corrected during
  S2's reconcile: the original criterion had `Database` citing the same cause, which would make two
  checks restate one verdict and is the second source of truth this design rejects elsewhere.
- Applied migrations the host never registered report `Degraded` naming them as `Surplus`, and the
  host keeps serving.
- The contract-test suite goes red against a deliberately broken `IProviderCapability` — one whose
  instant formatter trims and whose identifier encoder uses platform byte order. **This is the
  brief's fourth CI assertion.**

Out of scope: the outbox table and both `StampClaimAsync` and `DeleteBoundedAsync`, which arrive
with their consumers in S5 and S6; tenant **query filters**, which the brief makes a binding non-goal
for D3; any repository pattern over product tables — Persistence refuses to impose one; a retry
policy over `TransactionError` — Platform retries nothing on the request path.

---

## S3 — Host registration, heartbeat, and the split-brain surface

Delivers: every host records itself in the store it is actually using, and both roles report
degraded when the peer is missing or its fingerprinted settings disagree — the only mechanism that
can see two hosts pointed at different databases.

Touches:
- **Persistence** — `HostRegistration`, `IHostRegistrationStore`, the heartbeat registered as
  `IBackgroundWork` declaring `HostRoles.Both` under
  `PlatformBackgroundWork.HostRegistrationHeartbeat`, the `PeerHost` and `SettingsFingerprint`
  readiness checks
- **Core** — `ISettingsFingerprint`, `HostRegistrationOptions` and the derived
  `PeerLivenessThreshold`
- **Hosting** — `InstanceId` derivation, and deletion of the host's own row on graceful shutdown
- **samples/** — both roles against one store in CI

Depends on: S2.

Acceptance:
- Starting the sample web host writes exactly one row with `role = Web`, its instance, started-at,
  heartbeat-at and fingerprint. Each heartbeat interval updates heartbeat-at and no other column.
- A host started against a store with no schema does not fail: the heartbeat returns
  `TransactionError.Unavailable`, retries at its ordinary interval, and the row appears within one
  interval of migrate mode running. No bespoke startup retry exists.
- With only the web host running in a non-`Development` environment, readiness reports `Degraded` on
  `PeerHost` naming the missing `Worker` role once the absence has persisted for
  `PeerAbsenceGrace`; within the grace it does not. In `Development` it never degrades and the entry
  is informational.
- Advancing the fake clock by `2 × HeartbeatInterval` with no beat leaves the peer live; `4 ×` plus
  the grace degrades. A peer that returns inside the grace degrades nothing.
- Two hosts in one store differing on `Outbox:ProcessedRetention` both report `Degraded` on
  `SettingsFingerprint` naming the peer instance. Two hosts differing only on
  `Outbox:DispatchInterval` do not — it is not `[Fingerprinted]`.
- Two hosts pointed at different SQLite files each report `Degraded` on `PeerHost` while each
  individually serves and each is individually configured correctly.
- Graceful shutdown deletes the host's own row; the surviving peer sees the absence at once and
  degrades only after the rolling grace measured on its own clock.
- `ISettingsFingerprint.Compute` over identical `PlatformOptions` returns identical strings in two
  separate processes. Asserted by reflection over every property of `PlatformOptions`: the value
  changes when a `[Fingerprinted]` property changes and does not change when any other does.
- Two hosts of the same role on one machine hold different `InstanceId`s, and a restarted host holds
  a different one from the row it replaced.
- A dead instance's stale fingerprint never contradicts a live one's — peer and fingerprint checks
  consider live rows only.

Out of scope: pruning dead registration rows — every retention window lands together in S6; any
outbox condition on readiness (S6); reacting to a detected split beyond reporting it, since no host
refuses traffic over a missing peer.

---

## S4 — Outbox enqueue

Delivers: a product writes a domain row and enqueues an integration event in one transaction, and
the row is committed with the domain write or with neither.

**Culture arrives here rather than in S1**, under this document's own rule that a member is declared
in the slice that exercises it. The outbox column is culture's only consumer in D3 — S1 has none, S2
and S3 have none — so declaring the accessor earlier would land it unexercised, which is the
half-wired state this document forbids. S1 shipped without it and its criteria are unchanged.

Touches:
- **Abstractions** — `IIntegrationEvent`, `IIntegrationEventHandler<TEvent>`, `EventTypeName`,
  `CultureTag`, `ICurrentCulture`, and `IOperationScope.Culture`
- **Core** — the culture argument on both `IOperationScopeFactory.Begin` overloads, the scope's
  fifth member, and the `ICurrentCulture` accessor
- **Persistence** — the `platform_outbox` migration with its indexes and check constraints,
  `OutboxMessage`, `OutboxMessageId`, `OutboxMessageState`, `DueAt`, `IOutboxWriter`,
  `EventHandlerRegistration`, `IEventHandlerRegistry`, `IOutboxStore.InsertAsync`, the pinned
  `System.Text.Json` options, `EventHandlerRegistrationError`
- **Testing** — `CapturedEvent`, `IEventCapture.Enqueued`, and `FakeCurrentCulture`
- **samples/** — an event, its handler, its registration, and an endpoint that enqueues

Depends on: S2.

Acceptance:
- An endpoint writing a domain row and calling `Enqueue(new OrderPlaced(…))` leaves one product row
  and one outbox row after commit, and neither after a rollback, on both providers.
- `Enqueue` returns the id synchronously, before the transaction commits, and the committed row's
  `id` equals the returned value.
- `Enqueue` with no ambient transaction throws `PlatformContractViolationException` carrying
  `NoAmbientTransaction`; with no ambient operation scope, `NoAmbientOperationScope`; with an event
  type no registration bound to a name, `UnregisteredEventType`. Nothing is written in any of the
  three cases. **All three are provider contract tests.**
- The stored row carries: `type` equal to the registered literal and unchanged after the CLR class
  is renamed; `tenant` from the ambient scope; `trace_parent` the complete traceparent **including
  trace flags**; `trace_state` when the origin carried one and null otherwise; `correlation` equal
  to `ICurrentCorrelation.Current.TraceId`; `culture` equal to `ICurrentCulture.Current`; `attempts`
  0; and every dispatch-state column null.
- An event enqueued inside a scope opened with culture `bg` stores `bg`, and a handler dispatching it
  in a **worker process started under a different operating-system culture** observes `bg` from
  `ICurrentCulture.Current`. The assertion goes red when the dispatcher reads the ambient
  `CultureInfo.CurrentCulture` instead of the row — which is the defect the column exists to prevent
  and is invisible whenever the two happen to agree.
- A follow-up event enqueued by that handler stores `bg` unchanged, at any depth — culture propagates
  like `correlation`, not like `trace_parent`.
- Inside a request, `ICurrentCulture.Current` equals `CultureTag.Invariant` **even when the request
  carries an `Accept-Language` header** — nothing in D3 resolves culture, and a test sending one
  proves the absence rather than assuming it. Outside any scope it throws
  `PlatformContractViolationException` carrying `NoAmbientOperationScope`, as the other three
  accessors do.
- A scope opened explicitly with a culture reports it: `Begin(TenantId.Implicit, null, new CultureTag("bg"))`
  yields `ICurrentCulture.Current` of `bg`, and the same call omitting the argument yields
  `CultureTag.Invariant` — the invariant being the empty tag is what makes the omitted case correct
  rather than merely convenient.
- The check constraints hold: `claimed_by` and `claimed_at` are null together, `attempts >= 0`, and
  `poisoned_at` set implies `last_error` non-null. **No constraint makes `processed_at` and
  `poisoned_at` mutually exclusive** — all four combinations are legal and each names a state.
- `OutboxMessage.State` returns `Pending` for a freshly inserted row, and `DueAt` returns
  `OccurredAt` while `NextAttemptAt` is null.
- A second handler registration for a registered `EventTypeName` aborts startup with
  `DuplicateHandlerForType` naming the type and both handlers. A second `EventTypeName` for a bound
  CLR type aborts with `DuplicateNameForEventType`. A handler whose constructor dependency cannot be
  resolved aborts **worker** startup with `HandlerNotConstructible` naming the handler and the
  missing dependency, and does not fail the web host — which registers the same triple in order to
  enqueue.
- A payload written under one provider deserializes under the other and back. A stored payload
  carrying an unknown member deserializes without error; an enum round-trips as its string name; a
  type that gained an optional field reads rows written before it, and a type that has not gained it
  reads rows written after.
- The pinned serializer options are not resolvable from the container and no converter can be
  registered — asserted by attempting both.
- `IEventCapture.Enqueued` records the id, type, tenant, correlation and instant of every enqueue.

Out of scope: dispatch, claiming and every dispatch-state write (S5); redrive and discard (S7);
fan-out — a second handler for a type is a startup failure by design, not a gap to close; an
attribute-based alternative to the registration call, which stays available as later sugar.

---

## S5 — Outbox dispatch

Delivers: the worker claims, dispatches and marks messages one at a time; a process killed between
the domain commit and the dispatch delivers the message on restart.

Touches:
- **Persistence** — the dispatcher registered as `IBackgroundWork` declaring `HostRoles.Worker`
  under `PlatformBackgroundWork.OutboxDispatch`; `IOutboxStore.ClaimNextAsync`,
  `MarkProcessedAsync`, `RecordFailureAsync`, `PoisonAsync`, `DeferAsync`, `ReleaseClaimAsync`;
  `ClaimedWriteOutcome`; `PoisonAttemptMode`; `IProviderCapability.StampClaimAsync`; `HandlerError`;
  `DispatchError`;
  the per-message dependency and operation scopes; `ITraceContextCodec.StartLinked`
- **Hosting** — graceful shutdown stopping claims and releasing unstarted ones within the drain
  window
- **Testing** — `IEventCapture.Dispatched`, and `RunBackgroundWorkOnceAsync` over the dispatcher
- **CI** — the kill-and-restart assertion

Depends on: S3, S4.

Acceptance:
- Three enqueued events and one dispatch tick: all three handlers ran and all three rows are in the
  `Processed` state.
- With `DispatchTickBudget = 2` and five eligible rows, one tick dispatches exactly two.
- The sample's web process is killed after its domain transaction commits and before any dispatch
  tick runs. The row survives; after restart the worker's next tick delivers it and the handler
  observes the event. **This is the brief's third CI assertion and Persistence's stated
  done-criterion.**
- A handler returning `HandlerError.Transient` sets `attempts` to 1, `next_attempt_at` to now plus
  30 s, records `last_error`, and leaves the row `Pending`. The row is not claimed by a tick before
  that instant and is claimed by one at it, with the fake clock supplying both.
- Attempts 1 through 12 under base 30 s, factor 2 and cap 6 h produce a non-decreasing backoff that
  reaches and holds the cap; the twelfth failure sets `poisoned_at` with `last_error` non-null, and
  no later tick claims the row.
- `HandlerError.Permanent` poisons on the first failure, leaving `attempts` at 1 rather than burning
  the remaining eleven.
- An exception escaping a handler is treated as `Transient` and consumes one attempt.
- A row whose `type` resolves to no handler is deferred: the claim is released, `first_deferred_at`
  is stamped, `next_attempt_at` is set one fixed minute ahead, and `attempts` stays 0. A second
  deferral leaves `first_deferred_at` unchanged. A row still unresolvable at
  `first_deferred_at + 24 h` is poisoned — measured from first deferral, so a row whose
  `occurred_at` is three days old still gets the full window.
- A row whose payload does not deserialize takes the same deferral path and increments nothing.
- With registered migrations unapplied, a dispatch tick claims nothing, stamps nothing and
  increments nothing.
- Two dispatchers ticking concurrently against one eligible row: exactly one receives it and the
  other receives nothing, on both providers. **A provider contract test.**
- A dispatch-state write from a holder whose claim was reclaimed returns `ClaimLost`, changes no
  column, and is counted as duplicate-delivery evidence rather than escalated. The row keeps the
  state the reclaiming dispatcher left. **A provider contract test.**
- A claim older than `ClaimWindow` is picked up by the ordinary claim query, with no separate
  reclaim pass running.
- Inside a handler: `ICurrentCorrelation.Current.TraceId` equals the row's `correlation` column,
  `ICurrentTenant.Current` equals the row's tenant, `ICurrentPrincipal.Current` is null, and the
  active trace is a **new** trace carrying a link to the stored one and the stored sampled flag —
  its trace-id differs from the stored traceparent's.
- Request → event → follow-up → follow-up: all four rows carry the originating request's correlation
  unchanged, while each follow-up's stored `trace_parent` carries the trace-id of the link that
  enqueued it.
- Graceful shutdown stops claiming immediately, releases claims not yet started, finishes an
  in-flight message inside the 30 s drain window, and abandons one still running when the window
  closes — leaving that row to claim expiry.

Out of scope: prune and retention (S6); redrive and discard (S7); any ordering assertion — the
design offers no ordering guarantee and a test asserting one would encode a promise it refuses;
concurrent dispatch of several claimed rows; a signal from the writer to the dispatcher, since the
trigger is a timer.

---

## S6 — Leases, prune, and the outbox readiness conditions

Delivers: retention actually deletes, under a lease, in bounded batches; and readiness names a
backlog, a pending flood, and a poisoned row.

Touches:
- **Persistence** — `BackgroundWorkLease` and its migration, `ILeaseStore`, `ILeaseManager`,
  `ILeaseHandle`, `LeaseError`; the prune registered as `IBackgroundWork` declaring
  `HostRoles.Worker` under `PlatformBackgroundWork.Prune`; `PruneTarget`,
  `IProviderCapability.DeleteBoundedAsync`, `IOutboxStore.PruneAsync`, `OldestPendingDueAsync`,
  `PendingCountAsync`, `PoisonedCountAsync`; the `OutboxBacklogAge`, `OutboxPendingCount` and
  `OutboxPoisonCount` readiness checks
- **Core** — `LeaseOptions`, `HealthOptions`

Depends on: S3, S5.

Acceptance:
- One prune tick deletes a processed row older than `ProcessedRetention` and leaves one younger;
  deletes a poisoned row and a discarded row only past `PoisonedRetention`; and **never deletes a
  pending row of any age**.
- One prune tick deletes a host registration row whose heartbeat is older than
  `HostRegistration:RetentionWindow` and leaves a live one — the three `PruneTarget` values are one
  registration.
- With 1 200 eligible rows and `PruneBatchSize = 500`, no single delete statement removes more than
  500 rows.
- Pruning a poisoned row logs at warning naming the row.
- Two workers ticking prune concurrently: one acquires the lease and runs; the other's
  `AcquireAsync` returns `LeaseError.Held` and it skips the run entirely rather than waiting.
- A holder whose `RenewAsync` returns `LeaseError.Lost` aborts its run rather than continuing on the
  assumption it still holds.
- A lease whose `expires_at` has passed is acquired by a second holder, and the original holder's
  next renewal returns `Lost`.
- `OutboxBacklogAge` degrades when the oldest pending row's `DueAt` is more than
  `Health:BacklogAgeThreshold` in the past, and stays healthy for each of: ten rows deferred one
  minute ahead, ten rows backing off six hours ahead, and one row whose `occurred_at` is three days
  old but whose `next_attempt_at` is now. A worker stopped for ten minutes with due rows degrades
  it.
- A poisoned row of any age degrades `OutboxPoisonCount`; a discarded row does not. Neither returns
  `Unhealthy`, and one poisoned message never fails readiness's wire status.
- `PendingCountThreshold + 1` pending rows degrade `OutboxPendingCount`; `PendingCountThreshold - 1`
  do not. Poisoned, processed and discarded rows are not counted.
- Every one of these checks self-guards on an absent schema, reporting `Degraded` citing the schema
  rather than throwing.

Out of scope: redrive and discard (S7); a retention window for pending rows — the design forbids
one, since pruning an undispatched row is the message loss the outbox exists to prevent;
backpressure at enqueue; using the lease to guard migrate mode, which takes the provider-native lock
instead.

---

## S7 — Redrive and discard

Delivers: an operator recovers or retires poisoned rows, per id and in bulk by type, without editing
the database by hand.

Touches:
- **Persistence** — `IOutboxAdministration`, `OutboxAdministrationOutcome`,
  `OutboxAdministrationResult`, `OutboxError`, and `IOutboxStore.RedriveAsync`,
  `RedriveByTypeAsync`, `DiscardAsync`, `DiscardByTypeAsync`, `ListPoisonedAsync`
- **samples/** — a demonstration of calling both, not an endpoint

Depends on: S5, S6.

Acceptance:
- `RedriveAsync` over a poisoned row clears `poisoned_at`, sets `attempts` to 0, clears
  `first_deferred_at`, `claimed_by` and `claimed_at`, sets `next_attempt_at` to now, and returns
  `Applied`. The next dispatch tick claims and delivers it.
- A row poisoned with `next_attempt_at` six hours ahead is delivered by the next tick after
  redrive — not six hours later.
- Immediately after redriving a row whose `occurred_at` is three days old, `OutboxBacklogAge` is not
  degraded: the past-due age measures from the recovery.
- `RedriveAsync` over an id that no longer exists returns `NotFound`; over a discarded row (both
  marks set) returns `NotPoisoned` and changes nothing; over a pending row returns `NotPoisoned`.
- A forty-id call where one row was pruned returns thirty-nine `Applied` and one `NotFound` as a
  **successful** result, not a failed operation.
- `DiscardAsync` sets `processed_at`, keeps `poisoned_at`, and appends the reason to `last_error`.
  The row leaves `OutboxPoisonCount`, still prunes on the poison window, and is refused by a
  subsequent redrive.
- `RedriveByTypeAsync` over 500 poisoned rows of one type returns 500 and leaves rows of every other
  type untouched; `DiscardByTypeAsync` likewise.
- `ListPoisonedAsync(limit: 10)` returns at most ten rows, all in the `Poisoned` state and none
  discarded.
- No HTTP endpoint, console command or UI invokes any of these, on either host.

Out of scope: an administrative endpoint, console or UI — the design excludes one from D3 and it is
not smuggled in through the sample; redriving a processed or pending row; editing a payload as part
of a redrive; a bulk operation keyed on anything other than `EventTypeName`.

---

## S8 — Telemetry

Delivers: logs, traces and metrics configured by the standard registration call alone, exporting
nowhere by default, and never able to fail a request.

Touches:
- **Observability** — exporter configuration and opt-in, console and file defaults, service name and
  version, endpoint and database instrumentation, sampling policy, the bounded export queue and its
  drop behaviour
- **Core** — `ServiceName` and `ServiceVersion` derivation from the entry assembly

Depends on: S5.

Acceptance:
- With no exporter configured, the sample logs to console and file and makes no outbound connection
  attempt at startup or in steady state — asserted with outbound network blocked, which is the
  brief's environment rather than a contrivance.
- `ServiceName` and `ServiceVersion` left unset resolve to the entry assembly's name and
  informational version and appear on every exported span and log record.
- One request produces one server span carrying the correlation; a database call inside it produces
  a child span.
- A dispatched message produces a span whose trace-id differs from the row's stored trace-id and
  which carries a link to it — a trace reaching from the inbound request through the background job,
  which is Observability's stated done-criterion.
- Against an unreachable collector: request latency is unchanged within noise, the export queue is
  bounded, dropped signals are counted, and exactly one log line is written on the transition into
  dropping rather than one per failure.
- A configuration value named as a secret appears in no exported log, span attribute or metric
  label; no payload content appears in any of them.
- No metric carries a label whose value is an id, a correlation, a tenant or any other unbounded
  value.
- With telemetry in place the sample satisfies the brief's **first CI assertion** whole: health,
  readiness, correlation and telemetry all working through the standard registration call alone.

Out of scope: choosing, shipping or operating a collector; dashboards and alert rules; per-product
semantic conventions — Observability collects and does not interpret; making export synchronous or
fallible on any path.

---

## S9 — Pack, publish, consume, and the API reference

Delivers: the six packages publish to a private GitHub Packages feed, the sample restores them from
it, and the generated API reference gates the release.

Touches: **build/** — pack targets, versioning, symbols and doc-comment generation; **CI** — the
publish and authenticated-restore jobs; **samples/** — feed `PackageReference`s in place of project
references; **docs/** — the published reference.

Depends on: S1–S8.

Acceptance:
- `dotnet pack` produces six packages, each carrying its doc-comment XML, and the build fails when
  any public type or member lacks a doc comment.
- CI publishes the six to GitHub Packages as a private feed and the sample restores them from it
  with authentication — proving pack, publish and authenticated restore without spending the
  unreserved public identifiers.
- Every assertion in S1–S8 runs a second time against the sample built on the restored packages
  rather than on project references, and passes.
- The generated API reference contains every public type in all six packages. A public type added
  without doc comments fails the reference build, and the release job does not run when the
  reference build fails.
- No shipped package declares a dependency on Testing — asserted against the produced package
  manifests, since Testing refuses to be a development dependency of anything shipped.
- The published version is a 0.x version, and the release records that the API is explicitly
  unstable.

Out of scope: publishing to nuget.org or reserving the public identifiers — the brief leaves them
unspent deliberately; any stability or compatibility promise beyond 0.x; release-note automation;
the G1 engine edge, which the brief makes a follow-up rather than a done-criterion.

---

## What each slice discharges

| Obligation | Slice |
|---|---|
| CI 1 — the sample starts and serves, with health, readiness, correlation and telemetry through the standard call alone | S1, completed by S8 |
| CI 2 — a broken configuration aborts startup with a named error | S1 |
| CI 3 — a process killed between commit and dispatch delivers on restart | S5 |
| CI 4 — the provider contract tests go red against a broken provider | S2 |
| Abstractions — no Platform dependency; a consumer compiles against it alone | S1 |
| Core — explicit module registration; a bad graph fails startup with a named error | S1 |
| Hosting — one endpoint with health, readiness, correlation and graceful shutdown from the standard call | S1 |
| Persistence — two modules migrate in either order | S2 |
| Persistence — the tenant column in the first schema | S2 |
| Persistence — the outbox survives a process kill between the domain write and the publish | S4, S5 |
| Observability — a trace spans a request through a background job; no secrets; export opt-in | S8 |
| Testing — an integration test on a real provider and a frozen clock with no bespoke setup | S1, S2 |
| Testing — the contract tests exist and fail against a broken provider | S2 |
| Both providers pass the contract tests | S2 |
| The packages publish privately and the sample consumes them from the feed | S9 |
| A generated API reference for every public type, gating the release | S9 |
