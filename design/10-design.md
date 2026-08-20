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
itself on restart, how the store comes to exist for a suite that must run from a fresh clone, and
what every one of those does when it fails.**

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
> the engine at `0.8.0`, that is wrong twice over:
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
> **Re-verified at `0.8.0`, the version the workload vendors.** The adjudication was first made against
> `0.5.0` and re-read at `0.8.0`, and the engine has moved twice since. Every claim above reads off the
> current source: the cache-then-persistence `getSession`, the increment before dispatch against a write
> that happens only on the accepted branch, the freshly minted `saveId`, and `writeSession`'s `catch` —
> which at `0.8.0` no longer discards the cause but recognises the conflict brand, because G2's own
> engine deliverable has landed. `canonicalStringify` and `sha256Hex` still live where the `0.8.0`
> reading found them, so **the byte-identity criterion is unaffected by the version change**; what
> proves that rather than argues it is the two replays, which are gates.

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
| `attempt_counter` | `integer not null` | Engine | Stored because the record carries it. **Not the lock.** Its persisted value differs between the two configurations — *Module boundaries*, fourth consequence. |
| `replay_compatible` | `boolean not null` | Engine | |
| `engine_created_at` | `text not null` | Engine | The engine's `Clock` output, stored as text. |
| `engine_updated_at` | `text not null` | Engine | Likewise. |
| `profile_id` | `text null` | Engine | `null` ⇄ **key absent**, never `undefined`-valued. |
| `version` | `bigint not null` | **Host** | **The optimistic lock.** Starts at 1 on insert, `+1` on every accepted update. |
| `engine_version` | `text not null` | Host | The engine package version that produced `blob`. Stamped from the version startup already resolves. |
| `row_created_at` | `timestamptz not null default now()` | Host | Database clock. |
| `row_updated_at` | `timestamptz not null` | Host | Database clock, set on every accepted write. |
| `expires_at` | `timestamptz not null` | Host | **Derived**, in SQL, as `now() + <session idle TTL>` on every accepted write. |

Primary key `(tenant_id, session_id)`.

**Four things about this table are decisions, not transcription.**

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
- **`engine_version` records which engine wrote the bytes, and it is the one host column that cannot
  be backfilled.** Two instances share one store and are not restarted atomically, so a rolling
  deploy puts blobs from two engine versions in one table. *Failure modes* already says a blob that
  will not deserialise is "store corruption or a blob written by an incompatible engine version;
  either way the workload must not guess" — without this column it cannot even determine which, and
  the provenance of a row written before the column existed is gone permanently. It is stamped, never
  read on the serving path, and never compared against anything at runtime: it is evidence for an
  operator, not a gate. This is §7's own argument for the tenant column applied to the second fact
  that is cheap now and unreconstructable later.

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
| `engine_version` | `text not null` | Host — the engine package version that produced `blob` |
| `row_created_at` | `timestamptz not null default now()` | Host |
| `expires_at` | `timestamptz not null` | Host — **derived** as `now() + <save TTL>` at insert |

Primary key `(tenant_id, save_id)`. **No `version` column**, and the absence is a decision: a save row
has exactly one writer in its lifetime because `saveGame` mints its id, so an optimistic lock would
guard nothing. `saves.put` is nonetheless written as an upsert rather than a bare insert, because the
port's method is `put` and an implementation that fails on a re-put would be narrower than the
interface it claims to fill. **On a re-put every host column is recomputed** — `expires_at` from the
current clock, `engine_version` from the writing process — because a re-put is a write and a host
column that described the first one would then describe nothing.

**The absence of the lock rests on read engine source, not on an assumption.** Verified at `0.8.0`:
`saveGame` calls `newSaveId(recordIds)` on every invocation and writes through `writeSave` only, and
**it never calls `writeSession` at all** — the save path touches no session row. That is the same
standard the adjudication above applies when it rejects `attemptCounter`; the difference is that this
claim reads off the source rather than off a coincidence, which is what makes it usable. The
consequence for what a save's *contents* can race is in *Concurrency and ordering*, and is not the
same question as whether its row is contended.

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

**"The engine never issues such a save" is verified, not assumed.** At `0.8.0`, `upsertAchievements`
loads the profile, computes the ids not already present, and saves
`{ ...profile, achievements: [...profile.achievements, ...newRecords] }` — strictly the loaded set
plus additions, on every path. **The divergence also runs in the durable store's favour**, which is
worth stating because it inverts the usual reading of a named cost: when a load degrades to
`profile_corrupt` and returns an empty set, the engine's next `save` carries only the new
achievements, so a *replacing* store discards everything the player had earned and an *additive* one
does not.

**Neither profile table has an `expires_at` and the sweep does not touch them** — a deliberate
absence, recorded here rather than left to be inferred from a table that simply lacks a column. The
brief's lifecycle criteria bound *sessions*, and a profile is the row an account surface will own:
G3 attaches ownership to it, at which point deleting one is an account decision and not housekeeping.
The consequence to accept knowingly is that `profile` and `profile_achievement` grow monotonically
for the life of the deployment, with no principal to scope a bound to and nothing to reclaim a
profile whose sessions have all been swept.

#### Schema bookkeeping

One migrations table, owned by the migration tool, not by this design.

### Derived, and from what

| Value | Derived from |
|---|---|
| `session.expires_at`, `save.expires_at` | The **database** clock at write time, plus the configured TTL |
| `session.version` | Its own previous value, in the guarded statement |
| `engine_version` | The engine package version startup already resolved, stamped at write time |
| Liveness of a session id (`live` / `expired` / `absent`) | Row presence and `expires_at` versus the database clock |
| The wire's `session_expired` / `save_expired` | The classification above, consulted only on the failure path |
| The determinism dump | `select session_id, blob … order by session_id collate "C"`, and the same for saves |

**The dump's ordering is pinned to `collate "C"`, and that is load-bearing rather than decorative.**
Ordering `text` under a locale-aware collation is locale-dependent — ICU treats punctuation as
ignorable at the primary level where `C` compares by byte — and the replay's ids are hyphen-and-digit
dense (`counting-session-id-0`, `counting-session-id-10`). An unpinned collation makes the ordered
blob set depend on the database image's locale, so the in-memory run and the durable run could differ
in *order* while agreeing on every byte. That failure presents as a byte-identity failure, which is
the one signal in the suite that must mean exactly one thing. For the same reason the compose file
pins the server encoding to `UTF8` and the initdb locale explicitly rather than inheriting the
image's default.

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

**A fourth consequence follows and is merely accepted, which is why it is listed apart from the three
above.** `attemptCounter` increments before dispatch and a rejected submission never writes — the
adjudication at the head of this document establishes both, and they are the reason the counter is
not the lock. Under G1's long-lived layer a rejected submission's increment survived in the cached
record and was persisted by the next accepted write; under per-request composition it dies with the
request. **So the durable configuration stores a lower `attempt_counter` than the in-memory one for
the same sequence of inputs**, and the workload's composition choice determines the value of a column
the engine owns. Three things bound how far that reaches. It does not touch the blob: `attempt` is
observability, stamped onto emitted records by the command decorator, and the engine's own test
asserts it appears in no response body — so **byte-identity is unaffected**, which is the fact that
decides whether this matters. It does not reach the wire. And the counter remains monotonic, since
what changes is which increments are durable, not their order. What it does change is the numbering
in G1's cross-language trace, where `attempt` now counts accepted submissions rather than all of
them. Recorded rather than fixed, because every fix is either a second engine change or a write on
the rejection path, and both cost more than the numbering is worth.

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
| **Proof harnesses** *(test-scope)* | The replay fixture, the two-instance contention harness, the port-conformance suite, the perturbations | Composition, Dispatch, the HTTP surface over real sockets, Store for schema setup and — for the conformance suite alone — for the ports themselves | Nothing — leaves |

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

**One constraint on it is G3's, and it is stated here because G3 cannot discover it.** The brief's
*Lifespan* says G3 wraps *these stores* with an authorization decorator. The probe is not a store
port — it is Store's own interface, reached from Dispatch — so a decorator over `SessionPersistence`
and `ProfileStore` does not sit in its path, and G3 would inherit an interface its stated mechanism
does not cover. With no principal in G2 that is harmless and trusted-local; with a principal it is an
existence oracle, answering *live / expired / absent* for any id a caller cares to supply, which is
precisely the distinction the engine refuses to make. **The probe is therefore composed behind the
same seam the stores are**, so that whatever decorates them decorates it, and the three structural
guards it has today — no route, no tool, no table row — are recorded as what they are: guards on
*reachability*, not on *authority*. Nothing in G2 depends on this; it exists so the constraint is
inherited rather than rediscovered.

### The .NET edge

Unchanged except in one place. **Its readiness check now probes the workload's *readiness* endpoint
rather than its liveness endpoint.** G1's edge asked "is the workload alive"; with a durable store a
workload can be alive and unable to serve, and an edge that reports ready while every forward will
`503` tells an operator less than nothing. It remains `Unhealthy` + `Required`, for G1's reason: one
backend, one job.

**Readiness therefore reports whether the store is usable now, not whether it was usable once.** A
check that only ever recorded the outcome of startup would leave the workload reporting ready through
exactly the outage the sentence above changes the edge to surface — the change would buy nothing but
a different endpoint name, and the only case it caught would be a store that was never reachable,
which the first log line already reports. The readiness check evaluates the store on each probe;
`storage_failure` on the serving path and an unready readiness check are two views of one condition
rather than two mechanisms. **The cost, stated:** readiness can now flap, so a transient store blip
takes the edge unready for as long as it lasts. That is the correct direction for a single-backend
edge whose forwards would fail anyway, and it is the reason the check is a probe of the store rather
than a latch on the last request that failed.

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
profile. **Bind the listener and report live as soon as the first startup attempt settles** — a
listener bound against a store whose reachability is still unknown would answer `503` for the length
of that window without ever being able to say why. Report **not ready** until the store is usable.
The first attempt, and then with backoff: **run migrations to head under the migration tool's own
advisory lock, then connect** — two instances starting together must not both apply the same
migration, and a lock the tool already owns is not machinery this design should reimplement. On
success, compose the process-lived parts, assert the contract's recorded engine version against the
resolved package's (G1's invariant, unchanged), and report ready.

**Migrating inside that first attempt is what bounds the bind, and the bound is the lock's, not the
connection's.** The wait is the migration runner's own connect, plus its `lock_timeout` — the
dominant term by an order of magnitude — plus the migration run, and only then the store connect
bounded by `connectTimeoutMs`. **The cost, stated because it is larger than the one this paragraph
originally allowed for:** a first start against a lock another instance is holding can keep the
listener unbound for tens of seconds, and an unbound listener answers *nothing* — not even the `503`
the argument above is built on. That is accepted rather than fixed because the alternative is to bind
before the schema is known to exist, which puts the process in a state where the only honest answer
to every request is the same `503` it would give unbound, minus the operator's ability to tell a slow
start from a refused one. The bound is a rule on the lock timeout: it must stay short enough that a
contended start is a slow start rather than an outage.

**Starting-but-not-ready is the deliberate choice**, and it is G1's own precedent from the edge: a host
that refuses to start tells an operator that something is wrong, while a host that starts and names its
failing readiness check tells them *what*.

**The sweep** runs on a timer in every instance and is two plain `delete`s, one per bounded table, in
one transaction — idempotent, so two instances
sweeping concurrently need no coordination and none is added. It removes session and save rows whose
`expires_at` is older than the configured **retention horizon**, and nothing else. It does not touch a
row that has merely expired: an expired-but-retained row is what lets the wire answer *expired* rather
than *unknown*, and the horizon is what bounds how long that costs storage.

**The retention horizon is also a safety margin.** It is required to be far larger than any request's
duration, which is what makes it impossible for a sweep to delete a row between a live request's read
and its write.

**A sweep that fails is a first-class outcome, not an exception that escapes a timer.** Its statements
run under a statement timeout, its failure is caught, and the next tick simply tries again — the
work is idempotent, so a missed sweep costs retained rows and nothing else. A tick never starts while
the previous one is still running. The failure is what a readiness check will not show and what a
serving request will not hit, so it is the one condition in G2 that could persist unobserved; it is
therefore logged at each occurrence with the failing statement, which is the whole of the
observability the brief's non-goal leaves room for. A failed `delete` cannot report the rows it did
not remove without a second query against a store that has just failed one
([`20-contract.md`](20-contract.md), *Additions*, item 5).

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
   where tenant_id = … and session_id = … and version = <the value read> and expires_at > now()`. For
   an id not in the map: `insert`. On success the map is advanced to the new version.

   **The `expires_at` predicate is what stops the guarded write resurrecting a session the wire has
   already declared gone.** Reads treat an expired row as absent, so a request that read a live row
   microseconds before its TTL elapsed would otherwise extend it by a full TTL while a concurrent
   read on the other instance was answering `session_expired`. The predicate is cheap here and would
   be a data-correcting migration once rows exist; the expiry proofs seed `expires_at` into the past
   directly rather than waiting out a shortened TTL, which makes the window exact rather than raced.
4. **Zero rows affected is classified, never assumed, and the classification has three branches.**
   The adapter re-reads the row.
   - Present with a **different version** → **conflict**.
   - Present with the **same version but expired** → **expired**, not conflict. This is the branch the
     `expires_at` predicate creates; without it the re-read would have no outcome that was not
     "conflict", and a re-read that cannot change an answer is not a classification.
   - **Absent** → **conflict**, because the caller's read is no longer authoritative either way and the
     only correct client action is identical; the retention horizon makes this branch unreachable in
     practice, and it is written down rather than left to a comment.

   **A re-read that itself fails is classified as conflict, never as `storage_failure`.** Zero rows
   affected has already established the one fact a caller acts on — the write did not land — and
   every branch above tells them to re-read and decide. Letting the classifier's own driver error
   escape would convert a known non-outage into a `503` precisely when the store is degraded and
   races are most likely, which is the criterion the brief says cannot otherwise be met, defeated by
   the mechanism added to serve it.
5. **A conflict is signalled to the engine as a conflict.** The adapter throws a value carrying the
   engine's documented conflict brand. The engine's `writeSession` — which today catches everything and
   rethrows `storage_failure`, discarding the cause — recognises the brand and raises the new
   `SessionStoreError` code instead. **This is G2's single engine deliverable.** Anything else thrown
   still becomes `storage_failure`, so every existing implementation of the port is unaffected.
6. **Dispatch translates.** The conflict code travels to the wire verbatim, as every engine reason code
   does, and maps to **409**. `storage_failure` maps to **503**, unchanged. The two are now different
   answers to different questions, which is the criterion the brief says cannot otherwise be met.
7. **The store is discarded** with the request, cache and all.

**The guarded statement requires `read committed`, and that is a precondition rather than a
preference.** At `read committed` the losing `update` blocks on the winner's row lock, re-evaluates
its `where` against the committed row, finds no match and reports zero rows — which is the entire
premise of step 4. At `repeatable read` or `serializable` the same statement raises a serialization
failure instead, which is an ordinary throw, which *Failure modes* routes to `storage_failure` and a
`503`. **Every conflict would become an outage code**, and the one criterion the brief says no amount
of work on this side can otherwise deliver would fail by configuration. PostgreSQL's default is
`read committed`; the design depends on it, so the connection asserts it rather than inheriting it,
and a server default or a pooler that overrides it is a misconfiguration the store refuses at startup
rather than discovers under contention.

**Within one operation the unguarded writes are ordered by the engine, and that ordering is what
makes "the loser leaves no trace" true.** Read at `0.8.0` rather than assumed: `submitAction` calls
`writeSession` on the accepted branch and only then `upsertAchievements`, so a `sessions.put` that
loses the race throws before any achievement row is written — the loser cannot leave a durable
achievement behind. `saveGame` calls `writeSave` and **never calls `writeSession`**, so a save cannot
be orphaned by a conflict either: there is no session write in that path to lose. **This design
depends on both facts**, which is why they are named here with the version they were read at; an
engine change that wrote a profile before its session, or that made `saveGame` write the session
record, would invalidate *Failure modes*' claim that a conflict leaves nothing of the loser's.

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

**Dispatch is shared by both configurations, so the classification step is defined for both.** The
in-memory configuration has no `expires_at`, no rows and no lifecycle probe; Composition supplies a
probe that classifies every id as `absent`, so `unknown_session` and `unknown_save` pass through
verbatim and Dispatch carries no branch on which store was built. A no-op implementation rather than
a conditional is the point: the alternative puts configuration-dependent behaviour into the module
whose job is to be transport-neutral, and it is the shape that later grows a second one.

**The two configurations answer some requests differently, and the axis is durability, not instance
count.** *Concurrency and ordering* states the behaviour change and Open question 6 accepts it, both
in terms of one instance versus two; the sharper statement is that **the in-memory configuration and
the durable configuration are not wire-equivalent** — two concurrent same-session actions are queued
and both applied by the first, and one-success-one-`409` by the second. Since the in-memory
configuration is retained indefinitely (G1's replay must stay green), this is a standing property of
the workload and not a migration state. **What follows from it:** the in-memory configuration is a
proof fixture, never a supported deployment shape, and nothing outside the replay may be developed or
demonstrated against it — the byte-identity proof compares serialization, not concurrency semantics,
so no gate would catch a caller that had taken a dependency on the queued behaviour.

### 3. The three proofs — triggered by the test suite, in CI

**The store is provisioned by one committed compose file, and CI runs the same command the README
documents.** The workload's `game-service` CI job today runs on a bare runner with no database, and
every proof below needs one. A compose file under `workloads/game-service/` brings up PostgreSQL;
the job runs it as a step before the suite, and the README's fresh-clone story names the identical
command. **One provisioning path, documented and proven by the same artifact** — the reason that job
exists as a separate one in the first place is that its steps are the README's own commands, and a
CI-only mechanism the developer never runs would make the documented path the untested one.

**The compose file provisions the store and nothing else.** It does not start a workload instance,
supervise one, or describe a deployment. The two-instance harness still spawns its own processes, for
the brief's reason: a way to run two instances against one store is in scope, and orchestration is
not. The line is that compose owns the *dependency*, the harness owns the *instances*, and neither
crosses.

**The harness's spawn step is therefore reachable as a documented command, not only as a test.** The
brief requires the repository to tell a reader how to *"provision the store, run two instances, replay
the proof, and roll the schema forward"*, and compose is barred from the second clause by the
paragraph above — so without this the one clause of four with no artifact behind it would be the one
the brief added G2's whole deployment allowance for. The harness exposes its instance-spawning entry
point as an entry point the README names, and the contention proof invokes that same entry point
rather than a private copy of it. **Whether the README names it as a script or as the proof that
runs it is not the constraint** — one artifact serving both is. This is the compose file's own argument applied a second time: the
documented path and the proven path are one artifact, because a documented command nothing runs is
the failure the fresh-clone job exists to prevent. It remains the harness the brief's *Lifespan*
allows to be replaced without ceremony — what may not be replaced without ceremony is the README
naming *something* that runs two instances.

**Proof one: byte identity against the durable store.** G1's replay, run twice more. A run under the
replay profile against the in-memory store, and a run under the replay profile against the durable
store, each producing the ordered blob set through the same shutdown dump; the durable run's handle
reads `select session_id, blob … order by session_id` instead of a map. **Comparison A** is those two
blob sets, byte for byte. **Comparison B** is each run's response transcript against the committed
golden transcript.
G1's existing in-memory replay stays in the suite and stays green — two proofs passing is not evidence
that the first still does.

**The durable replay requires a pristine schema, and the harness provides one by creating a per-run
database schema, migrating it, and dropping it afterwards.** The counting `RecordIdSource` mints
`counting-session-id-0` on every run, so a second run against a dirty schema collides on the primary
key. **The tenant column must not be used for this** — isolating runs by tenant is tenancy behaviour,
which is a binding non-goal, and it is exactly the shortcut a durable store makes tempting.

**The durable replay runs with TTLs that cannot elapse during it — as does every other proof.** All
three lifecycle values are configuration, but no proof shortens the two TTLs to make a row expire:
the expiry proofs seed `expires_at` into the past with a direct `update` and then read through the
port, which asserts the predicate under test without a timing race and leaves `sessionIdleTtlSeconds`
and `saveTtlSeconds` at their production defaults everywhere. Only `retentionHorizonSeconds` is
varied, by the sweep proofs, which need a horizon a seeded row can already be past.

**The replay is the proof a shortened TTL would poison**, which is why invariant 82 names it: a
session that expires between two of the ten steps returns `unknown_session`, diverges from the golden
transcript, and reports a serialization failure for a clock problem. Seeding expiry rather than
waiting it out makes that hazard structural rather than a setting each proof must remember.

**Comparison A asserts the dumps are non-empty before it asserts they are equal.** Two empty ordered
sets compare byte-identical, so a dump that reads the wrong schema — a `search_path` that does not
match the per-run schema is the obvious way — passes Comparison A while Comparison B passes on its
own merits, because the responses were served correctly and only the dump was misdirected. The row
count is asserted against the fixture's own expected count rather than against zero, since "not
empty" is satisfied by one row as easily as by all of them.

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

**Proof three: port conformance, against both implementations.** The replay reaches four of the seven
port methods and no more. Its ten steps carry no `profileId`, so `profiles.load` and `profiles.save`
are never called, and no operation in the contract's table calls `saves.delete` at all — the profile
store is composed and never exercised, which is how G1 left it and which the brief's *"every store
operation is exercised against the durable implementation"* does not allow to stand. The proof is a conformance suite written **against the ports, not the wire**, and run
twice: once over the engine's in-memory implementations and once over the durable ones, asserting the
same behaviour from both.

It covers `sessions.get/put`, `saves.get/put`, `saves.delete` and `profiles.load/save` — **seven
methods; the engine's `SaveRecordStore` declares `delete` beside `get` and `put`, and nothing on the
wire reaches it, which is why the suite is the only thing that can**. It covers the three profile
outcomes *Failure modes* commits to — `profile_missing`, `profile_corrupt` from a bad
`format_version`, and `profile_write_failed` leaving a committed session write in place; the
set-union merge, including the
divergence the durable `save` deliberately carries; and the round trip that keeps host metadata out
of game state — the blob read back is exactly the bytes written, and no host column reaches the
`StoredSessionRecord`. **It is the only proof that addresses the store directly**, which is why
*Module boundaries* grants it the one dependency on Store that no other module has.

**Running it over both implementations is what makes it a conformance suite rather than a second set
of unit tests.** A durable-only assertion states what this implementation does; the same assertion
passing over the engine's own is what says the durable one *fills the port* — which is the question
the engine's composition root recorded as unanswerable until a second implementation existed, and
which the brief makes a deliverable.

**The answer it returns is "yes for six methods, and conditionally for the seventh", and saying so is
the deliverable.** `profiles.save` is the one method where the two implementations are asserted to
*differ* rather than to agree, so for that method the shared assertion cannot be the thing that
establishes conformance. What stands in its place is narrower and is stated as such: the durable
`save` conforms **given that every `save` the engine issues carries the loaded set plus additions**,
which is read off `upsertAchievements` at `0.8.0` rather than assumed, and which the suite asserts
directly as a property of the engine's caller rather than of either store. A conformance suite that
reported an unqualified yes here would be reporting agreement it did not test — and the question the
engine deferred deserves the qualified answer rather than the flattering one.

**The fixture and the golden transcript are untouched.** Adding a profile-carrying step to the replay
would have reached the same methods, and it would have made the byte-identity proof carry a second
job — a red run would no longer point at one thing.

**The gate is proven able to go red, four ways.** A run with the guard removed — the update's `where`
clause not asserting the version — must fail the two-instance assertion with two `200`s. A direct
adapter test that writes an artificially stale version must be rejected. A run against an
unreachable store must produce `503`, not `409`, which is the criterion's *distinguishable* half
tested from the other side. **And a run whose dump is pointed at an empty schema must fail
Comparison A** — the byte-identity proof is the effort's first criterion and the previous three
perturbations all attack the compare-and-swap, which would leave the older and more load-bearing of
the two proofs as the only one never shown able to fail.

**Merging is asserted absent**, not argued: after a conflict, the loser's action is shown to have left
no trace in the winner's state.

---

## Failure modes

### The store is unreachable at startup

**Detection:** connection failure, or the migration runner's. **What the system does:** the process
stays up, reports live, reports **not ready**, and retries with backoff. **It serves every operation
and every one fails** — the listener is bound and both surfaces are built, so a caller that addresses
it anyway reaches a persistence whose every method throws, and gets `storage_failure` at `503` for a
game operation and the profile warnings on a `200` for a profile read or write. Those are the same
answers a connected-but-failing store gives, which is the point: the window is not a distinct wire
behaviour a caller has to learn. **Falling back to an in-memory store for that window was rejected**
— a caller would be served a session that silently ceases to exist the moment the durable store
connects, which is the one failure worse than the outage it hides.
**What the operator sees:** the readiness body naming the store check, and a log line naming the host
and the failure. **State left behind:** none — no partial migration, because the runner applies the
whole run in a single transaction under its advisory lock. **Retry:** automatic, at startup only. Once
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
is a single statement — **and because the engine's own ordering puts every unguarded write after the
guarded one.** `writeSession` precedes `upsertAchievements` on the accepted branch and `saveGame`
writes no session row at all, both read at `0.8.0` (*Control flow* 2), so a losing `sessions.put`
throws before an achievement row or a save row can be written. The claim is a consequence of that
ordering rather than of the single statement alone; if the ordering changes, this paragraph is the
one that stops being true.

### A profile write fails

**Detection:** the adapter catches its own driver errors and returns `ok: false` with a
`profile_write_failed` warning; it does not throw. **Response:** the game action's `200`, carrying the
warning. **State left behind:** the session write, which already committed and is not rolled back —
§7's rule and the brief's criterion, asserted rather than argued. **Retry:** none; achievements are
re-derivable on the next action because the store's merge is a set union.

A missing profile yields `profile_missing` and an empty achievement set. A row whose `format_version`
is not 1, or an achievement row that fails shape validation, yields `profile_corrupt` and an empty
achievement set. **Neither ever produces a broken game**, and both are asserted — by the
port-conformance suite (*Control flow* 3), which is the only proof that reaches these ports at all.

**A profile *read* that fails also yields `profile_corrupt`**, because `ProfileStore.load` has no
error channel and a connectivity failure reaches the same return statement a malformed row does. So
while the store is degraded a player's achievements read as absent on a `200`, and nothing on the
wire says which of the two it was. It self-corrects — the merge is a set union, so the next
successful action re-derives what was earned — and readiness is what surfaces the condition itself.

**`format_version` may not be bumped in the same release that first writes the new format.** The
degradation above is designed for a corrupt row, and a rolling deploy would otherwise reuse it as the
answer to a routine one: a newer instance writes `format_version` 2, an older instance reads it,
classifies it `profile_corrupt`, and the same player has achievements on one instance and none on the
other for the length of the deploy — silently, since `profile_corrupt` is a warning on a `200`. The
migration rule under *The two instances are running different schema versions* is therefore a rule
about **data formats as well as column shapes**: a format bump ships as read-support first and
write-support in a later release, the same two-step every additive column takes.

### The sweep fails

**Detection:** the caught error from the sweep's own statement — a driver failure, a statement
timeout, or a lock held by the other instance's concurrent sweep. **What the system does:** logs the
failure and the rows it did not remove, and tries again on the next tick; a tick never overlaps its
predecessor. **State left behind:** rows past the retention horizon, retained. **What the operator
sees:** the log line, and nothing else — this is the one condition in G2 that no readiness check and
no request can surface, because the sweep is not on either path. **Retry:** automatic, every tick,
indefinitely. **Why it is not more than this:** the sweep bounds storage, not correctness — an
un-swept row is answered `expired` rather than `unknown_session`, which is the *more* informative of
the two answers, so a sweep that has not run for a week costs disk and nothing else.

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
either way the workload must not guess, and a caller cannot fix it. **Which of the two it is, an
operator can now determine**: the row's `engine_version` names the package that wrote the bytes, and
comparing it against the reading instance's own resolved version separates a version skew from
corruption without inference. The workload does not make that comparison itself — it would be a
runtime gate on a column that exists to be read after the fact, and a wrong guess in that direction
refuses to serve a blob that was fine.

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

**The same skew applies to engine versions, and there it is not checked either.** Startup asserts the
contract's recorded engine version against *its own* resolved package; nothing compares one instance's
engine to the other's, and nothing could without inventing the instance identity the design refuses
elsewhere. So a rolling deploy can put two serializations in one table — which byte-identity, proven
within a single-instance replay, does not cover. **The mitigation is a rule and a column, not a
gate:** the same two-step every schema change takes applies to an engine upgrade, and
`engine_version` records which version wrote each row so the skew is legible afterwards rather than
inferred. **State left behind:** blobs from two engine versions, each labelled.

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

**Save rows are never contended; a save's *contents* are.** A fresh `saveId` per `saveGame` means one
writer per row, for the row's whole life — that is a statement about the row, and it is the reason
there is no `version` column on `save`. It is not a statement about what the row holds.
`saveGame` reads the session and writes only the save (`0.8.0`, *Data model*), so nothing guards the
interval between them: instance **A** reads session *S*, instance **B** submits an action and wins
the compare-and-swap, and **A** then writes a save of the state *before* B's action. Both requests
succeed, correctly — no update was lost, and the save is a faithful snapshot of a state the session
genuinely held.

**This is accepted rather than fixed, and the reasoning is the design's own.** The alternative is to
make `saveGame` write the session so it can be guarded, which is a second engine behaviour change
bought to ease persistence — named as a non-goal in the brief in those words — to convert a
successful save into a `409` for a snapshot that was never wrong, only superseded. A save is
already a branch: `loadGame` mints a new session, so two loads of one save are two games by design,
and a save taken a moment before another instance's action is the same shape. **What would not be
acceptable and is not the case:** the save does not overwrite anything, does not affect the winner's
session, and cannot be mistaken for the session's current state, because nothing reads a save except
`loadGame`.

**Expiry cannot race a live request, and it takes two mechanisms rather than one to say so.** Expiry
is evaluated at read against the database clock — one clock, so two instances cannot disagree and
process clock skew is irrelevant. Deletion happens only past the retention horizon, which is required
to exceed any request's duration by a wide margin, so no sweep can fall between a request's read and
its write. **The horizon does not cover the expiry boundary itself**, which is a different event from
the sweep: a request that reads a row a moment before its TTL elapses and writes a moment after would,
with no further guard, extend a session that a concurrent read on the other instance is already
answering `session_expired` for. The `expires_at` predicate on the guarded update (*Control flow* 2)
is what closes that, and the resulting outcome is `expired` rather than `conflict` — which is the
third branch of the re-read classification and the reason that classification has more than one
answer.

**The replay is strictly sequential and single-instance.** Unchanged from G1, and now load-bearing for
a second reason: counting record ids and two instances are incompatible, so the replay and the
contention proof never share a configuration.

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

### Provisioning the store — one compose file, run by CI and by the reader

**Chosen:** a compose file committed under `workloads/game-service/`, brought up by a step in the
`game-service` CI job and by the identical command in that workload's README.

**Rejected — a GitHub Actions `services:` container in CI, with a compose file for developers.** More
idiomatic for Actions, less YAML, and the container is reachable on loopback so the job's existing
OTLP port rejections are unaffected. Rejected because it makes the path CI proves and the path the
README documents two different things, and that job exists as a separate one precisely so its steps
*are* the README's commands — a documented command nothing runs is the failure the fresh-clone job
was built to prevent.

**Rejected — Testcontainers, with the suite starting its own container.** One code path, identical in
CI and locally, and per-run isolation would come free. Rejected because it is a new dependency bought
to solve a problem a file already solves; because it moves provisioning inside the test process,
where the brief's *"tells a reader how to provision the store"* is satisfied by a library's internals
rather than by an artifact a reader can open; and because running two instances against one store by
hand would still need something else to exist.

**Rejected — assuming an externally provisioned store from a connection string.** Nothing to commit,
and it is what a real deployment looks like. Rejected because "the evidence runs in CI from a fresh
clone" then depends on a step nothing in the repository performs, which is the anecdote-on-my-machine
result G1 already refused once.

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
replay and the contention proof sharing one namespace, so a leaked connection or a stray instance from
an earlier test contaminates a later one — and the symptom would be a byte-identity failure, which is
the exact signal the suite exists to make meaningful.

### The profile port — a conformance suite over both implementations, not a wider fixture

**Chosen:** a third proof, written against the ports and run twice — over the engine's in-memory
implementations and over the durable ones — covering all seven port methods and the three profile
outcomes.

**Rejected — a profile-carrying step added to the replay fixture.** It reaches the same methods
through the real wire and through the same byte-identity comparison, which is the strongest kind of
evidence available. Rejected because it invalidates the committed golden transcript, and more
importantly because it gives the byte-identity proof a second job: a red run would then mean either
that serialization diverged or that the profile path broke, and the proof's whole value is that it
means exactly one thing.

**Rejected — unit tests on the durable adapter, named as such in this document.** Honest, cheap, and
it would close the gap in the sense that the code is covered. Rejected because it tests one
implementation against its own behaviour, which cannot answer whether the durable store *fills the
port* — the question the engine's composition root deferred *"until a second `SessionStore`
implementation is actually needed"*, and which the brief makes a deliverable rather than a side
effect. Only running the same assertions over both implementations answers it.

**Rejected — leaving profiles to the failure-mode tests already implied and saying nothing.** The
design's *Failure modes* already commits to the three outcomes, so a reader could reasonably assume
they are covered. Rejected because that assumption is exactly the shape of the failure `agent.md`
records: a criterion that reads as covered because an adjacent gate is green.

---

## Open questions

Each needs information the brief does not give, and each changes something concrete.

**Six of the original seven are closed, all on 2026-08-12**, and are recorded in
[`90-decisions.md`](90-decisions.md). Their original numbers are kept and their answers stated in
place, so nothing later cites a number that has quietly moved. They closed in two different ways, and
the distinction is worth keeping: **1, 2, 6 and 7 needed a decision** and got one; **3 and 4 turned out
not to be open at all** — they were answerable by reading the engine repository, which a later session
would otherwise have re-asked. **5 remains and belongs to `/contract`.**

**8 through 11 were opened by the red-team pass of 2026-08-12** and are numbered after the original
seven for the same reason those keep their numbers. **9 and 10 are not this document's to close:
they are conflicts with the brief, and the brief is not a document a model may author.** They are
recorded here so that the disagreement is visible to `/contract` and `/slices` rather than resolved
by whichever document a later session happens to read first.

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
   requires all three to be configuration, and that is unchanged — these are the production defaults,
   not the mechanism. In practice only the retention horizon is varied by a proof; expiry itself is
   asserted by seeding `expires_at` into the past. Sessions and saves deliberately do **not**
   share a number: a session is resumable working state on an idle clock, a save is immutable and is
   the artifact a player would notice losing, so it gets an absolute year from insert. The horizon
   only has to exceed any request's duration; 30 days is generous for a tombstone a few columns wide.

3. **What is the engine's conflict code called, and how is the brand shaped?**
   **Settled: `concurrent_modification`, as a ninth member of `SessionStoreErrorCode`, with a
   name-based duck-typed brand.** This was not a decision so much as a reading of the engine, which
   answers both halves. `SessionStoreErrorCode` was a closed union of eight members whose doc comment
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

8. **What refreshes a session's idle clock — every use, or only an accepted write?**
   As designed, `expires_at` advances on accepted writes only, because that is the one statement
   the store issues. `getScene`, `getView`, `getStrings`, `resumeSession`, `previewAction` and
   `saveGame` all read the session and write no session row (`0.8.0`), so **a session read
   continuously for the whole idle TTL still expires**, and the caller is told `session_expired` —
   "the session existed and no longer does" — about a session it was using. Calling the mechanism an
   *idle* TTL when it measures write recency is the part that does not survive contact with a
   reader.
   **Open, because the two answers cost differently and neither is obviously right.** Refreshing on
   read makes every read a write: it puts query operations inside the compare-and-swap's blast
   radius, doubles the statements on the cheapest path, and undoes *"every read reaches the
   database"* being a read-only property. Leaving it as it is costs a name and a caller-visible
   surprise, both fixable by saying "30 days since the last action" wherever the TTL is documented.
   Nothing else in this document changes on either answer, which is what keeps it a question rather
   than a redesign.

9. **Does the brief's tenant non-goal get amended, or does the design get narrowed to it?**
   The non-goal says, in words, *"Nothing reads it, nothing filters on it, and no request carries
   one."* Every query this design specifies filters on `tenant_id = <the implicit constant>`, which
   is the shape Open question 1 settled and `90-decisions.md` records twice. Both readings were
   argued at the time and the key shape won on §7's own grounds; what was never done is the
   consequent edit to the brief, so the binding list still forbids what this document specifies.
   **This is a brief conflict, not a design defect** — the design is the side that was adjudicated
   and signed off. It stays open here because `AGENTS.md` makes non-goals binding *until that file
   changes*, and a design document cannot discharge a constraint by disagreeing with it.

10. **Does session lifecycle extend to saves, or only to sessions?**
    The brief's lifecycle criteria name sessions in every clause. This design gives saves a 365-day
    absolute TTL, a `save_expired` wire code that widens a closed union in a published package, and a
    sweep that hard-deletes them — settled as Open question 2 and logged, but derived from a criterion
    that did not ask for it. The consequence is that G2 permanently destroys the artifact this
    document elsewhere calls *"immutable and… the artifact a player would notice losing"*, on a clock
    the brief never set and before G3 exists to own or warn about it. **Also a brief conflict rather
    than a defect**, and it resolves either way in one edit: the brief admits saves to the lifecycle
    scope, or the sweep stops at sessions and `save_expired` comes out of the contract.

11. **What happens between now and the engine ratifying `concurrent_modification`?**
    Every conflict assertion in *Control flow* 3 — both contention proofs, the perturbation red-gate,
    the 409 mapping — depends on a ninth member of `SessionStoreErrorCode` that existed in no
    published engine version when this was written, and Open question 3 records that *"the vocabulary
    is still the engine's to ratify."* **It has since been ratified**: the engine ships the member,
    and `writeSession`'s `catch`, under that name at `0.8.0`. Startup asserts the contract's recorded engine version against the resolved package's,
    so an instance cannot be pointed at a branch build without regenerating the contract first.
    **The design's own position is that the engine PR is the first deliverable and the proofs that
    assert the code are sequenced behind it**, which is a statement about ordering and costs nothing;
    what is genuinely open is what G2 does if the engine ratifies a different name or a different
    brand shape. Nothing in this document changes on the name — the adapter throws a branded value and
    Dispatch maps a code — so the exposure is rework in `/contract` and one slice, not here.
