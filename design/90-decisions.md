# Decision log — G2 effort

Append-only. Newest at the top. The rejected alternatives are the point — without them, every future
session relitigates the same choice.

Completed efforts keep their logs with their design sets:
[`g1/90-decisions.md`](g1/90-decisions.md), [`d3/90-decisions.md`](d3/90-decisions.md).

**This log is effort-local.** `AGENTS.md`, *Decision logging*, decides what belongs here and what
belongs in `docs/docs/adr/`.

### 2026-08-19 — Startup migrations: the durable branch brings its own schema to head, and a repeated migration failure backs its retry off

Context: nothing ran migrations at startup — an operator had to run `npm run migrate` (or the
equivalent) before the durable process would find a usable schema, and nothing said so loudly if
they forgot. `compose()`'s durable branch now calls `migrateToHead` before its first connection
attempt, and again on every reconnect retry, so a never-migrated or behind-head schema still reaches
ready without a separate command. `readiness()` gained a `detail` string (see the next entry) naming
which of "still migrating", "the lock is held past its bound", or "a migration's SQL failed" is
current.
Chosen: migration and connection retries share one backoff loop under `DURABLE_RECONNECT_INTERVAL_MS`
(5s), the same interval a bare connection failure already retried under, with one addition —
consecutive migration failures back that loop off exponentially (5s, 10s, 20s, ... capped at 60s),
reset the moment a migration succeeds. `node-pg-migrate`'s advisory lock is one id for the whole
database, not scoped per schema (`migrations.ts`); a schema whose migration keeps failing must not
keep re-requesting that lock every 5s forever, because a healthy, unrelated schema's own migration on
the same database can queue behind it and time out. Once a schema has migrated successfully for a
given `compose()` call, later retries (a store-connect failure, never a migration one) skip the
migration runner entirely and reconnect directly — a schema already at head does not pay a second
full migration invocation, including its own connection and lock acquisition, on every retry for the
life of the process.
Rejected: a flat, unbacked-off retry for migration failures too (the initial S12 shape). Simplest, but
leaves a permanently broken migration in one schema free to contend forever for a lock every other
schema on the same database instance also needs, exactly the collateral `LockTimeout`s this repository's
own test suite hit running the two concurrently (see "unit-level lock-timeout classification" below).
Rejected: capping retries at a fixed count and giving up. `compose()`'s whole shape is "come up not
ready and keep trying," not "abort" — a database that recovers after the cap would need a process
restart to notice.
Reversibility: cheap — the backoff schedule and the skip-once-migrated guard are both local to
`attemptConnect`.

### 2026-08-19 — `ProbeResult` gains an optional `detail`, amending G1's declaration

Context: `readiness()`'s `unhealthy` result had no way to say *why* — G1's `ProbeResult` declared
only `status`. S12.6/S12.7 need the durable branch's readiness to distinguish a still-running
migration from a lock held past its bound from a failed migration (naming which one) from a plainly
unreachable store, for both a caller and a test to tell apart.
Chosen: `ProbeResult.detail?: string`, present only on an unhealthy result, absent everywhere else —
including every G1-era caller, which never populated it and is unaffected. `design/20-contract.md`
now declares this explicitly rather than leaving `compose()` to construct an object outside the
declared shape.
Rejected: leaving `detail` undeclared and reaching it by casting the object `readiness()` returns.
That was the S12 shape before this entry — a type-safe caller can't discover the field exists, and a
future rename or typo compiles without error. Declaring it costs one optional field.
Rejected: a structured, discriminated-union `detail` (mirroring `MigrationError`'s own variants)
instead of a string. Considered for the type-safety parity with the rest of this codebase's
`Outcome<T,E>` conventions; deferred because `readiness()` is a diagnostic surface a human or a
dashboard reads, not a machine-branched one, and no caller in this workload branches on it.
Reversibility: cheap to widen further later; the string form does not block a future structured
version, since nothing yet parses it.

### 2026-08-19 — The retention horizon default no longer equals the save TTL

Context: `DEFAULT_LIFECYCLE_BOUNDS.retentionHorizonSeconds` was 365 days, the same value as
`saveTtlSeconds` — the one set of bounds every `DurableStoreConfiguration` in this codebase is built
from, proof and test runs included. Retention is how long a swept-past row's id stops being
distinguishable from one that never existed, not how long a save is kept; the two numbers agreeing
was never a stated requirement, just an artifact of both starting from the same "generous and out of
the way" value.
Chosen: 30 days — the production default the contract intends — comfortably clear of
`ASSUMED_FORWARD_TIMEOUT_SECONDS` (60s) for the `retentionHorizonSeconds` check `compose()` performs,
and no longer equal to `saveTtlSeconds`.
Rejected: leaving it at 365 days. Nothing in `design/20-contract.md` requires retention to match the
save TTL, and the old value understated how quickly this workload actually needs to stop
distinguishing a swept row's id from one that never existed.
Reversibility: cheap — one constant, asserted by name in `tests/lifecycle-bounds.test.ts`.

### 2026-08-19 — `DEFAULT_STORE_POOL_SIZE`/`DEFAULT_STORE_CONNECT_TIMEOUT_MS` move from `harness.ts` to `types.ts`

Context: `scripts/migrate.ts` — the fresh-clone migration entry point an operator's own shell reaches
— imported `RUN_SCHEMA_POOL_SIZE`/`RUN_SCHEMA_CONNECT_TIMEOUT_MS` from `harness.ts`, the proof
harness. The design's dependency graph ends "nothing depends on a harness" (`design/10-design.md`);
an operator-run script transitively importing test/proof support contradicted that, even though
nothing observed it at runtime.
Chosen: the same two values, renamed `DEFAULT_STORE_POOL_SIZE`/`DEFAULT_STORE_CONNECT_TIMEOUT_MS` and
moved to `types.ts`, which `harness.ts`, `scripts/migrate.ts`, `main.ts`, and the test support files
all import from alike — one un-tuned default, once, rather than duplicated or reached for across a
boundary the design says nothing should cross.
Rejected: leaving the values in `harness.ts` and having `scripts/migrate.ts` re-declare its own copy.
Duplicates the one number this move was meant to keep singular.
Reversibility: cheap — a rename and an import-path change, covered by
`tests/dependency-direction.test.ts`'s S12.8 case.

### 2026-08-19 — Reproducing the real 30-second advisory-lock bound end to end was tried and dropped

Context: S12.7 needed to prove `migrationNotReadyDetail` maps a real `LockTimeout` distinctly from a
real `MigrationFailed`. A first version held `node-pg-migrate`'s advisory lock open in one connection
for the full `LOCK_WAIT_TIMEOUT_MS` (30s) while a second `migrateToHead` call attempted the same lock,
to observe the real classification end to end.
Chosen: a unit-level test that calls `migrationNotReadyDetail` directly against constructed
`MigrationError` values, plus S12.5's and S12.6's existing compose-level tests, which already exercise
the surrounding retry and detail-surfacing machinery this mapping feeds against a live database.
Rejected: the end-to-end version. `node-pg-migrate`'s advisory lock is one fixed id for the whole
database, not scoped per schema — holding it for 30 seconds blocked every other test file's own
`migrateToHead` calls running concurrently under vitest's default file parallelism, producing exactly
the collateral `LockTimeout`s and readiness timeouts this entry warns against. What remains untested
end to end is `migrations.ts`'s own `isLockTimeout` classification of Postgres's `55P03`/`57014`,
which is unit-level, driver-facing logic outside this slice's `Touches`.
Reversibility: cheap to revisit if a future slice needs the real bound proven end to end — it would
need its own dedicated database or serialized test run to do so safely.

### 2026-08-19 — A migration run applies in one transaction, not one per migration

Context: `20-contract.md` and `10-design.md` both said each migration applies in its own transaction,
and the implementation passes `singleTransaction: true` to `node-pg-migrate`, which wraps the whole
run. `/reconcile` found the disagreement and had to decide which side was wrong.
Chosen: the run-wide transaction, and the two documents corrected to describe it. The property both
documents actually assert — no partial schema survives a failure — holds more strongly under it: a
run that fails on its third migration leaves nothing applied.
Rejected: per-migration transactions, to match the documents literally. Strictly weaker — the earlier
migrations of a failed run stay applied, which is the partial schema both documents say cannot exist.
Worth revisiting only if a future migration needs a statement PostgreSQL cannot run inside a
transaction (`create index concurrently` is the usual one); that migration ships alone, and the
decision is per-run rather than global.
Reversibility: cheap — one option on one `runner()` call.

### 2026-08-19 — `profile_corrupt` absorbs a profile read that fails, because the port has no other channel

Context: the durable `profiles.load` catches every error from its read — a connectivity failure as
well as a bad `format_version` — and returns `profile_corrupt`. `20-contract.md`'s three-warning
table named only the shape failures, so the outage case was unwritten.
Chosen: the code's behaviour, with the contract widened to admit it and to state its cost. The engine
declares `ProfileStore.load` returning a `ProfileLoadResult` with no error arm, so a connectivity
failure and a malformed row arrive at the same return statement with nothing to tell them apart.
Rejected: escalating a read failure to `storage_failure` and a `503`. It contradicts the engine's own
stated port behaviour — "a missing or corrupt profile degrades to no achievements with a warning,
never a broken game" — turns a store blip into a failed game action, and needs a channel `load` does
not have, so it means throwing from a method whose contract is not to.
Rejected: asking the engine for a fourth `ProfileWarningCode` so an outage is distinguishable on the
wire. A second cross-repository engine change on top of `concurrent_modification`, doubling the
ratification exposure `20-contract.md` Unresolved 3 records, for a distinction no caller acts on
differently — both answers are "no achievements this time".
Retained cost: while the store is degraded a player's achievements read as absent on a `200`, and
nothing on the wire says which failure it was. It self-corrects on the next successful action,
because the merge is a set union.
Reversibility: cheap for the classification; expensive for the fourth code, which is another repository's.

### 2026-08-19 — A request that arrives before the durable store has ever connected gets an honest failure

Context: `compose()` returns successfully when the store is unreachable and reconnects in the
background, so there is a window in which the process is up, not ready, and can still be addressed.
`20-contract.md` says what readiness reports in that window and nothing about what a request gets.
Chosen: `unavailablePersistence()` and `unavailableProfiles()` — every persistence method throws a
plain `Error`, which the engine's own catch converts to `storage_failure` and the wire answers `503`;
every profile read reports `profile_corrupt` and every profile write `profile_write_failed`, the same
answers a connected-but-failing store gives.
Rejected: falling back to an in-memory store for the window. A caller would be served a session that
silently ceases to exist the moment the durable store connects, and nothing on the wire would say so
— the one failure worse than the outage it hides.
Rejected: refusing to build the dispatcher until a store connects. That is a startup abort by another
name, which `compose()`'s whole shape exists to avoid.
Reversibility: cheap.

### 2026-08-19 — The retention-horizon check is enforced against an assumed forward timeout, not the edge's own

Context: invariant 62 requires the retention horizon to exceed any request's duration, and
`CompositionError.StorageConfigurationInvalid` rejects a configuration that does not. The bound it
must exceed is the edge's `GameEdgeOptions.ForwardTimeout` — which lives in a different repository
and a different process, and which no signature in `20-contract.md` threads into the workload.
Chosen: `ASSUMED_FORWARD_TIMEOUT_SECONDS`, a workload-owned constant of 60 seconds, exported so a
test can assert it still exceeds the real timeout `tests/support/hosted-edge.ts` configures.
Rejected: threading the edge's timeout through `WorkloadConfiguration`. It needs a contract amendment
to carry a value the workload cannot verify anyway — the edge fronting a given instance is not
something the instance can observe.
Rejected: dropping the check. Invariant 62 is what makes "a sweep cannot fall between a live
request's read and its write" a checked property rather than an assumption.
Retained cost: the guard is enforced against a guess, and the only thing keeping the guess honest is
one test against this repository's own test-support value. An edge deployed with a forward timeout
above 60 seconds would pass a configuration the invariant means to reject.
Reversibility: cheap.

### 2026-08-12 — The implicit tenant participates in storage keys without becoming request tenancy

Context: `/contract` stopped because the brief's tenancy non-goal said nothing filters on the tenant
identifier while the design and contract require the implicit tenant in every primary key and
statement. Non-goals are binding, so the contract could not treat the earlier design sign-off as an
amendment to the brief.

Chosen by Ben: **the store supplies the single implicit tenant as a constant in every key and
statement, while no request resolves or carries a tenant and no behaviour varies by tenant.** The
brief now says this directly. The schema therefore keeps `tenant_id` in every primary key from the
first migration without claiming that G2 ships tenant selection or tenant-dependent behaviour.

Rejected: **the literal former wording** — keep the column present but remove it from keys and
statements. It would make the old sentence true by deferring the correctness migration §7 exists to
prevent: adding a column later is cheap; adding it to every key and query after rows exist is not.

Reversibility: expensive once rows exist, which is why the brief and contract are aligned before the
first migration

---

### 2026-08-12 — The contract is derived; a failed lifecycle probe answers `absent`, the tarball stays vendored, and the engine's error union has one more member than the design says

Context: `/contract` against `10-design.md`. Three things came out of the derivation that the design
does not contain, and one of them is a correction to it.

Chosen:

**A lifecycle probe whose own query fails is read as `absent`, so the engine's `unknown_session` or
`unknown_save` passes through verbatim.** The design named three lifecycle states and was silent on
the probe failing; `Outcome<LifecycleState, StoreError>` was forced by the standing rule that every
error crossing a TypeScript module boundary here is an `Outcome` failure, and only the failure arm's
handling was open. Answering `absent` is consistent with the one rule the design does state about a
classifier's own failure — the zero-rows re-read is classified `conflict` and never `storage_failure`
— and it keeps a degraded store from turning honest `404`s into outage codes. **The retained cost:**
while the store is degraded, an expired session is answered as one that never existed, and nothing on
the wire says which. Readiness is what surfaces the condition.

**Design open question 5 is answered where it was routed: G2 vendors the regenerated tarball, as G1
did.** This is a reading of a constraint rather than a preference. The `@subzerodev` npm organisation
is still unreserved and [issue #81](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/81)
is open, so there is no registry to resolve `@subzerodev/service-contract` from — "switch to the
registry" is unavailable rather than rejected. The regeneration itself is forced: the error-coverage
gate fails against a widened `SessionStoreErrorCode`, and `TransportErrorCode` gains
`session_expired` and `save_expired`. It is a contract **minor** version. When #81 closes, the switch
is a one-line dependency change and no signature moves.

**The contract declares `SessionStoreErrorCode` with nine members, against the design's eight.**
`10-design.md` open question 3 calls the union *"a closed union of seven members"* and
`concurrent_modification` *"an eighth member"*, and the 2026-08-12 entry below repeats it. Read at
`0.6.1` on the engine's `main`, the union has **eight** members, so the new one is the ninth. Nothing
in the design turns on the count — the widening carries a `core.reason.*` message obligation whichever
ordinal it takes, which is what that question was actually establishing — but a contract that
transcribed the wrong count would ship a union with a member missing. The correction to `10-design.md`
belongs to `/design` and is not made here.

Rejected: **`storage_failure` → `503` on a failed probe** — the store failed and `503` says so;
rejected because it converts an honest `404` into an outage code on the one path that reaches the
probe, exactly when the store is degraded. **Logging the probe failure and passing through** —
identical on the wire and not silent; rejected because the brief admits only the observability a lost
update needs, and the sweep's failure is already the sole condition granted a log line of its own.
**Editing `10-design.md`'s count in this pass** — rejected because the contract stage does not author
the design, and a silent reconciliation is what `AGENTS.md` forbids when two documents disagree.

Reversibility: cheap for the probe rule (one branch and one invariant) and for the tarball (a
dependency line); the count is a correction, not a choice

---

### 2026-08-12 — A red-team pass hardens the design against fifteen findings; two are brief conflicts and two do not survive the engine source

Context: `/redteam` against `10-design.md`, followed by an instruction to fix everything it found.
Every load-bearing claim was then checked against the engine at `0.6.1` (`275aab1`) rather than
argued, which is what separated the findings that stood from the two that did not.

Chosen — **the corrections the design absorbs**, each closing a claim the document made but had not
established:

**`read committed` is a stated precondition of the compare-and-swap.** At `repeatable read` or
`serializable` the guarded `update` raises a serialization failure instead of reporting zero rows, so
every conflict would arrive as `storage_failure` and a `503` — the criterion the brief says no work
on this side can otherwise deliver, defeated by a server default or a pooler. Asserted at connect
rather than inherited.

**`engine_version` joins `session` and `save` as a host column.** Two instances share one store and
are not restarted atomically, so a rolling deploy mixes serializations; *Failure modes* already said
a bad blob is "corruption or an incompatible engine version… the workload must not guess", and
without the column it could not determine which. It is the second fact after `tenant_id` that is
cheap now and unreconstructable later, which is §7's own argument.

**The guarded update gains an `expires_at` predicate, and the zero-rows re-read gains a third
branch.** Without it a request that read a live row just before its TTL elapsed would resurrect the
session while a concurrent read answered `session_expired` — falsifying *Concurrency and ordering*'s
"expiry cannot race a live request", which covered the sweep but not the boundary. The predicate
gives the re-read an outcome that is not "conflict", which is what makes it a classification;
**a re-read that itself fails is classified as conflict, never `storage_failure`**, since zero rows
already established the fact the caller acts on.

**Readiness reports whether the store is usable now.** A startup-only check would leave the workload
ready through exactly the outage the edge's new readiness probe was introduced to surface. Cost
stated: readiness can flap.

**The dump is pinned to `collate "C"` and Comparison A asserts a non-empty result.** Locale-aware
collation makes the ordered blob set depend on the database image's locale, and two empty dumps
compare identical — so the effort's first criterion could go red for a locale or green for a
misdirected `search_path`. A fourth red-gate perturbation covers the dump, which the previous three
all left untested.

**Six further gaps closed by statement:** the sweep's own failure as a `Failure modes` entry; the
replay pinned to production TTLs so it cannot expire mid-run; a `format_version` bump made a two-step
release so a rolling deploy cannot silently empty a player's achievements; the README naming a
command that runs two instances, which is the one clause of the brief's four-clause fresh-clone
criterion that had no artifact behind it; the lifecycle probe composed behind the seam G3's
authorization decorator wraps, since it is not a store port and G3's stated mechanism would miss it;
and a no-op lifecycle probe for the in-memory configuration, so Dispatch carries no branch on which
store was built.

**Two consequences recorded rather than fixed.** Per-request composition drops a rejected
submission's `attemptCounter` increment, so the durable configuration stores a lower value than the
in-memory one — bounded, because `attempt` is observability that the engine's own test asserts
appears in no response body, so byte-identity is unaffected. And `saveGame` reads the session but
writes only the save, so a save's *contents* can capture a superseded state even though its row is
never contended; fixing it would mean making `saveGame` write the session, which is the second engine
behaviour change the brief names as a non-goal, to turn a correct save into a `409`.

Rejected: **fixing the two brief conflicts** — the tenant non-goal's "nothing filters on it" against
a design that filters on the implicit constant, and a save lifecycle the brief's criteria never
asked for. Both were adjudicated in the design's favour and logged, but the consequent brief edit was
never made, so the binding list still forbids what the design specifies. Rejected because
`00-brief.md` states that a model may interrogate it and not author it, and because `AGENTS.md` makes
non-goals binding *until that file changes* — a design document cannot discharge a constraint by
disagreeing with it. Recorded as open questions 9 and 10 so `/contract` and `/slices` see the
disagreement instead of resolving it by reading order.

**Two findings withdrawn against the source, and the withdrawal is the point.** A claimed orphan —
a committed save or achievement row left behind by an action whose session write lost the race — does
not exist: `submitAction` calls `writeSession` *before* `upsertAchievements` on the accepted branch,
and `saveGame` never calls `writeSession` at all. And the `attemptCounter` divergence was claimed as
a possible determinism break; `attempt` reaches only the event decorator, and an engine test already
forbids it in every response body. Both are now stated in the design *with the version they were read
at*, because "the loser leaves no trace" is a consequence of that ordering rather than of the guarded
statement alone.

Reversibility: cheap for every statement and precondition; **expensive for `engine_version`**, which
is the reason it was taken before rows exist, and which is also the only schema change in the pass

---

### 2026-08-12 — The store is provisioned by one committed compose file, and a third proof exercises the ports directly

Context: a third `/design` pass against an unchanged brief and an unchanged engine (`0.6.1`,
`3831051`), so again nothing to derive. Checking the design against the brief's *Definition of done*
found two criteria with no mechanism behind them — the first two gaps either of the previous two
passes left, and both are structural rather than editorial.

Chosen:

**The store is provisioned by one compose file under `workloads/game-service/`, run by the CI job and
by the README's own command.** The brief requires the evidence to run *"in CI from a fresh clone,
including the two-instance case and the store it shares"*, and `build.yml`'s `game-service` job runs
on a bare runner with no database — so every proof in `10-design.md` needed something that did not
exist. That job is deliberately separate so its steps *are* the README's commands, which is what
makes the fresh-clone story checkable rather than asserted; a compose file is the one option that
keeps the documented path and the proven path the same artifact. **Compose owns the dependency, the
harness owns the instances** — it starts no workload and supervises nothing, which is what keeps this
on the right side of the deployment-machinery non-goal.

**A third proof — port conformance over both implementations — exercises `profiles.load` and
`profiles.save`.** G1's committed fixture is ten steps and no step carries a `profileId`, so the
replay reaches four of the six port methods and the profile store is composed and never called. The
brief requires every store operation exercised against the durable implementation, and separately
requires the three profile degradations asserted; `10-design.md` committed to all three in *Failure
modes* while naming no proof that reaches them. The suite is written against the ports and run twice,
over the engine's in-memory implementations and over the durable ones. Running it over both is the
part that matters: a durable-only assertion says what this implementation does, and only the same
assertion passing over the engine's own says it *fills the port* — which is the question the engine's
composition root recorded as unanswerable *"until a second `SessionStore` implementation is actually
needed"*, and which the brief makes a deliverable.

Rejected: **a GitHub Actions `services:` container in CI with a compose file for developers** — more
idiomatic and less YAML, rejected because the path CI proves and the path the README documents become
two different things, which is the failure the fresh-clone job was built to prevent.
**Testcontainers** — one code path and free per-run isolation; rejected as a new dependency bought to
solve a problem a file already solves, because it satisfies *"tells a reader how to provision the
store"* with a library's internals rather than an artifact a reader can open, and because running two
instances by hand would still need something else to exist. **Assuming an externally provisioned
store from a connection string** — what a real deployment looks like; rejected because "runs in CI
from a fresh clone" would then depend on a step nothing in the repository performs.
**A profile-carrying step added to the replay fixture** — the strongest evidence available, through
the real wire and the same byte-identity comparison; rejected because it invalidates the golden
transcript and gives that proof a second job, so a red run would stop meaning exactly one thing.
**Unit tests on the durable adapter, named as such** — honest and cheap; rejected because testing one
implementation against its own behaviour cannot answer the fill-the-port question. **Saying nothing
and leaving profiles to the failure-mode tests already implied** — rejected because that is the
shape `agent.md` records as having cost a shipped release: a criterion that reads as covered because
an adjacent gate is green.

Reversibility: cheap for the compose file; cheap for the conformance suite's existence, and it is the
kind of artifact that gets more expensive to add the longer the durable adapters exist without it

---

### 2026-08-12 — The design's remaining open questions close; the adjudication is re-verified at engine `0.6.1`

Context: a second `/design` pass against an unchanged brief. The document was already complete and
questions 1, 2 and 7 already signed off, so the pass had nothing to derive — but it did have
something to *check*, and the check found the design citing the engine at `0.5.0` while the working
copy stands at `0.6.1`. Two of the four remaining questions then turned out not to need a decision
at all.

Chosen:

**(6) The wire-visible single-instance behaviour change is accepted.** Two concurrent same-session
actions on one instance produce one success and one `409` where G1 queued and applied both. It is
already the two-instance semantics, so the alternative is a deployment whose wire behaviour changes
when it scales; and it is what makes the brief's single-instance contention criterion reachable,
which the brief itself flags as at risk.

**(3, 4) Closed by reading the engine, not by deciding.** `SessionStoreErrorCode` is a closed union
of seven whose doc comment already requires a `core.reason.*` message per member, so
`concurrent_modification` is a widening with a message obligation and nothing further; and
`SessionStoreError` already discriminates by assigning `this.name`, so the design's duck-typed brand
is the engine's existing idiom rather than a new convention. `09-clients.md` §4 defines its columns
as one per **client**, with *"Hosted transport (Platform G1/S5)"* already the fifth and carrying its
effort tag in the header — so a durable store cannot be a column, and the annotation convention
already exists. Recorded because the cost of *not* recording it is that a later session re-asks a
question the source answers.

**(5) Routed to `/contract`, not answered.** Whether the regenerated contract package is vendored as
G1 did or consumed from the registry changes nothing in `10-design.md`, which is the test for
whether it was a design question.

**The adjudication is re-verified at `0.6.1`.** Every load-bearing claim reads off current source:
cache-then-persistence `getSession`, `attemptCounter` incrementing before dispatch against a
`writeSession` that runs only on the accepted branch, a freshly minted `saveId` per `saveGame`, and
`writeSession`'s parameterless `catch` discarding the cause. The only `v0.5.0..HEAD` change on the
serialization path is `sha256Hex` extracted into `canonical.ts` with `computeChecksum` delegating to
it — `canonicalStringify` untouched, so byte-identity is unaffected.

Rejected: **re-deriving `10-design.md` from the brief**, which is `/design`'s default path — rejected
because the brief has not changed, so the pass would restate an existing document and put the
signed-off wording on questions 1, 2 and 7 at risk of being quietly reworded. **Stopping and changing
nothing** — the design is complete and `/contract` could start; rejected because it would carry a
stale `0.5.0` citation and two needlessly-open questions into the two stages most likely to implement
them as written. **Answering (5) here to close all seven** — tidier; rejected because settling a
delivery question inside a design document is how a document acquires content no later stage
believes it owns. **Editing the earlier entry's `0.5.0`** — rejected as a matter of form: this log is
append-only, and "verified at `0.5.0`" was true of the work it describes.

Reversibility: cheap — every item is a document edit; the one behavioural commitment, (6), is
already what the two-instance path does

---

### 2026-08-12 — The design's three open questions that needed my answer are signed off: tenant in the key, the TTL values, and §6.1 corrected here

Context: `/design` closed with seven open questions. Three of them could not be answered from the
brief, the engine or the code — they needed a decision from me — and `/contract` cannot start
without them. Taken together in one sitting so the answers are consistent with each other rather
than accreted.

Chosen, in the order the design numbers them:

**(1) The tenant column goes in every primary key, with the implicit constant supplied in every
query.** This confirms what the design already proposed and had flagged as brushing a binding
non-goal; the flag is discharged rather than overruled. The non-goal's three clauses — nothing
reads it, nothing filters on it, no request carries one — all stay literally true when the value
is a compile-time constant rather than a resolved one. What is being bought is that the *key
shape* is right from the first migration.

**(2) Session idle TTL 30 days; save TTL 365 days; retention horizon 30 days.** Production
defaults only — all three stay configuration, so the suite still sets them to seconds. Sessions
and saves deliberately take different numbers: a session is resumable working state on an idle
clock, a save is immutable and is the artifact a player would notice losing, so it takes an
absolute year from insert. The horizon's only stated requirement is that it exceed any request's
duration.

**(3) §6.1 is corrected in this effort, before `/contract`.**
[`engine-hosting-contract.md`](../docs/docs/engine-hosting-contract.md) §6.1 now resolves
concurrency with a host-owned version, names the session as the contended row, states that saves
need no lock, and carries a dated note recording what the paragraph used to say and why it was
wrong.

Rejected: **the literal reading of the tenant non-goal** — column present, in no key and in no
query; it would make the brief's sentence true without interpretation, and was rejected because it
defers the expensive half of the migration §7 exists to prevent, shipping the appearance of the
requirement rather than the requirement. **One TTL shared by sessions and saves** — one number to
reason about, rejected because it treats a resumable working state and a player's artifact as the
same kind of thing, and the artifact is the one whose loss is user-visible. **A tighter save TTL
(90 days)** — bounds storage sooner and makes the save-expiry path easy to exercise; rejected for
the same reason. **Booking the §6.1 correction as a follow-up issue** — keeps G2's PRs to code and
schema, rejected because the brief's deferral was explicitly conditional on `/design` adjudicating,
that condition is now met, and the retained risk was scoped to "the duration of the design stage",
which ends here. **Folding the correction into the slice that builds the lock** — the document
would change when the code proves it; rejected because the wrong text would then sit in front of
`/contract` and `/slices`, the two stages most likely to implement it as written.

Reversibility: cheap for the TTL values and for the §6.1 text; **expensive for the key shape**,
which is the reason it was taken before any row exists

---

### 2026-08-12 — The two-instance contention proof addresses the instances directly; the edge's only change is its readiness probe

Context: `/design`. The contention proof needs two workload instances sharing one store. The edge has
one backend, and putting a balancer in front of two would be deployment machinery the brief excludes.
Separately, a workload can now be alive and unable to serve, which G1's edge readiness check cannot see.
Chosen: the harness addresses the two instances directly on their own ports, and the edge's readiness
check moves from probing the workload's **liveness** endpoint to its **readiness** endpoint. That is the
only change to `workloads/game-edge/` in G2.
Rejected: **a balancer or reverse proxy in front of both instances** — closer to a real deployment, and
it would exercise the edge under contention, but it adds a component the brief's deployment non-goal
excludes and proves nothing the direct addressing does not. **Leaving the edge's readiness on liveness**
— no edge change at all, at the cost of an edge that reports ready while every forward will 503, which
is the failure G1's own reasoning for `Unhealthy`/`Required` rejected.
Reversibility: cheap

---

### 2026-08-12 — The proofs isolate by a per-run database schema, never by the tenant column

Context: `/design`. The replay's counting `RecordIdSource` mints `counting-session-id-0` on every run, so
a second durable run against a dirty schema collides on the primary key. The tenant column is present
from the first migration and would isolate runs for free.
Chosen: the harness creates a database schema per run, migrates it, and drops it afterwards.
Rejected: **isolating runs by tenant id** — free, and the column already exists; rejected because it is
tenancy behaviour, which is a binding non-goal, and the first reader of the tenant column must not be a
test fixture. **Truncating between runs** — cheaper than creating a schema; rejected because both proofs
would share one namespace, so a leaked connection or a stray instance contaminates a later run, and the
symptom would be a byte-identity failure — the exact signal the suite exists to keep meaningful.
Reversibility: cheap

---

### 2026-08-12 — Session expiry is an idle TTL on the database clock, with a retained tombstone and a distinct wire code at 404

Context: `/design`. G1 deferred eviction to G2, and durable state no longer clears itself on restart. The
brief requires the bound to be asserted and requires the wire to distinguish an evicted session from one
that never existed — which the engine cannot, since both are `unknown_session`.
Chosen: `expires_at` computed in SQL from the **database** clock on every accepted write, treated as
absent on read, and hard-deleted by an idempotent sweep only past a retention horizon required to exceed
any request's duration. The retained row is what lets Dispatch answer `session_expired` / `save_expired`
via a lifecycle probe that returns a classification and never data. Both codes map to **404**, with the
code carrying the distinction. Saves take an absolute TTL from insert, being immutable. Past the horizon
the answer honestly degrades to `unknown_session`.
Rejected: **deleting the row at expiry** — bounds storage immediately, but cannot carry the distinction
the brief requires. **A count or size quota instead of a clock** — bounds storage directly, but there is
no principal to scope a quota to until G3, and a global cap would evict one player's live game to admit
another's new one. **The process clock** — composes with the engine's `Clock` port, but two instances
would disagree under skew and the same session would be alive on one and expired on the other.
**`410 Gone`** — semantically better HTTP; rejected because G1 established that the status never carries
the distinction and the code always does.
Reversibility: cheap for the values, expensive for the shape once rows exist

---

### 2026-08-12 — Profile achievements are stored append-only and merged by set union, not as a guarded blob

Context: `/design`. Per-request store composition means the engine's `profileLocks` no longer orders
anything across requests, and two instances can upsert one profile concurrently in any case.
Chosen: one row per `(tenant, profile, campaign, achievement)`, written `insert … on conflict do nothing`,
assembled into a `PlayerProfile` on load. Conflict-free: two instances awarding two achievements both
land, with no lock. Named cost, asserted by a test: the durable `save` is **additive** where the engine's
in-memory one replaces, so a save omitting a stored achievement removes nothing. The engine never issues
one.
Rejected: **a blob per profile guarded by its own compare-and-swap** — symmetric with sessions and reuses
the mechanism, but the loser's achievement is lost or must be retried, and for a mutation that is a set
union that is choosing a defect. **A blob per profile, last write wins** — what Adventures does; a silent
lost update, which is the class of defect this effort exists to eliminate.
Reversibility: expensive once profiles hold data

---

### 2026-08-12 — The tenant column is part of every primary key and every query supplies the implicit constant

Context: `/design`. `engine-hosting-contract.md` §7 requires a tenant identifier from the first schema
because retrofitting isolation is a correctness migration on every table at once. The brief's non-goal
says nothing reads it and nothing filters on it.
Chosen: `tenant_id` is `not null` with a default, is part of every table's primary key from the first
migration, and the store's SQL supplies the implicit constant. Raised as an open question in
[`10-design.md`](10-design.md) because it brushes a binding non-goal.
Rejected: **the column present but in no key and in no query** — the literal reading of the non-goal, and
it makes the brief's sentence true without interpretation; rejected because it defers exactly the
expensive half of the migration §7 exists to prevent — adding the column later is easy, adding it to the
keys and queries later is not — so it would ship the appearance of the requirement rather than the
requirement.
Reversibility: expensive once rows exist — which is the reason for taking it now

---

### 2026-08-12 — The session blob is a `text` column, and the engine's instants are stored as text beside the host's `timestamptz`

Context: `/design`. PostgreSQL's `jsonb` is the idiomatic column for a serialized object, and the record
carries two instants the engine stamps from its own `Clock`.
Chosen: `blob` is `text`; the engine's `createdAt`/`updatedAt` are `text`, stored verbatim; the host's
row timestamps and `expires_at` are `timestamptz` on the database clock. Two kinds of time, two column
types.
Rejected: **`jsonb` for the blob** — queryable and validated at write, but it is a normalised
representation that reorders members and renormalises numbers, so the blob would not round-trip byte for
byte and the effort's first criterion would fail. It would also make game state legible to the store,
which is the wrong relationship. **`timestamptz` for the engine's instants** — one time type; rejected
because reading them back reformats them, so the record would not round-trip and the replay profile's
fixed instant would return in the database's rendering rather than the engine's.
Reversibility: expensive once rows exist

---

### 2026-08-12 — The store is PostgreSQL over `pg`, with an existing migration runner

Context: `/design`, and the standing rule that a new dependency needs the alternatives named. The store
must be self-hostable, offline in steady state, and genuinely shared by two processes.
Chosen: PostgreSQL, driven by `pg`, schema managed by `node-pg-migrate`, all inside
`workloads/game-service/`.
Rejected: **SQLite** — zero provisioning and a hermetic suite, but two processes over one file is not the
deployment the brief describes, and a store that cannot credibly be shared by two instances cannot
exhibit §6.1's failure, which is the one thing G2 must reproduce. **A hand-rolled migration runner** —
two tables hardly need one, but the hard property is not applying SQL, it is the advisory lock that makes
two instances migrating concurrently safe, and the tool already owns it. **Prisma or Drizzle** —
migrations, types and queries in one, but both introduce a schema-first model that becomes a second
source of truth for a schema dictated by the engine's record types, which is the drift seam ADR-005
exists to close; Prisma's engine binaries also sit poorly with the offline constraint. **A key-value
store** — a blob per session is the natural shape and several offer compare-and-swap directly, but the
tenant column, the achievement set and the expiry sweep all want a relation, and the schema is the
artifact the brief says must survive G3 and G4.
Reversibility: expensive

---

### 2026-08-12 — Compare-and-swap is optimistic and holds no transaction across the engine call

Context: `/design`. The brief's criterion is "one success and one explicit rejection", and PostgreSQL
offers pessimistic and serializable alternatives that also prevent the lost update.
Chosen: a single guarded `update … where version = <the value read>`, with no transaction spanning the
read, the engine call and the write. Zero rows affected is classified by a re-read, never assumed. No
automatic retry anywhere.
Rejected: **`select … for update` in a transaction spanning the engine call** — also prevents lost
updates, but it produces a *serialised success*, so the brief's criterion could not be met at all; it
also holds a transaction open across a computation the database does not control, pins a connection per
in-flight request, and turns a conflict into a lock-wait timeout, which is the hardest failure to
distinguish from an outage precisely where telling them apart is the point. **`serializable` isolation
with automatic retry** — idiomatic; rejected because re-running a `submitAction` is a second action
against a moved state, and merging two is explicitly unavailable. **An `ETag`/`If-Match` version on the
wire** — puts the conflict where a client can reason about it; rejected because it puts engine-adjacent
state into a client contract with no room for it.
Reversibility: cheap

---

### 2026-08-12 — The session's optimistic-lock version is a store-owned column; §6.1 names the wrong table

Context: `/design`, resolving the contradiction the brief logged rather than settled.
`engine-hosting-contract.md` §6.1 resolves concurrency with compare-and-swap on `savedAtSeq`, "so the
version is present and needs no new concept". Verified against the engine at `0.5.0`: `savedAtSeq` is on
`StoredSaveRecord`, and `saveGame` mints a fresh `saveId` on every call, so saves are insert-only and
have no second writer; `attemptCounter` is on the session record but increments *before* dispatch,
including for rejected submissions that never call `writeSession`, so it is not in one-to-one
correspondence with the writes a lock must guard.
Chosen: a `version` column owned by the store, starting at 1 on insert and incremented by exactly the
guarded statement, invisible to the engine and absent from the wire. A consequence worth recording: G1
predicted that narrowing `savedAtSeq` off the wire was a cost "G2 will need it back" for — **it does
not**, because the lock lives entirely between one instance's read and that same instance's write, and
the contract needs no widening.
Rejected: **`savedAtSeq`, as §6.1 specifies** — it would version a row with no second writer and leave
the contended one unguarded, which is the failure §6.1 opens by describing. **`attemptCounter`** — at
least on the right record, and it does advance on every session update today; rejected because that is a
property of another repository's current implementation rather than a stated invariant, and an optimistic
lock must not depend on a coincidence in private code. **An `xmin` system column or a content hash** —
no column needed; rejected because both depend on incidental behaviour of the storage engine or the
serialization, and a hash cannot tell an identical-result write from no write.
Reversibility: expensive

---

### 2026-08-12 — The durable configuration composes the engine's session layer per request

Context: `/design`. The engine's `getSession` returns from an in-process `Map` and only reads
`SessionPersistence` on a miss, so a second instance's write is invisible to the first instance's cache.
With one long-lived session layer, a compare-and-swap at the port correctly rejects the losing write and
then leaves that instance's cache holding the losing state forever — the session becomes permanently
unusable there — and guards no reads at all, so `getScene` serves a superseded scene silently.
Chosen: persistence (the pool, the adapters, or the in-memory maps) is process-lived; the session layer
built over it is composed per inbound operation for the **durable** configuration and discarded with the
request. The engine, registry, `RecordIdSource` and `Clock` stay process-lived — a per-request counting
id source would restart at zero and collide. The **in-memory** configuration keeps G1's single long-lived
layer, because it has no compare-and-swap and removing its per-session queue would introduce the lost
update G2 exists to eliminate into the one configuration whose proof must stay green for unrelated
reasons. Two consequences are wanted: G1's recorded finding — the engine mutates `record.blob` before
writing through, leaving the record ahead of a failing store — is closed, since the record dies with the
request; and same-session contention becomes genuinely reachable on a single instance, which the brief
doubted and which a gate that cannot go red would otherwise hide.
Rejected: **one long-lived layer plus CAS at the port** — the obvious reading of §6.1 and the cheapest
change; rejected because it wedges the losing instance's session permanently and leaves reads stale.
**A long-lived layer plus an engine change evicting the cache on conflict** — repairs the wedge; rejected
because it leaves stale reads untouched and is a second engine behaviour change bought to ease
persistence, which the brief names as a non-goal in those words. **Session affinity at the edge** — one
cache stays authoritative; rejected because it makes the edge stateful, fails on failover to a cold
instance with the same problem, and would make the two-instance proof unreachable by construction.
Reversibility: cheap

---

### 2026-08-12 — A missing active slices document passes the marker gate with a stated skip

Context: applying the 2026-08-08 archive convention to the G1 set moved `design/30-slices.md` to
`design/g1/`, and `build/Test-SliceStatusMarkers.ps1` threw `FileNotFoundException` on a missing
document. `docs-ci.yml` runs it on every pull request, so the documentation gate would have gone red
from the archive commit until `/slices` writes G2's document. The script was written during G1, when
that file always existed; the D3 archive predates the script and never met this.
Chosen: on the **default** path only, a missing document prints a skip and exits 0. A repository
between `/slices` runs is stage 0 of the pipeline, not a broken repository. An explicitly supplied
`-Path` that does not exist still throws — that is a caller error — and a document that exists but
is malformed still fails. Exercised in both directions before commit: default-missing skips, G1's
nine slices validate, an explicit missing path throws, and a slice with no `**Status:**` line fails.
Rejected: **archiving all five and accepting a red gate meanwhile** — honest, and it makes the
interval visible, but it trains everyone to ignore a red gate, which is the expensive habit and
outlasts the interval. **Leaving `30-slices.md` in the root while its four siblings archive** — keeps
the gate green with no code change, at the cost of a split set where nothing tells a reader which
effort the root's slices describe.
Reversibility: cheap

---

## Open

_(previously tracked out of this section: issues [#146](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/146), [#147](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/147))_

---

## Index — decisions whose home is elsewhere

Reasoning, consequences and rejected alternatives live in the linked document, never here —
*Single ownership* in `AGENTS.md`. Effort-scoped decisions from completed efforts live in their
archive's own index; the ADR rows here are the permanent ones every effort inherits.

| Decision | Home |
|---|---|
| G2's durable stores live in the Node workload end to end; Platform's Persistence package gains no consumer | [`00-brief.md`](00-brief.md) |
| Compare-and-swap is proven at one instance and at two, asserted separately | [`00-brief.md`](00-brief.md) |
| G2 delivers one change into the engine: a conflict outcome distinguishable from a storage outage | [`00-brief.md`](00-brief.md) |
| §6.1 names `savedAtSeq` where the evidence says sessions version on `attemptCounter`; logged, resolved in `/design` | [`00-brief.md`](00-brief.md) |
| Session lifecycle is admitted to G2 rather than deferred again | [`00-brief.md`](00-brief.md) |
| Adventures is the reference implementation for G2 and G3, not a source this effort copies from | [`g1/90-decisions.md`](g1/90-decisions.md), 2026-08-09 |
| Completed efforts archive to `design/<effort>/`; the active effort owns the root | [`g1/90-decisions.md`](g1/90-decisions.md), 2026-08-08 |
| SkyNet HR is a second hosted workload; the edge becomes a Platform package | [ADR-007](../docs/docs/adr/ADR-007-second-hosted-workload.md) |
| Platform is a framework plus optional application modules | [ADR-006](../docs/docs/adr/ADR-006-application-modules.md) |
| Boundary contracts are projected, not authored; they get their own repository | [ADR-005](../docs/docs/adr/ADR-005-service-contract.md) |
| Platform is built in-house, with ABP as an architecture reference | [ADR-004](../docs/docs/adr/ADR-004-framework-build-not-adopt.md) |
| Package scope is per-registry, not one global name | [ADR-003](../docs/docs/adr/ADR-003-package-scopes-and-registries.md) |
| Platform is .NET, and the product boundary is a process boundary | [ADR-002](../docs/docs/adr/ADR-002-implementation-technology.md) |
| `SubZeroDev.Platform` is the framework, not the game product | [ADR-001](../docs/docs/adr/ADR-001-platform-identity.md) |
