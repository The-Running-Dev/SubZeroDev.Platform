# Decision log

Append-only. Newest at the top. The rejected alternatives are the point — without them, every future session relitigates the same choice.

**This log is slice-local.** `AGENTS.md`, *Decision logging*, decides what belongs here and what belongs in `docs/docs/adr/`.

### 2026-08-08 — Development-environment auto-migration stays a design promise, and becomes `Unresolved` 8 rather than an invented surface
Context: A second `/contract` verification pass, reading both documents in full, found a promise the
previous pass did not: `10-design.md` says twice — *Control flow* §1 and *Failure modes* — that
migration application is "automatic only in the development environment". `20-contract.md` names only
`RunPlatformMigrateModeAsync`, no slice criterion in `30-slices.md` covers the automatic path, and in
the tree `IMigrationRunner.ApplyAsync` has exactly one caller, `MigrateMode.cs`. So the design
promises a behaviour that neither the contract nor the code carries, and the contract's silence is
what kept it invisible through nine slices and two reconciles.
Chosen: **the design is right and the work is unbuilt.** The clauses stay; the gap is recorded as
`20-contract.md` *Unresolved* **8**, naming the four things the design does not determine — which
package invokes it (Hosting cannot reach the runner, so only `AddPlatformPersistence` can), whether
it is a registration side effect or an opt-in, what a failed automatic application does under a
taxonomy that prices only the manual operation's failure, and whether it takes the provider-native
migration lock that two concurrently starting development hosts would need. A bullet under `## Open`
carries it to `/track`. **A contract amendment settling those four comes before any slice.**
Rejected — **dropping the two clauses from `10-design.md`**, which was this pass's recommendation:
the design's own argument against migrate-on-start ("the least controlled moment available", and two
processes racing themselves) applies in development too, nine slices shipped without it, and
restoring the clause later is additive. Declined on the owner's call — the promise is wanted, and a
document that quietly retracts a convenience because nobody built it teaches the next reader that
design statements expire by neglect.
Rejected — **writing the surface into `20-contract.md` now.** It is the one thing this command
forbids: the design names the concept and not its construction, so a signature written here would be
four silent decisions wearing a contract's authority, and the `Unresolved` section exists precisely
to hold that shape until it is decided.
Rejected — **re-deriving `20-contract.md` around it.** Same reasoning as the entry below: the
`Unresolved` numbering is load-bearing for `30-slices.md`, this log and nine closed slice issues, and
appending item 8 perturbs no existing anchor while a regeneration risks all of them.
Reversibility: cheap. One appended `Unresolved` item, one `Open` bullet, two preamble sentences; no
type, signature, schema, invariant or persisted shape changes, and no code is affected.

### 2026-08-08 — The contract is not re-derived; three statements that had become false are corrected instead
Context: `/contract` run against `10-design.md`. The command writes `20-contract.md`, and the design's
own closing paragraph instructed it to: the fifth adversarial review's entry ended "the contract
predates this revision and contradicts it — it must be re-derived before any slice runs." That
sentence was true when written and has not been true since. `20-contract.md` was re-derived in full
against exactly that revision — its preamble names the six contradictions it closed — and all nine
slices then ran against the result, which the `Unresolved` items themselves record by citing S1, S2
and "ahead of S3" as where each was settled. Reading both documents in full found no signature the
design determines and the contract omits, and no place the two contradict. So the deliverable was a
verification, not a rewrite.
Chosen, as three corrections of statements that had become false:
1. `10-design.md`'s fifth-review paragraph drops the re-derivation instruction. It is a standing
   instruction to redo work that has been done, in the one document a `/contract` session reads first.
2. `20-contract.md`:832 and :1726 point at `Unresolved` **3** and **7** as resolved rather than
   asserting the error envelope's wire format and the contract tests' invocation surface are still
   open. Both items are struck through and settled; only the referring sentences had not caught up.
3. `10-design.md`'s malformed-correlation-header failure mode says the new root is **neither counted
   nor logged** per occurrence. It said "counted rather than logged", which the same document
   falsifies 685 lines earlier: Platform authors no instrument of its own in D3. The contract was
   already on the correct side of this — invariant 42 promises only that the request does not fail —
   so nothing in `20-contract.md` moved for it.
Rejected — re-deriving `20-contract.md` because the command says to write it. It would be churn
against a document `30-slices.md`, this log and nine closed slice issues all cite by section and by
numbered item, and the `Unresolved` numbering is explicitly load-bearing: that section states resolved
items keep their number and are struck through rather than removed, precisely because renumbering
silently breaks every citation. A regeneration that reproduced the same content would still risk
perturbing those anchors for no gain.
Rejected — declaring a Platform counter so "counted" becomes true. It puts public telemetry surface
at 0.x ahead of any consumer asking for it, which the design rejects at length in the section that
establishes the no-instrument rule, and it would need a contract amendment for an instrument no
operational condition depends on.
Rejected — leaving all three. Each is a document asserting something about itself or its sibling that
is no longer so, and the first one actively instructs the next session to redo settled work.
Reversibility: cheap. Three prose corrections; no type, signature, schema, invariant or persisted
shape changes, and no code is affected.

### 2026-08-08 — Reconciling D3's physical types: eight stale doc corrections, one dispatch log gap, and four unlogged values
Context: `/reconcile` against `10-design.md` and `20-contract.md` after the D3 packages shipped. The
2026-08-03 "Reconciling S2: the capability seam was not implementable, and four values it set" entry
already settled identifier and instant columns as text/blob on both providers rather than native
`uuid`/`timestamptz` on PostgreSQL — but that decision named only those two logical types. The
design's logical-type table still read `uuid`/`timestamptz`/`json`, and two more rows in the same
table were wrong for reasons the S2 decision does not cover: payload had drifted from native `json`
the same way identifier and instant had, and tenant's row claimed a 16-byte blob it has never been on
either provider — the code binds it as plain text throughout. Nothing caught either, and three more
places in both documents had
independently gone stale: the sequence mechanism, the lease-renewal paragraph, the startup heartbeat
timing, and where `MapPlatformProbes` and invariant 8 actually apply. One behavioural gap sat beside
them: `DispatchError.MigrationsPending` has existed since S2 and was never constructed.
Chosen, as eleven parts of one reconcile:

**Doc corrections — the code was right, the doc was wrong, in every case below:**
1. `10-design.md`'s logical-type table: PostgreSQL is blob for Identifier and text for Tenant,
   Instant and Payload — Tenant was never blob on either provider, a table row this reconcile first
   assumed by pattern-matching against Identifier and then corrected against `PlatformOutboxMigration`
   itself, which declares `tenant TEXT NOT NULL` unconditionally. The one migration source generates
   provider-specific DDL only for the identifier and sequence columns (`BLOB`/`BYTEA`,
   `INTEGER`/`BIGINT GENERATED ... AS IDENTITY`); every other column, including tenant and payload,
   is literal `TEXT` with no per-provider branch — extending the 2026-08-03 S2 decision's
   identifier/instant scope to a table that had also drifted on a column that decision never touched.
2. `10-design.md`'s sequence mechanism: app-allocated `MAX(sequence) + 1` on SQLite — a rowid alias
   is unavailable because the primary key is `id`, not `sequence` — and a `BIGINT` identity column on
   PostgreSQL. The reuse-after-prune consequence was already right; only the mechanism was described
   wrong, as a rowid alias, which SQLite's own schema for this table cannot produce.
3. `10-design.md`'s lease-renewal paragraph: D3's only leased work — prune — completes inside one
   lease and never renews. `ILeaseHandle.RenewAsync` and the abort-on-failed-renewal obligation are
   real, tested members (`LeaseTests.cs`), kept for the first consumer whose work outlives one lease
   window; nothing in D3 is that consumer.
4. `10-design.md`'s startup/heartbeat control flow: the first heartbeat lands one heartbeat interval
   after start, not at start — `BackgroundWorkService.RunAsync` awaits `PeriodicTimer.WaitForNextTickAsync`
   before the first `TickAsync`, heartbeat included. The peer-absence grace's one-interval floor
   already covers the gap; nothing needed to change there.
5. `20-contract.md`'s two encoding-rule paragraphs (identifier byte order, instant text format): bind
   both providers, not SQLite alone — the contract-layer half of (1).
6. `20-contract.md`'s `platform_outbox` sequence row: the same mechanism correction as (2).
7. `20-contract.md`'s `MapPlatformProbes` paragraph: Platform's own middleware serves the probes in
   both roles without either host calling it — the standard registration call has to be sufficient
   alone. `MapPlatformProbes` stands that middleware down for a host that places the probes in its
   own route table; it does not turn the probes on.
8. `20-contract.md`'s invariant 8: scoped to the enqueue path. Claim, marks, redrive, discard, the
   three readiness queries and prune each correctly open their own connection through
   `capability.BeginAsync` — none runs inside a caller's transaction — and the invariant as written
   forbade exactly that.

**Code:**
9. `OutboxDispatcher.TickAsync`'s pending-migration branch now logs the hold under
   `DispatchError.MigrationsPending` (`Errors.cs:211`), constructed for the first time since it was
   defined at S2. Behaviour was already correct — claim nothing, stamp nothing — the gap was purely
   observability. Cited by member name, not line number, since the line moves with every edit near it.
10. `PersistenceIntegrationTests.cs` gained the two contract-mandated assertions — the `Id` uniqueness
    row and the cross-provider payload row in `20-contract.md`'s provider-contract-tests table — that
    had no test: `Id` unique across a drain, prune-to-empty, insert cycle; and a payload written under
    one provider deserializing under the other.

**Found unlogged, recorded rather than corrected — nothing here contradicted either document, so
nothing above changed for these:**
11. A module is composed before the container exists (`PlatformHostExtensions.cs:184`): it must be
    registered as a type or an instance ahead of the standard registration call, with a public
    parameterless constructor if registered by type. A factory registration, or a constructor needing
    arguments, aborts startup with `HostStartupError.Registration`. Added to `20-contract.md`'s
    `IPlatformModule` block, which stated the interface but not this constraint on registering it.
    `HostStartupError.Registration` passes a `null` inner error on both of those failure paths
    (`StartupFailure.cs:47`) — an accepted shape, not a defect: neither path has an inner
    `PlatformError` to wrap, so there is nothing to pass.
    A `null RemoteIpAddress` reads as loopback, so the probe body is `Full` (`Probes.cs:184`) —
    invariant 45 says `Full` is loopback-or-development and does not say what a null remote address
    resolves to; this is that resolution, matching the platform's own treatment of a caller with no
    observable remote endpoint.
    The unhandled-failure envelope carries exactly one code, `UnhandledRequestFailure`, at HTTP 500
    (`Pipeline.cs:61`) — S1 already decided this (2026-08-03 "The values S1 set...", item (8)); it is
    restated here only because this is the first entry to log the three siblings above beside it, not
    because anything about it changed.

Rejected: **Native PostgreSQL types for tenant and payload** — `uuid` for tenant, `json` for
payload — matching what the table said before this reconcile; the code has never been written that
way for either column, and taking it now would mean a migration and a second `IProviderCapability`
bind path for two columns that already work correctly as text, spent to make the table's stale claim
true instead of fixing the table. **Leaving `OutboxDispatcher`'s migration hold
silent** — cheaper, and it is exactly the gap invariant 21 depends on an operator noticing without a
log line. **Leaving invariant 8 as written and treating the other five call sites as an implicit,
unstated exception** — shorter, and it is the same failure this whole entry exists to correct one
level up: an invariant that means something narrower than its own sentence says is a design table
lying about the code, at invariant scale rather than table scale.
Reversibility: cheap throughout. Every correction here describes the code as it already runs; no
public contract changes shape, and nothing external depends on any of the eleven differently than it
did before this entry.

### 2026-08-08 — ADR-004's architecture reading happens after the fact, and finds two gaps rather than a defect
Context: [ADR-004](../docs/docs/adr/ADR-004-framework-build-not-adopt.md) §3 names three things to
read closely in ABP **before** the thin equivalents are written — its module lifecycle and
dependency-graph validation, its outbox, and its tenancy query filters. D3 shipped all nine slices
without a single log entry citing any of them, which under that ADR's own reuse clause is
indistinguishable from having skipped it. The third was moot: tenancy query filters are a brief
non-goal, deferred to D5. The other two were read against ABP 10.6's documentation on 2026-08-08,
after the code existed.
Chosen: **Record the reading now, change no shipped code, and route what it found to the tracker.**
Two findings, and neither is a defect in what D3 built:
1. **No consumer-side idempotency, where ABP has an inbox.** ABP pairs `OutgoingEventRecord` with
   `IncomingEventRecord`, retaining processed ids so a redelivered message is dropped before the
   handler runs. Platform's dispatcher names the same exposure and stops there — `ObserveClaimedWriteAsync`
   logs "duplicate delivery is possible" on a lost claim. At-least-once is a *logged decision*, so
   this is not drift; but the contract offers handlers no dedupe seam, which makes idempotency a
   problem every consumer solves privately and inconsistently.
2. **`IPlatformModule` has one lifecycle hook where ABP has seven.** `Register(IServiceCollection)`
   is the whole surface. ABP's `PreConfigureServices` exists so a module can shape options a later
   module reads, and `OnApplicationInitialization`/`OnApplicationShutdown` give a module a built
   `IServiceProvider` and a shutdown path. Platform modules have none of these, so anything needing
   a built provider is pushed into `IBackgroundWork` or the host's own startup.
Two places the comparison **confirmed** Platform, which is why this is not a report of shortfalls:
its **claim-by-portable-conditional-update** is better suited than ABP's distributed lock around a
batch — the lock is what forces ABP's blocking `Retry` policy, and one-row-at-a-time claiming needs
no lock to be multi-process safe; and holding the dispatcher **while migrations are pending** has no
ABP equivalent.
Rejected: **Amend `IPlatformModule` or add an inbox table now** — both change a published contract
after D3 is packaged and released at v0.1.0, for no consumer that has asked; the extraction guard
says the second consumer earns the abstraction, and the G1 edge is the one that will. **Log the
reading as done and record nothing** — leaves an empty log next to hand-written infrastructure,
which ADR-004 names explicitly as the signal that its reuse clause was skipped. **Reopen ADR-004** —
nothing found changes the build-not-adopt verdict, which rests on per-product weight, not coverage.
Reversibility: cheap — both findings are additive to a 0.x API that is explicitly unstable.

---

### 2026-08-08 — The sample round-trip script becomes PowerShell, signalling through libc
Context: S9 added `build/Test-SampleRoundTrip.sh`, the only non-PowerShell script in `build/` against
`AGENTS.md` *House conventions* — "PowerShell Core for scripts". It is the script that proves the
brief's first and third CI assertions, and it is shared by `build.yml` and `release.yml`'s
verify-restore job.
Chosen: **convert it to `Test-SampleRoundTrip.ps1`** and delete the bash original; both workflow call
sites now read `pwsh ./build/Test-SampleRoundTrip.ps1`, matching how `Test-ApiReference.ps1` and
`Test-PackageManifests.ps1` are already invoked. This was **not** the recommendation — keeping the
script and logging the exception was, on the grounds that rewriting a working script a green release
depends on spends risk for consistency nobody executes. Overridden deliberately: the convention is
absolute as written, and one exception in the directory is what makes the next one arguable.
The one thing PowerShell cannot express, stated because it bounds what the conversion achieved:
**SIGTERM has no PowerShell or .NET API.** `Stop-Process` and `Process.Kill()` both send SIGKILL on
Unix, and TERM-versus-KILL is the entire mechanism of this script — the worker is asked to shut down
gracefully and must exit zero, and the web host is SIGKILLed so that nothing in its memory can bridge
the commit to the dispatch. Both signals therefore go through a P/Invoke to libc's `kill(2)` via
`Add-Type`, which keeps them symmetrical and legible beside each other. The script is Linux-only, as
the bash version already was.
What the conversion also bought, unlooked-for: `Invoke-RestMethod` parses the order response, which
retires the `python3 -c 'import json'` hop, so the runner needs one fewer thing. `sqlite3` is still
required, and `processed_at` is still read from the table rather than grepped from a log, for the
reason recorded at that step.
Rejected: **Keep the bash script and log the exception** — the recommendation, declined above.
**Amend `AGENTS.md` to exempt CI-only scripts** from the PowerShell convention — one line instead of a
rewrite, and it relaxes a standing instruction for a single file, which is broader than the case that
prompted it. **Shell out to `/bin/sh -c "kill -TERM …"`** for the signal — shorter than the P/Invoke
and it would leave the script depending on a POSIX shell for the one thing it converted away from,
which is the letter of the convention without its substance.
Known cost, not yet discharged: **the port is unproven.** The bash version is green on the release
commit; this one has been syntax-checked and reasoned through, and nothing has executed it. Two
things are most likely to bite — `Start-Process` cannot point stdout and stderr at one file, so each
stream now has its own log and the failure handler dumps six files rather than three; and
`ASPNETCORE_URLS` is set and then removed around the web host's start, because `Start-Process`
snapshots the environment and the worker must not inherit it. The next CI run on these workflows is
the verification, and until it passes this entry is the record that it has not.
Reversibility: cheap — the bash script is one `git revert` away in history, and reverting means one
line in each of two workflows.

### 2026-08-08 — The five things S9 took or set, none of which had been logged
Context: S9 merged in `473920d` and released `v0.1.0`, and this log's newest entry was 2026-08-06.
`AGENTS.md` requires an entry naming the alternatives whenever a dependency is taken, and
[ADR-004](../docs/docs/adr/ADR-004-framework-build-not-adopt.md) §4 requires one when a package is
passed over. S9 took a tool, chose a registry, pinned a version scheme and invented two mechanisms,
and logged none of it. **This is the second occurrence of exactly this miss** — the 2026-08-03 entry
"The six dependencies S2 took, none of which had been logged" is the same backfill, found the same
way, by a reconcile rather than by review.
Chosen, as five parts of one release decision:
**(1) docfx 2.78.5**, installed as a global tool in the `api-reference` job and required on `PATH` by
`build/Test-ApiReference.ps1`. It is the reference generator for .NET with no equivalent that reads
XML doc comments and produces a static site; a version is pinned rather than floating so a tool
update cannot change what the release gate accepts.
**(2) GitHub Packages as the .NET feed**, `nuget.pkg.github.com/<owner>/index.json`, authenticated
with the workflow's own `GITHUB_TOKEN`. The brief leaves the public nuget.org identifiers deliberately
unspent, and S9.2's whole purpose is to prove pack, publish and *authenticated* restore without
spending them — a private feed is the only shape that does both.
[ADR-003](../docs/docs/adr/ADR-003-package-scopes-and-registries.md) already governs naming on this
registry, so this settles which registry rather than which name.
**(3) `VersionPrefix 0.1.0`** in `src/Directory.Build.props`, with the version otherwise taken from
the tag. This is what makes S9.6's claim true rather than asserted: `build/Test-PackageManifests.ps1`
fails any package whose major version is not 0, so the unstable-API promise is enforced by the pack
gate instead of by a sentence in a release note.
**(4) `UsePlatformPackages`**, an MSBuild condition in both samples and the test project selecting
`PackageReference` over `ProjectReference`, with `PlatformPackageVersion` supplying the version. It is
the mechanism S9.3 rests on: the same sample and the same suite run against restored packages, with
no second sample to keep in step with the first.
**(5) `PublicApiLister`**, a throwaway console app that reflects over the six built assemblies, with
**reflection rather than the generated XML doc file as the source of truth.** The XML file lists any
type carrying a doc comment, including internal ones a contributor documented anyway, so it
over-counts "public" and would let the S9.4 comparison pass against a reference legitimately missing
nothing — a gate that cannot fail.
Rejected: **A hand-maintained public-API list** — no tool at all, and it is a second copy of the API
that drifts the first time someone adds a type without updating it, which is the failure S9.4 exists
to catch. **Parsing the generated XML doc file** — no extra project, and it over-counts as above.
**Publishing to nuget.org** — spends identifiers the brief holds deliberately, and proves nothing
about authenticated restore. **A floating docfx version** — one less thing to bump, and it lets a tool
release change the release gate's verdict with no commit. **A second sample built against packages** —
avoids the MSBuild condition, and it is the duplicated-consumer maintenance S9.3's one-sample
approach exists to refuse.
Known cost, accepted, and it is the point of writing this down: **these alternatives are
reconstructed after the fact.** The scripts and workflow carry their own reasoning in header comments
— unusually good ones — and this entry draws on those rather than on a record made while choosing. A
backfilled rejection is weaker evidence than a contemporaneous one, because the alternatives that
were genuinely considered and the ones that are merely defensible now read identically.
Reversibility: **expensive for (2) and (3)** once a consumer restores from the feed or reads a version
as a compatibility signal. Cheap for (1), (4) and (5) — a generator, a build condition and a test
utility, none of which any shipped package references.

### 2026-08-08 — Two joint settings constraints the binder already enforced become contractual
Context: The same reconcile found `PlatformOptionsBinder` rejecting `PeerAbsenceGrace <
HeartbeatInterval` and `RetryBackoffCap < RetryBackoffBase`, both as `InconsistentSettings`. Neither
was in the contract: `PeerAbsenceGrace`'s validation read "non-negative", the cap's read as a
single-setting `>= RetryBackoffBase`, and the `InconsistentSettings` row named only the retention and
drain-window pairs. The error code is public surface — S1.8 asserts codes by name — so the tables had
to match the code in one direction or the other.
Chosen: **the contract adopts both, as `InconsistentSettings`.** The grace floor is the substantive
half and the code is right about it: a grace shorter than one heartbeat elapses before the peer's next
beat can land, so `PeerHost` degrades on a host that is working, and zero — legal under "non-negative"
— turns a rolling grace into no grace on the surface this design elected as always-on. Both settings
tables now state the joint constraint, the `InconsistentSettings` row names four pairs rather than
two, and the reasoning sits beside `PeerAbsenceGrace` where an implementer reads it.
Rejected: **Relax the code to match the contract** — restores `PeerAbsenceGrace = 0` as legal and
reintroduces the false-degrade above, contradicting invariant 47's own rolling-grace argument.
**Keep the grace floor and reclassify the cap check as `InvalidSetting`** — keeps the Validation
column meaning exactly one thing, and it makes two structurally identical joint checks report under
two different codes, so an operator cannot predict which they will see. Both constraints are
properties of a pair, and `InconsistentSettings` is the code that says so.
Known cost, accepted: `PeerAbsenceGrace = 0` is no longer a legal configuration. Nothing can be
relying on it — no release ships a value for it — but it is a narrowing of a documented range rather
than a clarification, and this entry is where that shows.
Reversibility: cheap in the documents; tightening a validated range is the direction that breaks a
consumer, which is why it is worth taking at 0.x rather than later.

### 2026-08-08 — Prune ticks hourly, unconfigurably, and the drain rate becomes a stated commitment
Context: The same reconcile found `PruneWork.Interval` hard-coded to one hour, with a code comment
reasoning about it and no mention anywhere in `10-design.md`'s *Settings inventory* — which names
every other cadence, and which the design says Hosting invokes ticks on. A tick also issues exactly
one bounded delete per target, so the default drain rate is 500 rows per target per hour. That sat
against the design's own worst case for prune: "a worker returning after days down, or a retention
window shortened, leaves an **arbitrarily large** backlog to delete." A million-row backlog is
eighty-three days at that rate, and nothing said so.
Chosen: **keep the code and state what it commits to.** *Settings inventory* gains two rows — the
interval, marked not configurable, and the drain rate as an explicit consequence — and the
"arbitrarily large backlog" paragraph now says the bound resolves that case by *spreading* it rather
than absorbing it, with the arithmetic and the reason slow is acceptable: a row awaiting prune is
inert, no readiness condition counts processed or discarded rows, and a poisoned row stays queryable
the whole time. The limit this rules out is stated too — a prune keeping pace with a heavy poison
source — and the poison-count condition is named as the surface that already reports it. The contract
gets a parallel note beside the journal-mode one, since the interval is not a bindable setting.
Rejected: **Drain each target to completion within one tick** — clears a large backlog in one pass,
and it trades the property the bound exists for: an unbounded number of bounded statements is a
longer aggregate hold on SQLite's single write lock than this design priced, and invariant 23's
"every background write is bounded" would need rewording to stay true. **Add `Outbox:PruneInterval`**
— consistent with every other cadence, and it puts public configuration surface at 0.x on a knob no
deployment has asked for; it would also still need the drain-rate note, since the rate is the product
of two settings and not obvious from either.
Known cost, accepted: the rate is now a commitment that can be wrong, which is what *Settings
inventory* is for. If a consumer ever needs a faster prune the honest fix is the setting, not a
quietly changed constant.
Reversibility: cheap. The interval is one line in `Prune.cs`; promoting it to a setting later is
additive.

### 2026-08-08 — Platform publishes no instrument in D3; the metric guarantee is the allowlist, not the redactor
Context: Reconciling the tree against the design found that no Platform code constructs a `Meter` or
an instrument — there is no `new Meter`, no `CreateCounter` and no `CreateHistogram` anywhere in
`src/`. `PlatformTelemetry.MeterName` is public and Observability subscribes to it with `.AddMeter`,
so the name looked implemented from every angle a gate can see: it carries a doc comment, it appears
in the API reference, and S8.8's allowlist assertion passes — against
`http.server.request.duration`, a built-in ASP.NET Core instrument. Three design sentences depended
on an instrument that does not exist: poisoning "raising a metric", worker-down's "the metric still
exists for anyone exporting", and "each Platform instrument owns an allowlist". Separately, the
redactor runs over Serilog events, OTLP log records and spans and over no metric — while the design,
the contract, invariant 49 and S8.7 all named metric labels as inside its boundary.
Chosen: **correct the documents.** `MeterName` is stated as a reserved name with no publisher in D3,
subscribed so that publishing later is additive; the metric labels D3 exports come from the
instrumentation packages and are governed by the allowlist, which is named as the load-bearing
mechanism rather than as defence behind a redaction pass that never ran. Redaction's stated scope
narrows to logs and spans. The poison and backlog conditions cite the readiness checks that actually
carry them. Invariant 49 splits into its two mechanisms — redaction for logs and spans, the closed
label set for metrics — and 50 says what it is asserted against. S8.7 loses its metric clause and
points at S8.8; no criterion is renumbered.
Why the allowlist is the stronger half, stated because the reverse reading is the intuitive one:
redaction filters values a signal happened to carry, while an allowlist decides which labels may
exist at all. A closed set drawn from host role, method, route template, status, provider and closed
enums has nowhere for a secret to arrive, so a redaction pass over it has nothing to find.
Rejected: **Build the two instruments and a metrics redaction processor**, making every sentence true
as written — the option that leaves the design untouched. Declined because it adds public telemetry
surface at 0.x for no consumer, and reopens S8 after both its slice and the `v0.1.0` release have
shipped, against *One slice at a time*. The design's own health section already argues a metric is
not a mitigation on an installation that exports nowhere by default, which is why every operational
condition was routed through readiness — so the metrics were the weaker half of its own case, and
building them to satisfy a sentence inverts that argument.
**Correct the documents and open an issue for the instruments** — keeps the intent as intent; declined
as a second record of one decision when no consumer has asked for the instruments and the reservation
already states the direction.
Known cost, accepted: `MeterName` stays public surface with no publisher, which is exactly the shape
that made this invisible. What changes is that it now says so.
Reversibility: cheap. Adding an instrument later is additive and needs no consumer change, which is
what the reservation buys; the document edits are one revert.

### 2026-08-06 — Acceptance criteria carry stable ids, and all nine slice issues migrate to the fenced shape
Context: `/track` compares a slice against its issue on **criterion id, never prose**, and closes an
issue when every `Done when` box is ticked. Neither side could support either operation: no
`Acceptance:` bullet in `30-slices.md` carried an id, and none of the nine slice issues (#15–#23) had
a checkbox or an `<!-- agent:start -->` fence — each body was the slice section copied verbatim. So a
criterion could be added, removed or renumbered and no run of `/track` would see it, and S8 and S9
could never be closed by the command that owns closing them. The gap has a dated origin already in
this log: the `2026-08-04 — Kit catch-up install` entry names "the human-first fenced-issue shape,
stable criterion ids" among the incoming kit's additions. The kit was installed; the existing issues
and this document were never migrated to it.
Chosen: number every `Acceptance:` bullet `S<n>.<m>`, contiguous from 1 per slice — 103 across the
nine — and rewrite all nine issue bodies into the narrative + `Done when` + fenced-agent-block shape,
including the seven already closed. Prose is untouched: the transform was scripted and verified by
stripping the ids back out and diffing against the pre-change file, which came back identical. Ids
are positional and permanent; a future criterion appends rather than renumbering, since renumbering
silently repoints an existing checkbox at different work.
Rejected: **Migrate S8 and S9 only**, leaving the seven closed issues as the historical record of how
those slices were actually tracked — the smaller change, and the one recommended; declined because it
leaves the closed set unauditable by id, which is the only comparison `/track` performs.
**Ids in the document alone**, so future issues inherit them — helps no slice that exists, since all
nine already have issues. **Accept the gap** — makes `30-slices.md` and its `**Status:**` marker the
doneness signal in place of the tracker, contradicting `AGENTS.md`, *Tracking work*.
Known cost, accepted: the count is a moving target. S8 gained a ninth criterion in #44 while this
change was being prepared, which is exactly why the ids are generated from the document rather than
transcribed — but it also means any id list quoted outside `30-slices.md`, including in an issue,
is a copy that can go stale. That is what the `@ <sha>` pin on each issue exists to expose.
Reversibility: cheap for the document — one revert. Expensive for the issues: a rewritten body
replaces what was there, and the seven closed ones have no other copy of their original text.

### 2026-08-06 — S7's status marker is corrected on merge, and S8 advances to `in progress`
Context: PR #40 merged S7 and closed #21, but `40796bc` touched eight source and test files and not
`design/30-slices.md`, so the marker stayed `in progress` with no PR link while every shipped slice
before it carried `shipped · [#PR]`. `site/src/roadmap/roadmapData.ts` parses that line, so the
public roadmap was showing a merged slice as in-flight. Found by `/track`, which reports marker drift
but does not resolve it.
Chosen: S7 to `shipped · [#40]`, and S8 from `queued` to `in progress`. The second half was not the
initial recommendation — leaving S8 `queued` looked more honest, since nobody has started it — and
that recommendation was wrong on a checkable fact: `build/Test-SliceStatusMarkers.ps1:97` and
`roadmapData.ts:101` both require at least one `in progress` slice whenever any slice is `queued`,
so the combination fails the documentation gate and the site build. The invariant defines
`in progress` as the head of the queue rather than a claim that work has begun, which is what makes
advancing S8 correct rather than merely required.
Rejected: **Relax the invariant** in both the script and the parser — costs the gate whose stated
purpose is failing a pull request that merges a slice without updating its marker, which is precisely
the miss that produced this drift. **Leave the marker** — keeps the public roadmap wrong and returns
the same finding on the next `/track` run.
Reversibility: cheap. Two lines.

### 2026-08-05 — S8 routes both local log sinks through Serilog
Context: The recovered S8 amendment kept the standard console provider while assigning only the
file sink to Serilog, but the same amendment promised one redaction boundary and UTF-8 JSON Lines on
both local outputs. A provider outside that boundary could satisfy neither promise reliably.
Chosen: add **Serilog.Sinks.Console 6.1.1** and route both console and file output through Serilog,
using the same JSON formatter, redactor and 10 000-event non-blocking async buffer. This supersedes
only the earlier S8 dependency decision's phrase "beside the standard console provider"; its other
package and policy choices stand.
Rejected: **Keep the standard console provider** — bypasses the Serilog redaction and formatting
boundary. **Keep plain-text console output** — contradicts the contract's JSON Lines invariant and
makes the two mandatory local outputs structurally different. **Build a second console redactor** —
duplicates the safety boundary and invites drift.
Reversibility: moderate before publication; expensive once operators parse the console stream.

### 2026-08-05 — Outbox staging preserves the public provider transaction seam
Context: S2 reconciliation made `IProviderCapability` implementable by third-party providers and
removed casts from its public transaction return to Platform's internal implementation. S4's outbox
staging reintroduced that cast in `UnitOfWork`; built-in providers returned the internal type, so the
full test suite passed while any external implementation failed before commit.
Chosen: wrap the returned `IAmbientTransaction` in Platform's internal `AmbientTransaction`, carrying
the same intent, connection and transaction handles plus the pending outbox rows. The ambient
accessor exposes that wrapper while the unit of work is active, and the unit of work reads its own
staging state without assuming which concrete type the provider returned. A regression test returns
a transaction implemented only through the public interface.
Rejected: **Retract third-party provider support** — would weaken the reason the capability and its
transaction return are public. **Add pending outbox rows to `IAmbientTransaction`** — exposes a
Platform implementation detail and breaks existing external implementations. **Store staging state
on the provider-returned object by convention** — recreates the same undocumented concrete-type
requirement under another name.
Reversibility: cheap internally; the public contract is unchanged. Removing the wrapper would again
break an implementation the public interface explicitly admits.

### 2026-08-05 — S8 adopts Serilog for mandatory file logging and official OpenTelemetry for OTLP
Context: S8 promises console and file logs without a collector, but the .NET logging stack has no
built-in file provider. The existing design also promised traces, metrics and optional OTLP without
naming packages, versions or a provider-neutral database-instrumentation seam. That left the first
implementation to choose dependencies and public behaviour by accident.
Chosen: **Serilog.Extensions.Hosting 10.0.0**, **Serilog.Sinks.File 7.0.0** and
**Serilog.Sinks.Async 2.1.0** integrate mandatory file logging beside the standard console provider,
with daily and 100 MB rolling,
14-day and 31-file retention, shared role-specific files, and a 10 000-event non-blocking buffer
whose supported inspector exposes exact dropped-event counts. **OpenTelemetry.Extensions.Hosting,
OpenTelemetry.Exporter.OpenTelemetryProtocol, OpenTelemetry.Instrumentation.AspNetCore,
OpenTelemetry.Instrumentation.Http and OpenTelemetry.Instrumentation.Runtime**, all **1.17.0**,
provide traces, metrics, logs and OTLP HTTP/protobuf. The official experimental in-memory retry is
enabled and accepted for the 0.x Platform surface; it has no disk queue, and an OpenTelemetry
upgrade must revisit it. Persistence emits one provider-neutral activity around each unit-of-work
transaction through a stable Abstractions source name, so both database providers have the same
span without either taking an OpenTelemetry dependency.
Rejected: **Console-only Microsoft logging** — cannot satisfy mandatory file logging. **A custom
file provider** — reimplements rolling, retention, multi-process sharing and queue monitoring that a
mature provider supplies. **NLog** — capable, but adopting two equivalent providers buys no second
capability; Serilog's file and async sinks directly expose the required sharing and drop inspector.
**Npgsql.OpenTelemetry** — its database tracing remains experimental and SQLite has no equivalent,
so taking it would make provider behaviour diverge. **A `DbCommand` proxy** — hand-rolls broad SQL
instrumentation, creates a parameter-leak boundary, and exceeds S8's unit-of-work requirement.
**A custom OpenTelemetry batch processor or parsing SDK diagnostic strings** — would manufacture
exact OTLP drop accounting on unsupported internals. Exact OTLP queue drop counts remain outside D3
until the SDK exposes a supported metric or hook.
Reversibility: moderate before publication; expensive once consumers and operators depend on the
file shape, source names and package behaviour.

### 2026-08-05 — S8 telemetry policy is fixed, typed and non-blocking
Context: The earlier observability page called an OTLP endpoint a connection string, allowed tenant
metric labels, always sampled product-specific plugin work, and promised errors and slow traces were
always kept. The contract named no telemetry options, file policy, redaction boundary, resource
identity or testable backpressure behaviour. Those claims cannot all be implemented with head
sampling and the supported OpenTelemetry SDK surface.
Chosen: add `TelemetryOptions` under `Platform:Telemetry`, with only `LogDirectory` and nullable
absolute HTTP/HTTPS `OtlpEndpoint`; endpoint absence starts no exporter and makes no outbound
connection. The fixed policies are UTF-8 JSON Lines, bounded non-blocking local and OTLP queues,
four shared identity fields, a non-injectable redactor, per-instrument bounded label allowlists,
upstream sampling decisions honoured, deterministic 10% trace-id sampling for new HTTP roots, and
the stored origin decision copied to a new linked dispatch trace. Blocking-sink tests use a gate:
application work must complete while export remains blocked, then export must recover after release.
Rejected: **Also consuming `OTEL_EXPORTER_OTLP_*`** — creates two configuration sources with an
unstated precedence. **Exposing Serilog rolling, retention, buffer and sampling knobs** — turns fixed
D3 safety bounds into a public tuning surface before a deployment needs it. **Authentication
headers, client certificates, per-signal endpoints or alternate OTLP protocols** — useful later and
not required by any D3 deployment. **`service.instance.id`, tenant, correlation or other identifiers
as metric resource attributes or labels** — unbounded cardinality. **Always sampling plugins, errors
or slow traces in the host** — product policy in the first case and collector-side tail sampling in
the others. **An injectable redactor** — lets a consumer weaken a safety invariant the package
claims centrally.
Reversibility: expensive for the public option and source-name signatures after publication; cheap
for fixed internal bounds until operators rely on them.

### 2026-08-05 — Event handlers are reference types throughout the registration contract
Context: Reconciliation made `AddPlatformEventHandler<TEvent, THandler>` contractual with the
reference-type constraint required by dependency injection. The same registration triple reaches
`IEventHandlerRegistry.Register<TEvent, THandler>` at startup, but its public signature and the
deferred registrant still admitted value-type handlers. Review correctly identified the mismatch.
Chosen: require `THandler : class, IIntegrationEventHandler<TEvent>` at every registration stage.
The contract and implementation now state the constraint enforced by the supported composition API.
Rejected: **Leave the registry unconstrained** — permits a theoretical struct handler that cannot
enter through the supported extension, making the two public routes disagree. **Remove the extension's
constraint** — the DI registration API requires a reference type and would need a different service
registration mechanism with no design requirement. **Document the difference as intentional** — it
would preserve a route that no module can use and turn an inconsistency into policy.
Reversibility: expensive once third parties compile against the registry; relaxing it later is
additive, while this tightening rejects a previously compilable struct-handler call.

### 2026-08-05 — Handler registration is declared at module composition through one public extension
Context: S4 implemented the design's required explicit name–CLR-event–handler registration, but
`20-contract.md` specified only `IEventHandlerRegistry`, which does not exist while an
`IPlatformModule` receives its `IServiceCollection`. The implementation therefore exposed
`PlatformEventHandlerExtensions.AddPlatformEventHandler<TEvent, THandler>` without the contract
naming that public seam; reconciliation found the resulting contract drift.
Chosen: make the extension part of the contract. It records the triple at composition time, registers
the handler type for dependency injection, and leaves runtime validation to the startup-owned
registry. Both roles make the same declaration; only the worker constructs handlers.
Rejected: **Have modules resolve and call `IEventHandlerRegistry` directly** — no composition-time
registration path has a registry instance. **Move registration onto `IPlatformModule`** — couples
every module to Persistence even when it has no events, and broadens the Abstractions contract for a
Persistence-specific concern. **Keep the extension undocumented** — leaves a public API outside the
authoritative contract and lets its semantics drift.
Reversibility: expensive once consumers compile against the extension; removing or relocating it is
a source-breaking change.

### 2026-08-05 — Kit upgrade to `9896915`: adopt self-ticking checkboxes and carved-out milestones/projects
Context: Upgrading the agent kit from `9b8313c` to `9896915`. The kit's *Tracking work* section moved
from "only issue-opening is carved out of the authorization rule; milestones, projects, and ticking
a `Done when` box all need my sign-off" to "issue, milestone, and project writes are all carved out
(deletion excepted); `/slice` ticks its own boxes in the same run it reports a criterion met." This
repository had already adopted the adjacent piece — `/slice` branching, committing, and opening its
own draft PR (see the 2026-08-04 entry below) — so this is the next increment of the same direction,
not a new one.
Chosen: Adopt in full. `AGENTS.md`'s *Tracking work* and *Git and delivery* sections, and
`.claude/commands/{install-all,kit-help,pr,slice,track}.md`, now match the kit's current text. Also
carried forward, non-conflicting: the session-boundary "banner" instruction (adapted to name this
repository's *Effort and model selection* section rather than the kit's *Command routing* table,
since the 2026-08-04 entry below already declined the table conversion), and the Codex/Copilot scope
note on `tools/Measure-Session.ps1`.
Rejected: **Keep the current, more conservative rule** — ticking and milestone/project creation stay
mine; the six files above stay as they are. Rejected because the adjacent draft-PR decision already
established the direction, and holding this one piece back leaves `/track` and `/slice`'s own
descriptions of themselves inconsistent with what they actually do.
Reversibility: cheap — a doc/procedure change with no data-shape implication.

### 2026-08-05 — `tools/Measure-Session.ps1` upgraded; `-Watch` added as a second, `UserPromptSubmit` hook
Context: Same kit upgrade. The script gained a vendor-detection fix (a foreign, non-Claude transcript
now refuses rather than silently reporting zero cost), JSON-by-default output with `-Human` for the
old text table, and `-Watch` — a `UserPromptSubmit` hook that warns once a session's context crosses
150,000 tokens and stays silent below it. `INSTALL.md`'s bounded exception requires proposing the
exact hook JSON and waiting, separately from the general reconciliation sign-off, before adding a
second hook.
Chosen: Upgrade the script, and add the `UserPromptSubmit` hook exactly as proposed —
`Measure-Session.ps1 -Watch`, 10s timeout — alongside the existing `SessionEnd` hook in
`.claude/settings.json`. Neither event had a competing hook. `tools/Measure-Session.Tests.ps1`
installed alongside it (previously absent).
Rejected: **Upgrade the script but skip `-Watch`** — take the bugfix and JSON-default change only,
leave `settings.json` untouched. Declined once the user confirmed the fuller option.
Reversibility: cheap — one param, one script block, one `settings.json` key.

### 2026-08-05 — L2 pins the corrected landing-page package at 0.2.0
Context: L2 could begin only once an immutable release preserved L1's complete static-head contract.
`subzerodev-platform-ui-landing-page@0.2.0` is the first release to carry the typed custom-adapter
metadata for Open Graph, X/Twitter, icons, theme colour and no-script content.
Chosen: add `0.2.0` as an exact `site/` development dependency and route Platform's build, development
and protected merge commands through its CLI.
Rejected: **`0.1.0`** — its adapter cannot express the required static head. **A `0.x` range or
`latest`** — would let the integration change without a Platform review. **A Git or local-path
dependency** — makes the consumer depend on mutable or workspace-local source rather than the
released artifact.
Reversibility: cheap. A future reviewed release changes one exact version and its lockfile entry.

### 2026-08-04 — L2 consumes the reusable landing-page package only after its adapter preserves L1's static document contract
Context: L1 built a consumer-owned React site and also copied the Vite and protected-merge
integration into this repository. `SubZeroDev.Platform.UI.LandingPage` now publishes that reusable
integration as `subzerodev-platform-ui-landing-page@0.1.0`, from source commit
`d2625b7be51585371d9f0b6c0b435c25e6ea4ade`, specifically so consumers stop owning those copies.
The released adapter already owns route builds and the protected docs merge, but its metadata
contract cannot reproduce all static head content L1 currently verifies: Open Graph title,
description, type and URL; Twitter card metadata; icon links; theme colour; and `<noscript>` text.
Chosen: add L2 in [`40-site.md`](40-site.md) to consume the first corrected `0.x` release as an exact
`site/` development dependency through its custom adapter and CLI. The package owns Vite
configuration, entry HTML generation and protected merge; Platform retains its React, CSS, content,
route metadata values, tests and caller-owned CI/deploy policy. L2 is blocked until the package
publishes a contract that preserves the current static document surface, and no version number is
invented before that release exists.
Rejected: **Consume `0.1.0` now and drop the metadata its adapter cannot express** — the smallest
migration, and a silent public regression made to fit a dependency. **Keep the local Vite config,
entry HTML and PowerShell merge indefinitely** — avoids the blocker and leaves the duplicated
integration the package now owns. **Use the generic README renderer** — removes the React project
too, and discards the status-page composition and roadmap parser L1 exists to ship. **Adopt the
package's reusable deployment workflow or composite action in the same slice** — available, but it
also changes the caller's documentation-build orchestration, permissions boundary and deployment
shape; direct CLI consumption replaces the duplicated mechanism without transferring deployment
policy. **A Git submodule, local path or floating npm range** — each makes the consumer depend on
mutable or workspace-local source instead of the immutable package release this integration is meant
to provide.
Reversibility: moderate. Returning to local integration means recreating deleted build machinery;
switching between exact package releases is cheap and deliberate.

### 2026-08-04 — Kit catch-up install; picked up `/install-all` and a `Measure-Session.ps1` fix
Context: Target's `.claude/kit.json` recorded kit commit `8d4ffdb`; kit HEAD is `9b8313c`. The gap is five files: `.claude/commands/install-all.md` (new), one `AGENTS.md` line naming its effort tier, one bugfix in `tools/Measure-Session.ps1`, and two entries in the kit's own decision log (never copied into a target). `install-all.md` and the `kit.json` bump were already present, uncommitted, on this branch when this install started — a partially-applied attempt at this same increment; this run completed it rather than redoing it.
Chosen: Added one sentence to the existing effort-tier prose in `AGENTS.md` — that prose already names `/track`, `/verify`, `/pr`, `/resolve`, `/refine`, `/kit-help` individually but never `/install`, so `/install` and `/install-all` were added together, same tier (sonnet, medium), with `/install-all`'s one escalation condition. Applied the kit's `Measure-Session.ps1` fix verbatim (guards `$session.Id.Substring(0, 8)` against ids ≤ 8 chars) — pure bugfix, no conflicting rationale.
Rejected: **Leave the `/install` mention out** — it predates this kit bump and arguably wasn't this increment's gap to close; rejected because the kit's new `/install-all` row made the omission newly visible, and leaving it out means the next reader has no tier for either command in the one place that states tiers in prose.
Reversibility: cheap — one sentence, one guarded substring.

### 2026-08-04 — Kit catch-up install; skipped the new agent.md lesson, this repo is its source
Context: Installing the agent kit's catch-up (five commits since this repository's `da5d1f7`-era install: `/verify`/`/pr`/`/resolve`, the human-first fenced-issue shape, stable criterion ids). The kit's seed gained a new lesson, "a fix that only changed the odds is not a fix" — a generalised, evidence-stripped SQLite connection-pooling / stale-schema-snapshot incident. The installer's own provenance rule requires checking whether an offered lesson actually originated in the target before adding it back.
Chosen: Skip it. `agent.md`, *Verification*, already carries this exact incident as "Three green runs is not evidence a race is fixed" with more detail than the kit's version — the actual root cause (`Microsoft.Data.Sqlite` connection pooling serving a stale schema snapshot), the fix (`Pooling=false`), and the repro method. The kit's copy did not come from elsewhere; it came from here, generalised. Adding it back would duplicate a rule already stated more precisely.
Rejected: **Add the kit's version anyway, since the user approved it in general** — rejected because the approval was for the general case (Blog, GameEngine, neither of which had this lesson), and this repository is the one case the installer's provenance check exists to catch.
Reversibility: cheap

## Open
- **Development-environment automatic migration application is promised and was never built.**
  [`10-design.md`](10-design.md) says in *Control flow* §1 and again in *Failure modes* that
  application is automatic in the development environment; nothing implements it —
  `IMigrationRunner.ApplyAsync`'s only caller is `RunPlatformMigrateModeAsync` — and no slice
  criterion ever covered it. **The surface is undetermined**, so a contract amendment settling the
  four questions in [`20-contract.md`](20-contract.md) *Unresolved* 8 — owning package, trigger,
  failure behaviour, and whether it takes the provider-native migration lock — comes before any
  slice. Until then a developer's inner loop runs migrate mode explicitly, exactly as an operator
  does.
- **Two automated-review findings are valid, not fixed here, and now raised upstream** — [Docs-Template#62](https://github.com/The-Running-Dev/Docs-Template/issues/62) (mutable tag) and [Docs-Template#63](https://github.com/The-Running-Dev/Docs-Template/issues/63) (traversal). Both sit in files installed **byte-identical** from `ghcr.io/the-running-dev/docs-template` (verified by diffing against the image), and both were already tracked in `SubZeroDev.GameEngine`'s `TODO.md`.
  1. `docs-ci.yml` and `docs-deploy.yml` run in `ghcr.io/the-running-dev/docs-template:latest`, a **mutable tag** — the same commit can start failing after an image update, and past failures are hard to reproduce.
  2. `Test-Documentation.ps1`'s `Get-DocumentationFile` recurses the whole tree before applying `ExcludedSegments`, so excluded trees are still walked. Performance only.
  **Why not fix them here.** The installer keeps these files byte-identical to the template precisely so re-running it picks up upstream fixes; editing them makes this repository silently miss every future one, which is a worse failure mode than a mutable tag on a docs site. The fix belongs in `docs-template`. **Revisit when** that project ships a pin mechanism, or if a mutable-tag drift actually bites here.
- **`main` is protected** — both documentation checks required and strict (branch must be current), pull request required at 0 approvals so solo work is not blocked, review threads must be resolved, force-pushes and deletions blocked. Modelled on `SubZeroDev.GameEngine`, with one deliberate difference: that repository's `CLAUDE.md` claims `required_review_thread_resolution` is on, and its API says otherwise — enabled here anyway, because the automated reviewer leaves threads.
  **GitHub Pages is already enabled** and needs nothing — `build_type: workflow`, custom domain `platform.subzerodev.com`, `protected_domain_state: verified`. An earlier entry here said it was not enabled; that was asserted from the deploy workflow being newly installed rather than checked against the API, and it was wrong. `status` is `null` only because no deploy has run yet, which is expected — `docs-deploy.yml` triggers on push to `main`.

---

### 2026-08-04 — Poison writes name whether they consume a handler attempt
Context: S5 could not implement the contract's two attempt invariants through its only poison write. A `HandlerError` that poisons must increment `attempts` exactly once — including a permanent failure on its first delivery — while a `DispatchError` that ages out of deferral must poison without incrementing it. `PoisonAsync(id, holder, error)` carried no value that distinguished those transitions, so either implementation would contradict one invariant or infer policy from diagnostic text.
Chosen: Add `PoisonAttemptMode` with `Increment` and `Preserve`, and require it on `IOutboxStore.PoisonAsync`. The dispatcher selects the mode from the typed error branch it is already handling; the store performs one conditional state write against the live claim. This preserves the one-store policy boundary and keeps the attempt transition visible at the call site.
Rejected: **Infer the mode from the `error` string** — no signature change, and it turns stored diagnostic data into an undocumented control protocol where renaming an error code changes persistence semantics. **Make every poison preserve attempts and call `RecordFailureAsync` first for handler failures** — reuses existing members, and `RecordFailureAsync` releases the claim, so the following poison correctly returns `ClaimLost` and never applies. **Make every poison increment attempts** — simplest, and it violates the contract's defining separation between `HandlerError` and `DispatchError`. **Split poison into two methods** — the most explicit names, and it grows the public store surface with two operations whose only difference is one column expression.
Reversibility: cheap before S5 ships; expensive once a third-party provider or store implementation compiles against the signature.

---

## Index — decisions whose home is elsewhere

Reasoning, consequences and rejected alternatives live in the linked document, never here — *Single ownership* in `AGENTS.md`.

| Decision | Home |
|---|---|
| Platform is a framework plus optional application modules | [ADR-006](../docs/docs/adr/ADR-006-application-modules.md) |
| Catalogue and Ordering are admitted with one consumer, against the boundary test, with the objection retained | [`application-modules.md`](../docs/docs/application-modules.md) §4 |
| BarStrad is a third consumer; Notifications, channels, localized content and the command surface clear the guard | [`application-modules.md`](../docs/docs/application-modules.md) §2 |
| Near-term scope is six packages; the outbox is in scope; both a sample and the G1 edge prove D3 | [`00-brief.md`](00-brief.md) |
| The tenant column is a non-null `uuid` with an all-zero sentinel, not a nullable column or a slug | [`10-design.md`](10-design.md), *Alternatives* |
| Correlation ids belong to Observability, health endpoints to Hosting, the health check contract to Abstractions | [`10-design.md`](10-design.md), *Module boundaries* |
| Outbox delivery is at-least-once and unordered; rows are claimed by a portable conditional update, not a dialect-specific locking read | [`10-design.md`](10-design.md), *Concurrency and ordering* |
| The worker is a second host role of one Hosting package, not a separate package | [`10-design.md`](10-design.md), *Alternatives* |
| A database that is misconfigured fails startup; one that is merely unavailable starts and reports not-ready | [`10-design.md`](10-design.md), *Failure modes* |
| Background-work registrations declare a role, and the web host runs the registration heartbeat | [`10-design.md`](10-design.md), *Module boundaries* |
| The operation-scope contract and the trace-context contract live in Abstractions, beside the accessors | [`10-design.md`](10-design.md), *Module boundaries* |
| One handler per event Type, enforced at startup; a second registration is a named startup failure | [`10-design.md`](10-design.md), *Alternatives* |
| Outbox rows are claimed one at a time; the batch number is a per-tick budget, not a claim | [`10-design.md`](10-design.md), *Alternatives* |
| The four outbox row states — pending, processed, poisoned, discarded — are defined by predicate, and every consumer derives from them | [`10-design.md`](10-design.md), *Data model* |
| Schema change is expand-then-contract, and the dispatcher holds without consuming attempts while migrations are pending | [`10-design.md`](10-design.md), *Failure modes* |
| Identifiers store in RFC byte order on SQLite, and instant comparands are pinned with the column and clocked by the abstraction | [`10-design.md`](10-design.md), *Data model* |
| An event's stable Type name comes from explicit registration, and payloads serialize through a pinned `System.Text.Json` | [`10-design.md`](10-design.md), *Data model* |
| The provider seam is one store per Platform table over a capability contract, and transaction intent is a parameter | [`10-design.md`](10-design.md), *Module boundaries* |
| The worker probe binds loopback; last error and exception text never cross a wire in D3 | [`10-design.md`](10-design.md), *What the operational surfaces expose* |
| Migrate mode's exclusion is a provider-native lock; the lease guards scheduled work only | [`10-design.md`](10-design.md), *Failure modes* |
| Correlation is a persisted outbox column; eligibility and backlog age are past-due predicates; dispatch-state marks are conditional on the live claim | [`10-design.md`](10-design.md), *Data model* |
| The pending backlog is reported, never refused, and never pruned | [`10-design.md`](10-design.md), *Alternatives* |
| The pinned serialiser has no extension point; the registration triple is declarative, validated per role | [`10-design.md`](10-design.md), *Data model* |
| The background-work contract is tick-shaped; a persistence-less host is supported with scoped guarantees | [`10-design.md`](10-design.md), *Module boundaries* |
| Boundary contracts are projected, not authored; they get their own repository | [ADR-005](../docs/docs/adr/ADR-005-service-contract.md) |
| Platform is built in-house, with ABP as an architecture reference | [ADR-004](../docs/docs/adr/ADR-004-framework-build-not-adopt.md) |
| Package scope is per-registry, not one global name | [ADR-003](../docs/docs/adr/ADR-003-package-scopes-and-registries.md) |
| Platform is .NET, and the product boundary is a process boundary | [ADR-002](../docs/docs/adr/ADR-002-implementation-technology.md) |
| `SubZeroDev.Platform` is the framework, not the game product | [ADR-001](../docs/docs/adr/ADR-001-platform-identity.md) |
| "Narrative Engine" renamed to "Game Engine" | [ADR-001](../docs/docs/adr/ADR-001-platform-identity.md), consequences |
| Hosting is a workload boundary, not in-process port supply | [`engine-hosting-contract.md`](../docs/docs/engine-hosting-contract.md) §2 |

---

### 2026-08-04 — `Merge-LandingPage.ps1`'s docs/ guard hashes content, not just file count
Context: the ported script proved `docs/` untouched by comparing file counts under the docs subtree
before and after the merge. An automated PR review on L1 pointed out the real gap: a same-path
overwrite leaves the count unchanged, so the guard could not see one. Reproduced directly — a
synthetic landing dist carrying a `docs/somefile.html` overwrote the real docs subtree's file of the
same name with the count-only guard silently passing.
Chosen: hash every file under the docs subtree (`Get-FileHash -Algorithm SHA256`) into a relative-path
keyed map before and after the merge, and compare the two maps, naming every added, removed or
changed path in the thrown message. Cheap at documentation-site scale (tens of files).
Rejected: **Leaving the count check as the sole guard** — the option this finding correctly rejected.
**Hashing the whole tree as one aggregate digest** — cheaper to compute, but a single mismatch names
nothing; the per-path map costs one more line and tells the operator exactly which file changed.
Reversibility: cheap. One function, swapped for a stronger one; the script's external behaviour is
identical on every currently-passing case.

### 2026-08-04 — `site/vite.config.ts`'s dev-server file access is scoped to `design/`, not the repository root
Context: the roadmap's `?raw` import of `../design/30-slices.md` needs Vite's dev server to serve a
path outside `site/`, so `server.fs.allow` was widened to the repository root — ported unchanged from
the same pattern in `SubZeroDev.GameEngine/site/vite.config.ts`. An automated PR review on L1 pointed
out that this hands a network-exposed dev server (`vite dev --host`) read access to the entire
repository — including `AGENTS.md`, every design document, and anything else living at the root — for
a need that is exactly one directory.
Chosen: `server.fs.allow` widened to `design/` only. The import still resolves; nothing else outside
`site/` is reachable through the dev server's static file serving.
Rejected: **Leaving it at the repository root**, matching the engine's file exactly — the port was
convenient, not necessary, once the actual need was checked. **Vendoring or symlinking
`30-slices.md` into `site/`** — avoids widening `fs.allow` at all, but creates a second copy (or a
platform-specific symlink dependency) of a file `AGENTS.md`'s *Single ownership* says has exactly one
home.
Reversibility: cheap. One array literal.

### 2026-08-04 — L1's favicon and social-image PNGs are hand-encoded, not generated by an image library
Context: L1 needs a favicon set, an apple-touch icon and an Open Graph image, none of which exist yet
and none of which may reuse the engine's branded assets per `design/40-site.md`'s distinctness
requirement. `AGENTS.md` requires a decision-log entry both when a dependency is taken and when one is
passed over in favour of hand-rolling.
Chosen: `site/scripts/generate-status-icons.mjs`, a small one-off script building raw PNG bytes
(IHDR/IDAT/IEND chunks, a hand-written CRC-32) using only Node's built-in `zlib` for the DEFLATE step.
Run once, locally, to produce the four static PNGs committed under `site/public/`; not part of `npm
run check` or CI, since the assets do not change per build.
Rejected: **`sharp`** — the standard choice, but a native-binary dependency with platform-specific
prebuilds for four solid-colour circles that need no resizing, no format conversion and no photographic
fidelity; disproportionate for the job. **`canvas`** (node-canvas) — same shape of cost, native
bindings for drawing a circle. **Committing hand-drawn PNGs from an external tool** — no dependency at
all, but leaves no reproducible source if the mark ever needs to change, and an image asset with no
generator is exactly the kind of asset this repository's Markdown-generation conventions elsewhere
(`ConvertTo-Changelog.ps1`, `ConvertTo-DocumentationHomepage.ps1`) avoid.
Reversibility: cheap. Four small PNGs and one script; replacing either with a real library later costs
nothing this decision forecloses.

### 2026-08-04 — The slice-status marker check is a new repository-owned script, not an edit to `Test-Documentation.ps1`
Context: `design/40-site.md` originally named `Test-Documentation.ps1` as the file that would gain the
check for `30-slices.md`'s `**Status:**` markers staying internally consistent. Found during L1's
implementation, reconciled afterward: `Test-Documentation.ps1` is one of the files installed
**byte-identical** from `ghcr.io/the-running-dev/docs-template` (see the `## Open` entry above), and
this repository's established practice is not to hand-edit those files, so that re-running the
installer keeps picking up upstream fixes. Editing it for a check with nothing to do with the
template's own generic concerns would have silently broken that.
Chosen: `build/Test-SliceStatusMarkers.ps1`, a new, repository-owned script, run as its own
`slice-status-markers` job in `docs-ci.yml`, alongside the unmodified `documentation` job rather than
inside it.
Rejected: **Editing `Test-Documentation.ps1` directly** — satisfies the design doc as originally
worded, but breaks byte-identical parity with the template and contradicts the rationale already on
record in the `## Open` entry above; a future template re-install could silently overwrite the
addition, or this repository would silently stop picking up upstream fixes to that file. **Forking
`Test-Documentation.ps1` into a repository-owned copy** — decouples entirely from the template, which
is the most invasive option for the smallest problem: it would also lose every future upstream fix,
including the two findings the `## Open` entry already tracks upstream (mutable tag, traversal
performance).
Reversibility: cheap. The check is one small script and one CI job; folding it into
`Test-Documentation.ps1` later costs nothing this decision forecloses.

### 2026-08-04 — The landing banner reads "open incidents 0", not the brief's originally drafted "incidents 0"
Context: `design/40-site.md` specified the landing page's fact strip as `uptime 100% · incidents 0 ·
consumers 0 (2 planned)`. The roadmap page, specified in the same document, frames every merged slice
as a resolved incident. A banner claiming zero incidents at all, one click from a page showing four
resolved ones, reads as a contradiction rather than the intended joke — found during L1's
implementation.
Chosen: "open incidents 0". True in both senses at once — no incident is currently open, and several
are resolved on the roadmap — so the two pages no longer disagree with each other.
Rejected: **Keeping the literal "incidents 0"** — matches the document as originally drafted, but
relies on a reader supplying the "open versus historical" distinction unaided while looking at two
pages that appear to disagree.
Reversibility: cheap. One word, in one string, on one page.

### 2026-08-04 — `design/30-slices.md` gains a done marker, beneath each heading rather than inside it
Context: L1's roadmap page must derive slice status from a single source rather than inventing its
own, and the source has to be something the public page can import and something a slice already
touches when it ships. `30-slices.md` had no such marker.
Chosen: a `**Status:**` line — `shipped`, `in progress`, or `queued` — as the first line of each
slice's body, immediately under the heading. A slice sets its own marker to `shipped` in the same
change that satisfies it and sets the next one to `in progress`. This puts done-ness in the document
as well as in the slice's tracking issue, which `AGENTS.md` *Tracking work* separates deliberately —
the issue stays the tracker's record, and a disagreement between the two is reported per that section
rather than silently reconciled by editing either.
Rejected: **Deriving shipped-ness from `git log` subjects** (`S4 — Outbox enqueue (#32)`) — no second
home for done-ness, but it makes a static page depend on clone depth, git availability inside the
containerised CI jobs, and a commit-subject convention nobody validates; the failure mode is a page
that silently reports nothing shipped after a shallow checkout. **Querying the GitHub issue tracker
at build time** — a network dependency in the build of a page whose entire point is that it needs
none. **A hand-authored status list inside `site/`** — the option explicitly declined when L1 was
scoped, since it is a second copy of the tracker with nothing keeping it honest. **An `[x]` prefix on
the heading itself**, matching `SubZeroDev.GameEngine`'s `TODO.md` convention — rejected because
Docusaurus derives anchors from heading text, and prefixing one would change every slice's anchor and
break the existing `[S9](#s9--pack-publish-consume-and-the-api-reference)` in-document link along
with any inbound link written later.
Reversibility: cheap to relocate the marker again; expensive to have shipped without one, since every
already-merged slice would need the backfill this entry performs.

### 2026-08-04 — A Node/Vite/React toolchain enters the repository, matching `SubZeroDev.GameEngine`'s
Context: L1 needed a toolchain for a standalone status-page landing site with two build entry points,
component tests and a production-metadata check. The repository owner decided this directly rather
than leaving it derived: use Node and Vite, transcribing the engine repository's already-proven
`site/` setup, rather than choosing independently.
Chosen: Node, Vite with the React plugin and two rollup inputs, vitest with jsdom and
testing-library, oxlint, prettier, and TypeScript strict with project references — the same script
names and the same meanings as `SubZeroDev.GameEngine/site/package.json`. Ported: configuration and
scaffolding only. Written fresh: every stylesheet, token, copy block, page composition, and the
roadmap's parser — see [`design/40-site.md`](40-site.md), *Toolchain* and *Design language*.
Rejected: **Docusaurus pages inside the existing documentation site** — no control over the page
shell, and the status-page conceit needs that control. **Hand-written static HTML with no build** —
no component tests, and every count on the page becomes a typed number, which L1's acceptance
criteria forbid. **A Node toolchain that differs from the engine's** — two setups to keep current for
one person, for no gain: the requirement was that the two sites look unalike, not that they build
unalike.
Reversibility: moderate. Swapping the bundler later touches only `site/`; the accepted cost stands
regardless — this repository now carries a second package manager and a second lockfile alongside
its .NET tooling, and both need to stay current.

---

### 2026-08-04 — The settings fingerprint's canonical form and digest (unresolved item 1)
Context: S3's split-brain surface rests entirely on two hosts computing the same string from the same settings, and its acceptance criterion requires `Compute` to agree **across two separate processes**. The contract named the stake and not the construction: two hosts computing it differently "would report a permanent false mismatch", which turns the one check that can detect split-brain into the check an operator learns to mute. Three concrete traps sit in the way, and each produces a green single-process test with a broken pair of hosts: `Type.GetProperties()` guarantees no ordering, so reflection order is not reproducible across a trimmed or AOT publish or a runtime upgrade — and the brief commits to upgrading every release; `double.ToString()` is culture-sensitive and its default format has already changed once across a .NET major version, and `RetryBackoffFactor` is the one `double` among the nine `[Fingerprinted]` properties; and `string.GetHashCode()` is seeded per process, so anything built on it mismatches every time.
Chosen: key each `[Fingerprinted]` value by its **configuration path** — the same string a startup error names, so a fingerprint and an error message speak one language — then ordinal-sort the pairs by path, **length-prefix** both path and value so no two different inputs can concatenate to identical bytes, prefix a format version, hash with **SHA-256**, render lowercase hex. Values format invariantly: `TimeSpan` as `"c"`, `double` as `"R"`, enums by name, null distinctly from empty. Sorting by path rather than reflection order is the load-bearing part; everything else is hygiene.
Rejected: **Digest plus the stored canonical form**, so the check could name the differing *setting* rather than only the peer instance — materially better to diagnose, and rejected because it needs an unbounded column and forecloses ever marking a secret `[Fingerprinted]`, since the settings would then sit readable in a table. **Canonical JSON then SHA-256** — self-describing, and it makes a second serializer's exact behaviour contractual alongside the one already pinned for payloads; two things that must never drift are worse than one. **A non-cryptographic hash (xxHash, FNV)** — faster, at one computation per heartbeat, which is no reason at all; it needs `System.IO.Hashing` rather than the BCL, which ADR-004 §4 makes something to justify.
The **byte-exact encoding is normative in [`20-contract.md`](20-contract.md), beside `ISettingsFingerprint`** — UTF-8 throughout, `uint32` big-endian byte-count prefixes, a one-byte presence tag keeping null distinct from empty, and the version as literal ASCII inside the hashed input. It is specified there rather than left to the implementation because a prose description two implementations could follow differently is not a canonical form at all, and the interface is public surface a third party may reimplement. This entry holds the reasoning; that section holds the form.
Known cost, accepted: a digest can report *that* a peer disagrees and never *about what*. S3's criterion asks only that the peer instance be named, so this is in scope — but the operator diagnosing a real mismatch pays it, and the rejected alternative above is what would buy it back.
Reversibility: expensive once hosts are running — the format version exists so a change is a deliberate, visible break rather than a silent one. Changing it makes every host's fingerprint change at once, so a rolling upgrade shows a transient `SettingsFingerprint` degrade until both are on the new version. Degraded, never unhealthy, so it costs a warning and not traffic.

### 2026-08-04 — `InstanceId` is machine name plus a random suffix (unresolved item 5)
Context: The contract required two hosts of one role on one machine to differ, and a restart to produce a new value — because after a crash the dead registration row survives until the peer grace expires, and a restarted host reusing its identity would silently adopt that row and mask the crash. The value is also the outbox's `claimed_by`, so it is read by a human mid-diagnosis rather than only compared by code.
Chosen: `Environment.MachineName`, a slash, and eight hex characters from `RandomNumberGenerator`, minted once at startup — `homelab-01/7f3a9c2e`. Uniqueness and restart-freshness come from the random suffix alone, so neither process-id reuse nor a clock adjustment can break either property. Thirty-two bits against single-digit hosts per installation is orders past sufficient. The machine prefix earns its place at the moment the value is actually read, and is genuinely diagnostic for split-brain, which usually means two machines.
Rejected: **Machine name plus process id** — the most useful for an operator, who can go and look at that process, and the one derivation the requirement rules out: process ids are reused after a restart, and containers hand nearly everything PID 1, so two sibling hosts collide. **Machine name plus start instant** — needs no randomness, and two processes starting within one tick collide while a clock adjustment can order a host before its predecessor; the design routes every other instant through `IClock` precisely to avoid this class of thing. **An opaque UUIDv7** — smallest surface, sorts by mint time, and tells someone staring at `claimed_by` nothing about where that process is.
Also settled: the **role is not encoded** in the identifier. `HostRegistration` already carries a `role` column, and two homes for one fact is two things that can disagree.
Reversibility: cheap — nothing joins on the value's structure, and a changed derivation costs one heartbeat interval.

### 2026-08-03 — The operation scope carries a culture, and the outbox row a culture column
Context: Reviewing BarStrad — a running Discord-plus-web ordering product in Bulgarian and English — against this design. Nothing in Platform carried a culture: the scope held tenant, principal, correlation and trace, and the outbox row held no equivalent column. BarStrad demonstrates the failure concretely and already has the bug: `BarStrad.Bot/src/Bot.ts` keeps one shared `Menu` defaulting to `bg`, while `!menu-en` builds a throwaway instance, so an English customer's order reaches staff under its Bulgarian name. The structural version is worse than the transcription error — the outbox row is written by the web host and dispatched by the worker, a different process under a different operating-system culture, so a notification rendered from an event has no surviving signal of the language its actor was using. **The originating culture is not reconstructable at render time**, and the case that proves it is a recipient with no user record to hold a preference: a shared staff channel, an operations inbox, a printer.
Chosen: `CultureTag` as a value type whose invariant form is the empty tag, so `default(CultureTag)` is already `Invariant`; `IOperationScope.Culture` as a fifth member with `ICurrentCulture` beside the three existing accessors; both `Begin` overloads taking `CultureTag culture = default`; and a required `Culture` column on `OutboxMessage`, stamped at enqueue and propagating unchanged through derived events exactly as `Correlation` does. Platform resolves culture from nothing in D3 — no `Accept-Language`, no claim, no stored preference — so the interface exists to supply the column, precisely as `ICurrentTenant` does. Unlike the tenant, a product **may** set it explicitly when opening a scope, because no non-goal pins it.
Rejected: **The column alone, deferring the accessor to D4** — the brief's own tenancy split (data now, code later) argues for it and it was the recommendation, but a column with no ambient supplier forces every enqueue site to pass culture by hand, which is the bespoke wiring the brief's definition of done exists to forbid. **Neither, treating language as a recipient preference resolved at render** — sound wherever recipients have user records, and it fails on the case that forced the decision. **`CultureInfo` on the scope rather than a tag** — the runtime type is right for rendering and wrong for carrying; a column holds a tag, and the tag is what crosses a process boundary. **Deferring to D4 with Notifications** — S4 is unstarted, so the column is free today and a migration over existing rows later.
Reversibility: expensive for the column, cheap for the accessor — which is the whole reason it was taken at S4-minus-one rather than when Notifications is designed.
Sequencing: **S4**, not S1. The amendment was first written into S1's touches and criteria, which was wrong twice over — S1 had already merged, so the work breakdown credited a shipped slice with work absent from the code, and culture has no consumer in S1, S2 or S3, so declaring the accessor there would land it unexercised. [`30-slices.md`](30-slices.md)'s own rule settles it: a member is declared in the slice that implements and exercises it, and culture's only D3 consumer is the outbox column. The consequence, accepted: the contract specifies a five-member scope that the code will not satisfy until S4, which compiles either way because the parameter is optional and the invariant is the empty tag.

### 2026-08-03 — A signature S2 could not proceed without: how a module's migrations reach the runner
Context: `IMigrationRunner.ApplyAsync` and `RunPlatformMigrateModeAsync` both take no migration list —
by design, since the contract fixes both signatures already — so migrations must be discoverable from
dependency injection alone. Nothing in the contract named the contribution point: `IPlatformModule`
carries no migration member, and neither `IHealthCheck` nor `IBackgroundWork`'s registration route
had an equivalent stated for migrations. Found before any Persistence code was written, on the same
stop-and-ask rule S1's three absences were found under.
Chosen: **`IModuleMigration` and `IModuleMigrationSource`**, collected as
`IEnumerable<IModuleMigrationSource>` the same way checks and background work are collected. A module
registers one source naming every migration it owns; `IModuleMigration.Name` orders migrations within
one module's history, ordinally; `ApplyAsync` receives the connection and transaction the runner
already holds, so bookkeeping and the provider-native lock stay the runner's alone.
Rejected: **A parameter on `ApplyAsync`** — changes a contract signature already fixed for a reason
unrelated to this gap, and pushes migration discovery onto every caller of migrate mode instead of
onto module registration, where checks and work already put it. **Assembly-scanning modules for a
migration type** — no new member, and §2 permits scanning but forbids it as the only route, the same
objection S1 raised against scanning for `IPlatformModule` itself. **A method on `IPlatformModule`
returning migrations** — couples every module to Persistence even when it defines none, where the
DI-collection route lets a module with no migrations register nothing.
Reversibility: cheap — no Persistence code exists yet, and this is the moment the contract is
cheapest to change. Expensive once a provider or a product module compiles against it.

### 2026-08-03 — Persistence wires itself in; Hosting keeps zero reference to it
Context: `IUnitOfWork`, `IProviderCapability` and the `Database`/`PendingMigrations` health checks need
to reach a host's container somehow, and the contract states plainly that "Hosting does not
reference Persistence; a host composed without Persistence is a supported shape." `RunPlatformMigrateModeAsync`
is nonetheless listed beside `AddPlatformWebHost` under the same `PlatformHostExtensions` heading,
which only looks like a contradiction if a C# namespace is assumed to map one-to-one onto an assembly.
Chosen: **Persistence registers itself.** It exposes its own `IServiceCollection` extension, called
once by the product alongside `AddPlatformWebHost()` — the same shape `AddPlatformObservability`
already has as a standalone call, just without Hosting also calling it automatically.
`RunPlatformMigrateModeAsync` is implemented *inside the Persistence project*, as a static class in
the `SubZeroDev.Platform.Hosting` namespace — the idiom `Microsoft.EntityFrameworkCore`'s
`AddDbContext` already uses to extend `Microsoft.Extensions.DependencyInjection.IServiceCollection`
from a different assembly than the one that declares it. The call site stays
`builder.RunPlatformMigrateModeAsync(ct)`; `Hosting.csproj` gains no `ProjectReference`.
Rejected: **Hosting takes a project reference to Persistence** — one fewer line in a product's
`Program.cs`, and it contradicts the contract's own sentence, makes Hosting unbuildable without
Persistence existing, and quietly turns "a host composed without Persistence is a supported shape"
false the day Hosting's own composition path starts touching Persistence types unconditionally.
Reversibility: cheap now; expensive once a product's `Program.cs` is written against either shape.

### 2026-08-03 — The provider contract tests are an abstract base with one subclass per provider
Context: The last of the contract's seven unresolved items, assigned to S2 and settled by it without
being recorded — the same omission the entries above were written to correct. It decides how a third
party runs the suite against a provider of their own, so leaving it open invites a future session to
re-decide it and diverge from what shipped.
Chosen: **an abstract base class holding every assertion, with one subclass per provider.** A
subclass supplies a connection string, its lifecycle, and the handful of schema queries an assertion
needs that have no portable form — listing a table's foreign keys, counting tables by name. Every
assertion lives once. PostgreSQL's subclass takes a container per test class and a fresh database per
test; SQLite's takes a temp file. A third party adds a subclass.
Rejected: **A shared suite parameterised by a provider factory** — no inheritance, and the
provider-specific schema queries would have to be passed in as a bag of delegates or pushed into the
capability, which the membership rule forbids since they are test concerns and not runtime
differences. **`[Theory]` over a provider enum** — the least ceremony, and container lifetime per
test case rather than per class makes the PostgreSQL run far slower, while a third party could not
add a provider without editing Platform's own test file.
Reversibility: cheap for this repository, expensive once a third party's suite inherits the base.

### 2026-08-03 — The six dependencies S2 took, none of which had been logged
Context: `AGENTS.md` requires a log entry naming the alternatives whenever a dependency is taken, and
[ADR-004](../docs/docs/adr/ADR-004-framework-build-not-adopt.md) §4 requires one when a package is
*passed over* too. S2 took six and logged none — caught by a second reconcile pass, not by review.
The entry below on xUnit was titled "and nothing else was added", which stopped being true the moment
Persistence existed; its title is now scoped to S1, which is what its body always claimed.
Chosen: **`Npgsql`** and **`Microsoft.Data.Sqlite.Core`** with **`SQLitePCLRaw.bundle_e_sqlite3`** —
the ADO providers for the two databases the contract names by enum value, so there is no version of
D3 that does not take them. The `.Core` package plus an explicit SQLite bundle rather than the
umbrella `Microsoft.Data.Sqlite`, because the umbrella pulls a bundle transitively at a version
carrying a known advisory, and naming the bundle explicitly is what lets it be pinned. **The
`Microsoft.Extensions` DI and hosting abstractions**, already the dependency line Abstractions
accepted for `IPlatformModule.Register`, extended here for `IHostedLifecycleService` and the
registration extension. **`Testcontainers.PostgreSql`** for tests only: it starts a disposable
PostgreSQL per test class, so the contract suite runs identically on a developer's machine and in CI
with no ambient database to arrange or reset.
Rejected: **Pointing the tests at an already-running PostgreSQL** via a connection string from
configuration — no test dependency at all, and it makes the suite's result depend on the state of a
database nobody owns, needs a documented setup step the brief's self-hosted audience would also have
to follow, and gives CI something to provision separately. **Testing PostgreSQL behaviour against
SQLite alone** — the fourth CI assertion is that both providers pass, so a suite that runs one
proves the opposite of what it claims. **Entity Framework Core** for the store or the migrations —
the largest thing not taken, and [ADR-004](../docs/docs/adr/ADR-004-framework-build-not-adopt.md) §4
requires saying why: the design's provider seam is one store implementation parameterised by a
capability, the migrations are a dozen lines of DDL per module, and EF Core would supply a model,
a change tracker and a migration history this design has already specified differently — its own
per-module histories, its own instant and identifier encodings. It remains available to a *product*
for its own tables, which §2's refusal to impose a repository pattern exists to protect.
Reversibility: cheap for Testcontainers, which no shipped package references. **Expensive for the two
ADO providers**, which are the contract's own provider enum made real.

### 2026-08-03 — Reconciling S2, second pass: four defects the first pass did not look for
Context: A second `/reconcile` run over the same tree, verifying behaviour by executing it rather
than by reading it. The first pass compared signatures and prose and found real drift; it did not run
the system, and four defects were invisible from the source alone. One of them invalidates a claim
the first pass itself made.
Chosen: **(1) No `TransactionError` variant carries an exception message.** `Faulted` took
`exception.Message` and health checks passed it into `HealthCheckResult.Detail`, which the probe
renders at full detail — verified returning `"detail":"The operation was canceled."` on the wire, a
plain violation of invariant 46. `Faulted()` now takes no argument and every variant's detail is a
fixed operator-facing string. **(2) A connect or command timeout classifies as `Unavailable`, not
`Faulted`.** Both providers surface it as `OperationCanceledException`, which matched no provider
exception arm and fell through to `Faulted` — reporting `IsRetryable = false` for a database that is
merely down, the single most retryable condition in the system. **(3) The SQLite migration lock
honours `Persistence:SqliteBusyWaitBound`** instead of a hardcoded second, which also makes true a
sentence the first reconcile pass had added to the contract while the code disagreed with it. **(4)
`ApplyAsync` refuses two modules whose history tables resolve to one name**, with a new
`MigrationError.HistoryTableCollision`, before acquiring the lock and before applying anything.
Module names are unique *case-sensitively*, so `Orders` and `orders` are two legal modules — verified
— and both snake-case to one table; sharing a history means each reads the other's applied list and
skips its own migrations. The first pass had asserted in the contract that the registry already
prevented this. It does not.
Rejected: **(1) Qualifying invariant 46 to permit messages at full detail** — full detail is loopback
and Development only, so nothing leaves the box; declined because it turns an absolute invariant into
a conditional one, and full detail is still reachable by anything running on the host. **(2) Leaving
`Faulted` as the catch-all** and documenting it — no code change, and `IsRetryable` then misreports
the one condition it exists for. **(3) Correcting the doc to name a fixed one second** — keeps the
wait short, and leaves a magic number in a package where every other duration is a setting. **(4)
Correcting the doc to admit the hazard and leaving it unguarded** — honest, and it documents a
silent-corruption path instead of closing it for a dictionary lookup.
Reversibility: cheap for all four. (1) is expensive in one direction only — a consumer parsing a
detail string would break, and no consumer should be parsing one, which is why it is fixed now.

### 2026-08-03 — Readiness answers even when its own budget expires
Context: Found by the same executing pass, in S1's code rather than S2's. The probe links every
check's timeout to an endpoint budget; when the budget expired, `RunOneAsync`'s cancellation handler
was guarded on that same budget token, so the guard was false exactly when it was needed, the second
handler excluded `OperationCanceledException`, and the exception escaped `RunAsync`. Readiness then
answered **500 with an error envelope and no report**. Today's two checks sum to 15 s against a 15 s
budget — measured at 15.18 s, on the edge — and S3 adds two checks with S6 adding three, so this
moves from edge case to normal.
Chosen: guard on the **caller's** token instead. The budget expiring degrades the entries that did
not finish and still returns the report; only a genuinely disconnected client stops the probe, since
there is then nobody to read it. `AGENTS.md` says a defect found outside the current slice is noted
rather than fixed, and this was fixed anyway: readiness is the always-on surface this design routes
every silent condition through, and a 500 there drains the host while telling the operator nothing.
Both behaviours now have a test, and the budget test was confirmed to fail against the old guard
before the fix was kept.
Rejected: **Noting it and deferring to its own slice** — the strict reading of the out-of-slice rule,
and it leaves a latent 500 on the operational surface through the two slices that make it routine.
**Raising the endpoint budget above the sum of the check timeouts** — treats the arithmetic and not
the escape, and the sum grows with every slice while the budget would have to be guessed ahead of it.
Reversibility: cheap.

### 2026-08-03 — Reconciling S2: the capability seam was not implementable, and four values it set
Context: `/reconcile` against the working tree after S2. The largest finding is one no reading of the
contract would have caught, because the contract was self-consistent and the code was too: building
the *second* provider is what exposed it. `IProviderCapability.BeginAsync` returned
`Result<TransactionError>` — success or failure and nothing else — and `IMigrationLock` was a bare
`IAsyncDisposable`, so neither handed back the `DbConnection` and `DbTransaction` it had just opened.
The unit of work and the migration runner recovered them by casting to `internal` seam interfaces, so
a third party's own capability would compile and then throw `InvalidCastException` at first use. That
contradicts the entry below that prices this seam as expensive *precisely because* a third party
implementing a provider compiles against it.
Chosen: **(1) `BeginAsync` returns `Result<IAmbientTransaction, TransactionError>`** — the existing
public triple (intent, connection, transaction) rather than a second interface of identical shape;
the unit of work is what makes the pair *ambient*, the capability only opens it. **(2)
`IMigrationLock` gains `Connection` and `Transaction`.** **(3) `Classify(Exception)` joins the
capability**, admitted by the membership rule verbatim — what counts as busy, as a conflict, or as
unreachable is a different exception type and code per provider, while the response to each is
identical. Both internal seam interfaces are deleted, and the capability becomes stateless, so one
instance now serves every overlapping unit of work instead of one being constructed per transaction.
**(4) Migrate mode is atomic per run, not per migration** — the contract's failure table implied
earlier migrations survive a later failure; they do not, and this is a consequence rather than a
preference: on SQLite the lock *is* the transaction, so committing per migration would release the
exclusion mid-run and let a second invocation interleave. PostgreSQL could differ and deliberately
does not. **(5) The SQLite lock is an *immediate* transaction, not the *exclusive* one both design
documents specified** — `Microsoft.Data.Sqlite` exposes only deferred and immediate, a raw
`BEGIN EXCLUSIVE` yields no `DbTransaction` for `IModuleMigration.ApplyAsync` to receive, and under
the WAL this design mandates exclusive's extra property is blocking readers, which WAL exists to
avoid. **(6) Instants and identifiers store as text and blob on *both* providers**, not native
`timestamptz`/`uuid` on PostgreSQL. **(7) The migration history table is
`platform_migrations_{module}`** in lower snake case, resolving Unresolved #6. **(8) Health-check
timeouts**: `Database` 5 s, `PendingMigrations` 10 s, resolving the rest of Unresolved #4. **(9)
`IProviderCapability` is registered as a singleton** so product code writing its own tables formats
instants and encodes identifiers exactly as Platform's columns do. **(10)
`PersistenceStartupException`**, because Persistence must abort startup on a non-WAL file and may not
reference Hosting's `PlatformStartupException` across an edge the graph forbids.
Rejected: **(1) Retracting the third-party claim** instead of fixing the seam — no signature churn,
and it shrinks the capability's justification to "we did not want two copies of the dispatch policy",
which is true but is a smaller claim than the log makes; declined because an extension point that
type-checks and then fails at the first cast is not one, and this is the cheapest moment to fix it —
S4 to S7 add five more store methods on top of this shape. **(1) A new `IProviderTransaction`
interface** — clearer about ownership, and identical in shape to `IAmbientTransaction`, which is the
duplication this log has objected to before. **(4) Committing per migration** to match the contract
as written — avoids a large rollback on a long run, and needs a SQLite lock that is not the applying
transaction, which is a design change to the lock mechanism rather than a code fix. **(5) Issuing raw
`BEGIN EXCLUSIVE`** — matches the documents literally and leaves migrations with no transaction
object to enlist against. **(6) Native PostgreSQL column types** — smaller rows, native indexing, and
real type-safety, and the contract offers no bind-side member that would let a store bind a
`timestamptz` or `uuid` parameter; taking it would mean a new public capability member. **The cost is
named rather than hidden: PostgreSQL gets no native indexing or type-safety on instant and identifier
columns, and larger rows.** Available later additively, which is why it was not forced now.
Reversibility: **(1) through (3) are expensive once a third party ships a provider** — they are the
seam's shape. (4) and (5) are cheap in code and expensive in expectation once an operator relies on
what a failed run leaves behind. (6) is **expensive the moment product tables have rows**, which is
the argument for stating its cost here rather than discovering it at D4.

### 2026-08-03 — SQLite connections disable pooling, found by an intermittent contract-test failure
Context: `No foreign key crosses a module boundary` failed intermittently — reading a just-committed
table's foreign keys back through a brand-new `SqliteConnection` occasionally returned zero rows,
never reproducible when that one test class ran alone. Disabling the test assembly's parallelism
first appeared to fix it (three straight green runs), and then it failed again on a fourth — the
wrong diagnosis, caught by not stopping at three. A tight 200-iteration single-threaded scratch loop
reproduced the mismatch on iteration zero, with a locked file the next iteration's cleanup could not
delete: Microsoft.Data.Sqlite pools connections per connection string by default, and a pooled
connection's schema snapshot can predate a DDL change a *different* pooled connection to the same
file just committed. 50/50 iterations passed clean the moment pooling was turned off.
Chosen: every connection string `SqliteProviderCapability` builds sets `Pooling=false`, including the
migration lock's and the ordinary transaction path's. The test project's own verification
connections and the test host builder's WAL pre-seeding step do the same, since they are reading
back what the store just wrote through a separate pooled (or non-pooled) handle.
Rejected: **Serialising the test assembly** (`CollectionBehavior(DisableTestParallelization = true)`)
— tried first, looked like it worked, and was wrong: it lowers the odds of two pooled connections
colliding without removing the cause, which is why it passed three times and then didn't. Left in
place it would have shipped a store with a real, if rare, stale-read hazard in production — not just
in tests. **Retrying the read on a transient empty result** — treats the symptom, and a product
query hitting the same staleness would have no such retry.
Reversibility: cheap. Connection pooling is a performance detail with no public surface; the fix is
one property on a connection string builder.

### 2026-08-03 — xUnit is the test framework, and S1 added nothing else
Context: [ADR-004](../docs/docs/adr/ADR-004-framework-build-not-adopt.md) §4 and `AGENTS.md` both require a decision-log entry naming the alternatives whenever a dependency is taken. S1 takes exactly one that is not in the shared framework.
Chosen: **xUnit**, with `Microsoft.NET.Test.Sdk` and the Visual Studio runner. It is the .NET SDK template default, it needs no attribute ceremony for a plain fact, and its licence is durable — Apache 2.0 under the .NET Foundation, which is the qualifier ADR-004 §4 added after three foundational libraries in this corner of .NET changed licence inside one year.
Rejected: **NUnit** — equally durable, and its per-class fixture lifetime differs from the constructor-per-test model these tests are written against. **MSTest** — first-party and fine, and the ecosystem around it is thinner for the assertion styles used here. **Adding an assertion library** such as FluentAssertions or Shouldly — better failure messages, and FluentAssertions' own move to a paid licence at v8 is precisely the durability risk ADR-004 §4 exists to price; xUnit's built-in assertions cover every case here. **Adding a mocking library** — nothing here needs one: every double in the suite is a small hand-written class, which is also what keeps the doubles readable as specifications of the contract.
Reversibility: cheap. It is a test-only dependency, referenced by no shipped package, and Testing refuses to be a development dependency of anything shipped.

### 2026-08-03 — The values S1 set, and the two acceptance criteria it found wrong
Context: S1 is the first slice, so it sets every value the contract listed as unresolved for it, plus the conventions a first implementation fixes by accident if nobody names them. Two of its own acceptance criteria in [`30-slices.md`](30-slices.md) turned out to be wrong when implemented against the contract; they are recorded here rather than edited, because `/slice` does not touch the design documents and `/reconcile` owns that.
Chosen: **(1) Solution layout** — `src/`, `tests/`, `samples/`, one project per package, `net10.0`, warnings as errors, and XML documentation generated from the start so S9's reference gate is a packaging change rather than a retrofit across every file. **(2) Configuration lives under a `Platform` section**, so every key an error names is the full path an operator can paste. **(3) Options are bound by hand, not by the reflection binder** — the contract requires a missing setting to name *the configuration source expected to supply it*, which a generic binder cannot say, and hand-binding is also what lets every constraint in the settings inventory fail with its own name. **(4) Upper bounds**: `DispatchTickBudget` at 1 000 and `PruneBatchSize` at 5 000, each an order above its default — the prune bound is the one that matters, because a prune delete is a single statement holding SQLite's write lock, where a tick's budget is only serial duration. **(5) Probe timeouts**: 15 s for the endpoint, which must exceed the 5 s busy-wait bound the design sizes as "shorter than a probe timeout"; the per-check default has no consumer until Platform ships a check in S2. **(6) Wire formats**: probe body and envelope are plain `application/json`, camel-cased, enums as strings; the envelope is `{code, correlation}` and nothing else. **(7) Probe paths** `/health/live` and `/health/ready`. **(8) The unhandled-request code is `UnhandledRequestFailure`** — one code, because the envelope's job is to be greppable against the log line, not to classify. **(9) Probes are served by middleware the standard call installs**, with `MapPlatformProbes` setting a flag that stands the middleware down — the brief's done-criterion is that no second call is mandatory. **(10) Modules are composed during the registration call**, so they must be registered before it and must have a public parameterless constructor: `Register` contributes services, and nothing can be added once the container is built. **(11) The test host suppresses Hosting's timers**, which is what leaves `RunBackgroundWorkOnceAsync` the only caller of a tick.
Rejected: **`application/problem+json` for the envelope** — the standard shape, and its members would add fields past the two the contract fixed, with `detail` an open invitation for exception text. **Binding with `ConfigurationBinder.Get<T>()`** — far less code, and it cannot name a source, cannot express the joint constraints, and does not construct a record with required members. **Auto-mapping the probes as endpoints via a startup filter** — more idiomatic than middleware, and the ordering against a consumer's own `Map` calls is not something a filter can settle. **Assembly-scanning for modules** — §2 allows scanning but forbids it as the only route, and making it the first route would have set the precedent. **Leaving the timers running in the test host** — no suppression code, and a single-tick assertion would then be a race.
Reversibility: cheap for all but (6) and (7), which are the published wire surface and become expensive once a third party's monitoring depends on them.
**Two acceptance criteria in `30-slices.md` are wrong and were implemented against the contract instead.** (a) S1 claims `Resolve` over `B(→A)`, `C`, `A` returns `A, C, B`. It returns **A, B, C**: "ties broken by name" makes the result the lexicographically smallest topological order, and after `A` is emitted the ready set is `{B, C}`, whose name order is `B` first. (b) S1 claims only `Detail` and `Data` differ between the two body detail levels. The contract says `Minimal` renders "the aggregate and each entry's name and status", which excludes duration as well. The contract wins in both cases; the criteria need correcting at `/reconcile`.

### 2026-08-03 — Three signatures S1 could not proceed without, and the values S1 sets
Context: Implementing S1 stopped before any code was written, on `AGENTS.md`'s rule that a public interface absent from the contract is a stop-and-ask. Three absences blocked it, and the first blocks the brief's own second CI assertion. Writing signatures found the first two omissions and reading them side by side found the third — the same pattern the contract's *Open questions* already recorded, where absences survive review because nothing in the document says anything wrong about them.
Chosen: **(1) `PlatformStartupException`, carrying a `PlatformError`.** Nine conditions in this contract "abort startup with a named error" and every one produces a `PlatformError` *value*, which is not throwable — so there was nothing for a host to abort with. The "exactly two places an exception is correct" note becomes three, and the third is qualified as a different kind: the other two are defects at a call site, this is a fatal condition at host build time, where `AddPlatformWebHost` returns the builder and the runtime's own contract is an exception. **(2) `PlatformTestHost.CreateBuilder`** — `IPlatformTestHostBuilder` was declared and nothing produced one. **(3) `IPlatformTestHostBuilder.WithServices(Action<IServiceCollection>)`** — five S1 criteria need a test-owned health check or background work inside a host that also exposes `Clock` and `RunBackgroundWorkOnceAsync`, and those two members exist only on `IPlatformTestHost`. It also settles a question the contract left open for the *real* host: **modules, health checks and background work reach a host by plain DI registration**, which is the route `WithServices` reaches, so tests exercise the production collection path rather than a parallel one. That satisfies §2's "explicit rather than only assembly-scanned" without a Platform-specific registration surface.
Rejected: **(1) Reusing `PlatformContractViolationException` for startup** — no new type, and it contradicts that type's stated scope, which is a defect at a call site; a missing setting is a runtime condition of the installation, not a caller's bug. **Returning a `Result` from `AddPlatformWebHost`** — consistent with every other boundary here, and the failure does not happen there: it surfaces at build or start, after the call has returned. **(3) A dedicated `WithModule`, `WithHealthCheck` and `WithBackgroundWork` triple** — more discoverable, and it is three surfaces where one suffices, each needing its own equivalent on the real host. **Assembly-scanning the test assembly** — no new member, and one test defining a module would silently change every other test in the assembly. **Abandoning `IPlatformTestHost` for a hand-built host in the affected tests** — no contract change at all, and it gives up the fake clock and the single-tick invocation, which are the two things that make those criteria checkable without a wall-clock wait.
Reversibility: cheap for all three — no code exists, and this is the moment the contract is cheapest to change. (1) becomes expensive once a consumer catches it by type; (3) becomes expensive once a third party's test suite builds on it.
**Four values S1 sets that the contract listed as unresolved**, each recorded here because a future reader would ask why: the two batch upper bounds, the envelope and probe-body wire formats, and the two probe timeouts. Also settled with them: each `PlatformError` subtype is a sealed record with one static factory per named variant and the `Code` string being the variant name — the contract names the types and the variants but never their C# shape, and it is public surface a third party compiles against. And `PlatformOptions.Environment` and `Role` take an `internal init` accessor, because a get-only auto-property cannot be assigned outside a constructor and the record is constructed by object initializer; the public surface is unchanged, both still read as `{ get; }` externally and neither becomes bindable.

### 2026-08-03 — Two package-boundary contradictions slicing found, and which document was wrong in each
Context: Breaking the contract into slices meant naming, per slice, which package each type lands in — and two types could not be placed without contradicting something already written. Neither is a contradiction a red team would find by reading either document alone: each needs the package table and a signature held side by side, which is what assigning types to slices forces. **(1)** `PersistenceProvider` sat under a *Persistence — provider selection* heading while `PlatformOptions` — a Core record — carried a `required PersistenceOptions` whose `Provider` is that enum, so Core referenced a Persistence type across an edge the dependency graph forbids. **(2)** The design gave Abstractions *"Nothing but the BCL"* while `IPlatformModule.Register` takes an `IServiceCollection`, which is not in the BCL.
Chosen: **(1) The contract was wrong; the enum moves to Core**, beside `PersistenceOptions`. It names which provider a host is *configured for*, which makes it a setting rather than part of the provider abstraction, and Persistence depends on Core so `IProviderCapability.Provider` and `WithProvider` reach it freely. **(2) The design was wrong; the dependency line widens** to the BCL plus the dependency-injection abstractions, with the exception named in the section rather than left to be rediscovered. [`minimal-platform-packages.md`](../docs/docs/minimal-platform-packages.md) §2 — which owns the done-criteria — states the criterion as *no dependency on any other Platform package*, and a consumer compiling against Abstractions alone; both still hold, so the design's phrasing was stricter than the criterion it existed to meet. §2's reason survives intact too: the DI *abstractions* are container-agnostic and supply no container, so a product still inherits no runtime choice.
Rejected: **(1) Keeping the enum in Persistence and loosening `PersistenceOptions.Provider` to a string** Persistence parses — it moves a validated value into unvalidated text and pushes the failure from startup to first use. **Moving `PersistenceOptions` out of `PlatformOptions` entirely** — it breaks invariant 33, which makes Core responsible for the connection string being present or the host not starting. **(2) Dropping the parameter from `IPlatformModule.Register`** — it makes the contract literally true and leaves a module contract that cannot register anything, which is the only thing a module does. **Moving the module contract to Core** — also literally true, and it costs a product the ability to declare a module against Abstractions alone, which is the property that makes Abstractions a separate package. **Leaving both as found and noting them in the slices** — the cheapest, and it hands the first implementing agent a graph violation and a false dependency line to resolve by improvisation, which is the tier the contract exists to constrain.
Reversibility: cheap for both, and both are cheap *now* for the same reason the identity reversal was — no code exists. (1) becomes expensive the moment a provider implementation compiles against the enum's namespace; (2) becomes expensive if a consumer builds on Abstractions in an environment where the DI abstractions are unavailable, which nothing in the brief's deployment modes describes.
**No re-derivation follows.** (1) is a correction inside the contract that changes no signature, and (2) changes the design to match a contract signature that was already right — the direction that would force a re-derivation, the design changing under the contract's feet, did not happen.

### 2026-08-03 — Nine more things the re-derived contract added, and the six contradictions it closed
Context: [`20-contract.md`](20-contract.md) was re-derived in full against the post-fifth-review design and diffed against the previous derivation, which the entry below had flagged as contradicting it in six places. Those six are corrections the design forces and are not logged as choices: the correlation column joins the outbox row and `TraceContext` loses its `Correlation` member; redrive clears the dispatch state whole and sets the next attempt to now, applying only in the poisoned state; the migration lock moves onto the capability as `AcquireMigrationLockAsync` and off the lease; `AddPlatformPayloadConverter` is deleted with the extension point; `IBackgroundWork.RunAsync` becomes `TickAsync` with Hosting owning the timers; and the operation scope grows its fourth member. Three further corrections came with them — `OldestPendingOccurredAtAsync` becomes `OldestPendingDueAsync` because backlog age measures past due, `PeerAbsenceStartupGrace` becomes `PeerAbsenceGrace` because the design rejects a startup-scoped reading by name, and a pending-count check, threshold and store query appear because the fifth review put the pending backlog on the readiness surface. **Nine differences are none of those**: choices a signature cannot be written without, which `/contract` requires be logged rather than retrofitted from code later.
Chosen: **(1) `IOperationScopeFactory` gets two `Begin` overloads** — one taking tenant and principal alone, which originates a root trace and takes the correlation from it, and one taking trace context and correlation explicitly. The design names both establishment cases and one primitive; two overloads is what keeps origination from being expressible as fabrication. **(2) `ITraceHandle`, and `StartRoot` beside `StartLinked`** — the scope's origination path needs a root started through the same contract, and both callers need the established context back to populate the scope's fourth member. **(3) `IAmbientTransaction` and `IAmbientTransactionAccessor`** — the design makes the ambient transaction one connection every participant enlists in, and a product's own context cannot enlist against a connection nothing exposes. **(4) Every dispatch-state store write takes the holder and returns `bool`** — the design requires the write to apply only while the writer holds the live claim and to be a silent no-op otherwise; the boolean is the "your claim was lost" signal, and without it the caller cannot distinguish a no-op from a write. **(5) `OutboxMessage.DueAt`** as a derived member beside `State`, because the design states the due predicate three code paths evaluate and only `State` had been given a member. **(6) `IMigrationLock : IAsyncDisposable`** — a connection-scoped lock needs a release the type system enforces, which is the property the design chose it for. **(7) `EventHandlerRegistrationError.DuplicateNameForEventType` and `.HandlerNotConstructible`** — the first because enqueue resolves by CLR type and two names for one type leaves it nothing to stamp, the second because the design makes an unconstructible handler a named worker startup failure and no failure at all in the web role, which no other error type carries. **(8) `HostStartupError.ProbeBindFailed`** — the design requires a port collision to fail startup naming the setting, and Hosting's error type had no variant for it. **(9) The two readiness counts return `long`** — the pending count's threshold is 100 000 against a row set the design declares unbounded.
Rejected: **A single `Begin` taking a nullable trace context** — fewer members, and null-means-originate is exactly the implicit minting the design rejects, spelled as a parameter. **Persistence handling W3C strings itself** rather than growing the codec — no new members, and it puts trace handling in two packages that can drift apart on the one value promised greppable end to end. **Leaving enlistment to the sample** — atomicity that holds only when a sample arranges two writes on one context is the bespoke wiring the brief's definition of done names as failure. **`void` dispatch-state writes** — matches the previous derivation and makes a lost claim indistinguishable from success, so the duplicate-delivery evidence the design asks to be counted could not be counted. **Folding the unconstructible-handler failure into `HostStartupError`** — it is a registration verdict that differs by role, and Hosting does not know what a handler is. **`int` counts** — smaller, and it overflows precisely on the unbounded set the count exists to watch.
**Refined 2026-08-03**, on sign-off of (4): the boolean is replaced by a named `ClaimedWriteOutcome` of `Applied` and `ClaimLost` on all five dispatch-state writes. The objection is one this log already made when it rejected folding the attempt-consuming property into a flag on one handler-error enum — a boolean puts a correctness property on a value a caller can get wrong instead of on the type the dispatcher switches over — and the re-derivation reintroduced it five times over. The misreading it invites is specific: `false` reads as *the row wasn't there* rather than *I no longer hold the claim*, which turns a lost claim into an apparent success and stops the duplicate-delivery evidence the design asks to be counted. It also restores consistency with `OutboxAdministrationOutcome`, which already names this class of result — a well-formed operation that did not apply. Two variants are exhaustive because a claimed row is always pending and pending rows are never pruned, so the row cannot vanish underneath its writer. **Rejected: keeping the boolean**, on the grounds that two variants is a boolean with ceremony and only the dispatcher consumes the distinction — declined because the dispatcher is precisely the caller whose misreading manufactures a discarded row nobody discarded.
**Refined 2026-08-03**, on sign-off of (3): the exposure stands — `minimal-platform-packages.md` §2 has Persistence refuse to impose a repository pattern, so a product using Dapper or raw ADO for its own tables needs both the connection and the transaction to join the ambient one, and encapsulating enlistment inside Persistence would quietly restrict transactional product writes to EF Core, which is a larger decision and not the design's. What the exposure costs is a live handle a participant could commit, roll back or dispose, so the lifetime rule is now stated rather than assumed: a participant enlists and does nothing else, and commit and rollback happen exactly once in `ExecuteAsync`. It is an invariant and a contract-test assertion. **Rejected: keeping the accessor internal and enlisting each module's context from Persistence**, which is the more encapsulated shape and would work — Persistence already knows every module's context, since it owns the per-module migration histories — and was declined for the data-access rule above. Reversible in the additive direction only, which is the argument for exposing now: exposing later breaks nobody, withdrawing later breaks every product not on EF Core.
Reversibility: cheap for (5), (8) and (9). **Expensive for (1) through (4), (6) and (7)** once a third party compiles against them — (3) and (4) most of all, because they are the shapes a product's own data-access code and a provider implementation touch directly. The moment to disagree with (4) in particular is before the first slice: it puts a boolean on five store methods, and reading it as "did the row exist" rather than "did I still hold the claim" would silently restore the race that manufactures a discarded row nobody discarded.

### 2026-08-03 — Four forks from a fifth red team, settled before the contract is re-derived
Context: A fifth adversarial review of [`10-design.md`](10-design.md) produced eighteen findings, five blocking. Fourteen had one sound disposition each and are recorded in the sections they touch; four were real forks, asked and settled the same day. One supersedes part of an entry below.
Chosen: **(1) Migrate mode's exclusion is a provider-native lock** — an advisory lock on PostgreSQL, an exclusive transaction on SQLite, a new capability member. Connection-scoped closes both holes the lease had: no table, so it exists on a fresh store; released on process death, so no expiry window unfences a stalled migrator. **(2) The pending backlog is reported, never refused** — a pending-count threshold joins readiness, and no write fails over backlog. **(3) The converter extension point is cut** — the durable format ships with zero reachable extension. **(4) A persistence-less host is supported with its guarantees scoped** — Hosting still does not reference Persistence, and the probe body enumerates registered condition sources so an absent check is visible rather than indistinguishable from a passing one.
Rejected: **(1)** Bootstrapping the lease table outside the migration system — keeps one mechanism but keeps the expiry hole, and DDL cannot be fenced. Declaring concurrent invocation operator error — leaves the design's own ordinary double-invocation scenarios unguarded. **(2)** Backpressure at enqueue — fails the domain write with it, turning a worker outage into a web outage. A pending retention window — pruning an undispatched row is silent message loss. **(3)** Folding a named converter registry into the settings fingerprint — detects the drift instead of removing it, at the cost of a new registration channel for an escape hatch no consumer has asked for. Keeping it as a documented hazard — the silent-condition class this design routes through readiness everywhere else. **(4)** Making Persistence required by the standard call — puts a dependency in fact where the graph records none.
Reversibility: cheap for (1) and (2). **(3) and (4) are cheap in exactly one direction** — adding converters later or hardening guarantees later is additive; the reverse of either breaks consumers, which is why both took the restrictive reading now.

### 2026-08-03 — What else the fifth review changed, where a wrong default would have shipped
Context: The fourteen non-fork findings each had one sound disposition, and the reasoning lives in the design sections they touch. Five are additionally logged here because each replaced something the design previously said, or something an implementer would plausibly have built otherwise.
Chosen: **(1) Correlation is a persisted column on the outbox row**, stamped from the ambient correlation at enqueue, and dispatch rebuilds from the column — deriving it from the stored traceparent, the previous text, is right for exactly one hop, and a handler enqueuing a follow-up is the ordinary case. **(2) The ambient transaction is one connection every participant enlists in**, owned by the unit of work — per-module contexts otherwise make "ambient" two connections and no atomicity. **(3) Eligibility and backlog age are past-due predicates** — due is next-attempt-at, or occurred-at while it is null; redrive sets next-attempt-at to now and clears the claim columns. Age-since-occurred manufactures "worker down" out of deferral, backoff and bulk redrive, three routine states. **(4) The mint-order guarantee is millisecond-granular with the tie unspecified**, and the contract tests advance the fake clock between mints — a frozen fake clock makes the previous assertion false without the encoding being wrong. **(5) The registration triple is declarative** — the web host records the handler type without constructing it, and constructor graphs validate only in the dispatching role.
Rejected: **(1)** A second free-standing identifier — this is the same single value persisted, not a second propagation path; the design's one-value rule stands. **(2)** Leaving co-location to the sample — atomicity that holds only when a sample happens to arrange two writes on one context is the bespoke wiring the brief's definition of done names as failure. **(3)** A fifth row state for deferred rows — a second source of truth beside the predicate table. **(4)** A Platform-owned monotonic version-7 generator — repairs a property nobody is promised, with hand-written code where the runtime ships the generator, against [ADR-004](../docs/docs/adr/ADR-004-framework-build-not-adopt.md) §4's default. **(5)** Splitting registration into a name-binding call and a role-scoped handler call — reintroduces the two verdicts that can disagree, which binding-in-one-call exists to prevent.
Reversibility: **(1) is expensive the moment rows exist, which is why it happened now.** The rest are cheap — predicates, validation placement and test discipline, none of them persisted.

### 2026-08-03 — The provider abstraction cuts at one store per Platform table, over a capability contract
Context: The third of the three gaps. [`10-design.md`](10-design.md) committed to a provider abstraction that is "real, not notional" and verified by contract tests, and named none of its members — so its shape, the largest remaining piece of D3, would have been set by whoever wrote the first line of it. Everything provider-specific the design names passes through it.
Chosen: **one store interface per Platform-owned table — outbox, lease, host registration — with a single implementation of each, parameterised by an `IProviderCapability` contract.** Stores rather than a general data-access abstraction because `minimal-platform-packages.md` §2 has Persistence *refuse* to impose a repository pattern; these cover the three tables Platform both defines and stores, never product data. One implementation rather than two because the policy — which row to claim, whether a failure consumes an attempt, when a row is poisoned rather than deferred — is where the correctness lives, and the design's own objection to a dialect-specific claim applies with more force to the surrounding logic than to the statement. **The capability contract gets a membership rule so its growth is checkable: a member belongs there when the two providers must do something *different* to produce the same observable result.** That admits the instant formatter, the identifier encoder, the claim and bounded-delete statements, transaction-begin mode, the migration history name, and the startup preconditions — and nothing else. **One consequence is surfaced rather than left to be discovered: transaction intent becomes a parameter on the unit of work**, because "a transaction that will write begins immediate" is unactionable unless the caller says which kind it is opening.
Rejected: **A full abstraction implemented per provider**, with EF Core an implementation detail of each. The strongest interchangeability claim, and the best answer if a non-EF provider were ever likely. Rejected because it roughly doubles the Persistence code, duplicates what EF Core already gives, and puts two copies of the dispatch policy behind one interface — the failure this design already refused once. **Thin EF Core hooks only** — value converters, the claim and prune SQL, journal assertion, and nothing else. The least Platform code and the most literal reading of [ADR-004](../docs/docs/adr/ADR-004-framework-build-not-adopt.md) §4. Rejected because the abstraction becomes a bag of hooks with no seam, and "the two providers are interchangeable" stops being a property the contract tests can state — which is the one thing §2 requires those tests to establish. **Treating every transaction as a writer**, avoiding the intent parameter. Safe, and it makes the immediate-transaction rule unfalsifiable while hiding the deferred-then-upgrade case the rule exists for.
Reversibility: **expensive.** The stores and the capability are Persistence's whole internal shape, and the contract tests are written against them; a third party implementing a provider of their own compiles against the capability. This is the decision here most worth disagreeing with before the first slice.

### 2026-08-03 — Payloads serialize through a pinned System.Text.Json, with converters as the only extension point
Context: The second of the three gaps. [`10-design.md`](10-design.md) said "json" and stopped, so the contract typed `Payload` as `string` and could say nothing about what produced it. [ADR-004](../docs/docs/adr/ADR-004-framework-build-not-adopt.md) §4's check: the runtime already ships `System.Text.Json` and it covers the gap whole, so a serialisation dependency would fill nothing — recorded here as the clause requires, for a dependency passed over rather than taken.
Chosen: `System.Text.Json` with a Platform-pinned options instance that is **not injectable**, and converters for a product's own types as the one extension point. Four properties are pinned because they *are* the durable format: unmapped members ignored **in both directions**, enums as strings, Platform-owned property naming and null handling, fixed number handling. The both-directions rule is forced by the two-process overlap — an old worker reads rows a new web host wrote as well as the reverse — and the string-enum rule by the additive-only payload rule, which a numeric enum breaks the moment a member is inserted.
Rejected: **An injectable `IPayloadSerializer` with the serialiser's identity added to the settings fingerprint.** The more flexible option, and it would convert a silent format divergence into a degraded readiness signal, which is this design's house style for exactly this class of problem. Rejected because the serialiser is part of the durable format rather than a preference, a dependency-injection registration is not a setting, and pinning removes the condition instead of reporting it — a product that swapped serialisers after rows existed would mass-poison its own backlog. **No extension point at all** — the strictest reading, and contract tests could then assert exact bytes. Rejected because products with their own value types would have to shape payloads around primitives, and a converter's blast radius is confined to types the registering product also owns the handler for, which the additive rule already governs. **Newtonsoft.Json** — no gap to fill, and a dependency where the BCL suffices.
Reversibility: **expensive.** The format is on disk in every outbox row the moment the first one is written, and loosening it later means reading two formats.
**Partly superseded 2026-08-03** by the fifth-review forks entry above: the converter extension point is cut. A converter is a dependency-injection registration the settings fingerprint cannot see, so converter drift between the two hosts of a half-upgraded installation is exactly the silent format divergence pinning exists to remove — the objection this entry's own chosen paragraph raised against the injectable serialiser, applied one layer down. The pinned options instance and the four pinned properties stand.

### 2026-08-03 — An event's stable Type name is bound by explicit registration, not by an attribute or a member
Context: The first of three gaps that deriving the contract exposed and four adversarial reviews had missed — each was an absence rather than a contradiction, and red-teaming tests what a document claims. [`10-design.md`](10-design.md) required the persisted `Type` to survive a class rename and forbade it being a runtime type name, without saying what supplies it.
Chosen: one explicit registration call binds three things at once — the stable name, the CLR event type, and its handler. The deciding constraint is one the design implied without stating: **dispatch must get from a stored string to a CLR type in order to deserialize, and it has no instance to ask**, because the instance is what deserialization produces. Every candidate therefore builds a name-to-type map at registration; only the literal's location differs. Binding all three in one call makes name uniqueness and handler uniqueness one verdict rather than two that can disagree, at the place one-handler-per-Type is already enforced. **Enqueuing an unregistered event type is a named throw**, joining the missing-transaction and missing-scope throws. `IIntegrationEvent` consequently loses its `TypeName` member and becomes a marker.
Rejected: **A static abstract member on the event type.** The most rigorous — an event with no name would not compile, moving the failure from startup to build time. Rejected as the more constraining option on what an event may be, and because a product could not then bind a name to a type it does not own; the compile-time backstop was judged not worth those two costs at 0.x. **An attribute on the event class.** Best readability at the class, and it is the shape most .NET developers expect. Rejected because it needs reflection at startup and a missing attribute fails at runtime rather than at compile time — the same failure class as a forgotten registration, without the registry's named error. It stays available as later sugar: an attribute supplying the name *to* a registration call is additive, which is why this is the direction to take first. **An instance member on the event**, which the previous contract carried as the design's implied shape — it cannot answer the question dispatch actually asks.
Reversibility: **cheap for the mechanism, expensive for the names.** Adding an attribute later is additive; a name already written into rows is permanent, which is the property the whole decision exists to protect.

### 2026-08-03 — Nine things the re-derived contract added that the design did not imply
Context: The contract was re-derived in full from the post-fourth-review design and diffed against the previous derivation, per decision (4) below. Most differences are corrections the design forces — the version-7 id, the four-state predicate, `Roles` on background work, the operation-scope and trace-context contracts, the settings inventory's numbers, host registrations being pruned rather than kept forever. Nine differences are not that: they are choices the contract had to make because a signature cannot be written without them, and `/contract` requires each to be logged rather than retrofitted from the code later.
Chosen: **(1) `DispatchError` split from `HandlerError`** — the design distinguishes three no-attempt paths (unresolvable handler, undeserializable payload, pending migrations) from the two that do consume an attempt, and one enum could not carry "never increments attempts" as a property of the type. **(2) Per-id outcomes rather than a count on redrive and discard** — the design requires a clear "already pruned" and requires that a discarded row is never silently resurrected, neither of which an `int` can express. `OutboxError` is left with `Unavailable` alone, because a per-row disposition is a result and not a failure of the operation. **(3) `IOperationScopeAccessor`, and the three ambient accessors throwing outside a scope** — the design mandates that enqueue throws with no ambient scope, and nothing else in the contract could detect the absence. **(4) `PlatformContractViolationException` carrying a `PlatformError`** — the design says "throws a named error"; carrying the error keeps the code stable and enumerable rather than a message string. **(5) `HostStartupError`** — `/contract` requires an enumerated error type per module and the design names none for Hosting; it wraps the cause rather than adding conditions. **(6) Well-known `BackgroundWorkName` and `HealthCheckName` constants** — both appear in the probe body an operator reads and one is the handle `RunBackgroundWorkOnceAsync` takes, so leaving them unnamed sets public surface by accident. **(7) A `[Fingerprinted]` attribute** — the design states the membership *rule*; the attribute is what makes membership checkable, and invariant 23 is unassertable without it. **(8) `IEventHandlerRegistry` placed in Persistence**, beside the handler resolution the design assigns there, rather than in Core with the other registries. **(9) Four validation variants the design implies without naming** — `TransactionError.Busy`, `ConfigurationError.InconsistentSettings` and `UnsupportedJournalMode`, and `BackgroundWorkRegistrationError.NoRoleDeclared`, the last because empty `Roles` means work no host ever runs, which is the silent-never-running failure the field was added to prevent.
Rejected: **One handler-error enum with a "consumes an attempt" flag** — fewer types, and it puts a correctness property on a boolean a caller can get wrong instead of on the type the dispatcher switches over. **Counting successes and reporting the remainder as an error** — cheaper, and it loses which id was pruned and which was already discarded, which is the distinction the design asked for by name. **Nullable ambient accessors** — no new interface, and it pushes a null check onto every reader of a value the design says is always present. **Naming the checks and loops with plain strings at each registration site** — no new type, and two packages would spell one name two ways with nothing to catch it. **An enumerated fingerprint list in prose beside the hash** — what the previous derivation effectively had; rejected because the list and the code drift and only one of them is executable. **`IEventHandlerRegistry` in Core** — symmetrical with the other two registries, and it separates the registry from its only reader for the sake of the symmetry. **Leaving all nine to the implementing agent** — the cheapest option, and it puts nine public-surface decisions in the hands of the tier the contract exists to constrain.
Reversibility: cheap for (1), (5), (8) and (9). **Expensive for (2), (3), (4), (6) and (7)** once a third party compiles against them, which the brief's audience makes real; (6) additionally reaches an operator's probe body, which is read by people rather than by compilers.

### 2026-08-03 — Four things settled before the contract is re-derived, one of them superseding an entry below
Context: The fourth red team changed [`10-design.md`](10-design.md) in ways [`20-contract.md`](20-contract.md) contradicts, so the contract must be re-derived. Four forks would otherwise be decided silently by whoever runs `/contract`, and one of them is a claim in the *Four things the contract added* entry below that turns out to be unimplementable.
Chosen: **(1) The whole exposure posture survives into the contract** — loopback default, environment-dependent body detail, last error store-only, and an envelope carrying a stable code rather than exception text. The body narrowing is the part that matters most and would be the first to be cut: the web host's readiness endpoint shares the product's listener and is genuinely reachable, so a loopback default alone does not cover it. **(2) The check constraint "processed and poisoned never both set" is dropped and replaced by one that can hold: poisoned-at set requires last error present.** The original cannot exist — with four row states over two nullable columns, every combination is legal, and the constraint would have rejected the discard operation the design requires. The claim-column pairing constraint was always sound and stands. **(3) One handler per Type is enforced at startup only**, by the handler registry rejecting a second registration with a named error, alongside the duplicate-health-check-name failure that already works this way. **(4) The contract is re-derived in full from the current design and then diffed against the existing one**, with every difference accounted for — anything the new derivation drops is either a correction or a loss, and only the diff distinguishes them.
Rejected: **Cutting the exposure section and deferring to D5** — the probe body and envelope shapes would then be set by whoever writes the first slice and become public API with nobody having decided them. **Keeping only the loopback default and the store-only rule** — cheaper to test, and it leaves the reachable endpoint carrying migration names, peer identities and poison counts. **A status discriminator column** replacing predicate-over-timestamps — trivially constrained, and it is the second source of truth the predicate table deliberately avoided. **Dropping the poison constraints entirely** — defensible, since the dispatcher is the single writer, but it gives up the one invariant that is both real and expressible. **Enforcing one-handler-per-Type at dispatch as well as at startup** — the more rigorous reading, since a container can be populated directly and bypass the registry; declined for one error path rather than two, and revisitable if the bypass ever happens. **Patching only the four contradictions** — fastest, and it leaves a contract whose relationship to the design cannot be verified by re-deriving it.
Reversibility: cheap for (2), (3) and (4). **Expensive for (1)** — the envelope and probe body are in the first published surface, and the brief's third-party audience makes that real.

### 2026-08-03 — Four things the contract added that the design did not imply
Context: `/contract` requires that anything the contract introduces beyond the design gets logged, so the design stays the place intent is decided rather than being retrofitted from signatures. Four additions qualify; everything else in [`20-contract.md`](20-contract.md) is a transcription of [`10-design.md`](10-design.md), and eleven things the design left undetermined are listed there as unresolved rather than invented.
Chosen: **(1) Results, not exceptions, across every module boundary** — an abstract `PlatformError` with a stable code and a retryable flag, and a `Result` generic over it. **(2) Strongly-typed identifiers** — `TenantId`, `CorrelationId`, `TraceContext`, `InstanceId` and four name types as record structs carrying their own invariants. **(3) A `Permanent` handler-failure variant**, which poisons a row immediately rather than consuming the remaining attempts. **(4) Database check constraints** encoding two invariants the dispatcher would otherwise be trusted to maintain alone: processed and poisoned never both set, and the paired nullability of the claim columns.
Rejected: **Exceptions across boundaries** — conventional in .NET, and rejected because a caller cannot tell a retryable condition from a defect without catching by type, which is the thing the design's retry semantics need to be explicit about. **Raw `Guid` and `string` identifiers** — less ceremony, rejected because `TenantId.Implicit` and the trace-flags-travel-with-the-row requirement are invariants that then live in prose instead of in a type. **Only `Transient` and `Unresolvable` handler failures**, which is all the design names — rejected because a handler that knows its input is malformed would otherwise burn the full attempt budget to reach a conclusion it already had. **Enforcing the two constraints in code only** — rejected because both are cheap in the schema and the dispatcher is the single writer whose bug they guard against.
Reversibility: cheap for (3) and (4); **expensive for (1) and (2)** once a third party compiles against them, which the brief's audience makes real. Both are in the first published surface, so the moment to disagree is before the first slice, not after.
**Partly superseded 2026-08-03** by the entry above: half of (4) cannot be implemented. The mutual-exclusion constraint would reject the discard operation, which sets both marks by design; it is replaced by *poisoned-at set requires last error present*. The claim-column pairing half of (4), and (1) through (3), stand.

### 2026-08-03 — The outbox is built against an in-process bus, not adopted
Context: [ADR-004](../docs/docs/adr/ADR-004-framework-build-not-adopt.md) §4 inverts the usual default — reach for a package first — and requires that when one exists and is passed over, the log records why hand-rolling won. An empty log next to hand-written infrastructure is the signal the clause was skipped. This is that entry. The evaluation itself was already performed in [`minimal-platform-packages.md`](../docs/docs/minimal-platform-packages.md) §3a and is not redone here.
Chosen: implement the outbox behind Platform's own interface, against an in-process bus. Persistence's done-criterion — surviving a process kill between the domain write and the publish — stands as written, and [`00-brief.md`](00-brief.md) put the outbox in scope over §3a's advice to defer it.
Rejected: **MassTransit** — disqualified on licence durability, v9 commercial and v8's maintenance ending after 2026, against a codebase whose stated lifespan makes that disqualifying. **DotNetCore.CAP** — a real outbox under MIT, but every supported transport is a broker; adopting it puts a message broker into local developer execution, which is an in-scope deployment mode, to serve a transport decision that has not been taken. **Wolverine** — plausible, but the outbox claim was not confirmed from its documentation and nothing should be relied on unverified. **Deferring entirely**, which is what §3a recommended — overridden by the brief.
Reversibility: moderate. The mechanism sits behind an interface this repository owns, which is precisely why ADR-004 §4 asks for the interface to be ours — a library can replace it once a transport is chosen.

### 2026-08-02 — The staging tree became `SubZeroDev.Architecture`, private
Context: The ecosystem specification set — 96 files, fourteen destinations' worth — sat at `D:\Dropbox\Projects\SubZeroDev\Specs` with no version control. Nothing recorded a change and nothing caught a stale one, which is exactly how its directory table came to list `SubZeroDev.Platform` as "Platform repository (exists)" while pointing at a repository holding a different Platform entirely. Found by reading a directory listing, not by any check.
Chosen: `git init`, one commit, and a **private** GitHub repository named `SubZeroDev.Architecture`. Named that because the documents already call it "the Architecture repository" and other repositories cite its ADRs by number, so the name has to resolve. Its README now separates the two kinds of content it holds: `SubZeroDev.Ecosystem/` is at home there, everything else is staging until its destination repository exists.
Rejected: **Public, matching all nine sibling repositories** — it would immediately publish `REVIEW.md` (an internal critique naming blocking defects), the commercial model (billing provider, licence tiers, metered dimensions), the plugin signing and trust model, and thirteen open questions including plugin naming, which the root-naming ADR says is expensive to settle once identifiers are public. Flipping to public later is available; un-publishing is not. **`SubZeroDev.Specs`** — accurate about today's contents but diverges from the name the documents themselves use. **Leaving it on disk** — the drift it has already produced is the argument against.
Reversibility: cheap for visibility; the repository name is expensive once anything cites it by URL.

### 2026-08-02 — Ran `Invoke-SetupDocs`, and `build/` stopped being ignored
Context: This repository had no CI, no documentation gate, and no site root — nothing served `/` while the navbar brand linked there from every page, 16 broken links. Two earlier diagnoses blamed the base image's `src/pages` strip and then installer registration; both were wrong. The strip is correct, and the build was being invoked the wrong way (`docker build` the overlay in, then build inside the derived image, so the file is already there when the leak check runs). The installer was the actual missing piece.
Chosen: Run the installer without `-Overwrite`. It created the homepage generator, the documentation gate, `.config/DocumentationRules.psd1`, both workflows, the generated site root and a docs index, and skipped the five files this repository already owned (`docusaurus.config.ts`, `sidebar.ts`, `Dockerfile`, `.dockerignore`, `docs.ps1`). Fixed the generated title, which the installer took from the container mount point and set to `work`. Authored `docs/docs/index.md` properly rather than leaving the `# work` stub. Removed the README's document table — it now duplicated that index, and every repository-relative link in it broke on the generated homepage. With the root served, `onBrokenLinks` and `onBrokenMarkdownLinks` are now `'throw'`.
Also: **`.gitignore` had a bare `build/`**, which made both installed scripts invisible to git while `docs-ci.yml` runs one of them — green locally, broken in CI. `build/` is a scripts directory here; the ignore now names `dist/`, `artifacts/`, `bin/`, `obj/` instead.
Rejected: **`-Overwrite`** — it would have replaced the five preserved files, including a `docusaurus.config.ts` carrying this repository's own settings. **Converting the README to absolute site URLs with `SiteUrl`** — the documented way to make one README work in both places, but it points readers at a site that is not deployed yet; keeping the index in `docs/docs/index.md` solves the same problem without that claim. **Migrating `routeBasePath` to `'/'`** — considered while the cause was still misdiagnosed; it moves every page URL to fix something the installer fixes for free.
Reversibility: cheap for the config and the ignore; the workflows and generated files are regenerable by re-running the installer.

### 2026-08-02 — Design docs live at `design/`, not `docs/design/`
Context: Installing the agent kit. The kit ships its five design documents at `docs/design/`. In this repository `docs/` is the Docker build context for the documentation site — `docs.ps1` builds from it and `docs/Dockerfile` does `COPY . .` onto `/template`. A `docs/design/` directory would therefore be baked into the published image at `/template/design/`. It would not render as pages (the autogenerated sidebar's `dirName: '.'` resolves to the content root `/template/docs`), but internal design documents would ship inside a distributed artifact.
Chosen: Install at `design/` in the repository root, outside the build context. The path was rewritten in every file that names it — the seven stage commands under `.claude/commands/` and `AGENTS.md` — in one pass. The kit has since made `design/` its default for the same reason, so this repository is now on the standard layout rather than an exception.
Rejected: **Keep `docs/design/` and exclude it via `docs/.dockerignore`** — works, but the exclusion is invisible, nothing fails loudly if it is lost, and a docs-template upgrade that regenerates that file would drop it silently. **Keep `docs/design/` and accept publication** — no edits, but shipping internal design documents in a public image is not a default anyone would choose deliberately.
Reversibility: expensive — every cross-reference to `design/` breaks if the directory moves again.

### 2026-08-02 — Standing instructions moved from `CLAUDE.md` to `AGENTS.md`
Context: Installing the agent kit. This repository held its standing instructions in `CLAUDE.md` with no `AGENTS.md` — the inverse of the kit's arrangement, and of the SubZeroDev specification repositories. `CLAUDE.md` had uncommitted edits in flight on `design/platform-identity-and-engine-hosting` at the time; they were carried across verbatim.
Chosen: Move the content to `AGENTS.md` verbatim and reduce `CLAUDE.md` to a pointer. Matches the specification repositories, and `AGENTS.md` is the filename read by every tool rather than one vendor's.
Rejected: **Keep the content in `CLAUDE.md` and make `AGENTS.md` the pointer** — the smaller change, and what `SubZeroDev.GameEngine` does; rejected in favour of consistency with the specification repositories, which this one sits alongside. **Keep both files with content** — a copy that can disagree with its original is the exact failure this repository's own move-don't-copy rule exists to prevent.
Reversibility: cheap

### 2026-08-02 — Seven existing rules were not duplicated from the kit
Context: The kit's `AGENTS.md` carries conventions harvested from ten repositories. This repository already stated seven of them independently: one-at-a-time sign-off, recording declined suggestions as known-and-retained, staging by named path, never force-pushing, move-don't-copy, leaving the merge to the owner, and descriptive commit messages over Conventional Commits.
Chosen: Keep this repository's wording and do not add the kit's second copy. Where the local rule was more specific, it stands as written. The kit's placement test — "would a second consumer face this question?" — was already present here in sharper form as the package extraction guard, so the local wording was kept.
Rejected: **Add the kit's phrasing alongside** — two copies of a rule is a promise they will diverge. **Replace the local wording with the kit's** — the local rules were written against this repository and are more specific; the generic version loses information.
Reversibility: cheap

### 2026-08-02 — The source-of-truth chain is dual until a brief exists
Context: The kit asserts a five-document precedence chain under `design/`. This repository already has one: `docs/docs/platform-identity.md` is authoritative, followed by the sidebar reading order. Installing the kit's chain wholesale would assert authority over five files that do not exist while ignoring the one that does.
Chosen: State both in `AGENTS.md`. `platform-identity.md` is authoritative today; the `design/` chain governs design work once a brief is written, and a contract there is authoritative for its own package only. `platform-identity.md` remains authoritative for what this repository is.
Rejected: **Install the kit's chain as written** — asserts precedence for empty files over the document that currently decides everything. **Omit the kit's chain until a brief exists** — leaves the pipeline commands referring to an authority the contract never grants them.
Reversibility: cheap

### 2026-08-04 — Kit upgrade to `8d4ffdb`: two new commands install verbatim, their routing stays prose
Context: Upgrading the agent kit from `dcd0d8f` to `8d4ffdb`, which adds `/kit-help` and `/refine`, makes `/slice`'s argument optional, and adds transcript-based session cost measurement. This repository runs the kit's arrangement — `AGENTS.md` holds the contract, `CLAUDE.md` points at it, slice ids are `S<n>` — so both new commands are already correct as shipped.
Chosen: Copy both byte-identical, overwrite the unedited `slice.md`, and extend the existing routing **paragraph** with a clause for each. The kit states routing as a table; this repository states it as prose, and that is a deliberate local form.
Rejected: **Convert the paragraph to the kit's table** — easier to scan, and it is what the kit and two sibling repositories do; rejected because the installer must not reformat prose it is not otherwise changing, and a table conversion is a diff nobody asked for buried inside an upgrade. Worth doing on its own if it is worth doing.
Reversibility: cheap

### 2026-08-04 — Session boundaries and the model-work tiers, both scoped away from ADRs
Context: The upgrade adds two `AGENTS.md` sections written for a repository whose only long-form reasoning is the `design/` chain. This repository also has `docs/docs/adr/`, which is authored directly and is not a design cycle.
Chosen: Install both, each with one scoping sentence. The boundary table says an ADR does not inherit it. The model-work tiers cite ADR-005 as this repository's own precedent for a red item leaving the model: it rejects a hand-authored `.proto`/OpenAPI document precisely because it would be a second definition of types TypeScript already owns, and projects the contract instead.
Rejected: **Install both unscoped** — a boundary table with no scope note reads as requiring a fresh session per ADR, which nothing intends. **Skip them** — they are the substance of this upgrade; skipping leaves the repository on the kit's older model without saying so.
Reversibility: cheap

### 2026-08-04 — Created `tools/` for `Measure-Session.ps1` rather than using `build/`
Context: The kit ships `tools/Measure-Session.ps1`, run as a `SessionEnd` hook. This repository had no `tools/`, and `build/` already holds committed PowerShell — the documentation gate and homepage generator — with a `.gitignore` comment explaining at length why `build/` must never be ignored.
Chosen: Create `tools/` and install there, matching the kit's hook path `${CLAUDE_PROJECT_DIR}/tools/Measure-Session.ps1`. `settings.json` did not exist and was created holding only `hooks.SessionEnd` — no model pin, no permissions block.
Rejected: **`build/Measure-Session.ps1`** — one home for PowerShell here, and the naming fits. Rejected because `build/` is wired into `docs-ci.yml`; a per-machine session-cost reporter is not part of the documentation gate, and adding it there invites exactly the confusion that `.gitignore` comment records. Reversing this costs one file move and two path edits.
Reversibility: cheap
