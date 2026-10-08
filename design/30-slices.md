# Slices — commercial (D5)

**Document status:** Slices. Derived from [`10-design.md`](10-design.md) and
[`20-contract.md`](20-contract.md). The contract is authoritative for every signature named below;
**no slice may introduce one that is absent from it.** Where a slice needs a declaration the contract
does not carry — a module's own API surface is the likeliest place, since `20-contract.md`
§ *Public surface* 10 names what each module exposes without declaring it — the slice **stops and asks
for a contract amendment rather than inventing one.**

Each slice is vertical: it runs, and its acceptance criteria are observable from outside the code that
satisfies them. One repository is in scope, this one, plus the two sample hosts and the shell that S1
and S16 create inside it.

**The ordering is the design's risk ordering.** The riskiest thing in this design is not any one
capability — it is the single idea every capability is generated from:
[ADR-006](../docs/docs/adr/ADR-006-application-modules.md) rules 1 and 2 held **structurally**, so that
the framework can own nine questions while six modules own the answers and no module can reach another.
If that cannot be enforced by the build, the shape is wrong and everything downstream rests on a
preference. S1 therefore builds the enforcement
before there is anything to enforce it against, and proves it bites by failing it against a
deliberately broken graph — the standard
[`minimal-platform-packages.md`](../docs/docs/minimal-platform-packages.md) §2 sets. S2 takes the one
change that is cheap now and expensive after a consumer ships. S3 to S8 are the framework seams, each
placed where it first becomes checkable, ending with the fixed request order every later slice plugs
into rather than retrofits. S6 sits as early as its prerequisites allow because the deliberately shared
resource is the part of tenancy no library provides and the part
[`second-consumer-packages.md`](../docs/docs/second-consumer-packages.md) §3 names as how isolation
quietly stops holding. S9 to S16 are the six modules and the shell. S17 is the proof the brief's
definition of done actually asks for, and S18 is public delivery.

## Decisions that must be taken before the slice that needs them starts

Neither the design nor the contract settles these, and none is a slice's to settle silently.

| Question | Needed before |
|---|---|
| Contract Unresolved 1 — whether `IAuditable.CreatedBy` becomes non-null under a total principal | **S2**. Both readings are defensible and produce different declarations. Determined either way: the stored value is `PrincipalId.ToString()` and is never split, and `ModifiedBy`/`DeletedBy` stay nullable |
| Contract Unresolved 2 — how the frozen entitlement-contributor set reaches the settings-fingerprint input | **S8**, which is where I-C4 lands. The two shapes change different public declarations — a second parameter on `Compute`, or a projected `[Fingerprinted]` property. Determined either way: the names are inside the hashed input and `SettingsFingerprint`'s format version changes |
| Design Open question 2 — how the administration shell is delivered | **S16**. The recommendation is a separate front-end build with no .NET package; the alternative is a .NET-hosted UI. Neither introduces a .NET declaration, so nothing before S16 changes shape either way |
| Design Open question 4 — whether the D5 packages version in lockstep with the framework or independently | **S18**. The recommendation is lockstep for the whole of D5 |
| Design Open question 3 — whether ADR-006's vocabulary is corrected now that five infrastructure packages land in its module tier | **Not blocking any slice.** It is an ADR amendment and therefore the repository owner's; the placement in *Module boundaries* does not wait on the answer. Recorded here so `/slice` does not read the silence as settled |
| Issue #94's disposition once S23 lands. By the 2026-09-26 decision, D5 does not deliver #94's criteria 1, 3 and 4 (the sign-in hook, the sign-out proof and the hook documentation), and criterion 2 holds in validation terms only | **Not blocking any slice.** It is the repository owner's call whether #94 closes with those criteria recorded as accepted risk, or is split so they stay tracked. Recorded here so `/track` does not close #94 as fully delivered |

## How this document is kept

A slice's body is its specification while it is outstanding. **A re-run of `/plan` appends new slices
under `## Outstanding` only** — it never rewrites `## Landed`, and it never renumbers or reuses a
retired id, including one that never got an issue. Removing a criterion leaves a gap; the next
criterion takes the next unused number.

When a slice ships and its issue closes, its body is retired to a one-line entry under `## Landed`. The
index carries no criteria, because the issue is then the record of what was accepted — `/track` does
not sync a landed slice and must not re-derive its criteria from the index.

Every slice carries a `**Status:**` line reading `shipped`, `in progress` or `queued`, immediately
under its heading. [`build/Test-SliceStatusMarkers.ps1`](../build/Test-SliceStatusMarkers.ps1) fails the
documentation gate when the markers are inconsistent: at most one slice in progress, at least one in
progress while any is queued, and no shipped slice ordered after a queued one.

Each outstanding slice also carries a plain `Status:` line, `todo` or `done`, directly under the first.
That one is what `/agentkit:next` reads to pick the next slice. The two move together in the slice's
own pull request: `Status: done` and `**Status:** shipped` on the slice that ships, and
`**Status:** in progress` on the next queued one.

---

## Outstanding

S24 to S30 take every open issue that can be built without a decision from the repository owner. That
is the D5 work the issues surfaced after S18 landed. The riskiest of them goes first. S24 is a real
defect: during an outage, a caller is told it lacks permission when nothing was decided. It also changes
a public answer on two surfaces, HTTP and Mcp. S25 removes the CI flake that has failed two unrelated
pull requests, so that every later slice's CI result means something. S26 to S30 close issue
[#260](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/260). Each one tests the part of an
invariant that today is held only by instruction, then moves its record to `code`. They are grouped by
the package that owns the invariant, so that each pull request reads as one area.

A test in S26 to S30 may show that the code does not hold an invariant. That is then a defect, not a
test gap. If the fix stays inside the owning package and needs no contract change, the slice fixes it
and its pull request says so. Otherwise the record stays `instruction`, the slice opens an issue naming
the failing case, and the remaining criteria still land.

S31 to S48 take the issues the repository owner ruled on 2026-10-08. Each slice cites the ruling it
builds, in `90-decisions.md`. S31 makes the public roadmap render this document ([#80](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/80)). S32 lets an
operator cap audit retention ([#187](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/187)); it also bumps the fingerprint format to `szdfp4`, which S39
reuses. S33 and S34 add the inbox seam and its cleanup ([#62](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/62)). S35 adds opt-in optimistic concurrency
([#60](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/60)). S36 to S38 add the module start-up and shut-down hooks, seeding in `migrate` mode and
development-time automatic migration ([#63](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/63), [#61](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/61), [#68](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/68)). S39 and S40 cap how stale a mirrored
role may be ([#264](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/264)). They come after S24, because a stale mirror answers as an unavailable one. S41
and S42 add per-route streaming at the edge ([#110](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/110)). S43 to S45 add the layered runtime settings
module ([#58](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/58)). S46 to S48 add the optional account store and explicit linking ([#93](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/93) boxes 3–4).

## S24 — A permission check during an outage answers "try again", not "forbidden"
**Status:** shipped
Status: done

Delivers: someone calling a Platform-hosted service while a permission provider is unreachable is told
the service could not check their permission and that they may retry. Today they are told they lack
permission. An operator reading the answer can tell an outage from a real refusal.

Touches:
- **[`Authorization.cs`](../src/SubZeroDev.Platform.Abstractions/Authorization.cs)** (Abstractions):
  `AuthorizationDecision.ProviderFailure`, as `20-contract.md` *Types* § 2 scaffolds it
- **[`Authorization.cs`](../src/SubZeroDev.Platform.Core/Authorization.cs)** (Core): the evaluator
- **[`EndpointRequirement.cs`](../src/SubZeroDev.Platform.Hosting/EndpointRequirement.cs)** (Hosting)
  and **[`McpToolAdapter.cs`](../src/SubZeroDev.Platform.Mcp/McpToolAdapter.cs)** (Mcp): where a decision
  is answered
- **[`20-contract.md`](20-contract.md)**: the *Types* § 2 scaffold, and the I-A11 and I-A12 records
- **`tests/`**

Depends on: none

Acceptance:
- **S24.1** `AuthorizationDecision.ProviderFailure` exists exactly as the contract declares it. The
  *Types* § 2 scaffold block is replaced by the pointer to the tree, in the same change.
- **S24.2** With two registered providers, where the first returns any error and the second grants
  nothing, the decision is `Denied` and `ProviderFailure` is `ProviderUnavailable` naming the first
  provider. The same holds when the error the provider returned is not `ProviderUnavailable`.
- **S24.3** With three registered providers, where the second and third return errors, `ProviderFailure`
  names the second.
- **S24.4** With one provider that returns an error and another that grants the permission, the decision
  is `Allowed` and `ProviderFailure` is null. A denial in which every provider answered carries a null
  `ProviderFailure`.
- **S24.5** Over HTTP, an endpoint requiring a permission, with the Organizations permission provider's
  store unreachable, answers 503 with the `ProviderUnavailable` code. It does not answer 403 or 404, and
  the endpoint's handler never runs.
- **S24.6** Over Mcp, the same condition answers a tool result saying the permission could not be checked
  and may be retried. The tool's invoker never runs.
- **S24.7** A decision carrying both `AuditFailure` and `ProviderFailure` is answered with the audit
  failure's code, on both surfaces.
- **S24.8** The I-A11 and I-A12 records move from `instruction` to `code`, citing the tests above, in
  the same change. Issue #260's I-A11 box is ticked by this slice.

Out of scope: retrying the provider inside the evaluator, any change to the order providers are
consulted in, and caching a provider's last answer. None is in the contract.

## S25 — Cross-process trace evidence passes or fails on the code, not on a sampling draw
**Status:** shipped
Status: done

Delivers: a contributor whose pull request touches nothing near tracing no longer has CI fail on it at
random. When CI does fail on the tracing test, the failure means fresh-root tracing really broke.

Touches:
- **[`trace-evidence.test.ts`](../workloads/game-service/tests/trace-evidence.test.ts)** and its
  `tests/support/` helpers

Depends on: none

Acceptance:
- **S25.1** The malformed-`traceparent` case no longer needs any particular sampling outcome to occur
  within a fixed number of attempts. Either the test's pass condition is derived from the sampler's
  deterministic per-trace-id decision, or the attempt count is derived and stated so that a miss is
  below one in a million runs. The derivation is written in the test.
- **S25.2** With the edge's span pairing deliberately broken, for example the workload span's parent
  not propagated, the test fails on every run.
- **S25.3** The well-formed-`traceparent` assertions (S8.1, S8.2) are unchanged.
- **S25.4** The `game-service` CI job passes on this slice's pull request, and on the reruns needed to
  show S25.1 holds in practice: three consecutive runs.

Out of scope: changing `PlatformSampler`'s ratio, or making it configurable. Either one is a public
surface the contract does not declare. Also out of scope: the other `game-service` tests.

## S26 — The authorization and request-scope invariants are proven, not assumed
**Status:** shipped
Status: done

Delivers: a maintainer changing the evaluator, the composition provider or the operation scope finds out
from a failing test, not from production, if they break a promise the contract makes about who may do
what.

Touches: `tests/SubZeroDev.Platform.Tests/` (`AuthorizationTests.cs`, `OperationScopeTests.cs`,
`PrincipalTests.cs`, `TenancyTests.cs`), and the I-A3, I-A7, I-I1 and I-R3 records in
[`20-contract.md`](20-contract.md) *Invariants*

Depends on: 24

Acceptance:
- **S26.1** I-A3: the evaluator, called directly with a `PermissionName` that no registered
  `IPermissionCatalog` declares, refuses it. The test does not go through a path that would have caught
  the name earlier.
- **S26.2** I-A7: in `Operated`, the composition provider grants nothing to an `Account` or a `Delegated`
  principal, not only to System and Anonymous.
- **S26.3** I-I1: setting the ambient principal to null inside an open operation scope is refused, and
  reading it never yields null.
- **S26.4** I-R3: an attempt to change the scope's tenant or principal after the scope opens is refused,
  and a read at the end of the request returns the values set at its start.
- **S26.5** Each of I-A3, I-A7, I-I1 and I-R3 moves to `code` with its new test as Evidence, and #260's
  matching box is ticked.

Out of scope: any change to the evaluator's behaviour beyond what a failing test proves is a defect
(see *Outstanding*).

## S27 — Tenant isolation is proven for writes and for every entity type
**Status:** shipped
Status: done

Delivers: a product team that stores customer data on Platform can rely on one tenant never writing
another tenant's rows, and on isolation that does not depend on whether a type is shareable, because a
test fails the build if either stops being true.

Touches: `tests/SubZeroDev.Platform.Tests/` (`SharedReadTests.cs`, `TenancyTests.cs`,
`PackageGraphTests.cs`), and the I-T1, I-T2, I-T7 and I-O8 records

Depends on: 26

Acceptance:
- **S27.1** I-T1: inside tenant A's scope, an update, a delete and an insert carrying tenant B's id each
  fail or affect zero of tenant B's rows. This holds inside a shared-read scope too.
- **S27.2** I-T2: outside a shared-read scope, a query over a shareable type and a query over a
  non-shareable type each return only the current tenant's rows.
- **S27.3** I-T7: a test fails if any migration file present at D5's baseline commit is modified, or if
  the tenant identifier's column type, the primary keys or the implicit-tenant representation differ
  from that baseline.
- **S27.4** I-O8: a reflection test over every framework assembly finds no type or member that refers to
  a tenant's owner. It does not rely on I-C6's package-graph test.
- **S27.5** Each record moves to `code` with its test as Evidence, and #260's boxes are ticked.

Out of scope: any new escape hatch for writes, and any change to the shared-read audit.

## S28 — Audit's guarantees are proven across every surface that writes it
**Status:** shipped
Status: done

Delivers: a compliance reviewer can point at a test for each audit promise: that a record cannot be
changed after it is written, that secrets never reach it, and that an allowed action is recorded as
reliably as a refused one.

Touches: `tests/SubZeroDev.Platform.Tests/` (`AuditTests.cs`, `AuditStoreTests.cs`, `LicensingTests.cs`),
and the I-U4, I-U6, I-U7 and I-U9 records

Depends on: 26

Acceptance:
- **S28.1** I-U4: an allowed action whose work completes writes its audit row with the outcome known, in
  its own transaction. The denial case is already covered.
- **S28.2** I-U6: one test pushes a representative secret and payload through each audited input
  surface the tree has, and asserts neither appears in the stored record or in any captured log line.
  The surfaces are enumerated from the tree, not hand-listed, so a surface added later without coverage
  fails the test.
- **S28.3** I-U7: at the schema level, the audit table rejects an `UPDATE` issued directly against the
  database, not only through the store's API, and the store's public surface exposes no update and no
  delete. A schema-level `DELETE` rejection is **not** required: the retention prune deletes by age
  ([#187](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/187)). If the schema does not hold
  this today, see *Outstanding*.
- **S28.4** I-U9: the Core owner's detection site audits a repeated error condition once per detection,
  however many checks observe it.
- **S28.5** Each record moves to `code` with its test as Evidence, and #260's boxes are ticked.

Out of scope: audit retention, which is the retention slice's
([#187](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/187)).

## S29 — Host startup, telemetry, the shell and billing keep their promises under test
**Status:** shipped
Status: done

Delivers: an operator deploying a Platform host can trust that a bad configuration stops the host with a
named cause, that missing telemetry settings start no exporter, and that billing never calls out on a
request, because each of these is tested and not just written down.

Touches: `tests/SubZeroDev.Platform.Tests/` (`CompositionProfileTests.cs`, `TelemetryOptionsTests.cs`,
`TelemetryExportTests.cs`, `AdministrationShellTests.cs`, `PackageGraphTests.cs`, and a billing test),
and the I-C9, I-OB1, I-W1 and I-B6 records

Depends on: 26

Acceptance:
- **S29.1** I-C9: a test discovers every `HostStartupError` variant by reflection, and for each one
  provokes it and asserts the host fails to start, naming the registration. A variant added later
  without a provocation fails the test.
- **S29.2** I-OB1: with no OTLP endpoint configured, the host starts and no exporter instance exists in
  the built service provider. The test observes this directly; it does not infer it from the absence of
  traffic.
- **S29.3** I-W1: the shell's build output contains no server-side state store and no endpoint of its
  own, and every request it issues targets a route in the public HTTP API's route table.
- **S29.4** I-B6: with an outbound handler that fails the test on any call to a billing provider's host,
  a request, a host startup and a readiness probe each complete without calling it.
- **S29.5** Each record moves to `code` with its test as Evidence, and #260's boxes are ticked.

Out of scope: adding new startup checks or new telemetry options.

## S30 — Mcp, Organizations and Licensing keep their promises under test
**Status:** shipped
Status: done

Delivers: a product exposing tools over Mcp, inviting members, or selling paid features can rely on
credentials being checked once per connection, invitation tokens never being stored readable, and
licensed work already under way not being cut off when a licence lapses.

Touches: `tests/SubZeroDev.Platform.Tests/` (`McpTests.cs`, `McpInvocationTests.cs`,
`OrganizationsTests.cs`, `LicensingTests.cs`), and the I-M5, I-M7, I-O4 and I-L8 records

Depends on: 26

Acceptance:
- **S30.1** I-M5: a reflection test over the Mcp tool and invoker surface finds no parameter that
  carries a credential, and a call presenting a credential in its arguments is authenticated as the
  connection's principal, not as the credential's.
- **S30.2** I-M7: a manifest-projected producer and a product-owned fixed-table producer register through
  the same public surface in one host, and both tools are listed and invocable with identical
  authorization behaviour.
- **S30.3** I-O4: after an invitation is minted, the stored row holds a hash that is not the token, and
  no API returns the token again. A second read attempt returns nothing token-shaped.
- **S30.4** I-L8: after the grace period ends, work accepted before it ended runs to completion,
  scheduled work runs at its time, and existing data stays readable and exportable. New paid-feature
  work is denied.
- **S30.5** Each record moves to `code` with its test as Evidence, and #260's boxes are ticked. With S24
  to S29, that closes #260.

Out of scope: the cancelled-call test that
[#224](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/224) keeps skipped until
`ModelContextProtocol` ships its fix.

## S31 — The public roadmap shows the work in progress now, not D3's finished plan
**Status:** shipped
Status: done

Delivers: a visitor to the site's roadmap page sees the active effort's slices: what has shipped, what is being built now, what is queued, and D5's real non-goals. Today the page shows a closed effort's finished plan.
Touches: `site/src/roadmap/roadmapData.ts`, `site/src/roadmap/roadmapData.test.ts`, `site/src/roadmap/RoadmapApp.test.tsx`, `site/src/App.test.tsx`
Depends on: none
Acceptance:
  - S31.1 `roadmapData.ts` imports `design/30-slices.md`, not `design/d3/30-slices.md`. No module under `site/src/` imports anything under `design/d3/`.
  - S31.2 Every slice is counted once, from all three forms the active ledger uses: a `## S<n> — ` heading with a `**Status:**` line; a `| **S<n>** | <name> | … |` row of the `## Landed` table; and a `- **S<n> — <name>** — shipped:` bullet under `## Landed`. A Landed row or bullet parses as `shipped`. Against the ledger at this slice's merge, `totalCount` equals the highest slice number, every id from S1 to that number appears exactly once, and `shippedCount` equals the number of slices whose status is `shipped`.
  - S31.3 The parser still throws, never returning a partial roadmap, when a heading slice lacks its `**Status:**` or `Depends on:` line, when a status is unrecognised, or when the same id appears in two forms. Each case has a fixture test. A Landed row or bullet with no dependency line parses with `dependsOn` empty and does not throw.
  - S31.4 `assertConsistent` passes against the active ledger. It runs in the same test that proves S31.2, so a merge that breaks either one fails the site's tests.
  - S31.5 `nonGoals` lists D5's non-goals from `design/00-brief.md` *Non-goals*, hand-authored as before. Its doc comment names that file. No entry says Platform is self-host only.
  - S31.6 The roadmap page renders "<shipped> of <total> Slices Resolved." from those counts, and the existing `RoadmapApp` and `App` tests pass against the active ledger.
Out of scope: rendering archived ledgers beside the active one; restyling the roadmap page; generating `nonGoals` from the brief's prose.

## S32 — An operator can cap how long audit rows are kept, and a host with no value keeps them forever as before
**Status:** shipped
Status: done

Delivers: As an operator, I set one retention value in days, and the worker deletes audit rows older than
that every hour in small batches, while web hosts keep appending undisturbed. With nothing set, nothing
changes, and a mistyped value stops the host at startup with an error naming the key.
Touches:
- `SubZeroDev.Platform.Core`:
  - `PlatformOptions.cs` (`AuditOptions`, `PlatformOptions.Audit`)
  - `PlatformOptionsBinder.cs` (an optional bounded `int?` reader)
  - `SettingsFingerprint.cs` (`szdfp4`)
- `SubZeroDev.Platform.Audit`:
  - `AuditModule.cs` (conditional registration)
  - `AuditStore.cs` (migration `0002`; remove the "no retention migration" comment)
  - a new internal `AuditPruneWork.cs`
- tests:
  - `tests/SubZeroDev.Platform.Tests/AuditRetentionTests.cs` (new)
  - `SettingsFingerprintTests.cs` (golden vector)
  - `AuditStorePostgresTests.cs` (index on PostgreSQL)
- `design/state/invariants/`: I-U7, I-U11 to I-U14
Depends on: none
Acceptance:
  - S32.1 With `Platform:Audit:RetentionDays` absent, empty or whitespace, the composed Worker's
    background-work set contains no `platform.audit.prune`. Rows with `OccurredAt` 10 years old are still
    present after every registered work has ticked once (I-U11).
  - S32.2 With the value `30` and a fake clock at T, a Worker-role test host holds rows at T−31d, T−30d−1s,
    T−30d and T−29d. One `RunBackgroundWorkOnceAsync("platform.audit.prune")` leaves exactly the T−30d and
    T−29d rows (I-U12).
  - S32.3 With the value `30` and 1,201 eligible rows, one tick deletes all 1,201. With 50,001 eligible
    rows, one tick deletes exactly 50,000 and a second tick deletes the last one.
  - S32.4 With the value `30`, a Web-role host's `IBackgroundWorkRegistry.ForRole(HostRole.Web)` does not
    contain `platform.audit.prune`.
  - S32.5 With the work's lease held by another instance, a tick deletes nothing and logs nothing at
    Warning. Two ticks run back to back against the same rows leave the same result as one.
  - S32.6 Each of `0`, `-1`, `36501`, `1.5` and `abc` fails host construction in both roles with
    `PlatformStartupException`. Its error is `HostStartupError.Configuration`, and the inner error is
    `ConfigurationError.InvalidSetting` whose key is `Platform:Audit:RetentionDays`. `1` and `36500` bind
    (I-U13).
  - S32.7 With the value set and the Audit module not composed, the host starts and nothing is deleted.
  - S32.8 A tick that deletes rows emits one Information log entry carrying the deleted count and the
    cutoff, and writes no `AuditEvent`. A tick whose statement fails logs a Warning and does not change
    readiness.
  - S32.9 After migration, `ix_audit_event_occurred_at` exists on SQLite. On PostgreSQL it exists when the
    PostgreSQL test fixture is available. A database already at `0001` migrates to `0002` without data loss.
  - S32.10 Fingerprints differ for `RetentionDays` absent against `30`, and for `30` against `31`. The
    golden vector is updated and asserts format version `szdfp4` (I-U14).
  - S32.11 The I-U7 record carries the amended statement. I-U11 to I-U14 exist with
    `Enforcement: instruction` and the evidence above, and the regenerated invariants table matches the
    records.
Out of scope:
- archival or export before deletion
- per-tenant, per-plan or per-class retention
- a configurable interval or batch size
- an audit row or a readiness check for pruning
- a schema-level `DELETE` trigger. That is S28.3, which this ruling narrows.
- adding the audit table to Persistence's `PruneWork`

## S33 — A handler is no longer run twice when its message is redelivered after it already committed
**Status:** shipped
Status: done

Delivers: As an operator of any C# Platform host, when a worker loses its claim after a handler
committed, or two workers overlap on one message, the handler's database effects land once and the
redelivery is skipped and logged, rather than applied twice.
Touches: `src/SubZeroDev.Platform.Persistence` (new `0004_create_inbox` migration in `PlatformMigrationSource`, internal `InboxStore`, `OutboxDispatcher`'s dispatch path and lost-claim wording), `tests/SubZeroDev.Platform.Tests` (`OutboxDispatchTests`, `PersistenceContractTests`), `design/state/invariants/I-P4.md`, `I-P5.md`, `design/20-contract.md` (scaffold to pointer)
Depends on: none
Acceptance:
  - S33.1 On a fresh SQLite database and on PostgreSQL, applying migrations creates `platform_inbox` with primary key `(message_id, consumer)` and a non-null `tenant`. The history table lists `0004_create_inbox` after `0003_create_background_work_lease`. The bytes of `0001`–`0003`'s migration code are unchanged against the commit before this slice.
  - S33.2 Here is the redelivery test. An event is enqueued and dispatched once, so its handler inserts one row into a test table and commits. The outbox row's processed marker and claim are then reset to pending, simulating a lost claim. A second dispatch then runs. Result: the test table holds exactly one row, the outbox row is processed, `attempts` is unchanged by the second dispatch, and one Information log names the message id and the event-type name. This holds on both providers.
  - S33.3 A handler that returns a failure leaves no `platform_inbox` row for that message. On the retry it is invoked again, and on success exactly one inbox row exists.
  - S33.4 The message is enqueued under tenant T. The inbox row's `tenant` equals T in `platform_outbox.tenant`'s encoding. A message under the implicit tenant stores the sentinel.
  - S33.5 On PostgreSQL, two dispatch transactions for the same message run concurrently. The first holds its transaction open until the second has issued its inbox insert. The handler's test-table write then exists exactly once.
  - S33.6 The lost-claim warning text equals the contract's new sentence. No log line or inbox column contains the event payload.
Out of scope: a public inbox API, or deduplication for webhooks or other inbound messages (Unresolved 5). Making external side effects idempotent. Pruning, which is S34.

## S34 — Inbox records are removed once the message they guard is gone, so the table cannot grow without bound
**Status:** shipped
Status: done

Delivers: As an operator, the inbox stays as small as the outbox. Its records disappear on the outbox's
own retention, with no new setting, and a record is never removed while its message could still be
redelivered.
Touches: `src/SubZeroDev.Platform.Persistence` (`PruneTarget.OrphanedInboxRecords`, `PruneSql`, `PruneWork`, both providers' `DeleteBoundedAsync`), `tests/SubZeroDev.Platform.Tests` (`PersistenceContractTests`, `PruneLoggingTests`), `design/state/invariants/I-P6.md`
Depends on: 33
Acceptance:
  - S34.1 Three inbox records are set up. A has no outbox row. B's outbox row is processed. C's outbox row is pending. One prune pass deletes A only. B and C remain. This holds on both providers.
  - S34.2 There are 25 orphaned records and `PruneBatchSize` is 10. Each batch deletes at most 10, and the pass ends with zero orphans.
  - S34.3 After `ProcessedOutboxRows` prunes a processed row, the next pass's `OrphanedInboxRecords` deletes that row's inbox record.
  - S34.4 The prune log for the new target follows the existing target's format and names the target `OrphanedInboxRecords`.
Out of scope: a separate inbox retention setting. Foreign keys or `PRAGMA foreign_keys`. Pruning anything by tenant.

## S35 — Two hosts writing the same row no longer silently overwrite each other
**Status:** shipped
Status: done

Delivers: As a product developer on Automator or BarStrad, I mark a table as versioned and write
through the guard. A write based on a stale read is rejected as a whole, my endpoint answers 409, and my
tool answers a fixed failure. The later write never silently wins.
Touches: `src/SubZeroDev.Platform.Persistence` (`Columns.cs` `IVersioned`, `Errors.cs` `StaleVersion`, new `IVersionGuard` and implementation, `UnitOfWork` doom check, composition registration), `src/SubZeroDev.Platform.Abstractions/Results.cs` (two `ContractViolation` variants), `tests/SubZeroDev.Platform.Tests` (`PersistenceContractTests`), `docs/docs/` Persistence page, `design/state/invariants/I-P1.md`, `I-P2.md`, `I-P3.md`, `design/20-contract.md` (scaffolds to pointers)
Depends on: none
Acceptance:
  - S35.1 A test table has a `version` column (default 1) and one row at version 1. Two units of work each read version 1 and run a guarded update. The first returns success and the row is at version 2. The second returns failure with `TransactionError.StaleVersion` (`IsRetryable == false`), and the row holds the first unit's values at version 2. This holds on both providers.
  - S35.2 A unit inserts a row into a second test table and enqueues an outbox event and an audited action. Its guarded write then matches 0 rows, and its work **ignores** the result and returns normally. `ExecuteAsync` returns `StaleVersion`. The second table, `platform_outbox` and the audit store hold nothing from that unit.
  - S35.3 A guarded write runs against a row that was deleted, a row at version 5 (expecting 1), and a row with the right version under another tenant. Each returns the identical `StaleVersion` error: same code, same detail text.
  - S35.4 `IVersionGuard.ExecuteAsync` is called outside any unit of work, and also inside one opened `ReadOnly`. Both throw `PlatformContractViolationException` whose code is `GuardedWriteOutsideWriteTransaction`. A guarded update whose predicate matches two rows throws `GuardedWriteNotSingleRow`, and neither row changes.
  - S35.5 On PostgreSQL, two concurrent units issue the same guarded update, and the first holds its transaction until the second's update has been issued. Exactly one returns success, the other returns `StaleVersion`, and the final version is 2.
  - S35.6 The test host maps `StaleVersion` per the contract. Its endpoint answers 409 with a body whose `code` is `"StaleVersion"`, plus the request's correlation, and no other fields. Its MCP tool returns `IsError == true` with exactly the contract's sentence. Neither body contains the digits of the stored or expected version.
Out of scope:
  - Ending the Node game-service's own CAS.
  - SQL-building or ORM helpers.
  - A Hosting or Mcp mapper for `TransactionError`, which the package graph forbids.
  - Retrying on `StaleVersion`.
  - A `Platform.Testing` harness for the guard.
  - ETags or any version on a product wire.

## S36 — A module can do its start-up and shut-down work in its own two methods, in dependency order, and is always shut down if it started
**Status:** shipped
Status: done

Delivers: As a module author, I can put work that must happen before my host serves, and clean-up that must happen when it stops, in two methods on my module. They run in the order my module's dependencies imply. If anything fails during startup, every module that already started is shut down cleanly.
Touches:
- `src/SubZeroDev.Platform.Abstractions/Modules.cs` (two default members)
- `src/SubZeroDev.Platform.Hosting/PlatformHostExtensions.cs` (retain the resolved order; register the lifecycle service between `PlatformRegistryStartup` and `BackgroundWorkService`)
- `src/SubZeroDev.Platform.Hosting/HostedServices.cs` (the internal lifecycle service)
- `src/SubZeroDev.Platform.Hosting/StartupFailure.cs` (`ModuleInitialization`)
- `tests/SubZeroDev.Platform.Tests/ModuleLifecycleTests.cs`
- `design/20-contract.md` (*Public surface* § 15 scaffold to pointer)
- `design/state/invariants/I-C10.md` to `I-C13.md`

Depends on: none
Acceptance:
  - S36.1 Modules A, B (depends on A) and C (depends on B) each append their name to a shared list in both hooks. They are registered in the order C, A, B. After the test host starts and is disposed, the list reads `A.init, B.init, C.init, C.shutdown, B.shutdown, A.shutdown`.
  - S36.2 A module whose `InitializeAsync` awaits an unreleased gate keeps `StartAsync` incomplete. While the gate is closed, `RunBackgroundWorkOnceAsync` has not been reachable, and no registered `IBackgroundWork.TickAsync` has been called (asserted by a counting work item with the timers on).
  - S36.3 A, B and C as in S36.1, with B's `InitializeAsync` throwing `InvalidOperationException("secret-xyz")`. `StartAsync` throws `PlatformStartupException` whose `Error` is `HostStartupError` with `Code == "ModuleInitialization"`. Its `Detail` contains `B` and `InvalidOperationException` and does not contain `secret-xyz`. The list reads `A.init, A.shutdown`.
  - S36.4 A hosted service registered by the test after the platform host throws in its `StartAsync`. Disposing the host leaves A, B and C each with exactly one `shutdown` entry, in reverse order.
  - S36.5 Stopping the host normally, then disposing it, leaves exactly one `shutdown` entry per module.
  - S36.6 B's `ShutdownAsync` throws. A's `shutdown` entry is still recorded, and an Error-level log record names `B`.
  - S36.7 A module that declares neither hook compiles unchanged, and the existing test suite passes.
  - S36.8 The hooks run on a worker-role test host as on a web-role one. `RunPlatformMigrateModeAsync`, with module A registered, leaves the list empty.
  - S36.9 A hook resolving a scoped service from its provider succeeds in a host whose environment is `Development`, where `ValidateScopes` is on.
Out of scope: moving Licensing's, Mcp's or Identity's existing startup hosted services onto the hooks; a role filter, ordering attribute or extra hooks; letting a hook register into a frozen registry.

## S37 — An operator's `migrate` run also writes each module's seed data, in module order, and re-running it changes nothing
**Status:** shipped
Status: done

Delivers: As an operator, when I run `migrate`, each module's installation-wide seed data is written after the schema is current, in the order the modules depend on each other, and running `migrate` again leaves the data unchanged. If a seed step fails, I get a named error and a non-zero exit, and nothing after it runs.
Touches:
- `src/SubZeroDev.Platform.Persistence/Migrations.cs` (`ISeeder`)
- `src/SubZeroDev.Platform.Persistence/Errors.cs` (`SeedFailed`)
- `src/SubZeroDev.Platform.Persistence/MigrateMode.cs` (Core ambient defaults; topological `Register`; seeding loop)
- `src/SubZeroDev.Platform.Core` (an internal registration of the ambient defaults shared with Hosting's `AddCoreDefaults`)
- `tests/SubZeroDev.Platform.Tests/SeedingTests.cs`
- `design/20-contract.md` (*Public surface* § 16 scaffold to pointer)
- `design/state/invariants/I-P3.md` to `I-P5.md`
- `docs/docs/commercial-guide.md`

Depends on: none
Acceptance:
  - S37.1 Module X has a seeder that inserts row `k=1` if absent, and module Y (depends on X) has one that records the count of X's rows. Running migrate mode on SQLite returns 0. X's row exists, and Y recorded 1.
  - S37.2 Running migrate mode a second time against the same database returns 0, and the table holds exactly one row `k=1`. The seeders were invoked again, as a counter shows.
  - S37.3 Two migrate mode runs started concurrently against one PostgreSQL database both return 0 or one returns 2. The table holds exactly one row `k=1`.
  - S37.4 Y's seeder is registered before X's, and the modules are registered Y then X. X's seeder still runs first.
  - S37.5 A seeder that throws `InvalidOperationException` makes migrate mode return 1. Stderr starts `SeedFailed: Module '<module>' seeder '<name>' failed:`. The seeder registered after it was not invoked. The migrations applied in the same run are present in the history table.
  - S37.6 A seeder that enqueues an event without opening a scope makes migrate mode return 1, with stderr starting `SeedFailed:` and containing `NoAmbientOperationScope`.
  - S37.7 A seeder that opens `Begin(tenant T, Principal.LocalSystem)` and writes an `ITenantOwned` row leaves that row with tenant `T` and `CreatedBy` `system:local`.
  - S37.8 With the migration lock held by another connection, migrate mode returns 2, and no seeder is invoked.
  - S37.9 Modules with a dependency cycle make migrate mode return 1, with stderr naming the `ModuleGraphError` code.
  - S37.10 A web host composed with the same seeders starts and stops without invoking any of them.
Out of scope: seeding at development start; a seeding history table or per-seeder "already ran" tracking; a `Platform.Testing` seeder harness; per-tenant seeding when a tenant is created.

## S38 — A developer's local hosts bring their own database schema up to date when they start, and the two hosts never race each other to do it
**Status:** shipped
Status: done

Delivers: As a developer running the web and worker hosts locally in the Development environment, I no longer run `migrate` before starting them. Either host applies pending migrations at start, the second waits for the first, and a broken migration stops the host with its name. Outside Development nothing changes.
Touches:
- `src/SubZeroDev.Platform.Persistence/PlatformPersistenceExtensions.cs` (register the service)
- `src/SubZeroDev.Platform.Persistence/StartupCheck.cs` (the internal development-migration lifecycle service; `PersistenceStartupException` message carries a `MigrationError`'s `Detail`)
- `tests/SubZeroDev.Platform.Tests/DevelopmentMigrationTests.cs` (hosts built with `HostApplicationBuilderSettings.EnvironmentName`, since the test host is fixed at `Production`)
- `design/state/invariants/I-P1.md`, `I-P2.md`
- `docs/docs/commercial-guide.md`

Depends on: none
Acceptance:
  - S38.1 A worker host built with environment `Development` and module X (one pending migration) on an empty SQLite database starts. Before any `StartAsync` runs, `IMigrationRunner.GetStatusAsync` reports nothing pending for X, as observed by a test lifecycle service registered before `AddPlatformPersistence` that records the status in its `StartAsync`.
  - S38.2 The same host built with environment `Production`, `Staging` or `development-ci` starts with X's migration still pending.
  - S38.3 Setting `Platform:Environment=Development` in configuration under environment `Production` applies nothing.
  - S38.4 A web host and a worker host, both `Development`, started concurrently against one PostgreSQL database, both start. X's history table holds the migration exactly once.
  - S38.5 With the migration lock held by a test connection, a `Development` host's `StartAsync` stays incomplete. After the connection releases the lock, the host starts with nothing pending.
  - S38.6 A `Development` host whose migration throws fails `StartAsync` with `PersistenceStartupException`. Its message contains `Failed`, the module and the migration name. The history table is unchanged.
  - S38.7 A `Development` host whose database is unreachable starts. Its readiness probe reports not-ready, and a Warning log record names the unavailable migration.
  - S38.8 A `Development` host with a registered `ISeeder` does not invoke it, if S37 has landed. If S37 has not landed, this criterion is dropped.
Out of scope: an opt-in or opt-out setting; seeding at start; changing migrate mode; retrying on `Unavailable`.

## S39 — A role removed at the identity provider stops granting within a limit the operator sets
**Status:** shipped
Status: done

Delivers: an operator whose roles live in Entra or Keycloak, and reach a Platform-hosted service through
a consumer's mirror, knows the longest a removed role can keep granting. A caller whose only grant comes
from a mirror whose sync has stopped is told to retry, not let in and not told they lack permission.

Touches:
- **[`Authorization.cs`](../src/SubZeroDev.Platform.Abstractions/Authorization.cs)** (Abstractions):
  `IMirroredPermissionProvider`, as `20-contract.md` *Public surface* § 3 scaffolds it
- **[`Authorization.cs`](../src/SubZeroDev.Platform.Core/Authorization.cs)** (Core): the evaluator, which
  now takes `IClock` and the authorization options
- **[`PlatformOptions.cs`](../src/SubZeroDev.Platform.Core/PlatformOptions.cs)** and
  **[`PlatformOptionsBinder.cs`](../src/SubZeroDev.Platform.Core/PlatformOptionsBinder.cs)** (Core):
  `AuthorizationOptions`, bound from `Authorization:MirrorMaximumAge`. The member joins the fingerprint
  under S32's `szdfp4` format version, so the two changes make one bump; this slice updates the golden
  vector, not the version
- **[`20-contract.md`](20-contract.md)**: the *Public surface* § 3 scaffolds become pointers; the I-A11
  and I-A13 records
- **`tests/`**: `tests/SubZeroDev.Platform.Tests/MirroredProviderTests.cs`

Depends on: 24, 32

Acceptance:
- **S39.1** `IMirroredPermissionProvider`, `AuthorizationOptions` and `PlatformOptions.Authorization`
  exist exactly as the contract declares them. The *Public surface* § 3 scaffold block is replaced by
  pointers to the tree in the same change.
- **S39.2** A mirrored stub provider grants the permission, and its `LastSyncedAsync` returns T. With the
  host clock at T + 15 minutes and no setting, the decision is `Allowed` with the stub as its source. At
  T + 15 minutes + 1 tick, the decision is `Denied`, `ProviderFailure` is `ProviderUnavailable` naming
  the stub, and the stub's `GrantsAsync` call count did not increase.
- **S39.3** The same stub returning null from `LastSyncedAsync` gives `Denied` with `ProviderFailure`
  naming it, at any clock value. The same stub returning an error from `LastSyncedAsync` gives the same
  result. In neither case is `GrantsAsync` called.
- **S39.4** A stale mirrored stub with a second, non-mirrored provider that grants gives `Allowed`, with
  only the second provider among the sources and a null `ProviderFailure`. A stale mirrored stub
  registered before an erroring provider gives `ProviderFailure` naming the mirrored stub. Registered
  after it, `ProviderFailure` names the erroring provider.
- **S39.5** With `Authorization:MirrorMaximumAge` set to `00:00:30`, the stub stamped at T allows at
  T + 30 seconds and is unavailable at T + 30 seconds + 1 tick. The values `00:00:30` and `1.00:00:00`
  start the host. Each of `00:00:29`, `1.00:00:01`, `00:00:00` and `-00:00:01` fails startup with
  `ConfigurationError.InvalidSetting` naming `Platform:Authorization:MirrorMaximumAge`. Changing the value
  changes the settings fingerprint. Leaving it unset binds `00:15:00`.
- **S39.6** Over HTTP, an endpoint whose only grant comes from a stale mirrored stub answers 503 with the
  `ProviderUnavailable` code, and its handler never runs. Over Mcp, the same condition answers the
  retryable tool result, and the invoker never runs. Both reuse S24's paths unchanged.
- **S39.7** A non-mirrored provider is never asked for a timestamp. The existing `AuthorizationTests`,
  `OrganizationsTests` and `PermissionProviderHarness` tests pass unchanged.
- **S39.8** The I-A13 record moves from `instruction` to `code`, citing `MirroredProviderTests.cs`. The
  I-A11 statement is amended as the contract states, keeping S24's enforcement and evidence. The
  contract's invariants region is regenerated in the same change.

Out of scope: a readiness or health signal for a stale mirror; a per-provider maximum; caching the stamp
between requests; any sync, mirror table or vendor adapter inside Platform; the issuer-side harness
(S40). None of these is in the contract.

## S40 — A consumer can prove its mirrored roles are revoked within the limit by revoking at the identity provider
**Status:** shipped
Status: done

Delivers: a consumer that mirrors issuer roles runs one test that removes a role where it really lives,
at the identity provider, and fails if the role keeps granting past the limit. The test also fails if
the consumer's sync does not carry the removal. Today the only test removes the copy, and it passes
whatever the sync does.

Touches:
- **[`PermissionProviderHarness.cs`](../src/SubZeroDev.Platform.Testing/PermissionProviderHarness.cs)**
  (Testing): `AssertIssuerRevocationDeniesWithinBoundAsync`
- **[`20-contract.md`](20-contract.md)**: the *Public surface* § 9 scaffold becomes a pointer
- **[`commercial-guide.md`](../docs/docs/commercial-guide.md)**: the *Consumer tests* paragraph
- **`tests/`**: `tests/SubZeroDev.Platform.Tests/PermissionProviderHarnessTests.cs`, which gains a fake
  issuer, a fake mirror over the test host's persistence, and three mirrors that misbehave

Depends on: 24, 39

Acceptance:
- **S40.1** `AssertIssuerRevocationDeniesWithinBoundAsync` exists exactly as the contract declares it.
  The *Public surface* § 9 scaffold block is replaced by the pointer in the same change.
- **S40.2** It completes against a correct fake mirror. That mirror's sync reads the fake issuer, then
  writes rows and a stamp taken at read start from the host's `IClock`, in one transaction.
- **S40.3** Against a mirror whose sync stamps without reading the issuer, it throws
  `InvalidOperationException` naming the provider. The message says the sync did not carry the
  revocation.
- **S40.4** Against a mirror whose `LastSyncedAsync` always returns the host clock's current instant, it
  throws naming the provider. The message says the grant survived past `MirrorMaximumAge` with no sync.
- **S40.5** It throws naming the other provider when a second registered provider also grants the
  permission to the principal. It throws when the provider passed is not among the decision's sources
  after grant and sync.
- **S40.6** With `Authorization:MirrorMaximumAge` set to `00:05:00`, the harness advances the host clock
  by exactly 5 minutes plus 1 tick between the revoke and the second evaluation.
- **S40.7** `commercial-guide.md` *Consumer tests* gains a paragraph: a provider over roles the identity provider manages implements `IMirroredPermissionProvider`; its sync records when it began reading the issuer, with the rows, and only on a successful read; the maximum's default and range, and that it should exceed the sync interval plus the sync's duration; and what the new harness takes and when it fails. The
  documentation gate passes.

Out of scope: a harness case for a sync that advances the stamp after a failed issuer read (I-A14 stays
`instruction`); fake issuers for real vendors; changing `AssertRevokedGrantDeniesNextRequestAsync`.

## S41 — A caller on a route the operator opts in sees the workload's output as it is produced, and sees a broken stream, not a finished one, when the workload dies mid-response
**Status:** shipped
Status: done

Delivers: An operator lists a path prefix and its first-byte budget, and a client of that route
receives each piece of the workload's response as the workload writes it. If the workload dies partway
through, the client's response is visibly incomplete rather than cleanly ended. Every route not listed
behaves exactly as today.
Touches:
- `workloads/game-edge/SubZeroDev.Platform.GameEdge/`: `Options.cs` (`StreamingRoutes`,
  `StreamingRoute`), `Forwarding.cs` (`StreamedResponse`, `ForwardStreamingAsync`), `Endpoints.cs`
  (route match, commit, flush, abort), `Program.cs` (startup validation)
- `workloads/game-edge/SubZeroDev.Platform.GameEdge.Tests/`: `Support/FakeWorkload.cs` (incremental
  emission, then socket destruction, on demand), new `StreamingTests.cs`, `EdgeHostTests.cs`
Depends on: none
Acceptance:
  - S41.1 With `StreamingRoutes` = `[{ PathPrefix: "/console/stream", FirstByteTimeout: 00:00:02 }]`,
    the fake workload answers `GET /console/stream` with `200`, `Content-Type: text/event-stream`, and
    writes `data: 1\n\n`, then waits until the test reads it, then writes `data: 2\n\n`. The caller
    reads `data: 1\n\n` while the workload is still waiting
    (`Each_emitted_piece_reaches_the_caller_before_the_workload_emits_the_next`, I-E2).
  - S41.2 Same route. The workload writes `data: 1\n\n` and then destroys its socket. A raw TCP
    HTTP/1.1 client against the edge on real Kestrel (loopback, port 0) receives status `200`, the
    chunk carrying `data: 1\n\n`, and then end-of-stream with no `0\r\n\r\n` terminator and no further
    bytes (`A_workload_that_dies_mid_stream_aborts_the_response_without_a_terminating_chunk`, I-E3).
  - S41.3 Same as S41.2 via `HttpClient`: reading the body to the end throws (`HttpIOException` or
    `IOException`); it never completes normally.
  - S41.4 Same route. The workload accepts but sends no headers for 3 s. The caller gets `504`, body
    `{"code":"workload_timeout","correlation":"<trace id>"}`, within 2 s plus 1 s tolerance, even
    though `ForwardTimeout` is 10 s.
  - S41.5 Same route, workload stopped. The caller gets `503` `{"code":"workload_unreachable",…}`.
    With a counting handler, the workload sees exactly one attempt in S41.2, S41.4 and S41.5
    (`A_streamed_forward_that_fails_is_attempted_exactly_once`, I-E4).
  - S41.6 With `/console/stream` listed, `GET /console/streamer` and `GET /state` are buffered. A
    workload dying after headers on `/state` yields `503 workload_unreachable`, as #106's test does.
    Every existing ForwardingTests and EdgeHostTests case passes unchanged
    (`An_unlisted_route_is_buffered_when_another_route_streams`, I-E1).
  - S41.7 A stream the workload holds open for 12 s (longer than `ForwardTimeout` = 10 s), emitting
    every second, completes normally with all 12 events.
  - S41.8 Startup fails with `InvalidOperationException` naming the key for each of:
    - `PathPrefix` = `"console"` (`GameEdge:StreamingRoutes:0:PathPrefix`)
    - `"/"`
    - `"/console/"`
    - `"/a?b"`
    - two entries `"/console"` and `"/console/stream"` (the second key)
    - `FirstByteTimeout` = `00:00:00`
    - `FirstByteTimeout` = `00:00:11` with `ForwardTimeout` = `00:00:10`
    - an entry with no `FirstByteTimeout`
  - S41.9 The caller disconnects mid-stream. Within 2 s, the fake workload observes its response
    aborted, and the edge has not logged at `Error`.
Out of scope: the abort's `Warning` log and counter (S42); WebSocket upgrade; forwarding headers
other than status and `Content-Type`; streaming the request body; any change to buffered routes or to
`ForwardAsync`.

## S42 — An operator can see how often a streamed route was cut short by the workload, without a dashboard counting those cuts as successes
**Status:** shipped
Status: done

Delivers: When a workload dies mid-stream, the operator gets one warning naming the route and the
correlation id, and a counter they can chart and alert on. Callers who simply leave do not raise
either.
Touches:
- `workloads/game-edge/SubZeroDev.Platform.GameEdge/`: `Endpoints.cs` (log and counter on abort),
  `Program.cs` if the meter is created at registration
- `workloads/game-edge/SubZeroDev.Platform.GameEdge.Tests/StreamingTests.cs`
- `workloads/game-service/README.md` (one paragraph)
- the `Errors.cs` doc comment on `WorkloadUnreachable`, which still says "could not connect"; widen it
  to G1's buffered and streamed wording
Depends on: 41
Acceptance:
  - S42.1 In S41.2's scenario, exactly one log record is written with event id `1101` and name
    `EdgeStreamAborted`, at `Warning`, in category `SubZeroDev.Platform.GameEdge`. Its state holds:
    - prefix `"/console/stream"`
    - the response's correlation id
    - code `"workload_unreachable"`
    - bytes forwarded ≥ the length of `data: 1\n\n`
    - an exception type name
  - S42.2 Requesting `GET /console/stream/7?token=secret` with the S41.2 workload: no log record at any
    level that Platform or the edge writes (category `SubZeroDev.*`) contains `/console/stream/7`, `token`
    or `secret`. The framework's request and outbound-call records are out of the check; see
    `90-decisions.md`, 2026-10-09, S42.
  - S42.3 A `MeterListener` on meter `SubZeroDev.Platform` records exactly one measurement of `1` on
    `subzerodev.edge.stream.aborts`, tagged `route=/console/stream` and `code=workload_unreachable`.
  - S42.4 In S41.9's scenario (caller disconnect) and S41.7's (clean end), no `EdgeStreamAborted`
    record is written, no measurement is taken, and no record above `Debug` comes from the edge's
    category (`An_abort_is_logged_and_counted_once_and_a_caller_disconnect_is_neither`, I-E5).
  - S42.5 No record at `Error` from `ErrorEnvelopeMiddleware` ("Unhandled failure") appears in any
    S41 scenario.
Out of scope: a counter for streams started or their duration; an idle timeout; dashboards; flipping
I-E1..I-E5 to `code` (that is the reconciliation after both slices merge).

## S43 — A product declares its runtime settings once and reads the value that applies to whoever is asking
**Status:** shipped
Status: done

Delivers: As a product developer, I declare a setting with a default and the layers it may be set at,
and a read returns the user's value, else the tenant's, else the global one, else my default. A
mistaken declaration stops the host at startup instead of misbehaving later.
Touches: new `src/SubZeroDev.Platform.RuntimeSettings/` (module, catalogue, startup lifecycle service,
reader, store, migration, errors, permission names, audit actions); `SubZeroDev.Platform.slnx`;
`build/Test-PackageManifests.ps1`; `tests/SubZeroDev.Platform.Tests/RuntimeSettingsTests.cs`,
`RuntimeSettingsPostgresTests.cs`, `PackageGraphTests.cs`, `SettingsFingerprintTests.cs`.
Depends on: none
Acceptance:
  - S43.1 With `Sample.Runs.MaxConcurrent` declared as an integer, default 4, admitting all three layers,
    and no rows stored, a read returns value 4 with `From` null.
  - S43.2 With rows stored as global 8, tenant T1 6, and user (T1, account A) 2:
    - account A in T1 reads (2, User);
    - account A in T2 reads (8, Global);
    - a `Delegated`, a `System` and an anonymous principal in T1 each read (6, Tenant);
    - any principal under the implicit tenant reads (8, Global).
  - S43.3 With the same rows and the declaration admitting only Global and Tenant, account A in T1 reads
    (6, Tenant).
  - S43.4 Each of these fails host start with `HostStartupError.Registration` whose inner error is the
    named `SettingCatalogError`, and the host never serves a request:
    - two catalogues declaring `Sample.Runs.MaxConcurrent` gives `DuplicateSettingName`;
    - a default of 12 with the rule "at most 10" gives `InvalidSettingDefault`;
    - an empty layer set gives `SettingWithoutLayers`;
    - a setting named `Sample.Api.Token` gives `SensitiveSettingName`.
  - S43.5 Reading an undeclared name, or `Sample.Runs.MaxConcurrent` as a boolean, returns `NotDeclared`
    and issues no query.
  - S43.6 A stored global value of `abc`, or 12 against the rule "at most 10", with no tenant row,
    returns `StoredValueInvalid` (not retryable), not (4, null).
  - S43.7 With the store unreachable, a read returns `StoreUnavailable` with `IsRetryable` true.
  - S43.8 On SQLite and on PostgreSQL, a tenant row written for T1 is not returned to a read in T2, and
    the migration `0001_create_runtime_settings` applies once. The table rejects a global row whose
    tenant is not all zeros, and a tenant row with a non-empty principal.
  - S43.9 The package graph test reports fourteen packages. RuntimeSettings references only
    Abstractions, Core and Persistence, and no package references RuntimeSettings. The assembly contains
    no `IPermissionProvider` and no `IEntitlementContributor` implementation.
  - S43.10 The settings fingerprint of a host composing the module with stored rows equals that of the
    same host without the module. Every other module's migration list is byte-identical to `main`.
Out of scope: the writer, the permissions' enforcement and the audit (S44); any cache; any HTTP
endpoint or UI.

## S44 — An administrator changes a setting for the installation, a tenant or themselves, and the change is recorded
**Status:** shipped
Status: done

Delivers: As an administrator, I change a setting globally or for my tenant when I hold the grant,
and as an account I change my own value. Each change is audited without its value, and a change I am
not allowed to make, or that names a layer that does not exist for me, changes nothing.
Touches: `src/SubZeroDev.Platform.RuntimeSettings/` (writer, permission catalogue, audit);
`tests/SubZeroDev.Platform.Tests/RuntimeSettingsTests.cs`.
Depends on: 43
Acceptance:
  - S44.1 In Local as `system:local`, setting `Sample.Runs.MaxConcurrent` to 8 at Global then reading
    returns (8, Global). Exactly one audit record is stored: action `platform.runtime-settings.set`,
    class Required, resource `RuntimeSetting` / `global/Sample.Runs.MaxConcurrent`. With the value
    written as the sentinel text setting `SENTINEL-7f3a`, that string appears in no audit row and no log
    line.
  - S44.2 In Operated, account A in T1 with no grant setting the Tenant layer returns `PermissionDenied`.
    No row is written, and one `platform.authorization.denied` record exists. With a test provider
    granting `Platform.RuntimeSettings.WriteTenant` on `RuntimeSetting` / `Sample.Runs.MaxConcurrent`,
    the same write succeeds, and a write to another setting is still denied.
  - S44.3 Account A in T1 setting the User layer to 2 needs no grant. Account B in T1 still reads the
    tenant or global value, and A reads (2, User).
  - S44.4 Each of these returns `LayerUnavailable` with no row and no audit record:
    - a `Delegated` principal writing the User layer;
    - `System` writing the User layer;
    - any principal writing the Tenant or User layer under the implicit tenant.
  - S44.5 Writing a layer the declaration does not admit returns `LayerNotAdmitted`. Writing 12 against
    "at most 10" returns `InvalidValue`, and the value 12 does not appear in its `Detail`.
  - S44.6 Clearing a stored tenant row makes the next read fall through to global and writes one
    `platform.runtime-settings.cleared` record. Clearing an absent row succeeds and writes no record.
  - S44.7 With a failing durable audit sink, a set returns `StoreUnavailable` (retryable), and a
    subsequent read shows the previous value.
  - S44.8 With a permission provider that faults, a set returns `AuthorizationUnavailable` (retryable)
    and writes no row.
Out of scope: endpoints for products to call (products map their own); optimistic concurrency (#60);
Organizations granting the two names.

## S45 — Both hosts see a change on their next read, and the sample proves it on both databases
**Status:** in progress
Status: todo

Delivers: As an operator running a web host and a worker against one database, I change a setting on
one and the other uses it on its next read without a restart. The Local sample shows a self-hosted
installation reading and changing a global setting.
Touches: `samples/SubZeroDev.Platform.Sample.Local/Program.cs`;
`tests/SubZeroDev.Platform.Tests/RuntimeSettingsPostgresTests.cs`; `docs/docs/commercial-guide.md`;
`docs/docs/platform-specification.md`.
Depends on: 44
Acceptance:
  - S45.1 On PostgreSQL, with two hosts composed over one database (web role and worker role), setting
    Global to 8 on the web host makes the worker's next read return (8, Global), with no restart and no
    other call between them.
  - S45.2 On PostgreSQL, 50 concurrent sets of 3 and 50 of 5 to one Global setting all succeed. Exactly
    one row remains, holding 3 or 5, and exactly 100 `set` records exist.
  - S45.3 The Local sample composes the module, declares one setting, sets it at Global as
    `system:local`, and prints the value it reads back. The existing Local host proof still passes.
  - S45.4 `build/Test-PackageManifests.ps1` passes with fourteen packages. The commercial guide contains
    the `## Runtime settings (optional)` section and says "fourteen".
Out of scope: a cache or invalidation; a settings page in the shell; cleanup of rows for departed
accounts.

## S46 — A person who creates an account is the same principal on every later request, and two sign-ins that report the same email stay two different people
**Status:** queued
Status: todo

Delivers: As an Automator user or a GEaaS player, I can turn the identity I signed in with into an account. Every
later request with that sign-in acts as the account. A different sign-in that happens to report my email is never
mistaken for me. A host that does not opt in, like BarStrad's or SkyNet HR's, behaves exactly as before.
Touches:
- `src/SubZeroDev.Platform.Identity`:
  - csproj gains a reference to Persistence.
  - `IdentityAccountsModule`, the account store and its migration.
  - Mapping in the generic-path and test-grade providers.
  - `IAccountApi`'s `CreateAccountAsync` and `GetCurrentAccountAsync`, `AccountError`, `IdentityAuditActions`, and the `AccountId`/`Account`/`LinkedIdentity` types.
- `tests/SubZeroDev.Platform.Tests/AccountTests.cs` (new).
- `IdentityTests.cs`: rewrite S9.5 to "no migration source unless `IdentityAccountsModule` is registered".
- `ConfiguredBearerTests.cs`: update S20.11's exported-type list.
- `design/state/invariants` I-I4, I-I16, I-I17, I-I20 and I-I21.
Depends on: none
Acceptance:
  - S46.1 Two test-grade issuers, A and B, on a host that registers `IdentityModule` and `IdentityAccountsModule`. A token from A with `sub` `alice` and `email` `a@example.test` resolves to principal (A, `alice`). A token from B with `sub` `alice-b` and the same email resolves to (B, `alice-b`). Both are kind `Account` and the two ids differ.
  - S46.2 `CreateAccountAsync` under A's token returns an `Account` with exactly one identity, (A, `alice`). The next request with A's token has principal (`platform.account`, a 32-character lowercase-hex id equal to the account's), kind `Account`, and the token's display name. B's token still resolves to (B, `alice-b`).
  - S46.3 A second `CreateAccountAsync` under A's token, now an account principal, answers `NotEligible`. Two concurrent creates under the same unlinked token leave exactly one row in `identity_account`; the losing call answers `IdentityAlreadyLinked`.
  - S46.4 `CreateAccountAsync` under an upstream-proxy `Delegated` principal, under `Anonymous` and under `LocalSystem` each answers `NotEligible` and writes no row.
  - S46.5 A host that registers only `IdentityModule` reports no `IdentityAccounts` migration. After migrating, its database has no `identity_account` table. A's token resolves to (A, `alice`). Every existing Identity, ConfiguredBearer and SignIn test passes unchanged.
  - S46.6 With the store's connection failing, a request carrying A's token for a linked identity fails authentication with `ProviderFailed` and answers 401. The handler never observes (A, `alice`).
  - S46.7 A successful create writes exactly one `Required` audit row, `platform.identity.account-created`, with resource `("Account", <id>)` and actor (A, `alice`). With the audit sink failing, the create returns an error and `identity_account` stays empty.
  - S46.8 Inserting a link row for an (issuer, subject) that is already linked, against a different account, fails at the store.
  - S46.9 Columns:
    - `identity_account` has exactly `id, created_at`.
    - `identity_account_link` has exactly `issuer, subject, account, linked_at`.
    - No type in the Identity assembly has a name containing `User` or `Directory`.
  - S46.10 Registering `IdentityAccountsModule` without `IdentityModule` fails startup with the module graph's missing-dependency error.
  - S46.11 A test-grade provider configured with issuer `platform.account` has its tokens rejected with `CredentialRejected` on a host with the store.
Out of scope: linking and unlinking, which are S47 and S48. Also out: just-in-time creation, account deletion, any email or profile field, and HTTP endpoints, which are the host's.

## S47 — A person signed in to their account can add a second sign-in method by completing that sign-in, and in no other way
**Status:** queued
Status: todo

Delivers: As a player or Automator user already signed in, I can sign in with a second provider and attach it to my
account, so either sign-in reaches the same account afterwards. Nobody can attach a sign-in to my account, or mine
to theirs, by having the same email.
Touches:
- `src/SubZeroDev.Platform.Identity`:
  - `IAccountApi.LinkAsync`.
  - An internal issued-at check in the generic-path and test-grade validation.
- `tests/SubZeroDev.Platform.Tests/AccountTests.cs`.
- The SignIn test fixture, reused unchanged.
- `design/state/invariants/I-I18.md`.
Depends on: 46
Acceptance:
  - S47.1 Account X was created from A's token. `LinkAsync` under A's token, with a B token whose `iat` is now, returns X with identities (A, `alice`) and (B, `alice-b`). The next request with B's token resolves to X's principal.
  - S47.2 Under A's token, a B token whose `iat` is 7 minutes before the call (`ClockTolerance` 1 minute) answers `SecondCredentialRejected`, and so does a B token with no `iat`. Neither writes a row.
  - S47.3 `LinkAsync` under a principal with no account, here B's unlinked token linking a C token, answers `NotAnAccount`.
  - S47.4 B is linked to account Y. Linking it to X answers `IdentityAlreadyLinked`, and both X's and Y's identity lists are unchanged.
  - S47.5 Linking (A, `alice`) to X again succeeds, adds no row and writes no audit row.
  - S47.6 A token from an issuer no Identity provider trusts answers `SecondCredentialRejected`.
  - S47.7 A successful link writes one `Required` audit row, `platform.identity.identity-linked`, with resource `("Account", X)`.
  - S47.8 End to end with the sign-in fixture issuer:
    - Sign in through method A and read A's token; create the account.
    - Sign in through method B and read B's token.
    - `LinkAsync(B's token)` under A's token.
    - A request bearing B's token resolves to the account's principal.
    - Without the link step, it resolves to B's own pair.
Out of scope: merging two accounts, linking from the sign-in callback or any hook, and a configurable freshness window.

## S48 — A person can remove a sign-in method from their account, but never the last one
**Status:** queued
Status: todo

Delivers: As an account holder, I can see which sign-ins reach my account and remove one I no longer use. I cannot
lock myself out by removing the last.
Touches:
- `src/SubZeroDev.Platform.Identity`: `IAccountApi.UnlinkAsync`, and the store's account-row lock.
- `tests/SubZeroDev.Platform.Tests/AccountTests.cs`.
- `design/state/invariants/I-I19.md`.
Depends on: 47
Acceptance:
  - S48.1 X has (A, `alice`) linked first and (B, `alice-b`) second. `GetCurrentAccountAsync` under either token returns both, in that order.
  - S48.2 `UnlinkAsync((B, alice-b))` under A's token returns X with only (A, `alice`). The next request with B's token resolves to (B, `alice-b`). One `Required` audit row, `platform.identity.identity-unlinked`, is written.
  - S48.3 `UnlinkAsync((A, alice))` on X, now holding only that pair, answers `LastIdentity`, and X is unchanged.
  - S48.4 `UnlinkAsync` of a pair linked to account Y, and of a pair linked nowhere, both answer `IdentityNotLinked` with identical `Code` and `Detail`.
  - S48.5 Unlinking the pair the caller is presenting, while another remains, succeeds, and that token resolves to its raw pair on the next request.
  - S48.6 Two concurrent `UnlinkAsync` calls on X's two identities, on PostgreSQL and on SQLite: exactly one succeeds, the other answers `LastIdentity`, and X keeps one identity.
Out of scope: account deletion, re-authentication for unlink, and administrator unlink of another person's account.

## Not yet sliced

Every open issue that can be built in this repository now has a slice. These remain open and are not in
this plan, because none of them is work a slice here can do.

| Waiting on | Issues |
|---|---|
| An upstream release | [#224](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/224) `ModelContextProtocol` fix (csharp-sdk PR 1377 still unmerged; 2.2.0 is the latest release) |
| The repository owner, outside the code | [#81](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/81) an owner checklist, not a slice · [#25](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/25) stays open until the exposed credential is rotated; the rule itself is recorded |
| Another repository | [#97](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/97) Adventures adopting the packages as a hosted consumer; that work is planned in its own repository |
| No issue yet | WebSocket through the edge. S41 covers streamed HTTP responses only and adds no upgrade path |

---

## Landed

| Slice | Name | Issue | Criteria | Body complete at |
|---|---|---|---|---|
| **S19** | An untrusted credential is refused, not ignored | [#270](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/270), closed | S19.1–S19.8 | `c00c2af` |
| **S20** | An issuer with fixed keys is trusted from configuration alone | [#271](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/271), closed | S20.1–S20.12 | `bca2535` |
| **S21** | Keys are found through discovery and kept fresh | [#272](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/272), closed | S21.1–S21.10 | `60d4167` |
| **S22** | One issuer serves many customer tenants | [#273](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/273), closed | S22.1–S22.6 | `c00c2af` |
| **S23** | A vendor's settings arrive as a configuration source | [#274](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/274), closed | S23.1–S23.6 | `c00c2af` |

- **S1 — The two hosts and the enforced package boundary** — shipped:
  [#169](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/169) via
  [#195](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/195).
- **S2 — The total principal** — shipped:
  [#170](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/170) via
  [#196](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/196).
- **S3 — Audit: the contract, the writer and the log sink** — shipped:
  [#171](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/171) via
  [#197](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/197).
- **S4 — Authorization: names, providers and the evaluator** — shipped:
  [#172](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/172) via
  [#199](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/199).
- **S5 — Tenant resolution at the request boundary** — shipped:
  [#173](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/173) via
  [#200](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/200).
- **S6 — The shareable type and the audited cross-tenant read** — shipped:
  [#174](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/174) via
  [#201](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/201).
- **S7 — The entitlement seam and the Community baseline** — shipped:
  [#175](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/175) via
  [#203](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/203).
- **S8 — The fixed request order and the composition profile's startup rules** — shipped:
  [#176](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/176) via
  [#204](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/204),
  [#210](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/210),
  [#211](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/211),
  [#213](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/213) and
  [#215](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/215).
- **S9 — Identity: authenticating a principal at the transport** — shipped:
  [#177](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/177) via
  [#214](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/214).
- **S10 — Organizations** — shipped:
  [#178](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/178) via
  [#216](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/216).
- **S11 — Billing** — shipped:
  [#179](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/179) via
  [#217](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/217).
- **S12 — Licensing** — shipped:
  [#180](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/180) via
  [#218](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/218).
- **S13 — The durable audit store** — shipped:
  [#181](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/181) via
  [#219](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/219).
- **S14 — Mcp: the frozen catalogue and its startup checks** — shipped:
  [#182](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/182) via
  [#220](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/220).
- **S15 — Mcp: the transport, the connection principal and invocation** — shipped:
  [#183](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/183) via
  [#221](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/221).
- **S16 — The administration shell** — shipped:
  [#184](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/184) via
  [#223](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/223).
- **S17 — The proof: two databases, two hosts, contention and offline** — shipped:
  [#185](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/185) via
  [#226](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/226), [#227](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/227), [#228](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/228), [#231](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/231).
- **S18 — Packages, documentation and the corrected plan** — shipped:
  [#186](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/186) via
  [#233](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/233).
