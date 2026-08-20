# Slices — durable sessions (G2)

**Document status:** Slices. Derived from [`10-design.md`](10-design.md) and
[`20-contract.md`](20-contract.md). The contract is authoritative for every signature named below;
**no slice may introduce one that is absent from it.** Where a slice needs a signature the contract
does not carry, it stops and asks for a contract amendment rather than inventing one.

Each slice is vertical: it runs, and its acceptance criteria are observable from outside the code
that satisfies them. **Three repositories are in scope**, because G2's engine deliverable and
contract regeneration precede everything else — `SubZeroDev.GameEngine` (the conflict brand and the
widened reason code), `SubZeroDev.ServiceContract` (the regenerated package), and this one (the
store, the composition, the three proofs, the edge, the documentation).

**The ordering is the design's risk ordering.** The riskiest bet in the design is that per-request
composition over a compare-and-swap actually eliminates the lost update the brief exists to close —
and that the elimination is provable, not merely argued. [S6](#s6--contention-one-instance) is the
earliest point every prerequisite for that proof exists, and everything before it (S1–S5) is the
smallest path there: without the engine's conflict brand there is nothing to throw, without the
regenerated contract there is nothing to map a conflict onto, without the guarded store there is no
compare-and-swap, without per-request composition the store's cache would defeat the guard exactly as
*Module boundaries* describes, and without Dispatch's translation a conflict never reaches a caller
as anything but an outage. S7 repeats the proof across two processes, which is the shape the brief
actually asks for. Everything after S7 is the second and third proof, the edge, and the evidence a
fresh clone can re-run.

**S12 and S13 are the reconciliation's code side, and settle nothing.** Every criterion in them is a
divergence `/reconcile` found between the tree and these documents after S11, decided in the code's
direction on 2026-08-19 and staged in [`90-decisions.md`](90-decisions.md)'s `## Open` — the design
and the contract already read the way the finished slices must make the tree read. They split along
the seam that staging note itself draws, because the two halves are sized for one session each and
their union is not: **S12 is the durable process an operator starts, S13 is the guards the durable
slices declared and never enforced.**

## Decisions that must be taken before the slice that needs them starts

Neither the design nor the contract settles these, and none is a slice's to settle silently.

| Question | Needed before |
|---|---|
| Contract Unresolved 3 — the engine's ratification of `concurrent_modification`'s name and the brand's exact shape | **S1**. It is what the engine pull request settles; nothing downstream changes shape if the name or brand differs, but S1 is where the rework would land |
| Contract Unresolved 2 — the tenant non-goal and the save-lifecycle scope are conflicts with `00-brief.md` that this effort cannot discharge | **Not blocking any slice** — the schema, the keys and the save TTL this document builds toward already reflect the design's adjudicated answer, per `AGENTS.md`'s rule that a design document cannot resolve a brief conflict by disagreeing with it. Recorded here so `/slice` does not read the silence as settled, and carried into **S11**'s documentation obligation rather than left for a reader to rediscover |
| Contract Unresolved 4 — what refreshes a session's idle clock (design Open question 8) | **Not blocking any slice.** The design takes the accepted-writes-only reading and no signature above depends on the alternative; **S11** documents the consequence rather than any slice implementing a choice |

---

## S1 — Engine: the conflict brand and the widened reason code
**Status:** shipped

Delivers: for anyone embedding the game engine, two writes racing for the same session no longer look
identical to a database falling over — the engine can now say which one lost, distinctly from saying
the store failed.

Repository: **`SubZeroDev.GameEngine`**.

Touches:
- **`writeSession`'s `catch`** — gains one branch recognising a caught value's `name` as
  `SESSION_PERSISTENCE_CONFLICT`
- **`SessionStoreErrorCode`** — widened to nine members with `concurrent_modification`
- **`SESSION_PERSISTENCE_CONFLICT`, `SessionPersistenceConflict`** — the brand's declaration
- **The reason-code registry** — a `core.reason.*` message for the new member
- **The engine's release** — a published version carrying the widened union and the brand in its type
  declarations

Depends on: none.

Acceptance:
- **S1.1** `SessionStoreErrorCode` has nine members. A stub `SessionPersistence` whose `put` throws a
  value named `SESSION_PERSISTENCE_CONFLICT` causes `writeSession` to raise `SessionStoreError`
  carrying `concurrent_modification`.
- **S1.2** A stub `put` throwing a plain `Error`, or an `Error` whose `name` is something else, still
  causes `writeSession` to raise `storage_failure` — unchanged from today, asserted in the same suite
  as S1.1.
- **S1.3** `concurrent_modification` is a registered `ReasonCode` with a shipped `core.reason.*`
  message, checkable against the engine's own reason-code registry.
- **S1.4** `getSession`, `getSave` and `writeSave` gain no branch: a `SessionPersistenceConflict`-named
  value thrown from a stub implementing any of the three becomes `storage_failure`, exactly like any
  other thrown value.
- **S1.5** A released engine version's published type declarations export the nine-member
  `SessionStoreErrorCode` and the `SESSION_PERSISTENCE_CONFLICT` / `SessionPersistenceConflict`
  declarations, resolvable by a consumer without reaching into the engine's internals.

Out of scope: the workload's adapter that actually throws the branded value (S3); anything about
compare-and-swap itself, which is entirely a host-side mechanism the engine never sees.

---

## S2 — Contract: the regenerated package
**Status:** shipped · [#129](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/129)

Delivers: for anyone building against the hosted contract, the codes a durable deployment can return —
a stale write, a session that quietly expired, a save that quietly expired — become part of the
published package, not something only this workload's source code knows about.

Repository: **`SubZeroDev.ServiceContract`**, with the vendored tarball updated in **this one**.

Touches:
- **The status mapping** — three new entries: `concurrent_modification` → `409`, `session_expired` →
  `404`, `save_expired` → `404`
- **`TransportErrorCode`** — widened to six members with `session_expired` and `save_expired`
- **The error-coverage gate** — asserted against S1's nine-member `SessionStoreErrorCode`
- **The published package** — a minor version bump
- **`workloads/game-service/vendor/`** — the new tarball; `package.json`'s `file:` dependency moved to
  it

Depends on: S1.

Acceptance:
- **S2.1** Generation against S1's engine release and the unchanged authored row set succeeds and
  emits a status mapping with nine `EngineErrorCode` entries and six `TransportErrorCode` entries.
- **S2.2** Deleting the status-mapping entry for `concurrent_modification` fails generation with
  `ErrorCodeUncovered` naming the code, and writes no artifact.
- **S2.3** The emitted package's `engineVersion` equals the version resolved from S1's release.
- **S2.4** The published version is a minor bump over the last published G1 version.
- **S2.5** `workloads/game-service/vendor/` contains the new tarball and `package.json`'s contract
  dependency resolves to it; no file in the repository still references the previous tarball's path.

Out of scope: switching to registry resolution — Platform issue #81 is open and blocks it, and the
switch is a one-line change once it closes; any new authored row, since G2 adds no operation to the
table.

---

## S3 — Migrations and the guarded store
**Status:** shipped · [#133](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/133)

Delivers: an operator can point the workload at a real PostgreSQL database and get a session back
byte for byte after it is stored — and when two writers race to update the same session, exactly one
of their writes counts, provably, in the database itself.

**This is the design's central mechanism and the largest single slice.** Without a schema there is
nothing to guard; without the guard nothing else in G2 has anything to prove.

Repository: **this one**, under `workloads/game-service/`.

Touches:
- **Migrations** — the four tables from *Persisted schemas*, `node-pg-migrate`'s advisory lock,
  `migrateToHead`
- **Store** — `DurableStore`, `openDurableStore`, the guarded `update` with its `version` and
  `expires_at` predicates, the zero-rows re-read classification (`applied` / `conflict` / `expired`),
  the branded `SessionPersistenceConflict` throw, `LifecycleProbe`, `ReadVersionMap`,
  `TenantId`/`EngineInstant`/`DatabaseInstant`/`SessionRowVersion`/`SchemaName`
- **The compose file** — provisioning PostgreSQL only, pinned `UTF8` encoding and explicit initdb
  locale

Depends on: S1, S2.

Acceptance:
- **S3.1** Against a freshly migrated schema, `sessions.put` for a new session id inserts a row with
  `version = 1`; a second `sessions.put` for the same id, supplying the read version, updates the row
  and leaves `version = 2`.
- **S3.2** A `sessions.put` whose supplied read-version does not match the row's current version
  affects zero rows; the adapter re-reads and throws `SessionPersistenceConflict`.
- **S3.3** A `sessions.put` whose supplied read-version matches but whose row's `expires_at` has
  already passed (seeded directly) affects zero rows and is classified `expired`, not `conflict`.
- **S3.4** A `sessions.put` for an id absent from the store, any read-version supplied, is classified
  `conflict`.
- **S3.5** A row whose `expires_at` has passed is returned as not found by `sessions.get` and
  `saves.get`, independent of whether a sweep has ever run.
- **S3.6** The re-read itself failing (the connection is cut between the zero-rows write and the
  re-read) is classified `conflict`, never surfaced as a `StoreError`.
- **S3.7** A session's `blob` round-trips byte for byte, seeded with a payload containing duplicate
  object keys and a number requiring exact round-trip (e.g. `1.10`) — values `jsonb` would normalise
  and `text` will not.
- **S3.8** `engine_created_at` and `engine_updated_at` are stored and returned as the exact string
  supplied, never reformatted; `row_created_at`, `row_updated_at` and `expires_at` are `timestamptz`.
- **S3.9** Every row any write in the suite produces carries `tenant_id` equal to the implicit
  constant, and every statement the store issues includes it — asserted by inspecting the store's own
  generated SQL, not by inference from results.
- **S3.10** A second `saves.put` for an existing `save_id` succeeds as an upsert; `expires_at` and
  `engine_version` are recomputed from the second write, not carried over from the first.
- **S3.11** The generated `save` table has no `version` column.
- **S3.12** A primary-key collision on a `createSession` or `loadGame` insert (a duplicate id forced
  by the test) returns `StoreError.IdCollision`, mapped by the caller to `storage_failure`, never to
  the conflict classification.
- **S3.13** The store asserts `read committed` on connect. Started against a connection reporting
  `serializable`, `openDurableStore` returns `StoreError.IsolationLevelUnsupported` naming the
  isolation level found, and issues no statement.
- **S3.14** A dependency-direction test fails if Store's module graph imports anything from the
  engine's runtime entry point; it passes today, importing only the engine's type declarations.
- **S3.15** The committed compose file brings up PostgreSQL reachable on loopback, with server
  encoding `UTF8` and an explicit initdb locale, and starts no other process.
- **S3.16** `migrateToHead` run twice in sequence against one schema is a no-op the second time.
  Invoked concurrently by two callers against one fresh schema, one applies every migration and the
  other waits and finds the schema at head; no partial table is left by either.
- **S3.17** `sessions.get`'s returned `version`, and the value `ReadVersionMap.record` stores for it,
  are both of type `bigint` — asserted by `typeof`, not by equality, since an unconfigured driver
  would return a `string` that compares equal on a round trip.

Out of scope: composing the store per request (S4); the sweep (S4); anything Dispatch does with the
codes this slice produces (S5); the profile store's set-union merge and its cross-implementation
conformance (S9) — this slice's profile columns exist and round-trip but are not yet exercised beyond
that.

---

## S4 — Composition: per-request, and the sweep
**Status:** shipped · [#134](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/134)

Delivers: whether the workload is running as a quick in-memory demo or pointed at the durable store,
restarting or scaling out no longer means one instance's write is invisible to another — every request
that touches a session gets a database-backed answer, never a locally cached one, and storage that has
grown past its retention window is reclaimed automatically.

Repository: **this one**.

Touches:
- **`StoreProvider`, `compose`** — the durable configuration composes a fresh session layer per
  request; the in-memory configuration keeps G1's single long-lived one
- **`StorageProfile`, `DurableStoreConfiguration`, `LifecycleBounds`, `StoreConnection`**
- **`ProbeSurface.readiness`** — now asynchronous, evaluating the store on each call
- **`CompositionError.StorageConfigurationInvalid`**
- **The sweep timer** — owned by Composition, calling `sweepOnce`, never overlapping itself

Depends on: S3.

Acceptance:
- **S4.1** With `storage.kind = "durable"`, `stores.forRequest()` called twice returns two `SessionStore`
  instances sharing no cache: a write made through the first is invisible to the second's `getSession`
  until the second reads from the store directly.
- **S4.2** With `storage.kind = "in-memory"`, `stores.forRequest()` called twice returns G1's same
  long-lived session layer both times — a write on one call is visible to the other with no store
  access.
- **S4.3** `compose()` against an unreachable durable store returns successfully; `readiness()` reports
  unhealthy naming the store check; `compose()` never throws.
- **S4.4** `readiness()` called twice, with the store made unreachable in between, reports healthy
  then unhealthy — proving each call evaluates the store rather than replaying a memoized startup
  outcome.
- **S4.5** `liveness()` never calls into the store, asserted with a store stub that throws on any
  invocation; `liveness()` still reports healthy.
- **S4.6** `compose()` with a durable profile whose `retentionHorizonSeconds` does not exceed the
  configured forward timeout returns `CompositionError.StorageConfigurationInvalid` naming the
  setting, before any connection is attempted.
- **S4.7** With a short sweep interval, seeding one row past the retention horizon and one merely
  expired but within it: after two sweep intervals elapse, the past-horizon row is gone and the
  merely-expired row remains.
- **S4.8** The sweep never removes a `profile` or `profile_achievement` row, even one seeded with a
  `row_created_at` far in the past.
- **S4.9** A sweep tick that fails (a forced statement error) is caught and logged; the next tick still
  runs on schedule, and the failure never escapes as an unhandled rejection.
- **S4.10** With one tick artificially slowed past the next tick's scheduled time, the next tick does
  not start until the first completes — asserted by a counter that never exceeds one concurrent
  invocation.
- **S4.11** `close()` on a composed durable workload stops the sweep timer and closes the pool; no
  timer handle remains afterward.

Out of scope: what Dispatch does with a conflict or an expiry classification (S5); the two-instance
proof (S7); the guarded SQL itself (S3, already built).

---

## S5 — Dispatch: translating the durable outcomes
**Status:** shipped · [#136](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/136) (its commit landed in S6's pull request)

Delivers: a caller who submits an action against a session someone else just changed gets told plainly
that their information is stale, distinctly from being told the database is unreachable — and a caller
asking about a session that has quietly expired is told that, rather than being told it never existed.

Repository: **this one**.

Touches:
- **Dispatch's error translation** — `concurrent_modification` → `409`; `storage_failure` → `503`,
  now genuinely reachable
- **The lifecycle-probe consultation** — on `unknown_session` / `unknown_save`, synthesising
  `session_expired` / `save_expired`
- **The no-op probe** — Dispatch carries no branch on which store was composed

Depends on: S4.

Acceptance:
- **S5.1** A stub store whose `sessions.put` always throws `SessionPersistenceConflict` causes a
  `submit-action` dispatch to return `concurrent_modification`, mapped to `409`.
- **S5.2** The same dispatch against a stub whose `sessions.put` always throws an ordinary error
  returns `storage_failure`, mapped to `503` — the two are distinguished in the same test suite.
- **S5.3** `unknown_session`, with the lifecycle probe reporting `expired` for that id, becomes
  `session_expired` at `404`; with the probe reporting `absent`, the engine's `unknown_session` passes
  through verbatim at `404` — same status, different code, both asserted.
- **S5.4** The same pairing for `unknown_save` / `save_expired`.
- **S5.5** A lifecycle-probe call that itself fails causes `unknown_session` or `unknown_save` to pass
  through verbatim — never `storage_failure`, never a different status.
- **S5.6** `createDispatcher` is identical for both storage profiles; with the in-memory profile's
  no-op probe (every id classified `absent`), `unknown_session`/`unknown_save` pass through verbatim,
  and no branch in Dispatch's source references `storage.kind`.
- **S5.7** No outcome is retried automatically: a stub store that fails once then would succeed is
  called exactly once per inbound request, for both a conflict and a `storage_failure`.
- **S5.8** A rejected action (the engine's own unsuccessful result) still returns `200`, distinguishable
  in the same suite from a conflict's `409`.

Out of scope: producing the conflict or expiry conditions from real contention (S6, S7); the HTTP/MCP
wire plumbing for these codes, which G1 already built and this slice reuses unchanged.

---

## S6 — Contention, one instance
**Status:** shipped · [#136](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/136)

Delivers: an operator running one instance of the service against the durable store can watch, for
themselves, two players' simultaneous actions against one session stop silently overwriting each
other — exactly one succeeds, and the other is told plainly to re-read and decide.

Repository: **this one**.

Touches:
- **`readWritePauseMs`** — the perturbation seam
- **The one-instance contention test**, and the four red-gate perturbations *Control flow* 3
  describes

Depends on: S5.

Acceptance:
- **S6.1** Two `submit-action` requests for one session, dispatched concurrently to a single process
  against the durable store, produce exactly one `200` and one `409` carrying
  `concurrent_modification` — arranged deterministically via `readWritePauseMs` so both requests read
  before either writes, not merely likely to.
- **S6.2** With `readWritePauseMs` at its default of `0`, a test asserts it is inert: no observable
  delay is inserted between a session read and its write.
- **S6.3** Perturbation: with the guarded update's `version` predicate removed, the same S6.1 scenario
  produces two `200`s — proving the assertion can fail.
- **S6.4** Perturbation: a direct adapter call writing a session with an artificially stale
  read-version is rejected with the conflict classification.
- **S6.5** Perturbation: the S6.1 scenario run against an unreachable store produces two `503`s, never
  a `409`.
- **S6.6** After the loser's `409`, the winner's resulting session state shows no trace of the loser's
  submitted action — inspected directly, not inferred from the status codes alone.

Out of scope: the two-instance variant (S7); anything about how the pause is surfaced to other proofs.

---

## S7 — Contention, two instances, and the harness the README runs
**Status:** shipped · [#137](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/137), [#141](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/141)

Delivers: an operator can run two copies of the service against one shared database — the way a real
deployment scales out — and see the same guarantee hold across processes as within one, and the
repository's own documentation walks them through doing it themselves.

Repository: **this one**.

Touches:
- **`spawnInstances`, `WorkloadInstance`, `TwoInstanceOptions`**
- **The `game-service` CI job** — bringing up the compose file, then running the two-instance test
- **The README** — the documented two-instance command

Depends on: S6.

Acceptance:
- **S7.1** `spawnInstances` against one connection string and one schema returns two
  `WorkloadInstance`s, each reporting ready on its own base address, both bound to loopback only.
- **S7.2** A session created through one instance is readable through the other via a query operation.
- **S7.3** Two `submit-action` requests, one sent to each instance and arranged via the paired
  `readWritePauseMs` so both read before either writes, produce exactly one `200` and one `409`
  carrying `concurrent_modification`.
- **S7.4** No request, response, or stored row in the S7.3 run names which instance served it.
- **S7.5** The `game-service` CI job's steps run the compose file and then the two-instance test in the
  same sequence the README documents — the job's step definitions match the documented commands
  verbatim.
- **S7.6** The README names a single command that runs two instances against one store and replays the
  contention proof; running that exact command against a fresh clone reproduces S7.3.
- **S7.7** `shutdown()` on both instances exits cleanly; an instance that does not exit within its
  bound fails the harness with `InstanceShutdownFailed` naming which one.

Out of scope: load balancing, session affinity, or anything routed through the edge — the proof
addresses the two workload instances directly (the edge's own change is S10).

---

## S8 — The byte-identity proof, durably
**Status:** shipped · [#140](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/140)

Delivers: the proof G1 established — that a game played over the wire is the identical game, byte for
byte, as the same game played in-process — now holds when the game is stored in a real database
instead of memory, so persistence is shown to have changed nothing about what was recorded rather than
assumed to.

Repository: **this one**.

Touches:
- **`createRunSchema`, `RunSchema`, `runDurableReplay`, `assertNonEmpty`**
- **The per-run schema isolation**, the production lifecycle defaults during the replay, the
  `collate "C"` dump ordering

Depends on: S5.

Acceptance:
- **S8.1** `createRunSchema` creates a fresh, empty schema, migrates it to head, and its `drop()`
  removes it — verified by connecting afterward and finding no G2 tables under that schema name.
- **S8.2** The G1 replay fixture, run once against the in-memory store and once against a freshly
  created durable schema, produces two ordered blob sets that are byte-for-byte identical to each
  other.
- **S8.3** Both runs' response transcripts are byte-for-byte identical to the committed golden
  transcript.
- **S8.4** The durable run uses the production lifecycle defaults; the ten-step replay completes with
  no step observing `session_expired` or `save_expired`.
- **S8.5** `assertNonEmpty`, called before the byte comparison, fails and names the expected versus
  actual counts when pointed at a dump seeded from an empty schema — comparison A cannot silently pass
  on two empty sets.
- **S8.6** The durable replay run twice in sequence, each against its own freshly created and dropped
  schema, both succeed with no primary-key collision.
- **S8.7** The G1 in-memory replay is still in the suite and still green in the same CI run that adds
  the durable one.
- **S8.8** Seeding two rows whose ids would order differently under a locale-aware collation than
  under byte order, the dump's ordering matches byte order (`collate "C"`), not locale order.
- **S8.9** The two consecutive runs in S8.6 use two different schema names while every row in both
  carries the same `tenant_id` constant — the tenant column is not used for run isolation anywhere in
  the harness.

Out of scope: the two-instance harness (S7, unrelated to byte identity); the port-conformance suite
(S9).

---

## S9 — Port conformance, both implementations
**Status:** shipped · [#142](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/142)

Delivers: whoever built the durable store — this effort — gets a definitive answer to whether it
actually behaves the way the engine expects any `SessionPersistence` or `ProfileStore` to behave,
checked against the identical assertions the engine's own in-memory implementation has to pass, not
checked against itself alone.

Repository: **this one**.

Touches:
- **`runPortConformance`, `ConformanceTarget`, `seedCorruptProfile`, `seedProfileWriteFailure`**
- **The reference target** — the workload's own map-backed `SessionPersistence` and the engine's
  `createInMemoryProfileStore`

Depends on: S3.

Acceptance:
- **S9.1** `runPortConformance`, run against the in-memory target and against the durable target,
  asserts the identical behaviour for `sessions.get/put` and `saves.get/put`, and both targets pass.
- **S9.2** `profiles.load` against a target seeded with `seedCorruptProfile(profileId)` returns
  `profile_corrupt` and an empty achievement set on both targets.
- **S9.3** `profiles.load` against an unseeded `profileId` returns `profile_missing` and an empty
  achievement set on both targets.
- **S9.4** `profiles.save` against a target seeded with `seedProfileWriteFailure(profileId)` returns
  `ok: false` with `profile_write_failed`, and a session write issued in the same operation before the
  profile write remains committed afterward, on both targets.
- **S9.5** Two `profiles.save` calls against one profile, each adding a different achievement id, both
  land — the profile's achievement set afterward contains both — asserted concurrently on the durable
  target and sequentially on the in-memory one.
- **S9.6** The declared divergence is asserted directly: a `profiles.save` call omitting a previously
  stored achievement removes nothing from the durable target's set, and does remove it from the
  in-memory target's — both observed in the same run.
- **S9.7** `CallerPropertyViolated`, naming the method and the observed payload, is raised if the
  engine's actual `upsertAchievements` call sequence for a two-achievement campaign (driven through
  the engine, not the suite's own fixture) is ever observed to carry less than the loaded set plus
  additions.
- **S9.8** A blob written through `sessions.put` and read back through `sessions.get` on the durable
  target is byte-identical to what was written, and no host column appears in the returned
  `StoredSessionRecord`'s own key set.
- **S9.9** A target unable to honour `seedCorruptProfile` or `seedProfileWriteFailure` fails the suite
  with `SeamUnavailable` naming the method, rather than silently skipping the assertion.

Out of scope: exercising these ports through the replay fixture or the wire — the golden transcript
stays untouched; `sessions.put`'s conflict/expiry classification, which S3 and S6 already cover.

---

## S10 — The edge asks the right question
**Status:** shipped · [#143](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/143)

Delivers: an operator running the .NET edge in front of a durable-backed service gets an honest "not
ready" the moment the database becomes unreachable, instead of the edge reporting itself healthy while
every request behind it would fail.

Repository: **this one**, under `workloads/` beside the Node workload.

Touches:
- **`GameEdgeOptions.ReadinessTimeout`** (renamed from `LivenessTimeout`)
- **`IGameWorkloadProbe.ProbeReadinessAsync`**
- **`GameWorkloadReadinessCheck`** — now probing `/readiness` instead of `/liveness`
- **`appsettings.json`** — the corresponding key rename

Depends on: S4.

Acceptance:
- **S10.1** With the workload's readiness endpoint reporting unhealthy (store down) but its liveness
  endpoint reporting healthy, the edge's readiness check reports unhealthy.
- **S10.2** With the workload fully healthy, the edge's readiness check reports healthy.
- **S10.3** `GameEdgeOptions` has no member named `LivenessTimeout`; `ReadinessTimeout` governs the
  readiness probe's timeout, and the `appsettings.json` key is renamed in the same commit with no
  remaining reference to the old key anywhere in the repository.
- **S10.4** The readiness check still reports `Kind = Readiness`, `Criticality = Required`,
  `TouchesExternalDependency = true`; registering it as a liveness check still aborts startup with
  `ExternalDependencyInLivenessCheck`.
- **S10.5** After the readiness check runs any number of times against a healthy workload, the
  workload's session count is still zero.
- **S10.6** G1's edge-forwarding acceptance criteria (`g1/30-slices.md` S7.2–S7.6) still pass
  unmodified.

Out of scope: anything about the two-instance proof, which addresses the workload instances directly
and never through the edge.

---

## S11 — A fresh clone can prove all of it
**Status:** shipped · [#144](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/144)

Delivers: someone arriving at the repository with nothing but a clone can bring up the database, run
one instance or two, replay every proof this effort makes, and roll the schema forward — by following
what is written, with CI proving the instructions still work rather than a private script standing in
for them.

Repository: **this one**.

Touches:
- **`workloads/game-service/` documentation** — provisioning, single-instance and two-instance runs,
  the migration command, the durable replay, the conformance suite
- **CI** — running the documented commands
- **Handover notes** — the two brief conflicts, the engine-ratification dependency, the
  idle-clock-on-write-only behaviour

Depends on: S7, S8, S9, S10.

Acceptance:
- **S11.1** The documentation states, as runnable commands: bringing up the store via compose, bringing
  the schema to head, starting one instance against it, starting two instances against one store,
  running the durable replay, and running the port-conformance suite.
- **S11.2** CI executes those documented commands rather than a private script; the job fails if a
  documented command does not exist or does not run.
- **S11.3** Following the documented migration command against a freshly provisioned store brings the
  schema to the same head CI's own run reaches, compared by the migrations table's applied set.
- **S11.4** The documentation states, as open items for whoever next edits `00-brief.md`, the two brief
  conflicts this effort could not resolve: the tenant column present in every primary key and query
  despite the brief's non-goal wording, and the save table carrying a 365-day lifecycle despite the
  brief naming only sessions in its criteria.
- **S11.5** The documentation states that every proof in this effort depends on the engine having
  ratified `concurrent_modification` under the name and brand this contract assumes, and names the
  cost if the engine ratifies something else: rework in S1 and this contract, no shape change
  elsewhere.
- **S11.6** The documentation states that a session's idle TTL advances only on an accepted write, not
  on every read, so a session read continuously for its whole TTL still expires.
- **S11.7** G1's fresh-clone job (`g1/30-slices.md` S9) still passes unmodified in the same CI run.

Out of scope: a human-facing guide (`/make-human-docs`'s output); resolving either brief conflict,
which is `00-brief.md`'s author's decision and not a slice's; deployment machinery beyond the
hand-started and compose-started processes this effort already proves.

---

## S12 — The durable service an operator can actually start
**Status:** in progress

Delivers: an operator can start the service against their own PostgreSQL database by following the
README, instead of only ever getting the in-memory demo — the process is told where the database is,
brings the schema up to date itself before it serves a single request, keeps stored data for the
period the service documents rather than one chosen to keep a test comfortable, and CI proves that
documented start still works.

Repository: **this one**, under `workloads/game-service/`.

Touches:
- **The process entry point** — the durable storage profile, selected and read from the environment
  the same way the replay profile already is
- **`compose`'s durable branch** — `migrateToHead` before the first connect, under the startup
  backoff, so `MigrationError`'s three variants gain the startup caller they have never had
- **`DEFAULT_LIFECYCLE_BOUNDS`**, and the two migration constants `scripts/migrate.ts` currently
  reaches into the proof harness for
- **The README's start section and the `game-service` CI job** — the documented single-instance
  durable start

Depends on: S11.

Acceptance:
- **S12.1** Started with the documented durable environment set to a reachable database, the process
  serves a game operation and the session it created is present in that database afterwards. Started
  with none of that environment set, it composes the in-memory profile exactly as today, reaching no
  database.
- **S12.2** Started with the durable flag set but a companion variable missing, the process fails
  loudly at startup naming what is missing, and never degrades silently to in-memory.
- **S12.3** Started against a schema that has never been migrated, the process brings it to head
  itself before reporting ready, then serves — no separate migration command is run first.
- **S12.4** Two processes started concurrently against one never-migrated schema both reach ready:
  one applies the migrations, the other waits on the advisory lock and finds the schema at head, and
  neither leaves a partial table. This is *Concurrent startup migrations*' first code path.
- **S12.5** Started against an unreachable database, the process binds its listener, reports live,
  reports not ready, and reports ready without a restart once the database becomes reachable — the
  migration run and the first connect both retried under the startup backoff, and `compose()` never
  throws.
- **S12.6** A migration whose SQL fails leaves the process up and not ready, naming the migration in
  its readiness body, and serving no operation — `MigrationError.MigrationFailed`, observed at
  startup rather than as a thrown exception or a non-zero exit.
- **S12.7** With the migration runner's advisory lock held by another connection past the runner's
  bound, startup reports not ready naming a lock timeout and retries; released, the next attempt
  reaches head and the process reports ready — `MigrationError.LockTimeout`, at startup.
- **S12.8** `scripts/migrate.ts`'s transitive module graph names nothing under `tests/` or the proof
  harness, asserted by the same import-graph check the other dependency-direction assertions use.
  The design's dependency graph ends *nothing depends on a harness*, and this is the one edge that
  contradicted it.
- **S12.9** The three default lifecycle bounds are the production values — 30 days, 365 days, 30
  days — asserted by a test naming each. The retention horizon no longer equals the save TTL.
- **S12.10** Nothing in the tree describes those defaults as non-production: the constant is called
  the production defaults wherever it is described, and invariant 82's assertion — the durable
  replay runs at the production defaults — names that same constant.
- **S12.11** Following the README's durable start section from a fresh clone reaches a serving
  instance whose readiness reports healthy; CI runs that same documented command as a step and fails
  if it does not exist or does not produce a serving instance, on S11.2's standing terms.
- **S12.12** S7's two-instance proof, S8's durable replay and S11's fresh-clone job all still pass
  unmodified in the same CI run — the retention-horizon change moves no proof's outcome.

Out of scope: collapsing the harness's own process entry point into the operator's — it exists for
its stdin shutdown and its fixed harness bounds (S8.4), and merging them is a separate question; any
tuning of pool size, connect timeout or backoff interval, which the brief's performance non-goal
forbids; the guards S13 carries.

---

## S13 — The guards the durable slices declared and never enforced
**Status:** queued

Delivers: whoever runs this service, and whoever picks up the authorization work that comes next,
gets five promises the design made turned into enforced behaviour — a database row that is not the
shape the store expects is reported as corruption instead of passed on unchecked, the background
cleanup can no longer hold a connection open without bound, the one store method nothing ever called
is finally exercised, and the two boundaries the design draws are asserted rather than merely true
by luck.

Repository: **this one**, under `workloads/game-service/`.

Touches:
- **Store's row mappers** — column validation and `StoreError.RowUndeserializable`, which is declared
  and constructed nowhere
- **The sweep's statement** — run under `LifecycleBounds.sweepStatementTimeoutMs`, which is declared,
  defaulted and read by nothing
- **`runPortConformance`** — `saves.delete`, the seventh method of invariant 89
- **The dependency-direction test** — Store as a second forbidden target, for both surfaces
- **The lifecycle probe's composition point** — moved behind the seam `SessionPersistence` and
  `ProfileStore` are composed at

Depends on: S11. Nothing in S12 is a prerequisite; the numbering is the order they should land in,
because both edit composition.

Acceptance:
- **S13.1** With a `session` row seeded so a column's value cannot satisfy the type its mapper
  declares — the column's SQL type widened and a value written that the declared type cannot hold —
  `sessions.get` fails with `StoreError.RowUndeserializable` naming the column, and a request
  reaching it answers `storage_failure` at `503` — not `internal_failure` at `500`, which the read
  ports have no channel to reach (`90-decisions.md`, 2026-08-20, "`RowUndeserializable` answers
  503"). The criterion is the thrown `cause` and the column it names.
- **S13.2** The same seeding against a `save` row is classified the same way, over **every** column
  `saveSelectStatement` returns rather than only those a `StoredSaveRecord` field is mapped from; and
  the guarded write path is unaffected: a `RowUndeserializable` read is never reclassified as
  `conflict`.
- **S13.3** The same seeding against a `profile` or `profile_achievement` row yields `profile_corrupt`
  and an empty achievement set on a `200` — not `RowUndeserializable` — because `ProfileStore.load`
  has no error channel. Both answers are asserted in one suite, so the two are told apart rather
  than assumed distinct.
- **S13.4** The sweep's `delete` runs under the configured statement timeout: with the timeout low
  and the target rows locked by another connection, the tick fails with `StatementFailed`, is logged,
  releases its connection, and the next tick runs on schedule — asserted with the pool sized to one,
  so a serving request afterwards could not succeed if the timed-out sweep still held it.
- **S13.5** The same sweep over the same seeded rows succeeds when the timeout is configured
  generously and fails when it is not — the setting is read on the sweep path, proven by two
  configured values rather than by inspection.
- **S13.6** `runPortConformance` calls `saves.delete` on both targets: a save put and then deleted is
  absent from `saves.get` on both, and deleting an absent save id is a no-op that fails neither. A
  target whose `saves.delete` diverges fails the suite with `MethodDiverged` naming the method.
- **S13.7** The dependency-direction check names Store as a second forbidden target for both
  surfaces' module graphs. Adding an import of Store to either surface's graph fails the test, and
  removing it passes — the perturbation is asserted, not the passing state alone.
- **S13.8** A decorator applied once at the seam `SessionPersistence` and `ProfileStore` are composed
  at is observed on the lifecycle probe's calls too, asserted with a counting decorator that records
  every call it intercepts. Invariant 74 becomes structural rather than described.
- **S13.9** The same holds for the in-memory configuration's no-op probe, so invariant 74 is not a
  durable-only property; S5.6 still passes unmodified and Dispatch still carries no branch on which
  store was composed.
- **S13.10** S6's and S7's contention proofs, S8's byte-identity proof and S9's port-conformance
  criteria all still pass unmodified in the same CI run.

Out of scope: the durable process entry point and startup migrations (S12); any store failure
classification beyond the `RowUndeserializable` the contract already declares — a mapper that finds a
row unreadable classifies it, it does not gain a repair path; G3's authorization decorator itself,
which this slice only makes the seam able to carry.
