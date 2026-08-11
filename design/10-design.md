# Design — durable sessions (G2)

**Document status:** Design. Derived from [`00-brief.md`](00-brief.md); if the brief changes, this is
re-derived, not patched.

What this document does **not** decide is already owned elsewhere.
[`engine-hosting-contract.md`](../docs/docs/engine-hosting-contract.md) owns the ownership split and
§7's port obligations; [ADR-005](../docs/docs/adr/ADR-005-service-contract.md) owns how a boundary
contract comes to exist; [`g1/10-design.md`](g1/10-design.md) owns the operation table, the two
surfaces, the wire's error semantics and the byte-identity proof's shape. G2 inherits all of it
unchanged except where durable state forces a change, and every such change is named here.

This document decides what the brief leaves open: **what the schema is and which column is the
optimistic lock, how a compare-and-swap can work at all given where the engine caches, how a conflict
reaches a caller as something other than an outage, what bounds a session that no longer clears
itself on restart, and what every one of those does when it fails.**

Four facts shape almost everything below.

1. **The engine's session layer caches.** `getSession` returns from an in-process `Map` and only
   falls through to `SessionPersistence` on a miss. A second instance's write is invisible to the
   first instance's cache. **Compare-and-swap at the persistence port is necessary and not
   sufficient**, and the whole of *Module boundaries* turns on that sentence.
2. **The contended row is the session, and only the session.** `saveGame` mints a fresh `saveId` on
   every call, so saves are insert-only and no two writers ever target one save row.
3. **Two instances, one store, anonymous and interchangeable.** Nothing keys on which instance
   served a request; that is what makes the two-instance proof mean anything.
4. **Trusted-local, offline in steady state, no principal.** Unchanged from G1, and nothing here may
   be designed as though an untrusted caller arrives.

> **The §6.1 contradiction the brief logged is adjudicated here, and §6.1 is the side that is wrong.**
> It resolves concurrency with "compare-and-swap on the sequence number… the engine's save handle
> already exposes `savedAtSeq` — so the version is present and needs no new concept." Verified against
> the engine at `0.6.1`, that is wrong twice over:
>
> - **`savedAtSeq` is on the wrong record.** It is `state.actionLog.length` stamped onto a
>   `StoredSaveRecord` whose `saveId` is freshly minted by every `saveGame`. Saves are insert-only.
>   Versioning them guards a row that has no second writer, and would leave the session — the row two
>   `submitAction` calls actually contend for — unguarded, which is precisely the failure §6.1 opens by
>   describing.
> - **The session's own counter is not a version either.** `attemptCounter` increments *before*
>   dispatch, including for a submission the engine goes on to reject — and a rejected submission
>   never calls `writeSession`. So the counter advances without a write, and is not in one-to-one
>   correspondence with the writes a lock must guard. That it happens to increment on every session
>   *update* today is a property of the current implementation, not a stated engine invariant, and
>   an optimistic lock must not be built on a coincidence in another repository's private code.
>
> **The resolution: the version is a store-owned column, incremented by the store on exactly the
> writes it guards, and invisible to the engine.** §6.1's "needs no new concept" is the part that does
> not survive. **§6.1 has been corrected to say so** — signed off 2026-08-12, on the grounds that the
> brief's deferral was conditional on `/design` adjudicating, and it has. The contract document is the
> source of truth for the corrected rule; this section owns the adjudication that produced it.
>
> **A consequence worth stating, because G1 predicted the opposite.** G1's design recorded that
> narrowing `savedAtSeq` off the wire was a cost "G2 will need it back" for. **It does not.** The lock
> is server-side and lives entirely between one instance's read and that same instance's write; no
> caller supplies a version and no response carries one. The narrowing stands, and the contract needs
> no widening for concurrency.
>
> **Re-verified at `0.6.1`.** The adjudication was first made against `0.5.0`, and the engine has moved
> since. Between the two, the only change on the serialization path is `sha256Hex` being extracted into
> `canonical.ts` with `computeChecksum` delegating to it; `canonicalStringify` and its writer are
> untouched, so **the byte-identity criterion is unaffected by the version change**. Every claim above
> reads off the current source: the cache-then-persistence `getSession`, the increment before dispatch
> against a write that happens only on the accepted branch, the freshly minted `saveId`, and the
> parameterless `catch` in `writeSession` that discards the cause.

---

## Data model

Everything in G2 is one of three things: **a row in the durable store**, **per-request state that dies
with the request**, or **an artifact inherited from G1 unchanged**. The in-memory records G1 modelled
still exist — the in-memory configuration is retained, not replaced — but they are no longer the only
answer, and the durable rows below are what the brief's criteria are asserted against.

### The store — engine-owned values, host-owned columns

The split is the schema's organising rule and it is
[`engine-hosting-contract.md`](../docs/docs/engine-hosting-contract.md) §7 made literal: **every field
the engine puts on its record is stored verbatim and given back unchanged; every column the host needs
is the store's own and the engine never sees it.** A column is on exactly one side of that line.

#### `session`

| Column | Type | Owner | Notes |
|---|---|---|---|
| `tenant_id` | `text not null default` the implicit tenant | Host | §7's requirement. Part of the primary key from the first migration. Nothing computes it. |
| `session_id` | `text not null` | Engine | Minted by the engine's `RecordIdSource`. |
| `blob` | `text not null` | Engine | The canonical serialization, byte for byte. |
| `audience` | `text not null` | Engine | `StoredSessionRecord.audience`. |
| `attempt_counter` | `integer not null` | Engine | Stored because the record carries it. **Not the lock.** |
| `replay_compatible` | `boolean not null` | Engine | |
| `engine_created_at` | `text not null` | Engine | The engine's `Clock` output, stored as text. |
| `engine_updated_at` | `text not null` | Engine | Likewise. |
| `profile_id` | `text null` | Engine | `null` ⇄ **key absent**, never `undefined`-valued. |
| `version` | `bigint not null` | **Host** | **The optimistic lock.** Starts at 1 on insert, `+1` on every accepted update. |
| `row_created_at` | `timestamptz not null default now()` | Host | Database clock. |
| `row_updated_at` | `timestamptz not null` | Host | Database clock, set on every accepted write. |
| `expires_at` | `timestamptz not null` | Host | **Derived**, in SQL, as `now() + <session idle TTL>` on every accepted write. |

Primary key `(tenant_id, session_id)`.

**Three things about this table are decisions, not transcription.**

- **`blob` is `text`, never `json` or `jsonb`.** `jsonb` is a normalised representation: it reorders
  members, collapses duplicates and renormalises numbers. A blob that round-trips through it is not
  the same bytes, and the byte-identity proof is the effort's first criterion. `text` also makes the
  column opaque to the database, which is the correct relationship — the store must not be able to
  reason about game state.
- **The engine's two instants are `text`, and the host's three are `timestamptz`.** Storing the
  engine's strings as timestamps would reformat them on read, so a record would not round-trip; under
  the replay profile the fixed instant would come back in the database's rendering rather than the
  engine's. Two kinds of time, two column types, and the type is what keeps them from being confused.
- **`version` is host-owned and never leaves the store.** Not `attemptCounter`, for the reason in the
  adjudication above; not `savedAtSeq`, which is on another table. It is incremented by exactly the
  statement that performs the guarded write, which makes "the version advanced" and "a write landed"
  the same event by construction.

#### `save`

| Column | Type | Owner |
|---|---|---|
| `tenant_id` | `text not null default` implicit | Host |
| `save_id` | `text not null` | Engine |
| `campaign_id` | `text not null` | Engine |
| `blob` | `text not null` | Engine — the save envelope's serialization |
| `saved_at_seq` | `integer not null` | Engine |
| `audience` | `text not null` | Engine |
| `profile_id` | `text null` | Engine |
| `row_created_at` | `timestamptz not null default now()` | Host |
| `expires_at` | `timestamptz not null` | Host — **derived** as `now() + <save TTL>` at insert |

Primary key `(tenant_id, save_id)`. **No `version` column**, and the absence is a decision: a save row
has exactly one writer in its lifetime because `saveGame` mints its id, so an optimistic lock would
guard nothing. `saves.put` is nonetheless written as an upsert rather than a bare insert, because the
port's method is `put` and an implementation that fails on a re-put would be narrower than the
interface it claims to fill.

#### `profile` and `profile_achievement`

`profile`: `(tenant_id, profile_id)` primary key, plus `format_version integer not null`,
`row_created_at`, `row_updated_at`.

`profile_achievement`: `(tenant_id, profile_id, campaign_id, achievement_id)` primary key, plus
`row_created_at`. **Append-only.**

**Achievements are stored as rows and merged by set union, not as a blob replaced wholesale.** The
engine's only mutation is `upsertAchievements`, which adds; an `insert … on conflict do nothing` per
achievement is therefore faithful to every mutation that exists, and it is *conflict-free* — two
instances awarding two different achievements to one profile at the same moment both land, with no
lock and no lost write. `format_version` exists so "corrupt" stays a reachable, testable outcome
against a normalised store, the same reason the engine's in-memory profile store keeps raw entries.

**The named cost:** the durable `ProfileStore.save` is *additive*, where the engine's in-memory one
replaces. A `save` omitting a previously-stored achievement removes nothing. The engine never issues
such a save, and a test asserts the divergence deliberately rather than leaving it to be discovered.

#### Schema bookkeeping

One migrations table, owned by the migration tool, not by this design.

### Derived, and from what

| Value | Derived from |
|---|---|
| `session.expires_at`, `save.expires_at` | The **database** clock at write time, plus the configured TTL |
| `session.version` | Its own previous value, in the guarded statement |
| Liveness of a session id (`live` / `expired` / `absent`) | Row presence and `expires_at` versus the database clock |
| The wire's `session_expired` / `save_expired` | The classification above, consulted only on the failure path |
| The determinism dump | `select session_id, blob … order by session_id`, and the same for saves |

Nothing on the engine's side of the table is derived by the workload. The workload allocates no id,
computes no sequence, and stamps no engine field — G1's rule, unchanged.

### Per-request, in memory

**The read-version map.** One `Map<sessionId, version>` per request, held by the persistence adapter
that request composes, recording the `version` each `sessions.get` observed. `sessions.put` for an id
in the map is a guarded update asserting that version; for an id not in the map it is an insert. The
map dies with the request, which is the entire reason it cannot go stale.

**The composed session layer.** For the durable configuration, one per request — see *Module
boundaries*. It holds the engine's own caches, which now live and die inside a single operation.

**Request context.** Operation id, wire version, inbound `traceparent`, correlation — G1's, unchanged,
and still never persisted.

### Process-lived, in memory

The engine instance, the content registry, the `RecordIdSource`, the `Clock`, the connection pool, and
— for the in-memory configuration only — the persistence maps and the single session layer. **The
counting sources must be process-lived**: a per-request counting `RecordIdSource` would restart at zero
on every request and mint colliding ids, which would present as a primary-key violation in the middle
of the replay.

### Inherited unchanged

The operation table, the generated schema set, the contract package, the replay fixture and the golden
transcript — all G1's, all still authoritative. The fixture gains no rows: the same ten operations are
replayed, against a different store.

### Not modelled

No principal, owner, account, entitlement, metering record, rate-limit bucket or idempotency key. No
per-player save index. No instance identity. Each is a binding non-goal and each absence is decided.

---

## Module boundaries

### The problem this section exists to solve

`getSession` reads the engine's in-process `Map` first and only consults `SessionPersistence` on a
miss. Compose one long-lived session layer per instance and the following happens with two instances
and a compare-and-swap that works exactly as specified:

1. Instance **A** serves a request for session *S*, caching the record at `version` 1.
2. Instance **B** serves an action on *S*, reading `version` 1 and writing `version` 2.
3. **A** serves an action on *S*. Cache hit — the store never reads the database. It mutates the
   cached record's blob in place, then writes asserting `version` 1. **The CAS rejects it.** Correct.
4. **A** serves another action on *S*. Cache hit again, on a record whose blob is now the *losing*
   state and whose `attemptCounter` has advanced. It writes asserting `version` 1 again, and is
   rejected again — *and the session is now permanently unusable on instance A*, because A's cache is
   never invalidated and nothing in the engine's read path can bring it back.

Compare-and-swap converts a silent lost update into a permanently wedged session. That is better, and
it is not good enough. It also does nothing at all for **reads**: `getScene` on A serves the cached,
superseded scene with no write, no conflict and no way to notice.

**The fix is compositional rather than algorithmic: for the durable configuration, the engine's
session layer is composed per request.**

- The **persistence** — the pool, the schema, the adapters — is process-lived.
- The **session layer** built on top of it is not. Each inbound operation composes one, uses it, and
  discards it.

A cache that never outlives one request cannot serve a stale read and cannot carry a losing write
forward. Every read reaches the database; the compare-and-swap is then the only concurrency mechanism
in the system, which is what makes it provable.

**Three consequences follow, and all three are wanted.**

- **The engine's per-session queue no longer serialises across requests.** `sessionLocks` lives inside
  the store instance, so two concurrent same-session requests on one instance now genuinely race. The
  CAS catches them. This is what makes the brief's single-instance criterion *reachable* — the brief
  anticipates that it might not be, and a gate that cannot go red proves nothing.
- **G1's inherited finding is closed.** G1 recorded that the engine mutates `record.blob` before
  writing through, so a failing persistence leaves the record ahead of the store, and that G2 must
  answer it. A record that does not survive the request cannot be ahead of anything after it.
- **Composition is cheap.** `createSessionLayer` allocates four maps and closures over an engine and a
  registry it does not own. Nothing is rebuilt per request except that.

### The Node workload

| Module | Owns | Depends on | Exposes |
|---|---|---|---|
| **Contract** | Nothing — the pinned external artifact | The contract package | Table, schemas, code→status mapping |
| **Migrations** | The ordered schema definitions and the runner invocation | The database driver | One call: bring this schema to head |
| **Store** | The pool, the SQL, the CAS, expiry evaluation, the tenant constant, blob-to-column mapping | Migrations, the engine's port *types* only | A **per-request persistence factory**, a `ProfileStore`, a **lifecycle probe**, a **serialization handle** |
| **Composition** | The engine, the registry, the record-id source, the clock, the determinism profile, and **which of the two store shapes is built** | Contract, Store, the engine package | A **store provider**, the lifecycle probe, the serialization handle, a readiness signal |
| **Dispatch** | Operation id → store call; store outcome → transport-neutral result; **conflict and expiry classification** | Contract, Composition | One call in, a result or a code out |
| **HTTP surface** | Routing, validation, canonical encoding, status mapping | Contract, Dispatch | The versioned JSON wire |
| **MCP surface** | Tool list and invocation | Contract, Dispatch | The MCP projection |
| **Probes** | Liveness, and readiness that now includes the store | Composition's readiness | Two endpoints |
| **Proof harnesses** *(test-scope)* | The replay fixture, the two-instance contention harness, the perturbations | Composition, Dispatch, the HTTP surface over real sockets, Store for schema setup only | Nothing — leaves |

**Dependency direction:**

```text
            Contract  ←───────────────────────────┐
               ↑                                  │
  Migrations ← Store ← Composition ←── Dispatch ──┴──→ HTTP surface
                            ↑             ↑                ↑
                            │             └────────── MCP surface
                            │
        Proof harnesses ────┴──(as HTTP clients, over real sockets)──→ HTTP surface
```

Acyclic, by inspection: Contract and Migrations depend on nothing local; Store depends on Migrations
and on the engine's *type* declarations; Composition depends on Store and Contract; Dispatch on
Composition and Contract; the two surfaces on Dispatch and Contract and on each other not at all;
nothing depends on a harness.

**Three edges deliberately absent, each gated rather than promised.**

- **No surface imports Store.** The durable store can read every blob in the system, which makes it the
  one module that could put engine state on the wire. G1's dependency-direction test — the structural
  half of the projection-boundary gate — gains Store as a second forbidden target alongside the
  serialization handle. A route cannot return what its module cannot name.
- **No surface imports the serialization handle.** Unchanged from G1.
- **Store does not import the engine's runtime**, only its type declarations. The store maps columns to
  a `StoredSessionRecord`; it never deserialises, never validates game state, and never calls the
  engine. That is what keeps "the store is a store" checkable rather than asserted.

**The lifecycle probe is the one new interface, and it is deliberately not an operation.** It answers
one question — *does a row exist for this id, and has it expired?* — as a classification, never as
data. It returns no blob, no scene and no record. It is reachable from Dispatch and from nowhere else,
has no route, no tool and no table row, and the arity gate will fail generation if anyone gives it one.
It exists because the brief requires the wire to distinguish an evicted session from one that never
existed, and the engine — correctly — has one answer for both. This is the shape the brief's
"eleventh operation" non-goal warns about, so it is named here and constrained by structure rather than
by intention: **a host lifecycle fact about a host-owned column is Platform's side of the ownership
table, and nothing about it can change the outcome of a game.**

### The .NET edge

Unchanged except in one place. **Its readiness check now probes the workload's *readiness* endpoint
rather than its liveness endpoint.** G1's edge asked "is the workload alive"; with a durable store a
workload can be alive and unable to serve, and an edge that reports ready while every forward will
`503` tells an operator less than nothing. It remains `Unhealthy` + `Required`, for G1's reason: one
backend, one job.

Nothing else at the edge moves. No load balancing across the two instances, no session affinity, no
streaming, no package boundary. **The two-instance contention proof addresses the two workload
instances directly, not through the edge** — a balancer in front would add a component the brief
excludes and would prove nothing the direct addressing does not.

### The dependency rule

`build/Test-WorkloadIsolation.ps1` is unchanged and must stay green. Everything G2 adds lives under
`workloads/game-service/`; Platform's `Persistence` package gains no consumer, which is the brief's
second decision and is checkable by the same build-time assertion that already exists.

---

## Control flow

### 1. Startup, and the background sweep — triggered by process start

Read configuration, including the store's connection settings, the two TTLs and the determinism
profile. **Bind the listener and report live immediately**; report **not ready** until the store is
usable. Then, with backoff: connect, and run migrations to head under the migration tool's own advisory
lock — two instances starting together must not both apply the same migration, and a lock the tool
already owns is not machinery this design should reimplement. On success, compose the process-lived
parts, assert the contract's recorded engine version against the resolved package's (G1's invariant,
unchanged), and report ready.

**Starting-but-not-ready is the deliberate choice**, and it is G1's own precedent from the edge: a host
that refuses to start tells an operator that something is wrong, while a host that starts and names its
failing readiness check tells them *what*.

**The sweep** runs on a timer in every instance and is a plain `delete` — idempotent, so two instances
sweeping concurrently need no coordination and none is added. It removes session and save rows whose
`expires_at` is older than the configured **retention horizon**, and nothing else. It does not touch a
row that has merely expired: an expired-but-retained row is what lets the wire answer *expired* rather
than *unknown*, and the horizon is what bounds how long that costs storage.

**The retention horizon is also a safety margin.** It is required to be far larger than any request's
duration, which is what makes it impossible for a sweep to delete a row between a live request's read
and its write.

### 2. One operation, end to end — triggered by a caller

Everything through validation is G1's, unchanged: version prefix, operation segment, closed request
schema, `malformed_payload` before the engine is ever reached.

Then, for the durable configuration:

1. **Dispatch asks the store provider for a store.** A fresh persistence adapter is constructed with an
   empty read-version map, and a session layer is composed over it with the process-lived engine,
   registry, record-id source and clock.
2. **The store call runs.** Any `sessions.get` issues a `select`, records the row's `version` in the
   read-version map, and returns a `StoredSessionRecord` — engine columns only, `null` mapped to an
   absent key. A row whose `expires_at` has passed is returned as **not found**, so the bound is
   enforced at read time and does not depend on when a sweep last ran.
3. **Any `sessions.put` is guarded.** For an id in the read-version map:
   `update … set … version = version + 1, row_updated_at = now(), expires_at = now() + ttl
   where tenant_id = … and session_id = … and version = <the value read>`. For an id not in the map:
   `insert`. On success the map is advanced to the new version.
4. **Zero rows affected is classified, never assumed.** The adapter re-reads the row. Present with a
   different version → **conflict**. Absent → also **conflict**, because the caller's read is no longer
   authoritative either way and the only correct client action is identical; the retention horizon
   makes this branch unreachable in practice, and it is written down rather than left to a comment.
5. **A conflict is signalled to the engine as a conflict.** The adapter throws a value carrying the
   engine's documented conflict brand. The engine's `writeSession` — which today catches everything and
   rethrows `storage_failure`, discarding the cause — recognises the brand and raises the new
   `SessionStoreError` code instead. **This is G2's single engine deliverable.** Anything else thrown
   still becomes `storage_failure`, so every existing implementation of the port is unaffected.
6. **Dispatch translates.** The conflict code travels to the wire verbatim, as every engine reason code
   does, and maps to **409**. `storage_failure` maps to **503**, unchanged. The two are now different
   answers to different questions, which is the criterion the brief says cannot otherwise be met.
7. **The store is discarded** with the request, cache and all.

**Expiry classification happens only on the failure path.** When the engine raises `unknown_session` or
`unknown_save`, Dispatch consults the lifecycle probe. Expired-and-retained → the transport code
`session_expired` or `save_expired`; genuinely absent, or swept past the horizon → the engine's own code
verbatim. **Both expired codes map to 404, not 410**, and the *code* carries the distinction — G1
already established that `unsupported_version` and `unknown_operation` share a status and are told apart
by their codes, and one convention is worth more than a semantically prettier second one.

**No retry, anywhere.** Not on conflict, not on `storage_failure`, not at the edge. A retried
`submitAction` is a second action, and merging two is explicitly unavailable.

**A rejected action is still a `200`.** Unchanged from G1, and a conflict is not a rejected action: a
rejection is the game's verdict on a legal request, a conflict is the transport failing to commit one.

**The in-memory configuration keeps G1's single long-lived session layer**, and therefore G1's
per-session queueing. It has no compare-and-swap, so removing the queue would introduce the lost update
G2 exists to eliminate — into the one configuration whose proof must stay green for reasons that have
nothing to do with durability.

### 3. The two proofs — triggered by the test suite, in CI

**Proof one: byte identity against the durable store.** G1's replay, run twice more. A run under the
replay profile against the in-memory store, and a run under the replay profile against the durable
store, each producing the ordered blob set through the same shutdown dump; the durable run's handle
reads `select id, blob … order by id` instead of a map. **Comparison A** is those two blob sets, byte
for byte. **Comparison B** is each run's response transcript against the committed golden transcript.
G1's existing in-memory replay stays in the suite and stays green — two proofs passing is not evidence
that the first still does.

**The durable replay requires a pristine schema, and the harness provides one by creating a per-run
database schema, migrating it, and dropping it afterwards.** The counting `RecordIdSource` mints
`counting-session-id-0` on every run, so a second run against a dirty schema collides on the primary
key. **The tenant column must not be used for this** — isolating runs by tenant is tenancy behaviour,
which is a binding non-goal, and it is exactly the shortcut a durable store makes tempting.

**Proof two: contention, asserted twice.**

- **One instance.** Two `submitAction` requests for one session, dispatched concurrently to a single
  process. Assert exactly one `200` and one `409` carrying the conflict code.
- **Two instances.** Two processes, two ports, one schema, started by the harness. The session is
  created through one; two `submitAction` requests are then dispatched, one to each, arranged so both
  read before either writes. Assert exactly one `200` and one `409`.

**The race is made deterministic by a perturbation seam, not by hope.** The store adapter accepts a
configured pause between a session read and the corresponding write, defaulting to none. The instance
under test is started with a pause; the second request is sent inside it. **A test asserts the seam is
inert when unconfigured**, on the same terms G1 asserts that the default profile writes no dump — a
diagnostic that is merely usually off is on.

**The gate is proven able to go red, three ways.** A run with the guard removed — the update's `where`
clause not asserting the version — must fail the two-instance assertion with two `200`s. A direct
adapter test that writes an artificially stale version must be rejected. And a run against an
unreachable store must produce `503`, not `409`, which is the criterion's *distinguishable* half
tested from the other side.

**Merging is asserted absent**, not argued: after a conflict, the loser's action is shown to have left
no trace in the winner's state.

---

## Failure modes

### The store is unreachable at startup

**Detection:** connection failure, or the migration runner's. **What the system does:** the process
stays up, reports live, reports **not ready**, and retries with backoff. It never serves an operation.
**What the operator sees:** the readiness body naming the store check, and a log line naming the host
and the failure. **State left behind:** none — no partial migration, because the runner applies each
migration in its own transaction under its advisory lock. **Retry:** automatic, at startup only. Once
ready, a later outage is handled below rather than by returning to this state.

### The store is unreachable, or fails, during a request

**Detection:** the driver's error, surfaced through the adapter as an ordinary throw — *not* branded as
a conflict. **Response:** the engine's `storage_failure`, mapped to **503**, with the correlation.
**State left behind:** on a read, none. On a write, none: either the guarded statement committed or it
did not, and the engine's mutated record is discarded with the per-request store. **Retry:** none
automatic; the caller re-reads with a query operation to learn whether the action landed. This is the
partial-failure case and that is the honest answer to it.

**`storage_failure` is now genuinely reachable**, where G1 recorded it as declared and unreachable. A
test forces it and asserts the 503, which is what stops the code being an untested branch.

### A write loses the race

**Detection:** zero rows affected, classified by the re-read. **Response:** the engine's new conflict
code, mapped to **409**. **State left behind:** none of the loser's — the winner's row is untouched by
the losing statement, and the loser's in-memory record dies with its request. **What the caller sees:**
a code that means *your read is stale; re-read and decide*, distinguishable from 503, which means *the
store did not answer*. **Retry:** the caller's, and only after re-reading. Never automatic.

**Partial failure has one shape here and it is benign:** the winner's action is fully applied and the
loser's is fully absent. There is no state in which half of an action landed, because the guarded update
is a single statement.

### A profile write fails

**Detection:** the adapter catches its own driver errors and returns `ok: false` with a
`profile_write_failed` warning; it does not throw. **Response:** the game action's `200`, carrying the
warning. **State left behind:** the session write, which already committed and is not rolled back —
§7's rule and the brief's criterion, asserted rather than argued. **Retry:** none; achievements are
re-derivable on the next action because the store's merge is a set union.

A missing profile yields `profile_missing` and an empty achievement set. A row whose `format_version`
is not 1, or an achievement row that fails shape validation, yields `profile_corrupt` and an empty
achievement set. **Neither ever produces a broken game**, and both are asserted.

### A session or save has expired

**Detection:** the adapter treats an expired row as absent; the engine raises `unknown_session` or
`unknown_save`; Dispatch consults the lifecycle probe. **Response:** `session_expired` or
`save_expired`, at **404**. **State left behind:** the retained row, until the sweep. **What the caller
sees:** a code that says the session existed and no longer does.

**Past the retention horizon the answer honestly degrades to `unknown_session`.** The row is gone and
nothing distinguishes it from an id that never existed. The horizon is configuration, the degradation is
documented, and it is not a defect — it is the price of not keeping tombstones forever.

### The stored blob does not deserialise

**Detection:** the engine's own `deserialize`, which throws rather than returning a result. **Response:**
`500` with `internal_failure` and the correlation — never the exception text. **State left behind:** the
unreadable row, untouched. This is store corruption or a blob written by an incompatible engine version;
either way the workload must not guess, and a caller cannot fix it.

### An id collides on insert

**Detection:** primary-key violation on a `createSession` or `loadGame` insert. **Response:**
`storage_failure` → **503**, *not* the conflict code — a collision is a storage anomaly, not a lost
update, and conflating them would make the conflict code mean two things. **State left behind:** the
existing row, untouched. Under the default profile the ids are the engine's random ones and this is not
expected; under the replay profile it means the schema was not pristine, which the per-run schema exists
to prevent.

### The two instances are running different schema versions

**Detection:** none at runtime, deliberately. **What the system does:** serves. **The constraint that
makes this safe is a rule on migrations rather than a check:** every migration must be backward
compatible with the previously deployed code, because two instances share one store and are not
restarted atomically. Additive columns with defaults, never a rename or a narrowing in one step.
**State left behind:** whatever the older instance wrote, which the newer one must be able to read.

### Concurrent startup migrations

**Detection:** the migration tool's advisory lock. **What the system does:** one instance applies, the
other waits and then finds the schema at head. **State left behind:** none partial. **Why not a
first-one-wins race:** two concurrent `create table` statements is a failed startup on one instance for
a reason an operator would have to reconstruct.

### The connection pool is exhausted

**Detection:** an acquisition timeout. **Response:** `storage_failure` → **503**. **State:** none. The
pool is sized by configuration with a stated default and **is not tuned** — performance is a binding
non-goal, and a number presented as a result would be exactly the thing the brief forbids.

### The projection boundary is crossed

Unchanged from G1 in effect, wider in scope. Statically, every response schema is closed and none
resolves to the envelope type. Structurally, neither surface's module graph reaches the serialization
handle **or Store**. Dynamically, no response body in either transcript contains a canonical
serialization. **What the system does:** fails the build. Durable storage makes this more tempting, not
less, which is why the structural gate grows rather than merely persisting.

### Inherited unchanged

Package restore failure, contract/engine version mismatch, malformed payload, unsupported version,
unknown operation, unexpected exception, edge-to-workload unreachability and timeout, absent OTLP
collector, response-schema mismatch — all G1's, all unchanged, and none of them is restated here.

---

## Concurrency and ordering

**What can now happen simultaneously.** Two operations against one session, on one instance or on two.
Two operations against different sessions, always. Two profile upserts against one profile, from
anywhere. Two sweeps. Two startups.

**What must not happen: two writes derived from one read both landing.** The guarded update enforces it,
in the database, in one statement. Nothing else does, and nothing else is asked to.

**Same-session serialisation is gone for the durable configuration, and that is the design rather than a
regression.** The engine's `sessionLocks` lives inside a session layer that now lives inside one
request, so it orders nothing across requests. It could not have helped in any case: it is per-process,
and §6.1's failure is between processes. Replacing an in-process queue with a cross-process
compare-and-swap is the whole effort.

**The cost, stated plainly.** A client that fires two actions against one session concurrently used to
have both applied in some order; it now has one applied and one rejected. That is a wire-visible
behaviour change on a single instance, and it is the *correct* one — it is already what happens across
two instances, and a single-instance deployment that behaved differently would be a deployment whose
semantics changed when it scaled.

**Cross-session concurrency is unrestricted** and shares nothing but the pool. The engine's
`profileLocks` is likewise now per-request and orders nothing across requests; the achievement merge's
set union is what makes that safe, which is the second reason it is a merge rather than a replace.

**Saves are never contended.** A fresh `saveId` per `saveGame` means one writer per row, for the row's
whole life.

**Expiry cannot race a live request.** Expiry is evaluated at read against the database clock — one
clock, so two instances cannot disagree and process clock skew is irrelevant. Deletion happens only
past the retention horizon, which is required to exceed any request's duration by a wide margin, so no
sweep can fall between a request's read and its write.

**The replay is strictly sequential and single-instance.** Unchanged from G1, and now load-bearing for
a second reason: counting record ids and two instances are incompatible, so the two proofs never share
a configuration.

**Startup ordering is unconstrained.** Either instance may start first; either may be first to migrate.
The edge reports not-ready until the workload does, and the workload reports not-ready until the store
does. That chain is the entire ordering contract between the three processes.

---

## Alternatives considered

### The stale-cache problem — per-request composition, not cache invalidation

**Chosen:** for the durable configuration, the engine's session layer is composed per request over a
process-lived persistence.

**Rejected — one long-lived session layer plus a compare-and-swap at the port.** The obvious reading of
§6.1, and the cheapest change. Rejected because it is *wrong*, not merely weak: the losing instance's
cache is never invalidated, so the session becomes permanently unusable on that instance, and reads are
not guarded at all — `getScene` on the stale instance serves a superseded scene with nothing to detect
it. A concurrency fix that leaves reads silently stale has not fixed the class of defect.

**Rejected — a long-lived layer plus an engine change that evicts the cache on conflict.** Repairs the
wedged-session half. Rejected because it leaves the stale-read half untouched, and because it is a
second engine behaviour change bought to make persistence easier, which the brief names as a non-goal
in those words.

**Rejected — session affinity at the edge.** Routes one session to one instance, so one cache is
authoritative. Rejected on three grounds: it makes the edge stateful, which is a binding non-goal; it
fails on failover, when the session moves to a cold instance holding the same problem; and it would
make the two-instance proof unreachable by construction — the design would prevent the failure from
occurring rather than refusing to lose the update, which is not what the brief asks for.

### The lock — a store-owned version column, not an engine field

**Chosen:** a `version` column owned, incremented and asserted by the store, invisible to the engine and
to the wire.

**Rejected — `savedAtSeq`, as §6.1 specifies.** It is on the save record; saves are insert-only; it
would guard a row with no second writer while leaving the contended row unguarded. Adjudicated at the
head of this document.

**Rejected — `attemptCounter`.** It is at least on the right record, and today it does advance on every
session update. Rejected because it advances on rejected submissions that never write, so it is not in
one-to-one correspondence with the writes a lock must guard; because that correspondence is a property
of another repository's current implementation and not a stated invariant; and because it makes the
lock's correctness depend on engine internals the store cannot see change.

**Rejected — an `xmin`-style system column or a content hash of the blob.** Needs no column at all.
Rejected because both make the lock depend on the storage engine's or the serialization's incidental
behaviour, and because a hash makes an identical-result write indistinguishable from no write.

### The concurrency mechanism — optimistic, not pessimistic

**Chosen:** optimistic locking. No transaction spans the read, the engine call and the write.

**Rejected — `select … for update` inside a transaction spanning the engine call.** Also prevents lost
updates, and both callers succeed in turn. Rejected decisively because it produces a *serialised
success*, and the brief's criterion is "one success and one explicit rejection" — under a pessimistic
lock that criterion cannot be met at all. It also holds a database transaction open across a
computation the database does not control, pins a connection per in-flight request, and converts a
conflict into a lock-wait timeout, which is the failure mode that is hardest to tell from an outage
precisely when telling them apart is the point.

**Rejected — `serializable` isolation with automatic retry.** Idiomatic PostgreSQL. Rejected because
the retry is the problem: re-running a `submitAction` is a second action against a state that has moved,
and merging two is explicitly unavailable.

**Rejected — an `ETag`/`If-Match` version on the wire, with the client supplying it.** Pushes the
conflict to where a client can reason about it. Rejected because it puts engine-adjacent state into the
client contract, which has no room for it, and because the transport would then be participating in the
game's state model rather than projecting it.

### Expiry — an idle TTL on the database clock, with a retained tombstone

**Chosen:** `expires_at`, computed in SQL from the database clock at every accepted write, treated as
absent on read, and hard-deleted by a sweep only past a retention horizon. Saves take an absolute TTL
from insert, since they are immutable.

**Rejected — deleting the row at expiry.** Simplest, and it bounds storage immediately. Rejected because
the brief requires that "a session that has been evicted is not the same answer as a session that never
existed", and a deleted row cannot carry that distinction.

**Rejected — a count or size quota instead of a clock.** Bounds storage directly, which a TTL does not.
Rejected because there is no principal to scope a quota to until G3, and a global cap would evict one
player's live game to admit another's new one — strictly worse than unbounded growth in a
trusted-local, single-operator deployment. The bound G2 can honestly take is time.

**Rejected — expiry evaluated against the process clock.** No SQL involvement, and it composes with the
engine's `Clock` port. Rejected because two instances would disagree under skew, and the same session
would be alive on one and expired on the other — a second lost-update-shaped defect in a design whose
subject is the first one.

**Rejected — a distinct `410 Gone` status for the expired answer.** Semantically the better HTTP.
Rejected because G1 established that the status never carries the distinction and the code always does,
and one convention with no exceptions is worth more than a nicer status on two codes.

### The store — PostgreSQL over plain `pg`, with a migration tool

**Chosen:** PostgreSQL, driven by `pg`, schema managed by an existing migration runner
(`node-pg-migrate`), all inside `workloads/game-service/`.

**Rejected — SQLite.** Zero provisioning, and it would make the whole suite hermetic. Rejected because
two processes over one file is not the deployment the brief describes, and a store that cannot credibly
be shared by two instances cannot exhibit §6.1's failure — which is the one thing G2 must reproduce.

**Rejected — a hand-rolled migration runner.** Two tables and a few indexes hardly need one. Rejected
because the standing rule is that hand-rolling needs the justification, and because the property that is
genuinely hard here is not applying SQL — it is the advisory lock that makes two instances migrating
concurrently safe, which the tool already owns.

**Rejected — Prisma or Drizzle.** Migrations, types and a query builder in one. Rejected because both
introduce a schema-first model that becomes a second source of truth for a schema whose shape is dictated
by the engine's record types, and because a code-generation step between the engine's types and the
columns is exactly the drift seam ADR-005 exists to close. Prisma's engine binaries also sit poorly with
the offline, self-hostable constraint.

**Rejected — a key-value store.** A blob per session is the natural shape, and several offer
compare-and-swap primitives directly. Rejected because the tenant column, the achievement set and the
expiry sweep all want a relation, and because the schema is the artifact the brief says must survive G3
and G4 — a schema is a better thing to inherit than a key convention.

### Profiles — append-only achievement rows, merged by union

**Chosen:** one row per achievement, `insert … on conflict do nothing`, assembled into a `PlayerProfile`
on load.

**Rejected — one blob per profile, guarded by its own compare-and-swap.** Symmetric with sessions, and
it reuses the mechanism. Rejected because the losing writer's achievement is then lost or must be
retried, and an achievement is exactly the thing a player notices going missing — for a mutation that is
a set union and needs no ordering, choosing a mechanism that can lose one is choosing a defect.

**Rejected — one blob per profile, last write wins.** What Adventures does. Rejected for the same
reason, more sharply: it is a silent lost update, which is the class of defect this entire effort exists
to eliminate.

### The tenant column — in the primary key, supplied as a constant

**Chosen:** `tenant_id` is `not null` with a default, is part of every table's primary key from the
first migration, and every query supplies the implicit constant.

**Rejected — the column present but named in no query, and not in any key.** The literal reading of the
brief's "nothing reads it, nothing filters on it". Rejected because it defers the expensive half of the
migration §7 exists to prevent: adding the column later is easy and adding it *to the keys and the
queries* later is the correctness migration on every table at once. The column's presence without the
key shape is the appearance of the requirement rather than the requirement.

**The cost, stated:** a constant appears in the store's SQL, which is one step closer to tenancy than
the brief's wording. It is still not tenancy — no request carries a tenant, nothing resolves one, and no
behaviour varies by it. This brushes a binding non-goal, so it is also raised in *Open questions*.

### The proofs — a per-run schema, not a per-run tenant

**Chosen:** the harness creates a database schema per run, migrates it, and drops it.

**Rejected — isolating runs by tenant id.** The column is right there and it costs nothing. Rejected
because using it is shipping tenancy behaviour, which the brief forbids in those words, and because the
first thing that reads the tenant column should not be a test fixture.

**Rejected — truncating between runs.** Cheaper than creating a schema. Rejected because it leaves the
two proofs sharing one namespace, so a leaked connection or a stray instance from an earlier test
contaminates a later one — and the symptom would be a byte-identity failure, which is the exact signal
the suite exists to make meaningful.

---

## Open questions

Each needs information the brief does not give, and each changes something concrete.

**Six of the seven are now closed, all on 2026-08-12**, and are recorded in
[`90-decisions.md`](90-decisions.md). Their original numbers are kept and their answers stated in
place, so nothing later cites a number that has quietly moved. They closed in two different ways, and
the distinction is worth keeping: **1, 2, 6 and 7 needed a decision** and got one; **3 and 4 turned out
not to be open at all** — they were answerable by reading the engine repository, which a later session
would otherwise have re-asked. **Only 5 remains, and it belongs to `/contract`.**

1. **Does the tenant column belong in the primary key, given the non-goal's wording?**
   **Settled: yes — in the primary key, with the implicit constant supplied in every query.** §7's
   purpose is that the *shape* is right from the first migration, and the non-goal is about
   caller-visible behaviour: nothing resolves a tenant, no request carries one, and no behaviour
   varies by it, all of which remain literally true when the value is a constant. The literal
   reading — column present, in no key and in no query — was defensible and was rejected because
   adding the column later is easy while adding it to the keys and queries later is the correctness
   migration §7 exists to prevent.

2. **What are the two TTLs and the retention horizon?**
   **Settled: session idle TTL 30 days, save TTL 365 days, retention horizon 30 days.** The design
   requires all three to be configuration so tests can set them to seconds, and that is unchanged —
   these are the production defaults, not the mechanism. Sessions and saves deliberately do **not**
   share a number: a session is resumable working state on an idle clock, a save is immutable and is
   the artifact a player would notice losing, so it gets an absolute year from insert. The horizon
   only has to exceed any request's duration; 30 days is generous for a tombstone a few columns wide.

3. **What is the engine's conflict code called, and how is the brand shaped?**
   **Settled: `concurrent_modification`, as an eighth member of `SessionStoreErrorCode`, with a
   name-based duck-typed brand.** This was not a decision so much as a reading of the engine, which
   answers both halves. `SessionStoreErrorCode` is a closed union of seven members whose doc comment
   states that every member is a registered `ReasonCode` with a shipped `core.reason.*` message, so
   widening it carries a message obligation and nothing more exotic. And the brand needs no new
   convention: `SessionStoreError` already discriminates itself by assigning `this.name`, so a
   name-carrying plain throw is the engine's own existing idiom rather than something G2 invents —
   which is what makes duck-typing the conservative choice here rather than the loose one, since a
   duplicated package copy breaks `instanceof` and leaves `name` intact. The engine's three
   `ProfileWarningCode` members are likewise already exactly `profile_missing`, `profile_corrupt` and
   `profile_write_failed`, so *Failure modes* names real codes throughout and invents none. The PR is
   still cross-repository and the vocabulary is still the engine's to ratify.

4. **How does the engine's API coverage checklist record "exercised against the durable
   implementation"?**
   **Settled: an annotation on the existing hosted column, not a sixth one.** `09-clients.md` §4
   defines the checklist as one row per operation and **one column per client**, and *"Hosted transport
   (Platform G1/S5)"* is already the fifth column — carrying, in its own header, the effort that filled
   it. A durable store is not a client, so it cannot be a column; and the header's existing provenance
   tag is the convention for recording which effort produced the evidence. The design's reading was
   correct and needed confirming rather than deciding. The engine repository still owns the wording.

5. **Is the contract package's release path for G2 the same as G1's?** The new engine code forces a
   regeneration (the error-coverage gate fails otherwise), and `session_expired` and `save_expired`
   widen `TransportErrorCode`, which is a closed union in the published package. That is a contract
   minor version and a republish. G1 consumed it as a vendored tarball; whether G2 does the same or
   switches to the registry is a delivery decision, not a design one.
   **Left open deliberately, and routed to [`/contract`](../.claude/commands/contract.md)** — signed
   off 2026-08-12 as belonging to the stage that owns the contract artifact. Nothing in this document
   changes on either answer, which is the test for whether it was ever a design question.

6. **Is the wire-visible change to single-instance behaviour acceptable?**
   **Settled: yes, accepted as the correct semantics.** Two concurrent same-session actions on one
   instance now produce one success and one `409`, where G1 queued and applied both. Three things
   carried it. It is **already** the two-instance semantics, so the alternative is a deployment whose
   wire behaviour changes when it scales — the failure being bought is worse than the one being sold.
   It is what makes the brief's single-instance contention criterion **reachable**: the engine's
   `sessionLocks` would otherwise serialise the race away, and the brief anticipates exactly that risk
   when it says a gate that cannot go red proves nothing. And the queue it replaces never protected
   anything across processes in the first place, so what looks like a removed guarantee was only ever
   a single-process one. The cost is stated plainly in *Concurrency and ordering* and is not restated
   here.

7. **Who corrects §6.1?**
   **Settled: this effort does, and it is done.**
   [`engine-hosting-contract.md`](../docs/docs/engine-hosting-contract.md) §6.1 now resolves
   concurrency with a store-owned version, names the session as the contended row, and states that
   saves need no lock — with a dated note recording what the paragraph used to say and why it was
   wrong. The brief's deferral was conditional on `/design` adjudicating which side was wrong; that
   condition is met by the blockquote near the top of this document, so the retained risk ends here
   rather than being carried into `/contract`, `/slices`, G3 and G4.
