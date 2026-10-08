# Decision log — D5 effort

Append-only. Newest at the top. The rejected alternatives are the point — without them, every future
session relitigates the same choice.

Completed efforts keep their logs with their design sets:
[`g2/90-decisions.md`](g2/90-decisions.md), [`g1/90-decisions.md`](g1/90-decisions.md),
[`d3/90-decisions.md`](d3/90-decisions.md).

**This log is effort-local.** `AGENTS.md`, *Decision logging*, decides what belongs here and what
belongs in `docs/docs/adr/`.

## Open

_(previously tracked out of this section: issues [#187](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/187), [#263](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/263), [#264](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/264), [#265](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/265))_

---

### 2026-10-08 — S38: development application is one lifecycle service after the journal-mode check, and an unreachable database degrades readiness rather than failing it
Context: S38 makes a host in the derived `Development` environment apply pending migrations at start. The contract fixes the owner (Persistence), the phase (`StartingAsync`, after `PersistenceStartupCheck`), the lock (the runner's), and the per-variant behaviour in *Error semantics* § 13. The open questions were how the wait on `Locked` is bounded, what "not-ready" means for an unreachable database, and what the tests' "empty SQLite database" is.
Chosen: one internal `DevelopmentMigrationStartup` beside `PersistenceStartupCheck`, registered right after it, idempotently. It compares `PlatformOptions.Environment` with `Environments.Development`, ordinal and ignoring case, and calls `ApplyAsync` in a loop: success returns; `Locked` logs at Information and waits one second, bounded only by the start's cancellation token; `Unavailable` logs a Warning naming the code and detail and returns; anything else throws `PersistenceStartupException`, whose message now carries a `MigrationError`'s code and `Detail` as it already did for `ConfigurationError`. "Not-ready" is the existing contract: the database check degrades, and the probe answers 200 with a non-healthy report, since degraded takes traffic by design; S38.7 asserts the report, not the status code. The "empty SQLite database" is a WAL-mode file with no tables, as the test host's default is, because the journal-mode check runs first. The plan named `I-P1` and `I-P2`; the development-application invariants are `I-P7` and `I-P8`. I-P7 flips to `code`: S38.1 (both casings), S38.2 and S38.3 cover every clause. I-P8 stays `instruction`: S38.4 and S38.5 prove development application takes the lock through the runner, but "the only path that writes history" is a property of all code, not of a test. The commercial guide already said what S38 ships and is unchanged.
Rejected: running development application before the journal-mode check, so a brand-new SQLite file would be brought into WAL by the migration itself, which contradicts § 16's stated phase; a bounded number of `Locked` retries, which § 13 does not set and which would fail a slow first host's peer; failing startup on `Unavailable`, which § 13 rules out.
Reversibility: cheap. One internal hosted service; no public surface.

### 2026-10-08 — S37: migrate mode shares Core's ambient registrations with Hosting, and the trace codec moves into Core
Context: S37 makes migrate mode resolve the module graph, call `Register` in order, and run every `ISeeder` after a successful apply. A seeder opens an operation scope and a unit of work, so migrate mode needs the scope, accessors, clock and audit writer a host registers, and the scope factory needs `ITraceContextCodec`, which only Observability registered. The open questions were where the shared registration lives, how migrate mode gets the codec without Observability, and how seeders are ordered.
Chosen: an internal `CoreDefaults.AddAmbientDefaults` in Core, all `TryAdd`, called first by Hosting's `AddCoreDefaults` and alone by migrate mode, so the two cannot drift. `TraceContextCodec` moves from Observability to Core and is registered there; Observability's own `TryAdd` stays and finds it present. Seeders are sorted once, stably, by the composed order's index, with unknown modules after it ordered by name ordinally; a stable sort keeps registration order within a group. Resolving the seeders is guarded: a seeder whose dependencies the run did not compose prints `Registration: <message>` and exits 1 before any seeder runs, which the contract now states. S37.10 counts the web host's `ISeeder` descriptors rather than resolving them, since a seeder's dependencies need not exist in a host that never invokes it. The plan named `I-P3` to `I-P5`; the seeding invariants are `I-P9` to `I-P11`. I-P9 flips to `code`: S37.4, S37.8 and S37.10 cover every clause. I-P10 stays `instruction`, because convergence binds each seeder; S37.2 and S37.3 only show a converging seeder converges. I-P11 stays `instruction`: S37.6 and S37.7 prove migrate mode opens no scope, but the second half binds each seeder. The commercial guide already said what S37 ships and is unchanged.
Rejected: a Persistence reference to Observability, or calling `AddPlatformObservability` from migrate mode, either of which brings exporters and sampling into a one-shot operator command; duplicating the registrations in migrate mode, which is the drift `CoreDefaults` exists to prevent; grouping seeders through a dictionary, which loses registration order unless rebuilt.
Reversibility: cheap. One internal registration helper and an internal type moved between two framework packages.

---

### 2026-10-08 — S36: module lifecycle hooks run in one hosted service placed between registry freeze and background work, and claim shutdown once
Context: S36 adds `InitializeAsync` and `ShutdownAsync` to `IPlatformModule`. The contract fixes their timing (after freeze, before the first tick or request; shutdown in reverse after background work and the listener stop) and that shutdown runs exactly once, on stop or on disposal after a later startup failure. The open questions were how to get the timing without new host machinery, and what the disposal path can offer a hook.
Chosen: one internal `ModuleLifecycleService`, registered between `PlatformRegistryStartup` and `BackgroundWorkService`, so the generic host's own ordering does the timing: `StartingAsync` (the freeze) runs before every `StartAsync`, the first tick and the web listener start after this service, and stopping runs in reverse. Composition keeps the order it resolved in a `ModuleOrder` singleton rather than resolving the graph twice. Each completed init is pushed on a stack under a lock; stop, disposal and an init failure all take the stack through one claim, so whichever comes first shuts the modules down and the others find nothing. On disposal the container has already marked itself disposed, so no scope can be created; the hook still runs, gets the root provider, and anything it resolves fails and is logged. The contract's provider bullet now says so. I-C11 and I-C12 flip to `code`: S36.1, S36.3, S36.4 and S36.5 cover every clause of I-C11, and S36.3 covers I-C12. I-C10 stays `instruction`: the order, the gate and the tick are tested, but the request listener and the shutdown-after-listener clause are not. I-C13 stays `instruction`, because "must not assume it is the only process" binds each module.
Rejected: a decorating service-provider factory so shutdown could run before the container is torn down, which would replace a consumer's own container factory; waiting on `IHostApplicationLifetime`, which signals nothing when startup fails; running hooks from `PlatformRegistryStartup.StartingAsync`, which runs before the `StartingAsync` checks of services registered after it.
Reversibility: cheap. Default interface members; one internal hosted service.

### 2026-10-08 — S35: a guarded write that matches nothing dooms its unit of work, and the guard enlists the command itself
Context: S35 adds opt-in optimistic concurrency. The contract fixes the surface (`IVersioned`, `IVersionGuard`, `TransactionError.StaleVersion`, two `ContractViolation` variants) and that a zero-row write rolls back the whole unit even if the work ignores the result. The open questions were how the doom reaches `IUnitOfWork`, how the guard learns the transaction, and how far I-P1 to I-P3 can flip.
Chosen: the guard reads the ambient transaction and sets an internal `Doomed` flag on it. `UnitOfWork.ExecuteAsync` checks the flag right after the work returns and before the outbox and audit flushes, so a doomed unit stages nothing and returns `StaleVersion`. The guard sets the command's connection and transaction itself, because a command a consumer builds without them is not enlisted, and the consumer should not have to know. The matched-row count is the whole judgement, with no provider branch. S35.6 is a test host doing the product's mapping, since Hosting and Mcp cannot see `TransactionError`; it needed a small hook in the MCP test harness. I-P1 flips to `code`: S35.1, S35.2, S35.4 and S35.5 cover every clause on both providers. I-P2 stays `instruction`, because the predicate form is the consumer's SQL and Platform cannot see it. I-P3 stays `instruction`: S35.3 and S35.6 prove Platform's error and a host's answers name no version and do not tell the three cases apart, but the log and audit clauses bind each consumer.
Rejected: having the guard return a result and trusting the work to propagate it, which commits a unit whose work ignored the failure and fails S35.2; throwing on zero rows, which turns an expected race into an exception; a distinct error for a missing row, which would disclose another tenant's row.
Reversibility: cheap. Opt-in; no schema change to Platform's own tables.

### 2026-10-08 — S34: orphaned inbox records are drained last in each prune pass, until a short batch
Context: S34 keeps `platform_inbox` from growing without bound. The contract fixes the target (`PruneTarget.OrphanedInboxRecords`), its statement (a bounded delete of records whose outbox row is gone, `olderThan` = now) and that no setting is added. The open questions were where the target sits in `PruneWork`'s pass, and how a backlog larger than `PruneBatchSize` is cleared. The three existing targets run one bounded statement per hourly tick.
Chosen: the orphan target runs after the three others, so a record whose outbox row the same pass just pruned goes in that pass. It repeats until a statement deletes fewer than `PruneBatchSize` rows. The loop ends because orphans appear only when this leased work deletes outbox rows, and nothing else does that while it runs. A failed orphan statement ends the repetition with the existing `"Prune of {Target} failed: {Code}."` Warning, shared by every target; there is no success line. Both providers' `DeleteBoundedAsync` already delegate to `PruneSql.For`, so the statement — ordered oldest first, identical text on both — is the only provider change. A record whose outbox row exists, whether pending, processed or poisoned, is never touched, which is what I-P6 says; S34.1 and S34.3 prove it, and it flips to `code`.
Rejected: one statement per tick like the other targets, which leaves a 25-row backlog at batch 10 for three hours and does not meet S34.2; running the orphan step first, which leaves the records of rows pruned in that pass for the next hour; a retention window for inbox records, out of scope and redundant with the outbox's; a foreign key with cascading delete, out of scope and absent on SQLite without `PRAGMA foreign_keys`.
Reversibility: cheap. No schema change.

### 2026-10-08 — S33: the inbox record is written first, on the dispatcher's own transaction, and a duplicate commits nothing
Context: S33 stops a handler's database effects from landing twice when a worker loses its claim after the handler committed, or when two workers overlap on one message. The contract fixes the shape: no public inbox type, an internal `InboxStore`, a `0004_create_inbox` migration, and a new lost-claim sentence. The open questions were where the record goes relative to the handler, and how a duplicate is reported. The dispatcher already runs each handler inside one write unit of work, so the handler's effects and anything written on the ambient transaction commit or roll back together.
Chosen: inside that unit of work, the dispatcher calls `InboxStore.TryRecordAsync` before the handler. The store runs `INSERT … ON CONFLICT (message_id, consumer) DO NOTHING`, with identical text on both providers. When the insert affects no row, the handler is not invoked, the transaction commits having written nothing, the outbox row is marked processed with no attempt consumed, and one Information line names the message id and the consumer. A handler failure rolls the record back with its effects, so the retry is not mistaken for a duplicate. On PostgreSQL a concurrent second insert blocks on the first's uncommitted key; on SQLite the second write transaction cannot begin until the first commits. The consumer is the event-type name, because each event type has exactly one registered handler. `message_id` uses the outbox id's physical type (BLOB/BYTEA), and `tenant` uses the outbox's tenant encoding. I-P4 and I-P5 flip to `code`: S33.2, S33.5 and S33.6 prove the first, and S33.3 proves the second.
Rejected: recording after the handler, which leaves the window between effect and record open to a concurrent duplicate; a SELECT-then-INSERT check, which races under READ COMMITTED; a separate transaction for the record, which could commit without the effect; a capability member for the statement, when its text is the same on both providers.
Reversibility: cheap for the dispatch order. The `0004` migration is permanent.

### 2026-10-08 — S32: audit retention is its own Worker work, registered only when the setting is present
Context: S32 lets an operator cap how long audit rows are kept, and a host with no value must behave exactly as before. Persistence's `PruneWork` already prunes the outbox, inbox and host registrations, but Persistence knows nothing of Audit's table. The delete has to stay bounded on SQLite's single write lock, and a first tick over years of backlog has to end. `occurred_at` had no index: all three 0001 indexes lead with another column. The prune compares instants as text through `IProviderCapability.FormatInstant`, which is fixed-width on both providers.
Chosen: an internal `AuditPruneWork` in the Audit module, registered by `AuditModule` only when `PlatformOptions.Audit.RetentionDays` is set, so with no value no work exists to tick. It runs in the Worker role only, under the lease `platform.audit.prune`, hourly. It deletes oldest first in statements of 500 rows, each in its own write transaction, with at most 100 statements per tick, and stops early on a short batch. The rows it deletes are those whose `occurred_at` is strictly before the worker's now minus the configured days. A held lease ends the tick silently. A failed statement is a Warning and leaves readiness alone. A non-zero tick writes one Information line and no audit row. Migration `0002` adds `ix_audit_event_occurred_at` whether or not retention is set, so the schema never depends on a setting. The binder reads the value as an optional whole number in 1–36,500 and reports anything else as `InvalidSetting` on `Platform:Audit:RetentionDays`. The value is fingerprinted, which bumps the format to `szdfp4`.
Rejected: a fourth target in Persistence's `PruneWork`, which would make Persistence know Audit's table; always registering the work and no-oping on null, which leaves a work in the registry the I-U11 promise says is absent; a configurable batch size or interval, out of scope; one unbounded `DELETE`, which holds SQLite's write lock for the whole backlog.
Reversibility: cheap for the work and the setting. The index migration is permanent but harmless.

### 2026-10-08 — S31: the landing page's readiness demo is checked against the platform's source, not a slice ledger
Context: S31 repoints the roadmap at `design/30-slices.md` under the #80 ruling. Two other places under `site/src/` still cited the archived D3 ledger. `App.test.tsx` checked that each name in the landing page's readiness-probe demo (`Database`, `PendingMigrations`, `SettingsFingerprint`, `PeerHost`, `PeerAbsenceGrace`) appears in `design/d3/30-slices.md`. None of them appears in the active ledger, because D3 built them and they are not D5 work. The roadmap's "Read the slice ledger" link also pointed at the D3 file. The parser's special case for a fully retired ledger, one that has only Landed bullets and an `Outstanding` of "None.", returned an empty roadmap. Now that Landed entries count as shipped slices, that special case can never be reached.
Chosen: the demo-name test reads `WellKnownNames.cs` and `PlatformOptions.cs` and requires each name to be a declared public member. That is the claim the test exists for, "the demo cites nothing the code never built", now made against the code. A calibration check confirms that an undeclared name fails. The ledger link points at `design/30-slices.md`. The retired-ledger special case is removed: a retired ledger now parses to its shipped Landed entries, and a ledger with neither slice headings nor Landed entries still throws. The non-goals list all fifteen of the D5 brief's non-goals, each shortened to a title and a one-line reason.
Rejected: pointing the demo-name test at the active ledger, where every name would fail; keeping it on `design/d3/`, which leaves `site/src/` reading an archived effort; trimming the non-goals to a few, which drops ones the brief binds.
Reversibility: cheap. Site-only.

### 2026-10-08 — S30: each promise is proven at its boundary — the Mcp public surface, every stored value, and Hosting's own timer across the grace end
Context: S30.1 asks for a reflection test over "the Mcp tool and invoker surface", and a hand-kept list of types would miss a member added later. S30.3 asks that "no API returns the token again", which the existing S10.3 checked only for one column and one second mint. S30.4 asks that scheduled work run at its time after grace, but Licensing has no scheduler: scheduled work in Platform is an `IBackgroundWork` registration ticked by Hosting's timer, and the existing S12.9 used a hand-built contributor rather than the module and proved no work that straddles the grace end. Every test passed against the code as it stands; no code changed.
Chosen: S30.1 walks every exported type of the Mcp assembly. No parameter, property, field or return may be named like a secret (`Redaction.IsSensitiveKey`, with `CancellationToken` exempt) or typed as a credential carrier (`IAuthenticationRequest`, `HttpContext`, `HttpRequest`, `IHeaderDictionary`, `ClaimsPrincipal`, `AuthenticationHeaderValue`). Its behavioural half connects as alice and passes bob's principal and header value as call arguments; the permission provider, the ambient principal at invoke and the audit actor all name alice, and the invoker receives bob's value as data. S30.2 projects one producer's tools from JSON manifest text and the other's from a fixed C# table, registers both and their invokers as ordinary services in one host, and compares (IsError, invoker calls, audit outcome) per tool with the permission granted and denied. S30.3 scans every value in every SQLite table for the token and for a token-shaped string, finds by reflection that only `IOrganizationApi.InviteAsync` returns an `InvitationToken`, and serialises every later read — memberships, a redemption, a replayed redemption — and every audit record. S30.4 starts the real `LicensingModule` in a web host with a fake clock: paid work admitted inside grace is held while the clock passes the grace end and completes uncancelled; an `IBackgroundWork` scheduled for after grace is ticked by Hosting's own timer, does not run early, and runs at its time; reads and exports answer 200; new paid work answers 402 without reaching its handler.
Rejected: listing the Mcp public types by hand (a member added later would go unchecked); a JSON manifest loader in Mcp for S30.2 (a contract change in a test slice; the promise is that both shapes use the same surface, which a test-side projection proves); a Licensing-owned scheduler or lapse hook for S30.4 (the invariant is that Licensing gates only admission, and adding a hook would be the thing it forbids).
Reversibility: cheap. Test-only.

### 2026-10-08 — S29: each promise is observed where it lives — the built provider graph, the shell's own source and build inputs, and every client the container builds
Context: S29.2 asks that "no exporter instance exists in the built service provider", observed and not inferred from absent traffic, but OpenTelemetry holds its processors, readers and exporters in non-public fields of providers the container resolves. S29.3 asks about the shell's "build output", and CI does not build the shell. S29.4 asks for an outbound handler that trips on a billing provider's host, but nothing in the platform builds a client from `IHttpClientFactory`, so a handler alone would watch an empty room. S29.1's provocations raised every one of the ten variants with a detail naming its cause; no code change was needed.
Chosen: S29.1 lists `HostStartupError`'s public static factories by reflection and looks each up in a table of provocations, so a new variant without one fails by name. S29.2 walks the object graph from the resolved `TracerProvider`, `MeterProvider`, OpenTelemetry `LoggerProvider` and every `ILoggerProvider`, following fields only inside OpenTelemetry's own assemblies and the collections they hold, and collects every `BaseExporter<>`. A second test runs the same walk with an endpoint set and finds exactly the OTLP trace, metric and log exporters, so the walk is calibrated. S29.3 reads the calls from `shell/src/api.ts` itself: `fetch` appears once, inside `request`; every caller of `request` is parsed and each `(method, route)` must be served by a `RouteEndpoint` in the live data source, and the set must equal `AdminEndpoints.ShellCalls`. The build output is proven from what determines it: one `index.html` script entry, an import graph from `main.tsx` reaching only relative files and the declared `react`/`react-dom`, no dynamic import, no server-side build or server hook in `vite.config.ts`, and no .NET project or source under `shell/`. S29.4 installs the tripwire with `ConfigureHttpClientDefaults` and drives startup and migration, a transition, an inbound provider event and an entitlement decision in an operation scope, and a readiness probe; it then builds a client from the same container and shows the tripwire does trip. A metadata test closes the remaining route: Billing's assembly references no type in `System.Net.Http`, `System.Net.Sockets` or `System.Net.WebSockets`, calibrated against the test assembly, which does.
Rejected: asserting the `OtlpEndpoint` option is null (inference from configuration, not observation of the provider); building the shell in CI for this slice (a pipeline change in a test slice, and a bundle scan still needs the route table to mean anything); a process-wide `HttpClient.DefaultProxy` or `DiagnosticListener` tripwire (it reaches every parallel test's clients and, for the listener, turns on trace-header injection); a real HTTP request to a billing endpoint (Billing maps none; its request path is the evaluator the request asks).
Reversibility: cheap. Test-only.

### 2026-10-08 — S28: audited input surfaces are enumerated from the compiled IL, and log lines are read back from the real file sink
Context: S28.2 asks for one test that pushes a secret and a payload through every audited input surface, enumerated from the tree so a surface added later without coverage fails. S28.4 asks that "the Core owner's detection site" audit a repeated error condition once per detection. S28.3 asks that the audit table reject a direct `UPDATE` at the schema level. The test found two leaks: Billing logged a redelivered provider event's identifiers verbatim, and Licensing put the document's tier, unredacted, in the audit record's resource id and in two log lines.
Chosen: a surface is every non-compiler-generated type in a `src/*` package whose IL calls `IAuditWriter.WriteAsync`, read with `MethodBody.GetILAsByteArray` and a token resolver, excluding the writer's own implementations, plus the writer itself as the surface a consumer calls. A fact asserts that the enumerated set equals the drivers' keys. Each driver starts a real host whose log directory is a temporary folder, and the test reads the JSONL file back until a sentinel line logged last appears. A test `ILoggerProvider` would see nothing, because Platform composes Serilog with `writeToProviders: false`. These tests run in the serial telemetry collection, because Serilog's static logger is process-wide and a host disposed concurrently by another test closes it. The two leaks are fixed in their own packages with `Redaction.RedactValue`. Licensing still compares the raw tier, so a change between two secret-shaped tiers is still a new detection. For S28.4, Core's detection site is `AuditSinkDispatcher`, where a sink's failure is detected, and the checks are readiness probes: fifty probes after one failed write leave one record attempt and one warning line, and a second failing write is a second detection. S28.3's schema half needs a new Audit migration, which changes *Persisted schemas*, so under *Outstanding* I-U7 stays `instruction` and #306 names the failing case. The store-surface half is already proven by `S13_4`.
Rejected: a hand-kept list of surfaces (the slice forbids it); a source-text scan for `WriteAsync(` (it matches a name, not a bound call, so every other `WriteAsync` matches too); capturing through an `ILoggerProvider` (never written to); adding the update trigger in this slice (a contract change in a test slice); counting checks as detections (a probe observes the state and does not detect it).
Reversibility: cheap. The two redactions only change what an operator sees in a log line and in the licence record's resource id when a value has a secret's shape.

### 2026-10-08 — S27: a shared-read scope makes the database refuse writes, rather than trusting a read-only transaction's declared intent
Context: S27.1's test found two ways a write reached another tenant's published row while a shared-read scope was open. S6.5 refuses only a *write-intent* unit of work opened inside the scope. A unit of work declared `ReadOnly` could still issue an UPDATE or DELETE, because the intent is a declaration that neither provider enforced. A scope opened inside a write transaction that was already open left that transaction writing with the widened rows in reach. The durable audit sink also enlisted in any ambient transaction, so a read's record rolled back with a read-only unit of work that failed.
Chosen: while a scope is open, a read-only transaction refuses writes at the database: `PRAGMA query_only = ON` on SQLite (cleared before the connection returns to the pool, and the pool is cleared if that fails) and `SET TRANSACTION READ ONLY` on PostgreSQL. A refused write (SQLite error 8, PostgreSQL 25006) is reported as `WriteInsideSharedReadScope`, the same contract violation S6.5 throws, and `UnitOfWork` now rolls back and propagates a contract violation instead of classifying it as a retryable `TransactionError`. `Open` inside a write transaction throws the same violation before it writes the audit record. The sink enlists only in a `Write` transaction, as *Public surface* §6 already says ("a read … writes in its own transaction"). The provider switch is internal and keyed on `AmbientTransaction.Provider`. For S27.3, D5's baseline is `d6daabf`, the parent of D5-S1. The test pins the three migrations registered then by name, and pins the schema they produce, column by column with primary-key ordinals and indexes, per provider. For S27.4, the guard reads every framework assembly's metadata and fails on any identifier containing "Owner" or "Organization", with one exempt name: `AdministerOrganization`, a permission's name in Platform's catalogue rather than a relation.
Rejected: detecting a write after the fact through `total_changes` or a transaction id (it detects the write after it has reached the row); a new `IProviderCapability` member (it widens a public contract a third-party provider implements, for two statements); refusing every unit of work inside a scope (a read inside the scope is the scope's purpose); hashing the baseline migration source files (a comment or formatting change would fail it, while a semantically equal rewrite is what the invariant allows); narrowing the owner words to dodge the permission name (the exemption is enumerated, and an identifier carrying a second owner word still fails).
Reversibility: cheap. A consumer that wrote through a read-only unit of work inside a scope, or opened a scope inside a write, now gets a contract violation. Both were I-T1 violations already.

### 2026-10-08 — S26: the evaluator refuses an undeclared permission name by throwing a contract violation, not by denying
Context: S26.1's test showed the evaluator, called directly with a name no catalog declares, asked every provider and returned a decision: `Allowed` if any provider granted the name, otherwise `Denied` with a `Required` denial audited. I-A3 and *Authorization* say an undeclared name is a defect, never a runtime denial. Startup already refuses one on every endpoint and tool requirement. Only a direct caller could reach the evaluator with one.
Chosen: before asking any provider, the evaluator checks the name with `IPermissionCatalogRegistry.EnsureDeclared` and throws `PlatformContractViolationException` carrying the existing `PermissionCatalogError.UnregisteredPermission`. Nothing is audited and no decision is returned. This is the same shape as `NoAmbientOperationScope`: a defect in the caller throws. No new public type is added. The exception carries a `PlatformError`, which `PermissionCatalogError` already is. `OperatedScenarioTests` evaluated an undeclared name directly and now declares it.
Rejected: a `Denied` decision carrying a new error (this is a runtime denial, which the invariant forbids); a new `ContractViolation` variant (a second code for a failure that already has one); leaving the evaluator alone and proving I-A3 only through startup (S26.1 requires the evaluator itself).
Reversibility: cheap in code, but a behaviour change for a direct caller. A module evaluating a name it never declared now throws instead of being silently denied.

### 2026-10-08 — S25: the fresh-root case derives its attempt count from the sampling ratio, rather than predicting the sampler's per-trace-id decision
Context: S25.1 offers two readings: a pass condition derived from `PlatformSampler`'s deterministic per-trace-id decision, or a derived attempt count with a miss below one in a million. Predicting the decision in TypeScript means replicating the .NET OpenTelemetry `TraceIdRatioBasedSampler` arithmetic (which bytes of the id, which endianness, which threshold rounding) without its source in the repository, and keeping that copy in step with every SDK upgrade.
Chosen: the derived count. n = ceil(ln(1e-6) / ln(1 − 0.1)) = 132, so a healthy build misses with probability 0.9^132 ≈ 9.1e-7. The ratio and the bound are named constants and the derivation is written beside them. Broken pairing still fails every run, because no trace id then carries an edge→workload pair whatever the edge samples.
Rejected: replicating the sampler's decision (a silent wrong answer on an SDK change would make the test vacuous or always-red); raising the ratio or making it configurable for the test (S25 out of scope; a public surface the contract does not declare).
Reversibility: cheap. One constant block in one test.

### 2026-10-08 — S24: the HTTP outage test uses a provider that fails at the pipeline, since the Organizations provider cannot be reached from it
Context: S24.5 names the condition "the Organizations permission provider's store unreachable". The HTTP pipeline check is never resource-scoped (I-R7), and `OrganizationsPermissionProvider` answers an empty grant without touching its store unless the resource is an `Organization`, so that literal condition yields 403 through the pipeline, not 503. Mcp scopes the check to `ResourceRef(tool, id)`, which reaches the store only for a tool named `Organization`.
Chosen: both surface tests register a provider that returns an error at the check (a non-`ProviderUnavailable` one, to cover normalisation too), which is the state an unreachable store puts any provider in. No production change to Organizations.
Rejected: resource-scoping the pipeline check (breaks I-R7); making the Organizations provider consult its store on an unscoped check (a contract change, and a store read on every request); a tool named `Organization` to reach the real store over Mcp (contrived, and proves nothing the stub does not).
Reversibility: cheap. A test-only reading.

### 2026-10-08 — #93: Identity owns an optional, per-host account store keyed on (issuer, subject), as a second module type
Context: [#93](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/93) boxes 3–4. Ben ruled 2026-10-08: "Identity owns an optional account store keyed on (provider, subject), with explicit authenticated linking." Brief decision 6 makes it per host; decision 5 (no linking across products) and #98 (a principal is not an account) stand; ADR-009 decision 4 is amended in place.
Chosen: `IdentityAccountsModule` in the Identity package, depending on `IdentityModule`; only it registers the store, its migration `0001_create_accounts` and `IAccountApi`, and Identity gains a Persistence reference. Two tables, `identity_account(id, created_at)` and `identity_account_link(issuer, subject, account, linked_at)` with primary key (issuer, subject), no email, profile, claim or tenant column. An account id is 128 random bits as 32 lowercase hex, opaque. A linked identity authenticates as (`platform.account`, id), kind `Account`, mapped inside Identity's own providers with one uncached store read; a store failure is `ProviderFailed`, never the raw pair. Creation is explicit (`CreateAccountAsync`, Required audit); the upstream-proxy `Delegated` principal is never mapped.
Rejected: a new package; a flag on `IdentityModule` (a user table as a configuration value on SkyNet HR's host); identities serialised on the account row (uniqueness by check, not constraint); an email column; a ULID or v7 GUID (discloses creation time); an account field on `Principal` (a framework change naming accounts); the first identity's pair as the account id; a configured account issuer; just-in-time creation (an unauditable write before any scope); an `IPrincipalMapper` framework seam (fails seam admission); tenant-owned accounts; a new `AuthenticationError` variant.
Reversibility: cheap for the placement; expensive for the principal shape and the schema, which every membership and audit row stores.

### 2026-10-08 — #93: a second identity joins an account only through `LinkAsync`, with a credential validated now and issued within five minutes
Context: [#93](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/93). "Explicit authenticated linking" means the caller is authenticated as the account and completes sign-in with the second identity in the same flow, never by email or any other claim.
Chosen: the host passes the second sign-in's bearer token (for example from the sign-in module's token endpoint, the module unchanged) to `LinkAsync` on a request authenticated as the account. Identity validates it with its own providers and requires `iat` no more than five minutes before the call, `ClockTolerance` either side; no `iat` is refused; the window is fixed. Any identity except the last may be unlinked under a row lock; an identity on another account answers `IdentityNotLinked` like an unknown one. Create, link and unlink each write one Required audit record; refusals are logged, not audited. No permission is declared.
Rejected: a sign-in callback hook (its subject comes from an unvalidated id token and can differ from the access token's); any currently valid token (a stolen token could attach permanently); `auth_time` (access tokens usually lack it); a link nonce threaded through the sign-in module (it would know about accounts); unlinking the last identity; re-authentication for unlink; a "manage own account" permission every principal holds.
Reversibility: cheap. A silently refreshed token also passes the freshness check; if "completes sign-in" is read as interactive, this is the call to tighten.

### 2026-10-08 — #58: runtime settings are a new module, `SubZeroDev.Platform.RuntimeSettings`, the fourteenth package
Context: [#58](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/58). Ben ruled 2026-10-08: a layered setting provider (global, then tenant, then user, persisted), distinct from startup configuration, with no administration UI (brief decision 6; the shell's Settings exclusion stands).
Chosen: a module package referencing Abstractions, Core and Persistence only, holding every type, the catalogue and one table `runtime_setting`; no framework seam. Values are a closed set (boolean, 64-bit integer, text) built by three factories with a default and an optional rule. A name matching the redaction marker set fails startup, so a setting is never a secret.
Rejected: Core (touches no database); Abstractions (contracts only); Persistence (puts the table in every host, fails the seam-admission test); Organizations (the self-hosted Automator and SkyNet HR do not run it); the name `Settings` (collides with startup settings); a reader contract in Abstractions (no framework consumer); arbitrary `T` over JSON; encrypted values (key custody Platform does not hold).
Reversibility: expensive once published for the package; cheap to widen the value types or add a seam.

### 2026-10-08 — #58: the user layer exists only for an account, per tenant; the implicit tenant has no tenant layer
Context: [#58](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/58). Resolution is user, then tenant, then global, then the declared default, over the layers a declaration admits. BarStrad's and SkyNet HR's principals are never accounts (#98).
Chosen: a user row is keyed by (tenant, `PrincipalId`) and exists only for an `Account` principal; `Delegated`, `System` and `Anonymous` fall through it on a read and get `LayerUnavailable` on a write. No tenant layer exists under `TenantId.Implicit`, so Local has the global layer only. A stored value that fails the current declaration answers `StoredValueInvalid` and never falls through.
Rejected: keying `Delegated` rows (a BarStrad table is shared by successive guests); one user row across tenants (the escape I-T2 refuses); treating the implicit tenant as a tenant (global and tenant become two names for one thing); falling through on a bad stored value (silently changes the effective value).
Reversibility: expensive for the resolution order and per-tenant user rows; cheap to admit `Delegated` later.

### 2026-10-08 — #58: runtime settings are read through every time, with no cache and no fingerprint input
Context: [#58](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/58). A web host and a worker share one database, and a change on one must reach the other without a restart.
Chosen: each read is one query for at most three rows and the module holds no in-process copy (I-ST7). Writes are last-writer-wins by upsert; the row does not opt in to #60's version column. No runtime value or catalogue entry is a settings-fingerprint input, and no setting overrides a startup option (I-ST8), so the S32 `szdfp4` bump is untouched.
Rejected: a per-instance TTL (a staleness promise hard to withdraw); version stamps (still a query per read); an invalidation bus (a distributed bus is a non-goal); fingerprinting the catalogue (rolling-deploy hosts legitimately differ).
Reversibility: a cache is cheap to add and expensive to remove; opting in to the version column is an additive migration.

### 2026-10-08 — #58: global and tenant writes need module-declared permissions, and every change is a Required audit record without its value
Context: [#58](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/58). A setting change is an administrative act, and role policy belongs to the product.
Chosen: the module's own catalogue declares `Platform.RuntimeSettings.WriteGlobal` and `WriteTenant`, evaluated with resource `RuntimeSetting/<name>` so a grant can name one setting; a user-layer write needs no permission and reaches only the caller's row. In Local the composition provider grants both to `System`; in Operated only the product's provider does, and Platform grants them to nobody. Each set, and each clear that removed a row, writes one `Required` record in the change's transaction, naming layer and setting, never the value. The module maps no endpoint and sweeps no row.
Rejected: `WriteOwn` (every account would hold it); the names in `PlatformPermissions` granted by Organizations (couples its role policy to a module it cannot see); a module-shipped provider; `Recorded` audit (allows an unrecorded change); a module HTTP API (an administration surface); a sweeper.
Reversibility: cheap.

### 2026-10-08 — #110: the edge streams per route, with its own first-byte budget, and aborts a broken stream
Context: [#110](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/110), reconciling [#106](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/106). The edge buffered every response, which breaks SkyNet HR's SSE console (its own decision D10), and #106 made a buffered route's mid-body death an honest `503`. Ben ruled 2026-10-08: streaming is a per-route opt-in at the edge with its own time-to-first-byte budget, and a failure mid-stream aborts the stream.
Chosen: routes opt in under `GameEdge:StreamingRoutes`, each a `PathPrefix` matched ordinal on segment boundaries with a required `FirstByteTimeout` greater than zero and no greater than `ForwardTimeout`, validated at startup. The budget runs to the workload's response headers, the edge's commit point; after headers there is no edge deadline. A mid-stream failure aborts the response (no terminating chunk on HTTP/1.1, `RST_STREAM` on HTTP/2) with no edge-authored byte after headers, logged once as `EdgeStreamAborted` at Warning and counted once on `subzerodev.edge.stream.aborts`; caller disconnects are neither. An additive `ForwardStreamingAsync` leaves the buffered path, its byte identity and #106 untouched. No new `EdgeError` variant; exactly one attempt. WebSocket upgrade is not covered.
Rejected: a dictionary of prefixes (environment-variable keys), a global flag or glob patterns; a first-body-byte budget (holds headers back and times out an idle SSE stream); defaulting to `ForwardTimeout` (not "its own" budget); a total or idle timeout after headers; a clean end on failure (disguises truncation); an in-band error frame (teaches the edge content framing); rethrowing to `ErrorEnvelopeMiddleware` (logs an expected death as Error); a separate meter; changing `ForwardedResponse.Body` to a stream; a third `EdgeError` variant.
Reversibility: the key shape and the abort semantics are expensive once a deployment and a client rely on them; the rest is cheap.

### 2026-10-08 — #86: the Adventures POC is recorded against G2 as adopted, adapted, rejected or deferred
Context: [#86](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/86) asked what G2 did with each idea in the Adventures proof of concept. G2 shipped without that list. Ben ruled 2026-10-08: an adopt/adapt/reject record.
Chosen: a retrospective block in `docs/docs/implementation-plan.md` §5 Track B, inside G2 and before G3, that points into the G2 archive for each reason rather than restating it. "Deferred" marks ideas G2's non-goals sent to G3 or G4 without judging them. The G3 and G4 halves stay a reading list; G3's principal design answers its own adopt/adapt/reject, treating Adventures identity as evidence only (#97, #100).
Rejected: a new document (a second owner for G2's outcome); editing the G2 archive; deciding the G3 questions here.
Reversibility: cheap.

### 2026-10-08 — #187: audit retention is one setting, off by default, pruned by the Audit store
Context: [#187](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/187). D5 selected no retention, so `audit_event` grew without bound; `Prune` existed but covered only outbox rows and host registrations. Ben ruled 2026-10-08: Platform schedules the prune, off by default, set by `Audit:RetentionDays`; no archival or export.
Chosen: `Platform:Audit:RetentionDays` (whole days, 1–36500, `[Fingerprinted]`); absent keeps rows forever. When set, the Audit store registers `platform.audit.prune`: Worker, hourly, leased, at most 500 rows per statement and 100 statements per tick, with a new instant-only index. Not audited; logged. An invalid value fails startup as `Configuration`/`InvalidSetting`. The member moves the fingerprint to `szdfp4`. Archival and export are out of scope.
Rejected: a `PruneTarget` on Persistence's `PruneWork` (Persistence would own the audit table); the module reading `IConfiguration` itself (unfingerprinted); `0` meaning off; per-tenant or per-class retention; a configurable interval or batch size; an audit row per prune; a readiness check on prune failure.
Reversibility: the key name is expensive once operators set it; everything else is cheap.

### 2026-10-08 — #60: optimistic concurrency is an opt-in `IVersioned` column checked by `IVersionGuard`
Context: [#60](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/60). Two hosts writing the same row during a restart overlap (Automator's API and worker, BarStrad's staff and table) silently overwrote each other. Ben ruled 2026-10-08: opt-in optimistic concurrency.
Chosen: a marker `IVersioned { long Version { get; } }` mapping to a consumer `version` column (non-null, `1` on insert, advanced by exactly one per guarded write). `IVersionGuard` runs the consumer's own guarded statement on the ambient Write transaction and judges the matched-row count: 1 succeeds, 0 is the new non-retryable `TransactionError.StaleVersion` and dooms the unit of work (staged outbox and audit discarded even if the work ignores the result), more than 1 throws `ContractViolation.GuardedWriteNotSingleRow`; outside a Write transaction throws `GuardedWriteOutsideWriteTransaction`. The consumer's statement predicates and advances the version (I-P2). Product boundaries answer 409 `ErrorEnvelope("StaleVersion", correlation)` or a fixed tool failure, naming no version. No Platform migration; adopting tables add the column in their own new migration. The Node game-service keeps its existing CAS.
Rejected: `xmin` or rowversion (provider-native, none on SQLite); a timestamp (clock skew across hosts); a GUID token (no order); SQL-building helpers (an ORM, against d3 2026-08-03); a convention with no Platform code; reusing the retryable `Conflict`; letting the work commit after a stale write; a Hosting-side mapper (Hosting may not reference Persistence); echoing the current version; a test double in `Platform.Testing` (no second consumer yet).
Reversibility: the marker and column are expensive once a consumer adopts them; the error variant is cheap before release; dooming is cheap to relax and expensive to tighten, which is why the strict form was chosen.

### 2026-10-08 — #62: the dispatcher records an inbox row before each handler, so database effects are exactly-once
Context: [#62](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/62). Outbox delivery is at-least-once and every handler re-implemented its own duplicate check, or none. Ben ruled 2026-10-08: an inbox seam.
Chosen: `platform_inbox (message_id, consumer, tenant, …)` in a new Platform migration `0004_create_inbox`; the consumer is the registration's `EventTypeName`, unique while one handler per type holds. Every dispatch, always on, runs one `INSERT … ON CONFLICT (message_id, consumer) DO NOTHING` inside the handler's write transaction before the handler; 0 rows means a duplicate, which skips the handler, commits nothing, marks the row processed, consumes no attempt and logs at Information. The tenant column is stamped from the outbox row, not in the key. `PruneTarget.OrphanedInboxRecords` deletes records whose outbox row is gone. The guarantee covers in-database effects only; handlers stay idempotent for external side effects. No public inbox API.
Rejected: the handler's CLR name (renames change the key); a new consumer-name parameter; no tenant column (per-tenant purge cannot find rows); tenant in the key; an opt-in; SELECT-then-INSERT (races under READ COMMITTED); recording after the handler; treating a duplicate as failure or poison; a public record-and-check API (no second consumer; Billing owns its receipts); a cascading foreign key (SQLite does not enforce it by default); an age-only prune window; folding the table into `0002` (I-T7); claiming exactly-once delivery.
Reversibility: the tenant column and migration are expensive once shipped; the consumer value is cheap before first release; the rest is cheap.

### 2026-10-08 — #264: a mirror past its maximum age cannot answer, so a mirrored role's revocation is bounded
Context: [#264](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/264), red-team F3 (`design/redteam/2026-09-25-10-design.md`). The 2026-09-25 grant-source rule made issuer-managed roles reach the evaluator through a consumer's mirror and left the mirror's freshness to the consumer, unbounded, so a role removed at the issuer kept granting until the sync ran, and the revoke-then-deny harness passed because it revoked the mirror's row. The owner ruled: cap the staleness.
Chosen: a provider over a mirror implements `IMirroredPermissionProvider` (Abstractions) and reports the instant its tenant's last successful sync began reading the issuer, written by the consumer's sync with the rows. The evaluator (Core) reads it before the grants; null or older than `Platform:Authorization:MirrorMaximumAge` means the provider cannot answer, recorded as `ProviderUnavailable` naming it on S24's `ProviderFailure`. The maximum is one `[Fingerprinted]` value per host, default 15 minutes, 30 seconds to 1 day; unset means the default, and no value means unbounded. `PermissionProviderHarness.AssertIssuerRevocationDeniesWithinBoundAsync` proves the bound by revoking at the issuer through the host's evaluator and clock. The promise for a mirrored role is "within the configured maximum of its removal at the issuer"; every other grant keeps the next-request promise.
Rejected: the commit instant as the stamp (a revocation during a long read escapes the bound); a Platform-owned stamp or sync (a role store I-A9 forbids, a vendor adapter per issuer); the provider checking its own age (one rule re-implemented per consumer); per-provider maxima (free-text names as key paths; additive later); no default, or required only when a mirror is registered (most hosts mirror nothing); a one-hour default (no better than Entra's token lifetime); unset or a sentinel meaning unbounded (F3 as a default); asking the issuer per request (a vendor API on the request path); answering a stale mirror as forbidden (an outage read as a refusal).
Reversibility: the default and range are cheap; the interface and the stamp's meaning are expensive once consumers implement them; admitting an unbounded maximum reopens #264.

### 2026-10-08 — #61: seeding is `ISeeder`, run by migrate mode after migrations, each seeder stating its tenant
Context: D3's `NoAmbientOperationScope` remedy names "a seeder" and none existed. The owner ruled for an `ISeeder` beside `IMigrationRunner`, run by migrate mode.
Chosen: `ISeeder { Module; Name; SeedAsync(ct) }` collected from DI. It runs after every successful `ApplyAsync`, including one with nothing pending, outside the lock, in composed-module topological order then registration order. Migrate mode now registers modules in topological order and the Core ambient defaults. The seeder opens its own scope with an explicit tenant and `Principal.LocalSystem`, and must converge. A throw is `MigrationError.SeedFailed`, exit 1. Development start does not seed.
Rejected: migrate mode opening a scope on the implicit tenant (silent wrong-tenant writes for multi-tenant consumers); seeding under the migration lock (a runner API change); a `SeedError` type and a new exit code; seeding at development start (couples #61 to #68).
Reversibility: expensive for `ISeeder` and the `SeedFailed` code; cheap for ordering and the development choice.

### 2026-10-08 — #68: development-environment migration is built, in Persistence, through the runner's lock
Context: D3 promised automatic migration in development and left four questions open (D3 contract, *Unresolved* 8). The owner ruled to build it.
Chosen: an internal Persistence lifecycle service added by `AddPlatformPersistence`. In `StartingAsync` it calls `IMigrationRunner.ApplyAsync` when the derived `PlatformOptions.Environment` is `Development`, with no setting. `Failed` and `HistoryTableCollision` fail startup. `Locked` waits a second and retries until free. `Unavailable` starts not-ready.
Rejected: an opt-in or opt-out setting; a hook-based implementation (Persistence is not a module, and it would couple #68 to #63); failing on `Locked` (web and worker started together would always fail one); retrying on `Unavailable` (a silent hang).
Reversibility: cheap — no public surface.

### 2026-10-08 — #63: `IPlatformModule` gains `InitializeAsync` and `ShutdownAsync`
Context: `IPlatformModule` had only `Register`. Modules that need start-time work each register a hosted service, ordered by registration rather than the graph.
Chosen: two default interface members, run by a Hosting lifecycle service in `StartAsync` (after every `StartingAsync` check, before ticks and the listener). Initialization is topological and sequential. Each call gets a fresh scope's provider. Shutdown runs in reverse, exactly once per initialized module, including after a later startup failure. An initialization throw is `HostStartupError.ModuleInitialization` after unwinding. Migrate mode runs no hook.
Rejected: abstract members (break every module); a separate interface (the ruling says on `IPlatformModule`); concurrent initialization; the root provider; migrating Licensing, Mcp and Identity's hosted services now.
Reversibility: expensive — every module implements the interface.

### 2026-10-08 — #80: the public roadmap renders the active effort's slices
Context: [#80](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/80). The site's roadmap has rendered `design/d3/30-slices.md`, a closed effort's ledger, ever since the archive move. It shows D3's non-goals, one of which ("self-host only") D5 no longer holds.
Chosen: the roadmap renders `design/30-slices.md`, whichever effort is active. That ledger's Landed table rows and Landed bullets count as shipped slices, alongside its outstanding headings. The hand-authored non-goals follow the active brief. Ben ruled 2026-10-08.
Rejected: keeping D3 (the page then describes nothing being built); rendering both active and archived ledgers (a second page of history with no reader asking for it).
Reversibility: cheap.

### 2026-10-08 — #85: the engine owns the in-memory rollback on a failed persistence write
Context: [#85](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/85). The engine orders its in-memory update differently on each write path. `submitAction` mutates the held record and `saveGame` sets the save before persistence is awaited, and `attemptCounter` is never rolled back. So a failed `put` leaves memory ahead of the store. The Adventures compare-and-swap on `attempt_counter` rejects a lost write but leaves the rollback "out of this repo's control".
Chosen: the correction is the engine's. Its in-memory record rolls back, or is committed only after the write succeeds, uniformly on all four paths. Carried to [The-Running-Dev/SubZeroDev.GameEngine#580](https://github.com/The-Running-Dev/SubZeroDev.GameEngine/issues/580). The game service takes the fix by upgrading the pinned engine; it adds no workaround of its own. Ben ruled 2026-10-08.
Rejected: a workload-side re-read after a failed write, which is a second copy of the engine's state rules in the host; documenting the ordering as accepted, which leaves a non-retryable `503` whose retry applies the action twice.
Reversibility: cheap — the decision moves no Platform code.

### 2026-10-08 — #97, #100: Adventures becomes a hosted consumer; its identity stays evidence (supersedes g1 2026-08-09)
Context: [#97](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/97). D3's done-criterion, a product running on Platform by the standard registration call, has been met only by Platform's own sample. The g1 entry of 2026-08-09 made the Adventures POC a reference that G2 and G3 read, not a consumer. [#100](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/100) asks whether Adventures' identity replacement makes an Identity package.
Chosen: Adventures becomes a hosted consumer that restores the packages from the feed. The adoption work happens in the Adventures repository. Each gap it finds is filed here as D4 extraction evidence, not built speculatively. Its identity replacement stays evidence only, and no Identity package is created from it. `implementation-plan.md` §D3 states this. Ben ruled 2026-10-08.
Rejected: keeping Adventures as a reference only, which leaves D3 permanently unproven by an outside consumer; building its identity model into Platform, which is one consumer and the extraction guard refuses it.
Reversibility: cheap — the adoption is the consumer's work, and Platform's surface does not change for it.
Supersedes: `g1/90-decisions.md` 2026-08-09 — "The Adventures POC is a reference for G2 and G3, not a source this effort copies from". The g1 log is a closed record and is not edited.

### 2026-10-08 — #98: a principal is an authenticated party, and an account is optional
Context: [#98](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/98). BarStrad's principal is a table and SkyNet HR's is an operator asserted by a proxy, and neither is an account. The Automator and GEaaS have accounts.
Chosen: the question is answered by D5's principal model as built. `PrincipalKind` is `Anonymous`, `Account`, `Delegated` or `System`, and `PrincipalId` is the (issuer, subject) pair, so a party with no account is a first-class principal. No change follows. Ben ruled 2026-10-08.
Rejected: requiring an account behind every principal, which forces BarStrad and SkyNet HR to invent one.
Reversibility: expensive — the principal model is public contract.

### 2026-10-08 — #99: the Automator's audience is both, and stays unsettled
Context: [#99](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/99). One reading of the Automator is an open-core product licensed to outside customers. The other is the owner's own tool for managing plugins and workflows. The Identity row means different things under each.
Chosen: record it as stated and unsettled, the way BarStrad's billing cell was recorded. `platform-identity.md` §4 carries the caveat. Nothing is designed on either reading alone. Ben ruled 2026-10-08.
Rejected: picking a reading to unblock Identity's design, which is guessing on the owner's behalf.
Reversibility: cheap.

### 2026-10-08 — #24: BarStrad is a service operated for venues; an unreachable channel degrades readiness
Context: [#24](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/24). BarStrad's commercial model was undecided. A hosted answer contradicted the D3 brief's self-host-only non-goal. Separately, a chat channel is online in steady state, which conflicts with deployments that have no outbound network.
Chosen: BarStrad is a service the owner operates for venues. The D3 brief is closed, and D5's brief already names operated SaaS, so no brief changes. The billing cell in `platform-identity.md` §4 and `application-modules.md` §5 say so. Independently of the deployment answer, an unreachable channel degrades readiness and never fails the host or the triggering work (`events-and-notifications.md` *Delivery failure*). Ben ruled 2026-10-08.
Rejected: self-hosted and licensed per venue installation, which the owner did not choose; a channel whose reachability is a startup condition, which makes an offline deployment unstartable.
Reversibility: expensive once a venue is onboarded; cheap before.

### 2026-10-08 — #25: a channel credential never leaves the server
Context: [#25](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/25). A live Discord webhook is committed in the BarStrad repository, and a second reaches its browser bundle.
Chosen: Notifications carries the rule that a channel credential is held and used by the host, never sent to a browser, a client configuration or a committed file (`events-and-notifications.md` *Delivery failure*). This is stronger than "a secret appears in no log, span or metric label". The issue stays open until the owner rotates the webhooks in the BarStrad repository. Ben ruled 2026-10-08.
Rejected: relying on the log-redaction rule, which a bundled credential never touches.
Reversibility: cheap.

### 2026-10-08 — #94: the sign-in module's callback is mapped at the method's `RedirectPath`
Context: the contract (§ 13) lists the module's endpoints under `/signin/<method>` and also requires a configured `RedirectPath`. A callback at `/signin/<method>/callback` would make `RedirectPath` a second, redundant setting.
Chosen: begin, token and signout are mapped under `/signin/<method>`; the callback is mapped at the configured `RedirectPath`, so the address registered with the provider is the address that serves it. Two methods may not share a `RedirectPath`; that fails startup as `InconsistentSettings`.
Rejected: a fixed `/signin/<method>/callback` that ignores `RedirectPath`; a prefix-only `RedirectPath`.
Reversibility: cheap before a host registers a redirect address with a provider; moderate after.

### 2026-10-08 — #94: the id token is read without a signature check, and `{id_token_hint}` is always empty
Context: the session holds no id token (I-S1), and Platform validates the access token on every request, so the id token's only job here is to name the subject.
Chosen: the id token received directly from the token endpoint over TLS is decoded unverified, as OpenID Connect Core 3.1.3.7 permits, with `iss`, `aud`, `nonce` and `exp` checked; a failure is the generic sign-in error. `{id_token_hint}` expands to an empty string because no id token is kept. `Scopes` must include `openid`.
Rejected: verifying the id token's signature (a second validation path beside Identity, and a keys dependency this package must not take, I-I12); keeping the id token in the cookie (widens I-S1 and the cookie size).
Reversibility: cheap — verification and a hint can be added behind the same settings.

### 2026-10-08 — #94: hooks adjust, and the module re-sets what it owns
Context: I-S3 says a hook cannot change what Platform trusts; the contract does not say whether a hook may return a protected parameter.
Chosen: `AdjustAuthorizeRequest` returns the full parameter set, which replaces the module's; the module then re-sets `response_type`, `client_id`, `redirect_uri`, `scope`, `state`, `nonce`, `code_challenge` and `code_challenge_method`. `ReplaceEndSessionAddress` must return an absolute http or https address, or the sign-out fails. The anti-forgery header `X-Platform-SignIn` is required on the token endpoint only. After a successful callback the person is returned to `/`. Discovery-based sign-out appends `client_id` and `post_logout_redirect_uri`. An `http` issuer or endpoint is accepted only on a loopback host.
Rejected: failing when a hook returns a protected name (an unhelpful failure at the first request); a configurable post-sign-in path (not in #94).
Reversibility: cheap.

### 2026-10-08 — #94: `SubZeroDev.Platform.SignIn` is the thirteenth package
Context: the package scripts and `commercial-guide.md` count the checked packages.
Chosen: SignIn joins the packed, manifest-checked and consumer-graph-checked set; the count moves from twelve to thirteen. It reads the generic path's `Issuer` keys from configuration rather than referencing Identity (I-C7).
Rejected: shipping it unpacked; referencing Identity to read the issuers.
Reversibility: cheap.

### 2026-10-08 — The sign-in module is stateless, hosted on the Identity host, with a hook above vendor configuration
Context: [#94](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/94). The owner ratified the 2026-10-07 proposal below, agreed all six premises of the `/design` pass and chose the minimal approach. The proposal left the session store, the callback host and the hook shape open.
Chosen: an optional `SubZeroDev.Platform.SignIn` module (`10-design.md` *Data model* § 11, *Alternatives* § 14). The session is an encrypted cookie and the module owns no row. There is no refresh token and no client secret. The client reads its access token from a same-origin, anti-forgery-guarded endpoint, so the authentication seam is not widened. Vendor dialect is two configuration settings, an end-session template and extra authorize parameters, so a vendor package still references no Platform package (I-I13). The hook sits above configuration, is registered by the host, and cannot change what Platform trusts. Sign-out does not revoke an issued token. The endpoints are mapped on the operated host that takes Identity, recorded as a recommendation under *Open questions* 5. The 2026-09-26 accepted risk is superseded for a host that takes the module. Contract Unresolved item 4 is closed against this.
Rejected: a stateful backend-for-frontend with a session store; a client-side contract with no host; widening the seam to read the cookie; a vendor package that supplies the hook; a raw callback with no configuration path.
Reversibility: cheap to add, moderate to withdraw — the keys and endpoints become public contract once a host depends on them. A session store can be added later without removing this module.

### 2026-10-07 — Platform hosts an optional sign-in module so #94's hook has a method to attach to (reverses 2026-09-26) [PROPOSED — owner to ratify]

Context: [#94](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/94). The 2026-09-26 entry accepted red-team F1: with Platform a resource server (`10-design.md` *Alternatives* § 11), #94's first, third and fourth criteria — a hook per configured sign-in method, sign-out proven against a provider without the standard one, documentation that reaching for the hook is expected — describe a sign-in method Platform does not have. The owner ruled to reverse that choice rather than amend #94 down to the validation half.
Chosen: [DECISION TO BE MADE BY THE OWNER — the three points below are the ones the 2026-09-25 entries and #94's thread say must be settled, with the reading most consistent with them.]
1. **Placement.** Sign-in is a separate optional module, an ordinary client of Platform's authentication seam, as § 11's reversibility note already allows. The core stays protocol-only and a resource server. It references no vendor package (ADR-006 rule 1) and absence is invisible.
2. **The hook.** The module exposes the underlying OpenID Connect options and events for each configured scheme through a post-configure callback: configuration binds first and the hook adjusts after, so the configuration path stays authoritative. Three quirk classes are in scope: non-standard endpoint construction (Auth0 `/v2/logout`), extra authorize-request parameters (`audience`), and nothing else. Scheme forcing behind a proxy is a deployment concern for `ForwardedHeaders` and is documented as out of the hook's scope.
3. **Vendor packages.** The 2026-09-25 rule stands: a vendor package is a configuration source over the generic path, never a parallel implementation, and references no Platform package. `UseAuth0()` and its siblings write the module's settings, including the vendor's logout URL, once. Supabase Cloud and self-hosted Supabase are two methods that do not pretend to be one (self-hosted is not an OIDC provider until `GOTRUE_OAUTH_SERVER_ENABLED` is set; ADR-004).
Consequences for the design: the authentication seam is not widened — the module owns the request, query and cookie surface and the session; the session is durable state, so Identity's "no rows" must be amended or the module must hold its own store; § 11's *Rejected* reasons are answered by that placement, not removed. The web shell is static assets, so the module's host is a separate decision.
Rejected: widening the seam to carry cookies and queries (it widens it for Mcp too); a raw callback with no vendor package (invites vendor code in each consumer's host, the BlueLionheart failure #94 came from); amending #94 to the validation half (the owner's ruling).
Reversibility: cheap to add as a module; expensive to withdraw once consumers hold sessions on it (§ 11).
Supersedes: 2026-09-26 (accepted risk) — a later `/design` pass edits `10-design.md` § 11 and the contract's Unresolved item 4; this entry does not edit them.

Open for the owner before this is ratified: (a) whether the session store is a new module-owned table or the module is stateless (signed cookie); (b) which host serves the callback given D5 ships only static assets; (c) which vendor package is first.

### 2026-10-07 — The retained `Principal.Claims` stands; #92's criteria are amended to match

Context: [#263](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/263), red-team F2. `10-design.md` retains the public `Principal.Claims` and states the no-permission-data rule as a contract obligation on a consumer's provider, not a structural guarantee. [#92](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/92)'s first criterion asks that the principal carry no permission data and that the mistake be structurally impossible. The two cannot both hold. The owner ruled.
Chosen: the design stands and #92 gives way. `Principal` keeps `Claims`, which is the raw authentication result and never a grant source. For Platform's own providers the rule is structural (I-I6, I-A2, I-A10 and the revoke-then-deny test). For a consumer's provider it is a contract obligation, with the same test shipped in `Platform.Testing` as a harness, as 2026-09-25 recorded. #92's criteria are reworded to say this. Contract Unresolved item 3 is closed.
Rejected: removing or filtering `Claims` so that a token claim cannot grant, which would make the rule structural for consumers too. It is breaking at 0.x and cuts against `10-design.md` *Alternatives* § 5. It also does not hold while the raw authentication result stays reachable through DI, so it would promise more than it delivers. It would also be a contract and schema amendment, which is a `/spec` change and not a decision-log entry.
Reversibility: cheap to relax, expensive to tighten, as 2026-09-25 recorded.

### 2026-10-06 — Remove the per-repository SessionEnd cost hook
Context: `.claude/settings.json` ran `pwsh … tools/Measure-Session.ps1` on `SessionEnd`, but that script no longer exists — it left this repository when the kit moved to a single home install, and the kit later ported it to Node as `measure-session.ts` — so the hook failed at the end of every session. The kit's setup installs one global `SessionEnd` hook in `~/.claude/settings.json` that logs every project.
Chosen: remove this repository's `hooks.SessionEnd` entry, in the AgentKit sync to `v2026.10.06.1`. Nothing else in `settings.json` changes.
Rejected: point it at `measure-session.ts` — the global hook already runs that script, so every session would be logged twice; leave it — it keeps failing at every session end.

### 2026-10-01 — A credential no provider claims ends the chain rejected, not anonymous

Context: the `/spec` #94 closing review. A provider answers `Principal.Anonymous` for a well-formed
bearer token from an issuer it does not trust, so a token every registered provider declines ends the
chain `Anonymous`. The test-grade provider already behaves this way, and the generic bearer path was
specified the same way. `10-design.md` *Control flow*, path 1, step 1 says a bad token is rejected at
the transport and only an absent one continues as `Anonymous`. The owner ruled that the chain end
rejects.
Chosen: a provider that sees a credential of its kind it does not claim answers a new variant,
`AuthenticationError.CredentialNotClaimed`. The chain treats it as a pass and moves to the next
provider. If no provider establishes a principal and none rejects, the chain answers
`CredentialRejected`, naming the first provider in registry order that answered `CredentialNotClaimed`.
`CredentialNotClaimed` never leaves the chain (I-I15). The test-grade provider and the generic bearer
path both answer this way, and `/plan` slices the code change.
Rejected: the chain reading the `Authorization` header itself, because Core would then know every
credential format. Rejected: changing `IAuthenticationProvider`'s return type to a three-way result,
which breaks every provider's signature for one case an error variant can carry. Rejected: keeping
`Anonymous`, because a token from an untrusted issuer would succeed at being ignored, which path 1
forbids.
Reversibility: cheap until the slice lands. Moderate after, because a host's callers then see
unauthenticated where they saw an anonymous answer.

### 2026-09-30 — A generic-path settings defect is a `ConfigurationError` naming the full key

Context: `/spec` #94. Path 2, step 8 requires a malformed or unrecognised generic-path setting to fail
startup and name both the provider and the key. It does not say which error carries the failure.
Chosen: `HostStartupError.Configuration`, carrying the existing `ConfigurationError` variant that fits
the defect. `MissingRequiredSetting` covers a required key that is absent. `InconsistentSettings`
covers two keys that exclude each other. `InvalidSetting` covers a bad value, an unrecognised key, and
a value or section in the wrong place. The key is reported in full, as
`Platform:Identity:Bearer:<name>:<setting>`, so one string names the provider and the setting.
Rejected: a new `HostStartupError` code for the same fault. A settings defect is already a
`ConfigurationError` everywhere else in the host, and a second code would give operators two things to
search for. Rejected: carrying the provider name and the setting as separate fields. The full key is
exactly what the operator searches their configuration for.
Reversibility: cheap before a consumer ships. A new variant could be added later without removing
these.

### 2026-09-30 — The generic bearer path's configuration schema: one section per provider, fixed keys, bounded defaults

Context: `/spec` #94. `10-design.md` *Data model* § 10 names the generic path's settings and makes
their configuration schema public contract. It leaves the key names, the pattern syntax, the key
formats, the defaults, the bounds and the refresh mechanics to this contract.
Chosen: each child section `Platform:Identity:Bearer:<name>` is one provider, and `<name>` is its
name. The keys are `Issuer` or `IssuerPattern`, `Discovery` or `SigningKeys`, `Audiences`,
`Algorithms`, `ClockTolerance`, `SubjectClaim`, `DisplayNameClaim` and `KeyRefreshInterval`. A pattern
carries `{tenantid}` exactly once and it matches one or more characters other than `/`, which is
Entra's own template syntax. `Discovery` must be `https`, and `SigningKeys` is an inline JSON Web Key
Set of public keys. Algorithms come from `RS*`, `PS*` and `ES*`, with a default of `RS256`, the one
algorithm OpenID Connect requires every provider to support. `ClockTolerance` defaults to one minute
and is capped at five. `KeyRefreshInterval` defaults to five minutes and lies between 30 seconds and
one day. Every fetch has a fixed 30-second timeout, and the discovery document's `issuer` must equal
the configured issuer or pattern ordinally. The background work is
`platform.identity.key-refresh:<name>` and the readiness check is `platform.identity.key-set:<name>`. A
token naming no key id is tried against the cached keys and never causes a fetch. Registering the
Identity module and writing configuration is the whole registration, and a vendor method takes the
provider name as an argument.
Rejected: a regular-expression pattern, because one that matches too much accepts any issuer and no
reviewer sees it. Rejected: a tenant list, which is configuration no issuer publishes. Rejected: JWK
members spelled as configuration keys, because JOSE's own member names would collide with the rule
that an unrecognised key fails startup. Rejected: plain-`http` discovery, because the test-grade
provider already serves development. Rejected: the token library's own defaults, a five-minute
tolerance and a twelve-hour refresh, under which a failed startup fetch leaves the host not-ready for
twelve hours. Rejected: a configurable fetch timeout, one more public key with no requirement behind
it. Rejected: a registration call per issuer, which makes a vendor package chain off Identity, as
*Alternatives* § 12 refuses.
Reversibility: moderate. Keys are public once a vendor package writes them. Keys can be added but never
withdrawn, and a default never moves in the accepting direction.

### 2026-09-29 — Twenty-one invariants without a whole-statement test are held by instruction, not code

Context: `/align`. The #245 bootstrap copied each invariant's "Enforced by" prose into
`Enforcement: code` without naming a test, so seventy-three rows failed the design-state check for
missing Evidence. A row is `code` only when a test evidences the whole statement. Tests cover the whole
statement for fifty-two rows. For nineteen they cover part of it. I-B6 has code and no test, and nothing
implements I-A11 yet.
Chosen: the fifty-two keep `code` and name their tests as Evidence. The other twenty-one become
`instruction` and lose their "Enforced by code" clause. The nineteen partly tested rows name those
tests as Evidence. Each row flips back to `code` in the change that adds the test covering the rest of
its statement, and one tracker issue lists the gaps. I-A6 and I-L5 keep `code`, and their clauses stop
citing a sample scenario and an offline CI run that do not exist.
Rejected: keeping `code` and citing the partial tests, because the check would then pass on rows
nothing fully proves. Rejected: `code, instruction` for the partly tested rows. It is true for most of
them, but it hides which part lacks a test. The tracker issue says which part.
Reversibility: cheap. Each row is one field.

### 2026-09-28 — A provider failure is carried normalised, first-registered, and behind an audit failure

Context: `/spec`, declaring the member the entry below routes to it. That entry fixes that a denial
carries a provider's error in the shape `AuditFailure` takes and is answered as a retryable failure. It
leaves open what the member carries when a provider returns a variant other than `ProviderUnavailable`,
which provider it names when several fail, and which answer wins when the same denial also carries an
`AuditFailure`.
Chosen: the member is `ProviderFailure`, named like `AuditFailure`. It is always `ProviderUnavailable`
naming the provider as the registry knows it, whatever the provider returned. It names the first
provider in registry order that could not answer. Where both are set, `AuditFailure` decides the answer.
`PermissionDenied` now requires that every provider answered, and a provider returns an error only when
it could not answer.
Rejected: carrying the provider's error as returned. A provider returning `PermissionDenied` would then
reach the caller as a non-retryable error that the caller must still answer as retryable. A provider
could also name another provider in a decision. *Error semantics* § 4 already settles the same shape for
the audit sink, where the class decides, not the sink. Rejected: a collection of every failing
provider. The caller's answer is the same for one failure or many, and a list reads as an inventory of
the outage, which the decision does not claim to be. It also departs from the `AuditFailure` shape the
entry below chose. Rejected: `ProviderFailure` deciding when both are set. Answering with the provider's
code drops a `Required` write's failure from the response, which § 4 forbids.
Reversibility: cheap until the slice lands; moderate after, since a consumer may read the member.

### 2026-09-28 — A provider that cannot answer reaches the caller as a retryable failure, not as forbidden

Context: `/align` over the tree at f4822a5. `20-contract.md` *Public surface* § 3 ("a denial the caller
may retry") and *Error semantics* § 2 (`ProviderUnavailable`: retryable, "return a retryable failure;
the denial stands for this request"), and `10-design.md` *Failure modes*, the database ("A denial from
an unreachable store returns a retryable error code"), all promise a retryable answer. The evaluator
(`src/SubZeroDev.Platform.Core/Authorization.cs`) discards a provider's error and counts that provider as
having granted nothing, and `AuthorizationDecision` has nowhere to carry the error, so the Hosting
pipeline answers `PermissionDenied`/403 while the Organizations store is unreachable. D5-S4's test
asserts `ProviderUnavailable.IsRetryable` on the error alone, never what a caller receives.
Chosen: the code changes to match the documents. When a denial is reached with at least one provider
unable to answer, the decision carries that provider's error, in the shape `AuditFailure` already takes,
and Hosting and Mcp answer it the way they already answer `AuditFailure`: a retryable failure, not
forbidden. The denial still stands for the request, so failing closed is unchanged. The member is a
public-surface addition: `/spec` declares it and `/plan` slices it.
Rejected: rewriting the documents to make a provider failure an ordinary forbidden — it tells every
client that an outage is a missing permission, records a policy denial no policy made, and withdraws
"fails closed and still retryable" (*Error semantics* § 2); `EvaluateAsync` returning a failure result —
breaks "returns a decision, never a failure result" (*Public surface* § 3) and every call site's return
type.
Reversibility: cheap until a consumer reads the new member; moderate after, since it is public surface.

### 2026-09-26 — Red-team F1 is an accepted risk: #94's sign-out promise is not Platform's to deliver

Context: [`redteam/2026-09-25-10-design.md`](redteam/2026-09-25-10-design.md) F1 (BLOCKING), against
`10-design.md` @ 52794de. Under the resource-server decision below, a vendor configuration package
supplies bearer-validation settings only, so the non-standard sign-out #94 was raised over (Auth0's
hand-built `/v2/logout`) stays with each client — the hand-wiring #94 exists to remove — and #94's
sign-out proof cannot be run against Platform. The decision is known and retained, with its
alternatives (*Alternatives* § 11); its consequence for #94 was not recorded.
Chosen: accepted risk. Platform stays a resource server. Recorded consequences: #94's first, third and
fourth done-when criteria — a hook per configured sign-in method, sign-out proven against a
non-conforming provider, documentation that reaching for the hook is expected — describe a sign-in
method Platform does not have, and D5 does not deliver them; a vendor's sign-in and sign-out quirks are
handled once per client, and a vendor configuration package does not reduce that; contract Unresolved
item 4 is settled for the validation half only, and the `/spec` pass that closes it says so rather than
treating #94 as satisfied.
Rejected: defect — the finding names no higher-precedence source the retained decision contradicts; the
brief's Identity row commits to integration seams for hosted authentication, not to sign-in. Not
sustained — the consequence is real and was unrecorded.
Reversibility: cheap — a backend-for-frontend can still be added as a module that is an ordinary client
of Platform (*Alternatives* § 11).

### 2026-09-25 — Every permission provider takes grants from a source revocable before the credential expires

Context: `20-contract.md` § Unresolved item 3 (#92) — may a consumer-registered `IPermissionProvider`
derive grants from token claims? A claims-derived grant satisfies I-A10 literally, since nothing is
carried between requests, and defeats it, since the token carries the grant until the issuer-chosen
expiry. `Principal.Claims` is public and the raw authentication result is reachable through DI, so
Platform cannot stop a consumer's provider reading claims.
Chosen: the grant-source rule in `10-design.md` *Data model* § 3 — every provider, Platform's and a
consumer's, takes grants from a source revocable while the caller's credential is valid; claims and the
raw authentication result are never a grant source. Structural for Platform's providers through I-I6 and
a revoke-then-deny test; a contract obligation for a consumer's, with the same test shipped in
`Platform.Testing` as a harness. Cost: issuer-managed roles (Entra app roles, Keycloak realm roles)
reach the evaluator only through a store the consumer mirrors them into.
Rejected: removing `Claims` from `Principal` — breaking at 0.x, cuts against *Alternatives* § 5, and
does not hold while the raw result is reachable; claims-derived grants with a declared latency — hands
revocation latency to the issuer, which is #92's failure; leaving it to the consumer — #92 asks for all
providers.
Reversibility: cheap to relax, expensive to tighten — see `10-design.md` *Alternatives* § 10.

### 2026-09-25 — Platform validates credentials and never conducts a sign-in

Context: #94's done-when includes sign-out, and the owner's sketch reads as vendor sign-in inside
Platform. `IAuthenticationRequest` carries headers only, by design, and Mcp uses the same seam.
Chosen: Platform is a resource server. Interactive sign-in and sign-out are between the client and its
issuer (`10-design.md` *Control flow*, path 4); no Platform host serves a sign-in page, callback,
session or sign-out endpoint. An issued access token stays valid at Platform until expiry; grants do not
ride in it, so authorization is unaffected.
Rejected: a backend-for-frontend host conducting the code exchange and holding a session — needs a body,
query and cookie surface the seam excludes, a session store Identity would own against "no rows", a host
D5 does not ship (the shell is static assets), and every vendor's sign-in quirk as Platform's forever.
Reversibility: cheap — a BFF can be added later as a module that is an ordinary client of Platform.

### 2026-09-25 — Identity gains a production-grade generic bearer path, asymmetric only, with background key refresh

Context: ADR-009 "Not decided here" left the production bearer provider open, and #94's vendor packages
need a generic path to be sugar over. Only the test-grade HMAC and upstream-proxy providers exist.
Chosen: a generic bearer provider in Identity, one per trusted issuer, configured by issuer (or issuer
pattern), key source (discovery or fixed public keys), non-empty audiences, asymmetric algorithms only,
bounded clock tolerance, subject claim and optional display-name claim (`10-design.md` *Data model*
§ 10). Settings validated at startup, an unrecognised key failing it; the key fetch never fails startup.
Keys refresh on an interval off the request path with a whole-set atomic swap, per instance and with no
lease; an unknown key id is `CredentialRejected` and never triggers a fetch; a failed refresh keeps the
previous set and degrades readiness. This amends the 2026-08-24 design statement that D5 adds no
background work of its own: the key refresh is the one exception.
Rejected: request-path fetch on an unknown key id — an attacker-driven amplifier against the issuer;
a production shared-secret mode — a secret holder can mint tokens; deferring the provider until a vendor
package needs it — leaves nothing for the package to be sugar over.
Reversibility: moderate — the settings schema becomes public contract; the refresh policy is internal.

### 2026-09-25 — Token validation adopts Microsoft.IdentityModel; the 2026-08-24 "in-box" claim was wrong

Context: the 2026-08-24 placement entry and `10-design.md` *Alternatives* § 9 said token validation is
the in-box ASP.NET Core authentication handlers. Checked against the installed shared framework 10.0.12,
the JWT bearer handler and the token and discovery libraries are separate packages. That entry is not
edited; this one corrects it.
Chosen: `Microsoft.IdentityModel.JsonWebTokens` and `Microsoft.IdentityModel.Protocols.OpenIdConnect`
(MIT, Microsoft) under the generic bearer path. No IdentityModel type in Platform's public surface — the
provider projects at the boundary, as Mcp does with its SDK. The library's on-demand refresh is called
only from the refresh timer. Licence to be re-checked at the version actually taken, per `AGENTS.md`
*Verification*.
Rejected: `Microsoft.AspNetCore.Authentication.JwtBearer` — an ASP.NET scheme handler bound to the HTTP
context, a second authentication pipeline beside a transport-agnostic seam Mcp also uses, and on-demand
metadata refresh from the request path; hand-rolling over `System.Security.Cryptography` and
`System.Text.Json` — the JOSE algorithm-confusion and key-selection cases are where an original
implementation fails.
Reversibility: cheap — no library type crosses the public surface, so the library can be replaced
behind the provider.

### 2026-09-25 — Vendor dialect ships as configuration-source packages that reference no Platform package

Context: `20-contract.md` § Unresolved item 4 (#94). The owner's 2026-08-09 direction on #94 is a named
package per vendor, sugar over the generic path, never a parallel implementation. Its sketch
(`AddPlatformIdentity().UseAuth0()`) assumes the generic wiring is framework, but ADR-009 point 4 put
providers in the Identity module, so a vendor package implementing a provider would reference a module —
ADR-006 rule 2 (ADR-006:51) forbids it.
Chosen: a vendor configuration package is a configuration source writing the generic path's settings; it
references no Platform package, and a build check fails if it does (`10-design.md` *Data model* § 10,
*Module boundaries* § 5). A quirk configuration cannot express becomes a generic-path capability first.
This pass builds the mechanism and no vendor package; the first is built when a consumer names its
issuer. Proof without one: a configuration source built like a vendor package yields settings equal to an
equivalent settings file.
Rejected: presets inside Identity — departs from the per-vendor direction and couples Identity's release
to every vendor; a vendor-options contract in the framework — fails the seam-admission test with no
producer; vendor packages registering their own provider — rule 2, and the parallel implementation the
direction rules out; a raw callback — rejected by the owner on 2026-08-09; documentation recipes only —
untested, and drift fails at a consumer's startup.
Reversibility: moderate — configuration keys are public contract once a vendor package writes them.

### 2026-09-25 — Hosted and self-hosted deployments of one vendor are two methods when their protocol surfaces differ

Context: #94 asks whether hosted versus self-hosted for one vendor is one method with a discriminator or
two methods. Per ADR-004's 2026-08-10 re-verification, self-hosted Supabase is an OIDC provider only with
`GOTRUE_OAUTH_SERVER_ENABLED` set.
Chosen: two methods whenever the deployments speak different protocol surfaces; one method when the same
settings with different values describe both (`10-design.md` *Data model* § 10).
Rejected: one method with a discriminator — hides that one value may not be OIDC at all, surfacing it
as a rejected token at run time rather than a named requirement at configuration time.
Reversibility: cheap — two methods can collapse later; a diverged discriminator cannot be split without
breaking callers.

### 2026-09-25 — ADR-009 ratifies Identity's placement, superseding the 2026-08-10 withdrawal

Context: `design/g1/90-decisions.md`'s 2026-08-10 entry withdrew a proposed identity-substrate ADR
before it was written, because `platform-identity.md` gave Identity the tier Undecided deliberately
and no G1/D3-era consumer's principal was uniformly an account. Issue #90 later proposed a
Platform-owned Identity package with a user store behind `UseIdentity()`. The D5 brief and design
settled the question instead — Identity is a framework seam plus an optional module that owns no
users — and D5-S2 and D5-S9 built it.
Chosen: [ADR-009](../docs/docs/adr/ADR-009-identity-package.md) records that placement as the
architectural decision, its costs, and the alternatives rejected, including #90's own original
proposal. This entry supersedes the g1 withdrawal rather than editing it — the withdrawal was correct
reasoning for the question as it stood in G1/D3, and `g1/90-decisions.md` is that effort's closed log.
`platform-identity.md` §3's former "Identity, authorization, tenancy" row is split: Identity now
points at ADR-009; authorization and tenancy remain their own, still-open question.
Rejected: editing the 2026-08-10 entry in place to say Identity is now decided; rejected because a
withdrawal that was later reversed is a fact about the repository's history, not an error to correct.
Reversibility: cheap — the entry is a pointer to ADR-009, not a second copy of its content.

### 2026-09-21 — `ConnectionUnauthenticated` is dropped from `McpError`

Context: `/align` after D5 found the variant declared in `20-contract.md` § *Error semantics* 8 and in
`McpError.cs`, and raised nowhere. A principal change on an established session is answered by the
adopted SDK's own 403 at the transport (`McpPrincipalBindingMiddleware`) before any tool is reached,
and a connection with no principal is `Anonymous` and authorized per call.
Chosen: remove the variant from the code and the contract, and state in § 8 where each case is
answered instead.
Rejected: keeping it as a reserved variant, because a declared error nothing raises tells a caller to
handle a case that cannot reach it; and raising it from Platform code, which would duplicate the SDK's
transport answer with a second one.
Reversibility: cheap — re-adding a variant is additive.

### 2026-09-21 — `10-design.md` § Open questions 2 resolved: the shell is a separate front-end build

Context: the question was open since `/design`; `/align` surfaced it with no slice depending on it.
Chosen: a separate front-end build, served as static assets by whichever host chooses to, with no
server-side rendering and no .NET package the backend could reference. Q2 and the already-decided Q4
are struck through in `10-design.md`, per its rule that resolved questions keep their number.
Rejected: a .NET-hosted UI shipped as a package, because the reference it needs is the one ADR-006
rule 1 exists to prevent, and every admin-only convenience endpoint it acquires is a step away from
replaceable.
Reversibility: cheap while no shell exists; the choice is not yet built.

### 2026-09-21 — Organizations and Billing gain a retryable `StoreUnavailable`

Context: both APIs mapped a failed store transaction onto a business variant — Organizations onto
`OrganizationNotFound`, Billing onto `PlanNotFound`, `SubscriptionNotFound`, `InvalidTransition` or
`ProviderEventMalformed` depending on the call — so an outage was answered as a non-retryable fact
about the caller's data. The code carried comments admitting no variant fit.
Chosen: `StoreUnavailable`, retryable, on both error types, returned from every store-failure fallback
including the subscription transition and membership revocation paths. `OrganizationNotFound` is kept
for a membership check that failed. Recorded in `20-contract.md` § *Error semantics* 5 and 6.
Rejected: reusing `OrganizationNotFound` on the ground that it already confirms nothing about
existence, because a caller told "not found" does not retry, and one told nothing distinguishes an
outage from a revoked membership.
Reversibility: cheap before external consumers; a variant removed later breaks every caller's match.

### 2026-09-21 — `NotAMember` names the principal an administrative action targets, never the caller

Context: the contract defined `NotAMember` as a member-only action by a revoked principal; the code
answers a non-member caller `OrganizationNotFound` and raises `NotAMember` only about the principal an
administrative action names. `/align` asked which was right.
Chosen: amend the doc to the code. A caller who is not an active member gets `OrganizationNotFound`, so
existence is never confirmed to a caller who may not see it.
Rejected: raising `NotAMember` for the caller, because it would confirm the organization exists to
someone outside it — the leak `OrganizationNotFound` exists to prevent.
Reversibility: cheap; the variant's meaning is fixed by the doc and one test.

### 2026-09-21 — Auditing is not a pipeline step: the evaluator audits denials, the writer audits actions

Context: `10-design.md` and `20-contract.md` § *Public surface* 11 listed "audit" as a final step, and
I-A8 said the evaluator audits every decision once. The code audits a denial in the evaluator and an
allowed action in the writer performing it, inside that action's transaction.
Chosen: amend the doc to the code, in § 11, the provider rule under *Types* 2, and I-A8.
Rejected: a trailing pipeline audit step, because a record written after the transaction commits can
be lost while the action stands, and one written by the evaluator on allow records a decision rather
than the action that followed.
Reversibility: cheap; the change is to the documents.

### 2026-09-21 — Required audit failures are surfaced, never discarded

Context: `/align` found three paths where a `Required` audit write's failure was dropped: the
authorization evaluator's denial record, `SharedRead.Open`'s escape record, and a `SinkRejected` under
`Required`, which the writer answered as success. I-C7 and I-C8's tests passed vacuously, written
before these paths existed.
Chosen: fix all three and widen the tests. `AuthorizationDecision.AuditFailure` carries the failure
and the HTTP pipeline answers 503 with its code, Mcp a retryable tool result; `Open<TEntity>` returns
`Result<IDisposable, AuditError>` and stays synchronous because its state is ambient; `SinkRejected`
under `Required` surfaces as `SinkUnavailable` naming the sink. I-C7, I-C8 and I-I4's tests were widened
to the paths that now exist. Recorded in `20-contract.md` § *Error semantics* 4 and *Types* 2 and 7.
Rejected: amending the doc to permit best-effort denial records, because a denial that cannot be
recorded would then be answered as though it were, which is the silent-loss `Required` exists to
refuse.
Reversibility: cheap before external consumers; `Open`'s return type is a breaking change after.

### 2026-09-21 — Platform takes two endpoint exemptions: the probes and the Mcp transport

Context: the 2026-09-05 entry below says the probes are the only exemption Platform takes. The Mcp
transport endpoint also carries `ExemptFromPlatformAuthorization`, because its permission varies per
call and it authorizes and checks entitlement per tool call. That entry is not rewritten; this one
amends it.
Chosen: amend the doc to the code. § 11 and the code comments now name both exemptions.
Rejected: declaring a transport-wide permission on the Mcp endpoint, because no single permission
covers every tool, and one that did would be granted to everyone who may call any tool.
Reversibility: cheap; the exemption and the per-call check are both local to `Platform.Mcp`.

### 2026-09-21 — `ByActorAsync` filters on the issuer and the subject

Context: `PrincipalId` is the (issuer, subject) pair, but the audit read API filtered on the subject
alone, so two issuers sharing a subject string read each other's records.
Chosen: filter on both columns. The existing subject-led index still serves the query.
Rejected: documenting the subject-only filter, because a principal's identity is the pair everywhere
else in the contract, and one read that disagrees merges two principals' histories.
Reversibility: cheap; the read API is additive-only and the filter only narrows.

### 2026-09-21 — An endpoint's undeclared permission fails startup as `UnregisteredPermission`

Context: § *Error semantics* 9 names `UnregisteredPermission` for a tool, an endpoint or any
registration requiring an undeclared permission. The endpoint check threw
`HostStartupError.Registration` instead, while Mcp's tool check used the named code.
Chosen: the endpoint check throws `HostStartupError.UnregisteredPermission`, carrying the
`PermissionCatalogError` as the inner error. § 9's table now says which conditions are their own codes
and which are registry errors wrapped by `Registration`.
Rejected: amending the doc to `Registration`, because one condition would then surface under two codes
depending on whether a tool or an endpoint declared it.
Reversibility: cheap before external consumers match on the code.

- 2026-09-20 — [ADR-008: Standalone Observability fails fast on invalid OTLP configuration](../docs/docs/adr/ADR-008-observability-startup-failure.md).

### 2026-09-19 — D5 package delivery uses the approved lockstep version

Context: S18 needed the answer to `10-design.md` § Open questions 4 before producing package
artifacts. Ben approved lockstep on 2026-09-17, recorded on issue #186.

Chosen: the framework and all D5 modules use one shared, explicitly unstable 0.x version. CI packs
and restores the artifacts without publishing to a public registry. The shared version prefix
remains owned by `src/Directory.Build.props`; CI supplies a common prerelease version per run.

Rejected: independent module versions during D5, because there is no independently running
consumer to justify the compatibility matrix. Revisit when a module has such a consumer.

Reversibility: cheap before external adoption; independent releases later require a compatibility
policy and its tests.

### 2026-09-19 — D5 retains the consumer-evidence objection

Context: the brief's decision 2 admits Authorization, Licensing, Audit and shared web UI without
consumer evidence under ADR-006 rule 4. S18.6 requires this objection to remain visible.

Chosen: retain all four capabilities in D5 while recording the objection as **unresolved**.
The sample supplies executable evidence of the stated boundaries; it does not establish external
adoption or satisfy the second-consumer extraction guard. Placement must remain reversible.

Rejected: treating package artifacts, passing sample scenarios or completion of D5 as evidence
that an outside product runs on these capabilities. No named consumer is onboarded by this slice.

Reversibility: cheap while packages remain 0.x and have no external consumer; later consumer
evidence must be assessed separately.

### 2026-09-05 — An endpoint declares its permission and its feature, and a mapped endpoint declaring neither fails startup

Context: `20-contract.md` § *Public surface* 11 fixed the order — authorize at step 4, check
entitlement at step 5 when the endpoint admits new paid-feature work — without saying how either step
learns what the endpoint requires. I-R1 claims the order is enforced by **code, pipeline order**, which
is only true if the pipeline knows what to check; and I-R5's premise, that some endpoints are
deliberately not entitlement-gated, was carried by nothing but this document.
Chosen: **an `EndpointRequirement` carried as endpoint metadata, and a startup check over the mapped
endpoints** that fails the host with `HostStartupError.UndeclaredEndpointRequirement` when an endpoint
carries neither a requirement nor a reasoned `EndpointRequirementExemption`. `RequiredPermission` is
not optional, on the rule *Types* § 10 already states for a tool: an endpoint reachable by an
unauthenticated caller declares a permission the composition provider grants, never an absent check.
`RequiredFeature` is optional and takes no default, so "not entitlement-gated" is something somebody
wrote down rather than something nobody typed. The probes are the only exemption Platform itself
takes, because the composition provider grants nothing at all in `Operated` and a probe declaring a
permission is a probe denied in every operated host.
Rejected: **refusing an undeclared endpoint at request time instead**, which needs no endpoint
enumeration and no exemption list; rejected because a forgotten declaration would then surface on first
reach in production, which is the failure mode I-A3 already refuses for an unregistered
`PermissionName` — a typo that denies is indistinguishable from a policy that denies, and the same
argument applies to a declaration that was never written. Also rejected: **leaving the declaration
optional, absence meaning ungated**, which changes no invariant and is what I-R5's `instruction` row
describes today; rejected because a forgotten `RequiresPlatformAuthorization` then serves an
unauthorized endpoint silently, which is exactly the automatic-on-install default-open that
[`second-consumer-packages.md`](../docs/docs/second-consumer-packages.md) §4 refuses on the Mcp side,
and a rule cannot be default-closed for tools and default-open for endpoints without one of the two
being wrong. The cost of the chosen option is stated in § 11 and is real: an endpoint Platform did not
map needs annotating, and one whose mapping returns no convention builder must be exempted where it
joins the route table.
Reversibility: cheap, and only in one direction. Relaxing the startup check later breaks no consumer;
adding it after consumers have mapped endpoints turns every unannotated one into a startup failure,
which is why it is taken now rather than when the first endpoint needs it.

### 2026-09-05 — The pipeline's authorization check is never resource-scoped

Context: `IAuthorizationEvaluator.EvaluateAsync` takes a `ResourceRef?`, and the fixed order's step 4
has to pass something. Mcp scopes its check to the parsed resource because the SDK parsed arguments
against a declared schema before any producer code ran; an HTTP endpoint convention has no equivalent —
the identifier is in a route value or a body at the moment the declaration is written.
Chosen: **the pipeline passes no resource**, and an endpoint whose authorization is genuinely
per-resource makes a second, explicit `EvaluateAsync` call in its handler with the reference it
constructed. Recorded as I-R7, enforced by instruction.
Rejected: **the declaration naming a resource type and the route parameter that supplies the id**, so
Hosting could build the `ResourceRef` from matched route values. Both strings are opaque to Platform,
so this is no more product knowledge than carrying a `PermissionName`, and it was the option that made
I-R7 unnecessary. Rejected because it buys less than it looks: an identifier carried in a request body
cannot be expressed at all, so those endpoints re-check in the handler regardless — the second check
returns for a subset rather than being eliminated — and the resulting `ResourceRef` is only ever as
good as the route template, which makes a renamed route parameter a silent widening of a check rather
than a compile error. It also puts Hosting in the business of reading a route value to mint an
identifier, against *Module boundaries* § 3's "it knows no product concept".
Reversibility: cheap. An overload of `RequiresPlatformAuthorization` taking the two strings adds the
rejected shape later without breaking a call site, and I-R7 becomes narrower rather than false.

---

### 2026-09-03 — The frozen contributor set reaches the fingerprint as a parameter, not as a projected option

Context: `20-contract.md` § *Unresolved* item 1 (numbered 2 in `30-slices.md`) left two defensible
readings open, and named S8 as where the answer is needed because I-C4 lands there. The contributor
set must be inside the settings-fingerprint input so two instances that disagree about who may grant
surface through `platform.settings-fingerprint` rather than through divergent behaviour — but the set
is a *registration*, and `ISettingsFingerprint.Compute` took `PlatformOptions` alone.
Chosen: **`Compute` gains a second parameter**, `IReadOnlyCollection<EntitlementContributorName>`. The
names are sorted ordinally and emitted one length-prefixed entry each under
`EntitlementContributors[NNNN]`, so registration order is not a disagreement — the evaluator takes a
union, which is order-independent, and a fingerprint that flapped on registration order would be a
permanent false mismatch training an operator to ignore the one check that catches a real one. The
format version moved `szdfp2` → `szdfp3` in the same commit. Persistence's two call sites — the
heartbeat that writes a host's own row and the readiness check that compares it against peers — both
go through one helper, so the written value and the compared value cannot be computed from different
inputs.
Rejected: **projecting the frozen set onto a `[Fingerprinted]` property of `PlatformOptions` at
startup**, which changes no published signature and was the cheaper option on paper; rejected because
`PlatformOptions` is the operator-facing record of what a deployment was configured with, and a
property no operator can set — one that appears after startup, from a source the configuration binder
never saw — makes the type lie about what it is. The cost of the chosen option is a breaking change to
a published 0.x surface, which is the class of change the brief calls those packages explicitly
unstable for; paying it after a consumer ships would cost the consumer instead. Also rejected:
**joining the names into one entry with a separator**, which needs a character reserved out of a
contributor name — a constraint `EntitlementContributorName` does not impose and this decision has no
business adding.
Reversibility: expensive. Both the signature and the fingerprint's stored values change; reversing it
is a second breaking change plus another format version.

### 2026-09-03 — A consumer declaring `Operated` supplies the profile's two registrations itself

Context: S8 makes I-C1 and I-C2 real — an `Operated` host with no authentication provider, or with no
sink declaring `IsDurable`, refuses to start. The packages that supply those are Identity (S9) and the
audit store (S13), both of which land *after* S8. Every `Operated` host in this repository — the web
and worker samples, the game-edge workload, the test host — therefore stopped starting the moment the
gate went in, and S8.12 forbids degrading instead of failing.
Chosen: each host supplies what its declared profile requires, at the layer that declares it. The two
samples register a `NoCredentialAuthenticationProvider` and a `FileAuditSink` of their own
(`samples/SubZeroDev.Platform.Sample.Web/Composition.cs`), written as consumer code and marked as what
S9 and S13 replace. `SubZeroDev.Platform.Testing` gains `FakeAuthenticationProvider` and
`FakeDurableAuditSink`, which `PlatformTestHost` registers when the profile it will declare is
`Operated` — S18.2 already owes Testing the composition profile on the test host, so this is that
obligation arriving when the gate made it necessary. The game-edge workload's declared profile moved
`Operated` → `Local`, because it authenticates nobody and keeps no durable audit trail: `Operated` was
a claim it could not back, and S8 is the slice that makes such a claim checkable.
Rejected: **a framework-supplied default provider or sink**, which would have kept every host starting
with no per-host change; rejected because a default that satisfies a startup check is the "registered
check that always passes" the brief refuses by name — the check would have been unfalsifiable from the
moment it shipped. **Moving the samples to `Local` as well**; rejected because S1 exists to give the
effort two hosts of different shapes, and a repository with no operated host cannot demonstrate any
later slice's operated behaviour. **Deferring I-C1 and I-C2 until S9 and S13 land**; rejected because
that is the degradation I-C9 rules out, and a gate that arrives after the packages it gates is a gate
nothing was ever checked against.
Reversibility: cheap for the samples and the test host — a registration each. Expensive for the
game-edge profile change, which is a statement about what that deployment is rather than a wiring
detail.

### 2026-09-03 — An entitlement contributor registers under a keyed DI slot, never the plain interface

Context: S7.7 requires that no caller can resolve an entitlement contributor from the container — the
evaluator must be the only public entry — while the Community baseline still has to be collected and
frozen into `IEntitlementContributorRegistry` at startup, the same way S4's permission providers and
S5's tenant resolvers are. The contract does not say how that collection happens without also making
`IEntitlementContributor` resolvable.
Chosen: the Community baseline registers as a keyed singleton (`services.AddKeyedSingleton<IEntitlementContributor, CommunityEntitlementContributor>(EntitlementContributorRegistration.ServiceKey)`,
the key an internal `const string`), and `PlatformRegistryStartup` collects contributors via
`[FromKeyedServices(...)] IEnumerable<IEntitlementContributor>`. Ordinary unkeyed resolution —
`GetService<IEntitlementContributor>()`, `GetServices<IEntitlementContributor>()` — finds nothing,
which is what S7.7's test asserts directly. The feature set the baseline grants comes from an internal
`CommunityBaselineOptions` record, empty by default and overridable by registering one before
`AddPlatformWebHost`/`AddPlatformWorkerHost`, on the same override-before-compose precedent `IClock`
already establishes.
Rejected: **the `IEnumerable<IEntitlementContributor>` + `TryAddEnumerable` pattern S4 and S5 use**,
because that registers contributors under the plain interface and defeats S7.7 outright. **A
constructor-injected concrete-type singleton with no keyed slot at all**, which would satisfy S7.7 for
the Community baseline alone but gives Billing's and Licensing's contributors (S11, S12) no channel to
register through without either reopening this question or inventing their own — the keyed slot is one
mechanism both this slice and those two can share. **A public `[Fingerprinted]` `PlatformOptions`
property for the baseline's feature set**, matching `CompositionProfile`'s shape; rejected because the
feature set joining the settings fingerprint is I-C4, explicitly S8's to decide, and a public contract
property is a new declaration `design/20-contract.md` does not carry — the internal record can be
widened or replaced without a breaking change once S8 settles that question.
Reversibility: cheap. The keyed slot and `CommunityBaselineOptions` are both internal; nothing external
depends on their shape, and S8, S11 and S12 are free to settle the contributor registration channel and
the fingerprint input differently.

---

### 2026-09-02 — `ISharedReadScopeFactory` gains `IsOpenFor<TEntity>()`

Context: `20-contract.md` § *Public surface* 7 declared `ISharedReadScopeFactory` with `Open<TEntity>()`
only, and invariant I-T2 named its enforcement "code — model-build filter" — EF Core vocabulary. But
`SubZeroDev.Platform.Persistence` has no EF Core dependency and imposes no repository or ORM
(`design/d3/90-decisions.md`, 2026-08-03): a consumer's own data-access code enlists against the ambient
connection and writes its own queries. With only `Open<TEntity>()` declared, nothing public let a
consumer's own query code ask whether a scope is currently open, so I-T2 was unimplementable by anyone
outside this repository's own test project.
Chosen: **add `bool IsOpenFor<TEntity>() where TEntity : class, IShareable` to the interface.** It reads
the same ambient state `Open` sets, on the same terms `IAmbientTransactionAccessor.Current` already
exposes the ambient transaction to a consumer's own data-access code — an established precedent, not a
new pattern.
Rejected: **leaving the interface as declared and treating "model-build filter" as internal-only,
exercised only by Platform's own tests.** Technically implementable, but it would mean I-T2 holds inside
this repository's test suite and nowhere else — a consumer adopting Platform could not honour it at all,
which defeats the point of declaring it a public invariant.
Reversibility: cheap. Additive; nothing in the tree references `ISharedReadScopeFactory` yet.

---

### 2026-08-31 — `IAuditable.CreatedBy` becomes non-null under the total principal

Context: `20-contract.md` § *Unresolved* 1 (formerly item 1) left two defensible readings open, and
`30-slices.md`'s pre-slice decision table named this a blocker on S2: `CreatedBy` becomes non-null, or
stays `string?`. Both readings produce different declarations and the contract deliberately chose
neither. Asked of the user directly, since S2 cannot implement `IAuditable` against two undetermined
shapes at once.
Chosen: **`CreatedBy` becomes non-null (`string`).** The ambient principal is now total —
`ICurrentPrincipal.Current` never returns null — so "every row names an actor" becomes a property of
the type rather than of the writer, and no consumer runs on Platform today, so the migration cost is
zero right now. `ModifiedBy` and `DeletedBy` stay nullable regardless of this answer, because their
null means "not yet modified" and "not deleted", a different fact from "no actor".
Rejected: **leaving `CreatedBy` as `string?`.** Preserves a reading where null means "written before
D5" for an existing consumer's row, which is real information Platform cannot recover once a consumer
exists — but no consumer exists today, so the alternative costs nothing now and would cost a migration
later precisely when it stops being free.
Reversibility: cheap today, expensive later — the same asymmetry the contract's own framing named.
Widening `CreatedBy` back to nullable after a consumer ships is a breaking change to every row that
consumer already wrote; narrowing it now, before any row exists, changes nothing on disk.

---

### 2026-08-29 — `Platform.Mcp` adopts the official MCP C# SDK and projects to it at the boundary

Context: `10-design.md` § *Open questions* 1 made the ADR-004 §4 evaluation a gate on `/contract`,
because the answer decides whether tool registration and invocation are Platform's own contracts or a
projection of somebody else's. `minimal-platform-packages.md` §3a's finding that three foundational
.NET libraries changed licence within one year made licence durability a first-class criterion. The
evaluation was run on 2026-08-29 against the sources, not from memory, per `AGENTS.md`, *Verification*.
Chosen: **take a dependency on `ModelContextProtocol.Core` and `ModelContextProtocol.AspNetCore` for
the transport, the session and the filter pipeline, and keep registration, exposure, required
permission and required feature as Platform's own types, projecting to the SDK's `Tool` and
`McpServerTool` at the module boundary.** No SDK type appears in Platform's public surface, and no
package other than `SubZeroDev.Platform.Mcp` references the SDK (`20-contract.md`, I-M9). All three of
the design's criteria are met: the licence is Apache-2.0 with a documented MIT→Apache-2.0 transition,
and the copyright holder is *"Model Context Protocol a Series of LF Projects, LLC"* — a Linux
Foundation series project rather than a vendor-owned library, which is the structure that forecloses
the unilateral relicensing §3a warns about; transport is separable from tool production, because
`McpServerTool` is an abstract class exposing `ProtocolTool` and `InvokeAsync`, so a producer can
supply the protocol tool and its `InputSchema` entirely as data and neither producer is privileged
(I-M7); and connection-level authentication is not merely expressible but already enforced —
`StreamableHttpHandler` binds the session to the principal established at the HTTP transport and
answers a session request carrying a different principal with 403 (I-M5).
Rejected: **implementing the transport ourselves**, which the design named as the answer if any
implementation baked in a single tool-definition producer. The SDK does not, so this now buys nothing
the projection does not, while costing a protocol implementation to write and to keep current with a
specification this repository does not own. **Exposing the SDK's types directly as Platform's
surface**, which is less code and gives consumers a documented surface; rejected because the SDK went
v1.0 (2026-03) to v2.0 (2026-07-28) to v2.2.0 (2026-08-13), so a pass-through makes an SDK major
version a Platform breaking change and Platform's contract stops being Platform's to keep stable — the
projection layer is the price of that, and it is the cheaper side. **A third-party .NET MCP library**;
none was found, so nothing was passed over on merit.
Known and retained: the SDK's default authorization filter answers an unauthorized call with "Access
forbidden: This tool requires authorization", which confirms the tool exists and contradicts I-M4.
Filters are ordered lists, so `Platform.Mcp` installs its own ahead of it and does not use the SDK's
authorization-metadata path at all (I-M11) — composition rather than a fork, but it is a constraint
the implementing slice must honour rather than discover.
Reversibility: cheap, and that is what the projection buys. Dropping the SDK later replaces one
module's internals and changes no Platform type; the reverse — retracting SDK types from a published
Platform surface — is the direction that is not available, which is why the projection is taken now
rather than added when it hurts.

### 2026-08-29 — A tool's exposure is not a field its producer fills in

Context: `10-design.md` § *Data model* 8 describes one `ToolRegistration` carrying the name, the
producer, the schema, the required permission and whether it is exposed, and requires exposure to be
configuration, resolved at startup, default closed.
Chosen: two types. `ToolDefinition` is what a producer supplies and carries no exposure;
`ToolRegistration` is what the catalogue produces by combining a definition with configuration, and it
carries the producer and the exposure. `IToolCatalogue` exposes only the exposed set and an
exposure-respecting lookup — no `All`, no exposure-ignoring `TryGet`.
Rejected: **the design's single type with an `IsExposed` field**, which is one type rather than two;
rejected because a producer would then supply a value the catalogue must ignore, and a field that is
ignored is a trap — it makes default-closed a rule every present and future producer has to remember,
which is exactly the automatic-exposure-on-install that `second-consumer-packages.md` §4 refuses.
**A catalogue that can enumerate every registration for diagnostics**; rejected because an enumeration
of hidden tools is the disclosure I-M4 exists to prevent, and a diagnostic surface is where that leak
would be least examined.
Reversibility: cheap. Merging the two types later is additive for a caller that only reads
`ToolRegistration`.

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
