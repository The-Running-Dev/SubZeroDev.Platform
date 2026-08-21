# Contract — durable sessions (G2)

**Document status:** Contract. Derived from [`10-design.md`](10-design.md). Authoritative for the
artifacts and modules it describes; [`00-brief.md`](00-brief.md) stays authoritative for scope and
non-goals, and [`platform-identity.md`](../docs/docs/platform-identity.md) for what this repository
is.

Two languages, unchanged from G1. **TypeScript** with `strict` for the contract package and the Node
workload. **C#** with nullable reference types enabled for the .NET edge.

**[`g1/20-contract.md`](g1/20-contract.md) stays authoritative for everything it declares.** This
document declares only what durable state adds, and names every G1 declaration it amends — it never
restates one. A type that appears below unchanged from G1 appears because a G2 member was added to
it; a type that does not appear is unchanged and is still G1's.

**Invariant numbering continues G1's**, from 48. G1's 1–47 keep their numbers and their home; the
seven of them G2 amends are listed under [*Amended invariants*](#amended-invariants) rather than
rewritten in place, so a citation to a number resolves to exactly one statement.

> **This contract depends on one engine change**, which is G2's single engine deliverable and is
> declared under [*The engine seam G2 adds*](#the-engine-seam-g2-adds). Every conflict assertion in
> the design's proofs depends on it, and the design's Open question 11 records that the vocabulary is
> the engine's to ratify.

> **One count in [`10-design.md`](10-design.md) did not survive the source, and this contract took
> the source's side.** Open question 3 called `SessionStoreErrorCode` *"a closed union of seven
> members"* and `concurrent_modification` *"an eighth member"*; the decision log of 2026-08-12
> repeats it, and keeps it, because a dated entry records what was believed on its date. The union
> before the widening carried **eight** members — `unknown_session`, `unknown_save`,
> `storage_failure`, `unknown_campaign`, `invalid_state`, `unknown_kind`, `save_requires_migration`,
> `migration_failed` — so `concurrent_modification` is the **ninth**, which is what the engine ships
> at `0.8.0`. Nothing else in the design turned on the count: the widening carries a `core.reason.*`
> message obligation whichever ordinal it takes, which is the part the design was actually
> establishing. `10-design.md` was corrected to say so on 2026-08-19.

---

## Types

### The engine seam G2 adds

```ts
export type SessionStoreErrorCode =
  | "unknown_session"
  | "unknown_save"
  | "storage_failure"
  | "unknown_campaign"
  | "invalid_state"
  | "unknown_kind"
  | "save_requires_migration"
  | "migration_failed"
  | "concurrent_modification";
```

**Declared in the engine, widened by one member**, and carrying the same obligation every other
member carries: a registered `ReasonCode` with a shipped `core.reason.*` message. The engine's own
doc comment on the union states that obligation, so the widening is a message and a union member and
nothing more exotic.

```ts
export const SESSION_PERSISTENCE_CONFLICT = "SessionPersistenceConflict";

export interface SessionPersistenceConflict extends Error {
  readonly name: typeof SESSION_PERSISTENCE_CONFLICT;
}
```

**The brand is the `name` property and nothing else.** `SessionStoreError` already discriminates
itself by assigning `this.name`, so a name-carrying throw is the engine's existing idiom rather than
a convention G2 invents — and duck-typing on `name` survives a duplicated package copy, where
`instanceof` does not. **The spelling is the engine repository's to ratify**; this contract names it
so the G2 slices and the engine pull request describe one thing
([Additions](#additions-requiring-a-decision-log-entry), item 1).

**What the engine change is, exactly.** `writeSession`'s `catch` today is parameterless and rethrows
`SessionStoreError("session", "storage_failure")`, discarding the cause. It gains one branch: a
caught value whose `name` is `SESSION_PERSISTENCE_CONFLICT` raises
`SessionStoreError("session", "concurrent_modification")` instead. **Everything else thrown still
becomes `storage_failure`**, so every existing implementation of `SessionPersistence` is unaffected.
`writeSave`, `getSession` and `getSave` are untouched: saves have no second writer, and a read that
fails is an outage on any reading.

### Contract package — the widened transport codes

```ts
export type TransportErrorCode =
  | "malformed_payload"
  | "unsupported_version"
  | "unknown_operation"
  | "internal_failure"
  | "session_expired"
  | "save_expired";
```

**Six members**, two of them G2's. `TransportErrorCode` is closed and is the contract package's own
because no engine concept corresponds to either new code: the engine has one answer for an evicted
session and a session that never existed, correctly, and the distinction the brief requires is a host
lifecycle fact about a host-owned column.

`EngineErrorCode`, `WireErrorCode` and `HttpStatus` are **unchanged**. `concurrent_modification`
enters through `EngineErrorCode`, which is branded rather than enumerated precisely so the engine's
vocabulary has one home; `409` and `404` are already members of `HttpStatus`, so no status widens.

**The status mapping gains three entries** — `concurrent_modification` → `409`, `session_expired` →
`404`, `save_expired` → `404` — and the generator's error-coverage gate is what requires them rather
than this sentence.

### Workload — the store's own identifiers and constrained values

```ts
export type TenantId = string & { readonly __brand: "TenantId" };

export type EngineInstant = string & { readonly __brand: "EngineInstant" };

export type DatabaseInstant = Date & { readonly __brand: "DatabaseInstant" };

export type SessionRowVersion = bigint & { readonly __brand: "SessionRowVersion" };

export type SchemaName = string & { readonly __brand: "SchemaName" };
```

**`TenantId` is non-empty and, in G2, is one value.** The implicit tenant is a constant the store
supplies to every statement. Nothing resolves it, no request carries one, and no behaviour varies by
it — which is what keeps *tenancy behaviour* out while the key shape is right from the first
migration.

**`EngineInstant` and `DatabaseInstant` are two kinds of time and the types are what stop them being
confused.** `EngineInstant` is whatever the engine's `Clock.now()` returned, stored and returned as
the same string; under the replay profile it is the fixed instant, and a round trip that reformatted
it would break the replay. `DatabaseInstant` is the database clock's, never the process clock's, and
never enters a `StoredSessionRecord`.

**`SessionRowVersion` is never computed in TypeScript.** The store reads it, holds it, and passes it
back as a statement parameter; the guarded `update` increments it in SQL. It is `bigint` and not
`number` because the column is `bigint`, and the driver's `int8` parser is configured to produce
`bigint` — an unconfigured `pg` returns `int8` as a `string`, and a version silently typed `string`
would still compare equal on a round trip while making any future arithmetic on it a concatenation.

**`SchemaName` is the per-run PostgreSQL schema the proof harness creates and drops.** It exists so
run isolation has a type of its own and is visibly not `TenantId`.

### Workload — the durable rows

Every row type below is the store's internal shape. **None of them crosses the port**: the adapter
maps a row to a `StoredSessionRecord` or a `StoredSaveRecord`, which carry engine-owned members only.

```ts
export interface SessionRow {
  readonly tenantId: TenantId;
  readonly sessionId: string;
  readonly blob: string;
  readonly audience: ProjectionAudience;
  readonly attemptCounter: number;
  readonly replayCompatible: boolean;
  readonly engineCreatedAt: EngineInstant;
  readonly engineUpdatedAt: EngineInstant;
  readonly profileId: string | null;
  readonly version: SessionRowVersion;
  readonly engineVersion: SemanticVersion;
  readonly rowCreatedAt: DatabaseInstant;
  readonly rowUpdatedAt: DatabaseInstant;
  readonly expiresAt: DatabaseInstant;
}

export interface SaveRow {
  readonly tenantId: TenantId;
  readonly saveId: string;
  readonly campaignId: string;
  readonly blob: string;
  readonly savedAtSeq: number;
  readonly audience: ProjectionAudience;
  readonly profileId: string | null;
  readonly engineVersion: SemanticVersion;
  readonly rowCreatedAt: DatabaseInstant;
  readonly expiresAt: DatabaseInstant;
}

export interface ProfileRow {
  readonly tenantId: TenantId;
  readonly profileId: string;
  readonly formatVersion: number;
  readonly rowCreatedAt: DatabaseInstant;
  readonly rowUpdatedAt: DatabaseInstant;
}

export interface ProfileAchievementRow {
  readonly tenantId: TenantId;
  readonly profileId: string;
  readonly campaignId: string;
  readonly achievementId: string;
  readonly rowCreatedAt: DatabaseInstant;
}
```

**`ProjectionAudience` is the engine's own type** — `"player" | "ai"` at `0.8.0` — imported, never
re-declared. The column carries the constraint, so a value the engine does not name cannot be stored.

**`profileId` is `string | null` on the row and an *absent key* on the record.** The engine's
`StoredSessionRecord.profileId` is an optional member, and the design's rule is `null` ⇄ key absent,
never a member whose value is `undefined`. A record carrying `profileId: undefined` serializes
differently from one that omits the member, which is the whole reason the mapping is stated as a type
rule rather than left to a spread.

**`SessionRow.version` has no counterpart on `StoredSessionRecord` and never will.** The engine
cannot read it, cannot supply it, and cannot be made to depend on it, which is the property that
makes the lock the store's and the design's adjudication load-bearing rather than stylistic.

**`SaveRow` carries no `version`.** `saveGame` mints a fresh `saveId` on every call and writes
through `writeSave` only — verified at `0.8.0` — so a save row has exactly one writer for its whole
life and an optimistic lock would guard nothing.

### Workload — the guarded write and the lifecycle classification

```ts
export type GuardedWriteOutcome = "applied" | "conflict" | "expired";

export type LifecycleState = "live" | "expired" | "absent";
```

**`GuardedWriteOutcome` has exactly three members and the third is why the re-read is a
classification.** Zero rows affected is re-read: a different `version` is `conflict`, the same
`version` past its `expires_at` is `expired`, and an absent row is `conflict` because the caller's
read is no longer authoritative either way. **A re-read that itself fails is `conflict`**, never a
storage outcome — zero rows has already established the one fact the caller acts on. Anything the
three do not name is `conflict` too; the members are the classification, and `conflict` is its floor.

**`expired` and `conflict` leave the adapter as the same branded throw, and reach the wire as the
same `concurrent_modification` at `409`.** `writeSession`'s `catch` discriminates on `name` and
nothing else, so the port has exactly one channel out; a second brand is a second engine change
bought to ease persistence, which the brief names as a non-goal. **This type is therefore diagnostic
rather than a routing decision** — it says *why* zero rows were affected, is carried on the thrown
value as an inert member, and is read by this workload's own tests and by nothing on the wire.
**The cost:** a caller whose write lost to expiry is told to re-read and decide rather than that the
session expired, and learns which it was on that re-read — an expired row reads as absent, the engine
raises `unknown_session`, and the lifecycle probe answers `session_expired` at `404`.

**`LifecycleState` is a classification and never data.** It answers *does a row exist for this id,
and has it expired* and returns no blob, no scene and no record.

```ts
export interface LifecycleProbe {
  session(sessionId: string): Promise<Outcome<LifecycleState, StoreError>>;
  save(saveId: string): Promise<Outcome<LifecycleState, StoreError>>;
}
```

**It is not a store port and it is not an operation.** It has no route, no MCP tool and no row in the
operation table, and the generator's arity gate fails if anyone gives it one. It is reachable from
Dispatch and from nowhere else.

**It is composed behind the same seam `SessionPersistence` and `ProfileStore` are**, so that whatever
decorates them decorates it. Nothing in G2 depends on that; it is stated so G3's authorization
decorator inherits the constraint rather than rediscovering that an undecorated probe is an existence
oracle.

**A probe that fails is read as `absent`, so the engine's own code passes through verbatim**
([Unresolved 1](#1-what-dispatch-answers-when-the-lifecycle-probe-itself-fails), settled
2026-08-12). The failure arm exists because the probe crosses a module boundary and every error that
does is an `Outcome` failure; what Dispatch does with it is to answer the less specific of the two
true things rather than the more alarming of them.

### Workload — the store provider and the per-request seam

```ts
export interface StoreProvider {
  forRequest(): SessionStore;
}
```

**One method, and the two configurations differ only in what it returns.** The durable configuration
constructs a fresh persistence adapter with an empty read-version map and composes a session layer
over it with the process-lived engine, registry, record-id source and clock; the in-memory
configuration returns G1's single long-lived layer on every call.

**A cache that cannot outlive one request is what makes the compare-and-swap the only concurrency
mechanism in the system.** The type is the seam that makes it so: Dispatch cannot hold a store across
requests because it is never handed one that lasts.

```ts
export interface ReadVersionMap {
  observed(sessionId: string): SessionRowVersion | undefined;
  record(sessionId: string, version: SessionRowVersion): void;
  advance(sessionId: string, version: SessionRowVersion): void;
}
```

**One per request, and it dies with the request** — which is the entire reason it cannot go stale.
`sessions.put` for an id the map holds is a guarded update asserting that version; for an id it does
not hold, an insert. `advance` is called only after a write lands, so the map's value and the row's
value are the same event.

### Workload — configuration

```ts
export interface StoreConnection {
  readonly connectionString: string;
  readonly poolSize: number;
  readonly connectTimeoutMs: number;
  readonly schema: SchemaName | null;
}

export interface LifecycleBounds {
  readonly sessionIdleTtlSeconds: number;
  readonly saveTtlSeconds: number;
  readonly retentionHorizonSeconds: number;
  readonly sweepIntervalSeconds: number;
  readonly sweepStatementTimeoutMs: number;
}

export interface DurableStoreConfiguration {
  readonly connection: StoreConnection;
  readonly bounds: LifecycleBounds;
  readonly readWritePauseMs: number;
}

export type StorageProfile =
  | { readonly kind: "in-memory" }
  | { readonly kind: "durable"; readonly store: DurableStoreConfiguration };

export interface WorkloadConfiguration {
  readonly listen: ListenEndpoint;
  readonly determinism: DeterminismProfile;
  readonly otlpEndpoint: string | null;
  readonly storage: StorageProfile;
}
```

**The discriminated union is G1's determinism trick applied a second time.** No code path holds an
in-memory profile with a connection string, so "the in-memory configuration reaches no database" is a
type-level fact rather than a branch anyone has to keep correct.

**Every member of `LifecycleBounds` is configuration, and all five carry a stated production
default.** The three that bound a *row's life* are 30 days, 365 days and 30 days; sessions and saves
deliberately do not share a number. The two that bound the *sweep's own work* are a **1-hour
interval** and a **5-second statement timeout**, stated apart from the three because they answer a
different question and are varied by a different set of proofs
(`design/90-decisions.md`, "The sweep's two bounds are the sweep's, not a row's").

The defaults are values, not the mechanism. **The three row bounds are what every proof runs at**:
expiry is asserted by seeding `expires_at` into the past rather than by shortening a TTL and
waiting, so the replay's requirement — that no session expire between two of its ten steps, which
would report a serialization failure for a clock problem — holds by construction rather than by each
proof choosing the right bounds. Only `retentionHorizonSeconds` is varied among them, by the sweep
proofs. **The two sweep bounds are varied deliberately and often**: a sweep proof cannot wait an
hour for a tick, and S13.4 drives the statement timeout below a held lock precisely to prove the
bound is enforced. Neither is a bound the design reasons about — that is what makes them the two
values a proof may move freely.

**`retentionHorizonSeconds` is required to exceed any request's duration by a wide margin**, and that
requirement is what makes it impossible for a sweep to fall between a live request's read and its
write.

**`readWritePauseMs` is the perturbation seam and its default is `0`.** It pauses the store adapter
between a session read and the corresponding write, and it is what makes the contention race
deterministic rather than hoped for. A test asserts it is inert at `0`, on the same terms G1 asserts
that the default profile writes no dump: a diagnostic that is merely usually off is on.

**`StoreConnection.schema` is `null` outside the proof harness.** It names the per-run PostgreSQL
schema the durable replay is isolated by. **The tenant column is never used for run isolation** —
that is tenancy behaviour, and it is the shortcut a durable store makes tempting.

### Workload — readiness

```ts
export interface ProbeResult {
  readonly status: ProbeStatus;
  readonly detail?: string;
}

export interface ProbeSurface {
  liveness(): ProbeResult;
  readiness(): Promise<ProbeResult>;
}
```

**`readiness` becomes asynchronous and that is forced rather than chosen.** It evaluates the store on
each probe, so it reports whether the store is usable *now* rather than whether it was usable once —
a latch on startup would leave the workload reporting ready through exactly the outage the edge's new
readiness probe was introduced to surface. **The stated cost is that readiness can flap.**
`liveness` never consults the store and stays synchronous.

**`ProbeResult.detail` amends G1's declaration (`g1/20-contract.md`), which had no such field.**
Whenever the durable branch reports unhealthy it names the condition, and there are two such
moments rather than one. **Before the store has ever connected** it names what is holding startup
back — a migration's advisory lock held past its bound, a failed migration naming which one, or the
store unreachable or at the wrong isolation level (`design/90-decisions.md`, "Startup migrations").
**After it has connected and since degraded** it names what the readiness probe's own `check()`
classified — the pool out of connections, or a statement that failed. Present only on an unhealthy
result; absent everywhere else, including every G1-era caller that never populated it.

**Which conditions each moment can name is decided by where each is reachable, not by taste.**
`PoolExhausted` is reachable only in the second: at startup the pool holds no checked-out client, so
`max` cannot be hit and a `connectTimeoutMs` expiry there is the server not answering — which
`openDurableStore` classifies `Unreachable`. Naming an exhausted pool at startup would therefore
report the one condition that cannot occur there in place of the one that did.

**`detail` reaches the wire.** The readiness endpoint's body carries it whenever the probe does, and
omits the member — never `null` — when it does not. That is stated here because it is the field's
only point: an operator reads the endpoint, not the in-process `ProbeResult`, and a `detail` computed,
mapped and asserted in-process but dropped at the transport is machinery with no consumer. **Nothing
parses it.** The edge's own readiness check reads the status code alone, so this is a diagnostic
member and not a shape a caller may branch on.

**A migration still running is deliberately not among them.** The listener binds only once the first
startup attempt settles, and that attempt runs the migration inside itself
(`design/90-decisions.md`, 2026-08-20, "Migrating inside the first startup attempt is what bounds the
bind"), so no probe is ever served while a migration is in flight and no detail string could report
one. Naming that condition would require binding the listener ahead of the migration — the ordering
that decision considered and declined.

### Workload — the sweep

```ts
export interface SweepResult {
  readonly sessionsRemoved: number;
  readonly savesRemoved: number;
}
```

**The sweep is two plain `delete`s in one transaction and is idempotent**, so two instances sweeping concurrently need no
coordination and none is added. It removes only rows whose `expires_at` is older than the retention
horizon; a row that has merely expired is retained, because an expired-but-retained row is what lets
the wire answer `session_expired` rather than `unknown_session`.

**A failed sweep reports what it removed, which on failure is nothing.** The design asks for "the row
count it did not remove"; that number is not knowable from a failed `delete` without a second query
against a store that has just failed one, so the honest report is the failing statement and a removed
count of zero ([Additions](#additions-requiring-a-decision-log-entry), item 5).

### Proof harness — the durable replay, the two instances, the conformance suite

```ts
export interface RunSchema {
  readonly name: SchemaName;
  drop(): Promise<Outcome<void, HarnessError>>;
}

export function createRunSchema(
  connectionString: string,
): Promise<Outcome<RunSchema, HarnessError>>;
```

**A pristine schema per run, created and dropped by the harness.** The counting `RecordIdSource`
mints `counting-session-id-0` on every run, so a second run against a dirty schema is a primary-key
violation in the middle of the replay.

```ts
export interface WorkloadInstance {
  readonly baseAddress: string;
  shutdown(): Promise<Outcome<void, HarnessError>>;
}

export interface TwoInstanceOptions {
  readonly connectionString: string;
  readonly schema: SchemaName;
  readonly readWritePauseMs: readonly [number, number];
}

export function spawnInstances(
  options: TwoInstanceOptions,
): Promise<Outcome<readonly [WorkloadInstance, WorkloadInstance], HarnessError>>;
```

**`spawnInstances` is both a test entry point and the README's documented command.** The brief
requires the repository to tell a reader how to run two instances against one store, and the compose
file is barred from that clause — it provisions the dependency and starts no instance. One artifact
serves both, because a documented command nothing runs is the failure the fresh-clone job exists to
prevent.

**`readWritePauseMs` is a pair because the two instances are configured differently**: the instance
under test carries the pause, the second is sent inside it. Nothing else distinguishes them — the
instances are anonymous and interchangeable, which is what makes the two-instance proof mean
anything.

**Each `WorkloadInstance` is a separate operating-system process, and that is load-bearing rather
than incidental.** §6.1's failure is *between processes*; two compositions sharing one event loop,
one module registry and one heap cannot establish that the compare-and-swap survives the separation
the failure is defined by. `spawnInstances` therefore spawns the harness's own process entry point,
the same one the hosted replay target spawns, and every input a child needs travels as an
environment variable because that is the only channel a separate process has. **`shutdown` is a byte
on the child's stdin, never a signal** — on Windows libuv raises every signal name as
`TerminateProcess`, and a hard kill would leave the child's pool connections open to race the
caller's schema drop.

```ts
export interface ConformanceTarget {
  readonly label: "in-memory" | "durable";
  readonly persistence: SessionPersistence;
  readonly profiles: ProfileStore;
  seedCorruptProfile(profileId: string): Promise<void>;
  seedProfileWriteFailure(profileId: string): Promise<void>;
}

export function runPortConformance(
  target: ConformanceTarget,
): Promise<Outcome<void, ConformanceError>>;
```

**One assertion set, run over two targets**, which is what makes it a conformance suite rather than a
second set of unit tests. It covers `sessions.get/put`, `saves.get/put`, `saves.delete` and
`profiles.load/save` — **seven methods, not six**: the engine's `SaveRecordStore` declares `delete`
beside `get` and `put`, and no operation in the contract's table reaches it, which is precisely why
it needs the suite. The suite also covers the
three profile outcomes; the set-union merge including the divergence the durable `save` deliberately
carries; and the round trip that keeps host metadata out of game state.

**The two seeding methods exist because the two targets reach the same outcome by different
mechanisms** — a malformed raw entry against the engine's in-memory profile store, a row with an
unrecognised `format_version` against the durable one. `seedProfileWriteFailure` must break the
profile write and **only** the profile write, since the criterion it serves is that a committed
session write survives it.

**The reference target's `persistence` is the workload's own map-backed implementation, not the
engine's.** The engine exports the `SessionPersistence` *type* and no implementation of it — its
in-memory session store keeps private `Map`s and treats `persistence` as an optional host port
(verified at `0.8.0`). `createInMemoryProfileStore` *is* an engine-supplied `ProfileStore`, so half
of the design's *"the engine's in-memory implementations"* is literal and half resolves to G1's
`inMemoryPersistence()` ([Additions](#additions-requiring-a-decision-log-entry), item 2).

**The answer the suite returns is "yes for six methods, conditionally for the seventh."**
`profiles.save` is the one method where the two implementations are asserted to *differ* — the
durable store's merge is additive where the engine's replaces — so for that method the shared
assertion cannot establish conformance. What stands in its place is a property of the engine's
*caller*, asserted directly: every `save` the engine issues carries the loaded set plus additions,
read off `upsertAchievements` at `0.8.0`.

---

## Persisted schemas

**G2 is this repository's first schema.** There is no existing data on any table below, so each
table's migration story begins at creation; what governs every migration *after* the first is the
standing rule in the last subsection.

All four tables live in one PostgreSQL database, in one schema, provisioned by the committed compose
file and brought to head by `node-pg-migrate` under its own advisory lock.

### `session`

| Column | Type | Null | Owner | Notes |
|---|---|---|---|---|
| `tenant_id` | `text` | not null, default the implicit tenant | Host | Primary-key member |
| `session_id` | `text` | not null | Engine | Primary-key member; minted by `RecordIdSource` |
| `blob` | `text` | not null | Engine | The canonical serialization, byte for byte |
| `audience` | `text` | not null | Engine | `check (audience in ('player','ai'))` |
| `attempt_counter` | `integer` | not null | Engine | Stored because the record carries it. **Not the lock** |
| `replay_compatible` | `boolean` | not null | Engine | |
| `engine_created_at` | `text` | not null | Engine | The engine's `Clock` output, verbatim |
| `engine_updated_at` | `text` | not null | Engine | Likewise |
| `profile_id` | `text` | null | Engine | `null` ⇄ key absent on the record |
| `version` | `bigint` | not null | **Host** | **The optimistic lock.** `1` on insert, `+1` on every accepted update |
| `engine_version` | `text` | not null | Host | The engine package version that produced `blob` |
| `row_created_at` | `timestamptz` | not null, default `now()` | Host | Database clock |
| `row_updated_at` | `timestamptz` | not null | Host | Database clock, on every accepted write |
| `expires_at` | `timestamptz` | not null | Host | Derived in SQL as `now() + <session idle TTL>` on every accepted write |

- **Primary key** `(tenant_id, session_id)`.
- **Index** on `(expires_at)`, for the sweep. It is the only non-key access path any statement in G2
  takes.
- **`blob` is `text`, never `json` or `jsonb`.** `jsonb` reorders members, collapses duplicates and
  renormalises numbers; a blob that round-trips through it is not the same bytes, and byte identity
  is the effort's first criterion. `text` also makes the column opaque to the database, which is the
  correct relationship — the store must not be able to reason about game state.
- **The engine's two instants are `text` and the host's three are `timestamptz`.** Storing the
  engine's strings as timestamps would reformat them on read, so under the replay profile the fixed
  instant would come back in the database's rendering rather than the engine's.
- **Migration story:** created by the first migration, empty. No backfill exists and none is
  possible; `engine_version` in particular is the one host column that cannot be reconstructed for a
  row written before it existed, which is why it is taken now.

### `save`

| Column | Type | Null | Owner |
|---|---|---|---|
| `tenant_id` | `text` | not null, default the implicit tenant | Host |
| `save_id` | `text` | not null | Engine |
| `campaign_id` | `text` | not null | Engine |
| `blob` | `text` | not null | Engine |
| `saved_at_seq` | `integer` | not null | Engine |
| `audience` | `text` | not null, `check (audience in ('player','ai'))` | Engine |
| `profile_id` | `text` | null | Engine |
| `engine_version` | `text` | not null | Host |
| `row_created_at` | `timestamptz` | not null, default `now()` | Host |
| `expires_at` | `timestamptz` | not null | Host — derived as `now() + <save TTL>` at write |

- **Primary key** `(tenant_id, save_id)`. **Index** on `(expires_at)`.
- **No `version` column**, and the absence is a decision — see `SaveRow` above.
- **`saves.put` is an upsert, not a bare insert**, because the port's method is `put` and an
  implementation that failed on a re-put would be narrower than the interface it claims to fill. **On
  a re-put every host column is recomputed**: a re-put is a write, and a host column describing the
  first one would then describe nothing.
- **Migration story:** as `session`.

### `profile`

| Column | Type | Null | Owner |
|---|---|---|---|
| `tenant_id` | `text` | not null, default the implicit tenant | Host |
| `profile_id` | `text` | not null | Engine |
| `format_version` | `integer` | not null | Engine — `1` at this release |
| `row_created_at` | `timestamptz` | not null, default `now()` | Host |
| `row_updated_at` | `timestamptz` | not null | Host |

- **Primary key** `(tenant_id, profile_id)`. No `expires_at` and no index beyond the key.
- **`format_version` exists so `profile_corrupt` stays a reachable, testable outcome** against a
  normalised store — the same reason the engine's in-memory profile store keeps raw entries.
- **Migration story:** created empty. **`format_version` may not be bumped in the release that first
  writes the new format.** A rolling deploy would otherwise have a newer instance write `2`, an older
  instance classify it `profile_corrupt`, and the same player hold achievements on one instance and
  none on the other for the length of the deploy — silently, since `profile_corrupt` is a warning on
  a `200`. Read support ships first; write support ships in a later release.

### `profile_achievement`

| Column | Type | Null | Owner |
|---|---|---|---|
| `tenant_id` | `text` | not null, default the implicit tenant | Host |
| `profile_id` | `text` | not null | Engine |
| `campaign_id` | `text` | not null | Engine |
| `achievement_id` | `text` | not null | Engine |
| `row_created_at` | `timestamptz` | not null, default `now()` | Host |

- **Primary key** `(tenant_id, profile_id, campaign_id, achievement_id)`. **Append-only**, written by
  `insert … on conflict do nothing`.
- **Achievement ids are unique only within a campaign**, which is why `campaign_id` is in the key.
- Set union is *conflict-free*: two instances awarding two different achievements to one profile at
  the same moment both land, with no lock and no lost write.
- **Migration story:** created empty. **Neither profile table has an `expires_at` and the sweep does
  not touch them.** The accepted consequence is that both grow monotonically for the life of the
  deployment — there is no principal to scope a bound to until G3, and a profile is the row an
  account surface will own.

### Schema bookkeeping and the rule for every migration after the first

One migrations table, owned by `node-pg-migrate` and not by this contract. A migration run applies in
a single transaction — the whole run, not one transaction per migration — under the tool's advisory
lock, so two instances starting together cannot both apply one migration and no partial schema
survives a failure. **The run-wide transaction is the stronger of the two shapes**: a multi-migration run that
fails halfway leaves nothing applied, where per-migration transactions would leave the earlier ones
in place. It is what the tool does by default and what this workload takes; the constraint that
matters is the outcome, not the granularity.

**Every migration after the first must be backward compatible with the previously deployed code**,
because two instances share one store and are not restarted atomically. Additive columns with
defaults; never a rename or a narrowing in one step. **This is a rule about data formats as well as
column shapes** — `profile.format_version` above is the same two-step.

### Artifacts carried across a process boundary

G1's five — the contract package, the authored row set, the replay fixture, the golden transcript and
the determinism dump — are unchanged, and [`g1/20-contract.md`](g1/20-contract.md) stays their home.
**The fixture and the golden transcript gain no rows**: the same ten operations are replayed against
a different store, and adding a profile-carrying step would have given the byte-identity proof a
second job.

Two are added:

| Artifact | Written by | Read by | Migration story |
|---|---|---|---|
| **The compose file** | A human, committed under `workloads/game-service/` | The `game-service` CI job and the README's reader, by the identical command | Reviewed as a diff. It provisions the store and **nothing else** — it starts no workload instance, supervises none, and describes no deployment. It pins the server encoding to `UTF8` and the initdb locale explicitly rather than inheriting the image's |
| **The migration set** | A human, committed, ordered | `node-pg-migrate`, at every startup | Append-only. A migration that has been applied anywhere is never edited; a correction is a new migration. The backward-compatibility rule above governs each one |

---

## Public signatures

Internal helpers are out of scope. Everything below crosses a module boundary named in the design.
A signature G1 declared and G2 does not change is not repeated.

### Migrations — workload

```ts
export function migrateToHead(
  connection: StoreConnection,
): Promise<Outcome<void, MigrationError>>;
```

**One call: bring this schema to head.** It runs under the migration tool's own advisory lock, which
is the property that makes two instances starting together safe and is the reason the tool was taken
rather than a runner hand-rolled for two tables.

### Store — workload

```ts
export interface DurableStore {
  persistenceForRequest(): SessionPersistence;
  readonly profiles: ProfileStore;
  readonly lifecycle: LifecycleProbe;
  readonly serialization: StoreSerializationHandle;
  check(): Promise<Outcome<void, StoreError>>;
  sweepOnce(): Promise<Outcome<SweepResult, StoreError>>;
  close(): Promise<void>;
}

export function openDurableStore(
  configuration: DurableStoreConfiguration,
  engineVersion: SemanticVersion,
): Promise<Outcome<DurableStore, StoreError>>;
```

**`persistenceForRequest` is the only per-request member.** The pool, the schema, the profile store,
the probe and the serialization handle are all process-lived; what a request gets is a fresh adapter
holding an empty `ReadVersionMap`.

**`openDurableStore` takes the resolved engine version because the store stamps it**, and it takes
nothing else from composition. It asserts `read committed` on connect rather than inheriting it: at
`repeatable read` or `serializable` the guarded `update` raises a serialization failure instead of
reporting zero rows, every conflict would arrive as `storage_failure` and a `503`, and the one
criterion the brief says no work on this side can otherwise deliver would fail by configuration.

**`check` is what readiness calls, on every probe.** It evaluates the store rather than reporting a
remembered outcome.

**`sweepOnce` is the statement; the timer is Composition's.** The sweep runs under a statement
timeout, catches its own driver errors, and returns them — it never escapes a timer as an exception.

**`close` releases the pool and returns nothing.** There is no failure a caller could act on at
shutdown, and a close that reported one would only complicate the exit path.

**Store imports the engine's *type* declarations and never its runtime.** It maps columns to a
`StoredSessionRecord`; it never deserialises, never validates game state, and never calls the engine.

### Composition — workload

```ts
export interface ComposedWorkload {
  readonly stores: StoreProvider;
  readonly lifecycle: LifecycleProbe;
  readonly serialization: StoreSerializationHandle;
  readiness(): Promise<ProbeResult>;
  close(): Promise<void>;
}

export function compose(
  configuration: WorkloadConfiguration,
  contract: ContractPackage,
): Promise<Outcome<ComposedWorkload, CompositionError>>;
```

**`compose`'s parameters are unchanged and its result is widened.** It still owns the engine-version
assertion, which is why it takes the contract at all. G1's `ComposedWorkload.store: SessionStore`
becomes `stores: StoreProvider`; every other member is new.

**`compose` returns successfully when the store is unreachable.** The process stays up, reports live,
reports **not ready**, and retries with backoff — a host that refuses to start tells an operator that
something is wrong, while a host that starts and names its failing readiness check tells them *what*.
`StoreUnavailable` is therefore a readiness condition and not a `CompositionError`; the
`CompositionError` variants are the ones no retry can fix.

**Composition supplies a lifecycle probe for both configurations.** The in-memory one classifies
every id as `absent`, so `unknown_session` and `unknown_save` pass through verbatim and Dispatch
carries no branch on which store was built. A no-op implementation rather than a conditional is the
point: the alternative puts configuration-dependent behaviour into the module whose job is to be
transport-neutral.

#### The storage seam

```ts
export interface StorageSeam {
  readonly persistence: SessionPersistence;
  readonly profiles: ProfileStore;
  readonly lifecycle: LifecycleProbe;
}

export type StorageDecorator = (seam: StorageSeam) => StorageSeam;

export const IDENTITY_STORAGE_DECORATOR: StorageDecorator;

export function composeStorageSeam(seam: StorageSeam, decorate?: StorageDecorator): StorageSeam;
```

**This is invariant 74 made into a type.** The invariant says the lifecycle probe is composed behind
the same seam `SessionPersistence` and `ProfileStore` are; a seam nothing names is a convention, and
a convention is what a later effort extends to two of the three ports and not the third. Every one of
the three ports this workload builds — for **both** storage profiles, at every call site — passes
through `composeStorageSeam` on its way to a caller, so a decorator wraps all three by construction
rather than by being remembered.

**G2 applies no decorator, and `IDENTITY_STORAGE_DECORATOR` is the only one that ever runs.** The
seam exists for G3: the brief's *Lifespan* has G3 wrap these stores with an authorization decorator,
and the probe is not a store port, so a decorator over `SessionPersistence` and `ProfileStore` alone
would leave G3 an undecorated existence oracle answering *live / expired / absent* for any id. That
constraint is stated here rather than in G3 because G3 cannot discover it.

**The three ports enter as one value, not three parameters**, which is what makes the decorator's
coverage checkable: a seam member added later is a compile error at every implementation of
`StorageDecorator` rather than a port that quietly goes undecorated.

**What the seam guarantees is coverage of all three ports, not that all three live ports ever meet
in one grouping.** The in-memory branch composes once, with all three live. The durable branch
composes twice: once process-lived to obtain the lifecycle probe, and once per request to obtain the
persistence and profile ports — because invariant 69 forbids rebuilding the probe per request, while
`persistenceForRequest()` requires exactly that. Each call still passes a full `StorageSeam`, so no
member escapes the decorator; but in the process-lived call the persistence and profile members are
the unavailable placeholders, and in the per-request call the lifecycle member's result is discarded.
**G3's decorator must therefore expect to be applied more than once per process, to see placeholder
members in some applications, and to carry no state between them.** That is stated here for the same
reason the seam itself is: G3 cannot discover it.

**Composition owns the sweep timer**, calls `sweepOnce`, and never starts a tick while its
predecessor is still running. The in-memory configuration starts no timer.

`writeDeterminismDump` is unchanged in signature; the durable configuration's
`StoreSerializationHandle` reads
`select session_id, blob from session where tenant_id = $1 order by session_id collate "C"`, and the
same for saves.

**The dump's ordering is pinned to `collate "C"` and that is load-bearing.** Ordering `text` under a
locale-aware collation is locale-dependent, and the replay's ids are hyphen-and-digit dense; an
unpinned collation makes the ordered blob set depend on the database image's locale, so two runs
could differ in *order* while agreeing on every byte. That failure presents as a byte-identity
failure, which is the one signal in the suite that must mean exactly one thing.

### Dispatch — workload

`createDispatcher` is declared in `workloads/game-service/src/dispatch.ts`.

**Dispatch takes the provider and the probe, never the composition**, so it still has no path to the
serialization handle. `Dispatcher` and `DispatchOutcome` are unchanged: the conflict arrives as an
`EngineErrorCode` like every other engine reason code, and the two expiry codes are raised by
Dispatch itself after consulting the probe.

**Expiry classification happens only on the failure path.** When the engine raises `unknown_session`
or `unknown_save`, Dispatch consults the probe; expired-and-retained becomes `session_expired` or
`save_expired`, and genuinely absent — or swept past the horizon — passes the engine's own code
through verbatim.

**Dispatch retries nothing.** Not on conflict, not on `storage_failure`, nowhere. A retried
`submitAction` is a second action, and merging two is explicitly unavailable.

### Probes — workload

```ts
export interface ProbeGate {
  readonly surface: ProbeSurface;
  markSurfacesBuilt(): void;
  markListening(): void;
}

export function createProbeSurface(readiness: () => Promise<ProbeResult>): ProbeGate;
```

**Readiness is now the conjunction of three things** — surfaces built, listener bound, and the store
answering — and the third is supplied as a thunk so the probe surface never learns what a store is.

### Proof harness — test scope

```ts
export function runDurableReplay(
  fixture: ReplayFixture,
  target: HostedTarget,
  schema: RunSchema,
): Promise<Outcome<RunResult, ReplayError>>;

export function assertNonEmpty(
  snapshot: StoreSerializationSnapshot,
  expectedSessions: number,
  expectedSaves: number,
): ComparisonResult;
```

**`assertNonEmpty` runs before comparison A, not instead of it.** Two empty ordered sets compare
byte-identical, so a dump that read the wrong schema would pass comparison A while comparison B
passed on its own merits — the responses were served correctly and only the dump was misdirected.
The counts are asserted against the fixture's own expected numbers rather than against zero, since
"not empty" is satisfied by one row as easily as by all of them.

`runInProcess`, `runHosted`, `compareSerializations`, `compareTranscripts` and `readDeterminismDump`
are G1's and are unchanged. **G1's in-memory replay stays in the suite and stays green** — two proofs
passing is not evidence that the first still does.

### The edge — .NET

```csharp
public sealed record GameEdgeOptions
{
    public required Uri WorkloadBaseAddress { get; init; }
    public required TimeSpan ForwardTimeout { get; init; }
    public required TimeSpan ReadinessTimeout { get; init; }
}

public interface IGameWorkloadProbe
{
    Task<Result<EdgeError>> ProbeReadinessAsync(CancellationToken cancellationToken);
}
```

**One change, in one place.** The edge's readiness check probes the workload's **readiness** endpoint
rather than its liveness endpoint: with a durable store a workload can be alive and unable to serve,
and an edge that reports ready while every forward will `503` tells an operator less than nothing.

`GameEdgeOptions.LivenessTimeout` is **renamed** to `ReadinessTimeout` — a property named for
liveness that bounds a readiness probe is a name that will be read as the thing it is not. The
rename and the corresponding `appsettings.json` key change land in one commit
([Additions](#additions-requiring-a-decision-log-entry), item 4).

`GameWorkloadReadinessCheck` keeps `Unhealthy` + `Required` and `TouchesExternalDependency = true`,
for G1's reason: one backend, one job. `IGameWorkloadForwarder`, `ForwardedRequest`,
`ForwardedResponse` and `MapGameWorkloadForwarding` are unchanged. **Nothing else at the edge moves**
— no load balancing across the two instances, no session affinity, no streaming, no package boundary.
**The two-instance contention proof addresses the two workload instances directly, not through the
edge.**

---

## Error semantics

Every variant below is a value with a stable `code`, and each module's error type is a discriminated
union on `code`. **No bare exceptions and no string errors cross a module boundary.** The single
standing exception is the engine's own `SessionStoreError`, which is thrown because no `SessionStore`
signature has an error channel — and, from G2, the branded `SessionPersistenceConflict` the store
adapter throws for the engine to recognise. Both are caught at the boundary and never travel further
as exceptions.

### Store — `StoreError`

| Variant | Raised when | Retryable | Caller does |
|---|---|---|---|
| `Unreachable` | The pool cannot connect, or a connection is lost mid-statement | **Yes** — the condition is transient by nature | Readiness reports unhealthy; a request in flight becomes `storage_failure` → `503` |
| `PoolExhausted` | A connection acquisition exceeds its timeout | **Yes** | As above. The pool is sized by configuration with a stated default and **is not tuned** — performance is a binding non-goal |
| `IsolationLevelUnsupported` | The connection the store checks at pool open does not report `read committed`. The store **checks and refuses**; it never sets the level, because a store that silently corrected a misconfiguration would hide it | **Yes**, on the shared startup loop | The process stays up and not-ready, naming the isolation level found, and retries — `compose()`'s shape is *come up not ready and keep trying*, and the check runs fresh on every attempt, so a server or pooler corrected underneath a running process is picked up without a restart. **The accepted cost:** a misconfiguration nobody corrects re-opens a pool at the retry interval indefinitely |
| `StatementFailed` | A driver error on a `select`, `insert`, `update` or `delete` that is not one of the above | **No** | `storage_failure` → `503` on the serving path; a logged failure and a retry on the next tick on the sweep path. **On the sweep path the variant names the failing statement**, on the same footing as `RowUndeserializable` naming its column and for the same reason — the sweep reaches no readiness check and no request, so its log line is the whole of its observability, and a bare classification does not say which of the two `delete`s failed |
| `IdCollision` | A primary-key violation on a `createSession` or `loadGame` insert | **No** | `storage_failure` → **503**, *not* the conflict code. A collision is a storage anomaly, not a lost update, and conflating them would make the conflict code mean two things |
| `RowUndeserializable` | A `select` returns a row whose columns do not satisfy their declared types | **No** | `storage_failure` → **503**, and the variant names the offending column for a log line. This is store corruption and a caller cannot fix it, but the workload has no channel to say so: `sessions.get` and `saves.get` return a record or throw, and the engine's own `getSession`/`getSave` convert **every** throw from the port into `storage_failure`. Reaching `internal_failure` would need a second branded throw the engine recognises — a cross-repository change bought to ease persistence, which the design names as a non-goal. The distinction an operator needs is on the row: `engine_version` separates a version skew from corruption after the fact |

**`StoreError` never carries a conflict.** A conflict is not a store failure — it is a successful
statement that matched no row — and it leaves the adapter as the branded throw, not as an `Outcome`
failure. That separation is what keeps `409` and `503` answers to different questions.

**A `StoreError` on the read-version re-read is not raised at all.** Zero rows affected has already
established the one fact the caller acts on, so the classifier answers `conflict` and swallows its
own driver error. Letting it escape would convert a known non-outage into a `503` precisely when the
store is degraded and races are most likely, defeating the criterion the mechanism was added to
serve.

### Migrations — `MigrationError`

| Variant | Raised when | Retryable | Caller does |
|---|---|---|---|
| `Unreachable` | The runner cannot connect | **Yes** | Startup retries with backoff, reporting not ready. No partial schema exists — the whole run applies in one transaction |
| `LockTimeout` | The advisory lock is not acquired within the runner's bound | **Yes** | As above. The other instance is mid-migration; the next attempt finds the schema at head |
| `MigrationFailed` | A migration's SQL fails | **Yes**, on a backing-off loop | The process stays up and not-ready, naming the migration in `ProbeResult.detail`, and retries — `compose()`'s shape is *come up not ready and keep trying*, and a migration that failed on a lock, a full disk or a permission not yet granted recovers without a process restart. Consecutive failures back the shared retry loop off exponentially to a cap, because `node-pg-migrate`'s advisory lock is one id for the whole **database**: a schema whose migration keeps failing must not re-request at the unbacked-off interval forever, or an unrelated schema's own migration queues behind it and times out (`design/90-decisions.md`, *Startup migrations*). **The accepted cost:** a permanently broken migration keeps re-requesting that lock at the cap, indefinitely |

### Composition — `CompositionError`

G1's three variants are unchanged. One is added.

| Variant | Raised when | Retryable | Caller does |
|---|---|---|---|
| `StorageConfigurationInvalid` | A `durable` profile carries an unusable connection string, a non-positive pool size, or a retention horizon not greater than the workload's assumed forward timeout — a constant, not the edge's own configured value, which no signature threads into the workload (`90-decisions.md`, 2026-08-19, "The retention-horizon check is enforced against an assumed forward timeout") | **No** | Fails startup, naming the setting. This is the one storage condition that aborts rather than degrading to not-ready, because no amount of waiting makes an invalid setting valid |

**Store unreachability is deliberately not here.** It is a readiness condition, reported through
`ProbeSurface.readiness` and retried with backoff, for the reason stated with `compose`.

### Dispatch — the two new outcomes

`DispatchOutcome` is unchanged in shape. Two codes are new on its error arm.

| Code | Origin | Status | Retryable | Caller does |
|---|---|---|---|---|
| `concurrent_modification` | The engine, recognising the store's brand — raised for **both** guarded-write classifications, `conflict` and `expired` alike, since the port has one channel out (see `GuardedWriteOutcome`) | **409** | **No, not automatically** | *Your read is stale.* Re-read with a query operation, then decide. Never resubmit blind: a retried `submitAction` is a second action. Where the cause was expiry, that re-read is what surfaces it, as `session_expired` |
| `session_expired` | Dispatch, after consulting the lifecycle probe on `unknown_session` | **404** | No | The session existed and no longer does. Start a new one, or load a save |
| `save_expired` | Dispatch, after consulting the lifecycle probe on `unknown_save` | **404** | No | As above |

**A lifecycle probe that fails is read as `absent`, so the engine's code passes through unchanged.**
The caller sees `unknown_session` or `unknown_save`, which is true — the workload cannot establish
that the row was ever there. Escalating to `503` would convert an honest `404` into an outage code on
the one path that reaches the probe, and it would do so precisely when the store is degraded, which
is the same mistake the re-read classifier's rule exists to prevent. **The cost, stated:** while the
store is degraded a session that had merely expired is answered as one that never existed, and
nothing on the wire says which — the readiness check is what surfaces the underlying condition.

**Both expired codes map to 404, not 410**, and the *code* carries the distinction — G1 already
established that `unsupported_version` and `unknown_operation` share a status and are told apart by
their codes, and one convention with no exceptions is worth more than a semantically prettier second
one.

**Past the retention horizon the answer honestly degrades to `unknown_session`.** The row is gone and
nothing distinguishes it from an id that never existed. That is the price of not keeping tombstones
forever, and it is documented rather than defended.

**A rejected action is still a `200`, and a conflict is not a rejected action.** A rejection is the
game's verdict on a legal request; a conflict is the transport failing to commit one.

### Profiles — the three warnings

The engine's `ProfileWarningCode` members are used unchanged and none is invented.

| Code | Raised when | Response | State left behind |
|---|---|---|---|
| `profile_missing` | No `profile` row for the id | The game action's `200`, carrying the warning and an empty achievement set | None |
| `profile_corrupt` | `format_version` is not `1`, an achievement row fails shape validation, **or the read itself fails** | As above | The row, untouched |
| `profile_write_failed` | The adapter caught its own driver error on a profile write — **it returns `ok: false` and does not throw** | The game action's `200`, carrying the warning | **The session write, which already committed and is not rolled back** |

**None of the three ever produces a broken game**, and all three are asserted by the port-conformance
suite — the only proof that reaches these ports at all.

**`profile_corrupt` absorbs a store outage, and that is forced by the port rather than chosen.**
`ProfileStore.load` returns a `ProfileLoadResult` and has no error channel, so a connectivity failure
and a malformed row arrive at the same return statement with nothing to tell them apart. The adapter
answers the warning the port has. **The cost, stated where it can be found next to the other one:**
while the store is degraded, every profile read reports `profile_corrupt` and an empty achievement
set on a `200`, so a player's achievements read as absent for the length of the outage — the same
silent shape the `format_version` two-step rule exists to prevent, arrived at by a different route.
It self-corrects, because the merge is a set union and the next successful action re-derives what was
earned; nothing is lost, only unreported. Readiness is what surfaces the underlying condition.

**No retry on `profile_write_failed`.** Achievements are re-derivable on the next action because the
store's merge is a set union, which is the second reason the merge is a union rather than a replace.

### The harness — `HarnessError`, `ConformanceError`

| Type | Variant | Raised when | Retryable | Caller does |
|---|---|---|---|---|
| `HarnessError` | `SchemaCreateFailed` | The per-run schema cannot be created or migrated | No | Fails the suite. A dirty schema would surface as a primary-key violation mid-replay |
| `HarnessError` | `SchemaDropFailed` | The per-run schema cannot be dropped afterwards | No | Fails the suite, naming the schema, so a leaked schema is reported rather than accumulated |
| `HarnessError` | `InstanceSpawnFailed` | An instance does not report ready within its bound | No | Fails the suite, naming which of the two |
| `HarnessError` | `InstanceShutdownFailed` | An instance does not exit within its bound | No | Fails the suite, naming which of the two |
| `ConformanceError` | `MethodDiverged` | A port method behaves differently across the two targets, outside the one declared divergence | No | Fails the suite, naming the method and both targets |
| `ConformanceError` | `SeamUnavailable` | A target cannot seed a corrupt profile or a write failure | No | Fails the suite. A degradation that cannot be provoked is not asserted, and a suite that skipped it would read as coverage |
| `ConformanceError` | `CallerPropertyViolated` | An engine `save` was observed carrying less than the loaded set plus additions | No | Fails the suite. This is the property the durable `save`'s conditional conformance rests on, so it is asserted rather than cited |

### Inherited unchanged

`ContractLoadError`, `SurfaceBuildError`, `EncodingError`, `ValidationFailure`, `StartupError`,
`ShutdownError`, `DumpReadError`, `ReplayError`, `WireError` and `EdgeError` are G1's and are
unchanged in shape. `WireError` gains no variant: `session_expired` and `save_expired` arrive through
`EngineRejection`'s path as codes resolved by the mapping, exactly as every engine code does.

**`storage_failure` is now genuinely reachable**, where G1 recorded it as declared and unreachable. A
test forces it and asserts the `503`, which is what stops the code being an untested branch.

---

## Invariants

Each is written to be assertable, with the module responsible for maintaining it. **G1's 1–47 stand**;
these continue from 48.

| # | Invariant | Owner |
|---|---|---|
| 48 | Every column on `session`, `save`, `profile` and `profile_achievement` is on exactly one side of the engine/host ownership line, and no host column ever reaches a `StoredSessionRecord` or a `StoredSaveRecord` | Store |
| 49 | The blob written for a session is exactly the engine's canonical serialization and carries nothing else — no timestamp, no owner id, no tenant id, no correlation | Store |
| 50 | A blob read back is byte-identical to the blob written; `blob` is `text` and passes through no JSON normalisation at any layer, including the driver's | Store |
| 51 | `tenant_id` is `not null` on every row every write produces, is a member of every table's primary key, and appears in every statement's `where` or column list | Store, Migrations |
| 52 | `session.version` is `1` on insert and increments by exactly `1` in the same statement that performs the guarded write — so "the version advanced" and "a write landed" are one event | Store |
| 53 | No `session` update is ever issued without a `version` predicate for an id the request has read, and no `where` clause is widened to make an update succeed | Store |
| 54 | Every guarded `session` update also predicates on `expires_at > now()`, so a write cannot resurrect a session a concurrent read is already answering `session_expired` for | Store |
| 55 | Zero rows affected is classified by a re-read into exactly one of `conflict` or `expired`, never assumed and never reported as a storage outcome; a re-read that itself fails is `conflict` | Store |
| 56 | A conflict leaves the store as a value branded `SessionPersistenceConflict` and never as a `StoreError`; every other adapter failure is an ordinary throw the engine reads as `storage_failure` | Store |
| 57 | The store checks that a connection reports `read committed` when the pool is opened, and refuses to serve otherwise — a single check, because the setting is a server, database, role or pooler default rather than something a connection varies. **Not re-checked per pooled connection**: a pooler that varied it per connection is outside what the design contemplates, and a per-acquisition round trip would buy that case at a cost on every connection | Store |
| 58 | The store imports the engine's type declarations only. It never deserialises a blob, never validates game state, and never calls the engine | Store |
| 59 | `expires_at` is computed from the **database** clock in SQL, never from the process clock, on both tables | Store |
| 60 | A row whose `expires_at` has passed is returned as **not found** by `sessions.get` and `saves.get`, so the bound does not depend on when a sweep last ran | Store |
| 61 | The sweep deletes only rows past the retention horizon and touches neither profile table; it is two plain `delete`s in one transaction, idempotent, and needs no coordination between instances | Store, Composition |
| 62 | The retention horizon is required to exceed any request's duration by a wide margin, and a configuration that does not is rejected at startup | Composition |
| 63 | A sweep tick never begins while its predecessor is still running, and a failed tick is caught, logged and retried on the next tick — never escaping as an unhandled rejection | Composition |
| 64 | `save` has no `version` column and `saves.put` is an upsert; on a re-put every host column is recomputed from the writing process and the current clock | Store, Migrations |
| 65 | `profiles.save` writes achievement rows with `insert … on conflict do nothing` and removes none; the durable merge is a set union | Store |
| 66 | `profiles.save` returning `ok: false` never rolls back a committed session write, and never throws | Store |
| 67 | A missing or corrupt profile yields an empty achievement set and a warning, never a failed request | Store |
| 68 | For the durable configuration, the engine's session layer is composed per request and discarded with it; no session-layer cache outlives one operation | Composition |
| 69 | The pool, the schema, the profile store, the lifecycle probe and the serialization handle are process-lived and are never rebuilt per request | Composition |
| 70 | The read-version map is constructed empty for every request and is never shared between two requests | Store |
| 71 | Every `sessions.get` in the durable configuration reaches the database; no read is served from a cache | Store, Composition |
| 72 | The counting `RecordIdSource` and the counting `IdSource` are process-lived, so a per-request source cannot restart at zero and mint colliding ids | Composition |
| 73 | The lifecycle probe has no route, no MCP tool and no row in the operation table, and returns a classification only — never a blob, a scene or a record | Store, Composition |
| 74 | The lifecycle probe is composed behind the same seam `SessionPersistence` and `ProfileStore` are | Composition |
| 75 | The in-memory configuration is supplied a probe that classifies every id as `absent`, so Dispatch carries no branch on which store was built | Composition |
| 76 | The in-memory configuration keeps G1's single long-lived session layer and therefore G1's per-session queueing; it has no compare-and-swap | Composition |
| 77 | Neither surface's module graph reaches Store, in addition to G1's `StoreSerializationHandle` prohibition | HTTP surface, MCP surface |
| 78 | Readiness evaluates the store on every probe and reports no remembered outcome; liveness consults the store never | Composition, Probes |
| 79 | The listener binds and the process reports live before the store is reachable; an unreachable store is reported as not-ready and retried, and never aborts startup | Composition |
| 80 | Every `session` and `save` row a write produces carries the writing process's resolved engine package version in `engine_version`; it is stamped, never read on the serving path, and never compared at runtime. The two profile tables carry no such column and are not in scope: `engine_version` is provenance for a **blob**, and a profile row holds none — it is normalised host-shaped columns the store writes itself, with no engine serialization whose producer could need recording | Store |
| 81 | The durable replay runs against a schema created for that run and dropped afterwards, never against a tenant id and never against a truncated shared schema | Proof harness |
| 82 | The durable replay runs at the production lifecycle defaults, so no session can expire between two of its steps | Proof harness |
| 83 | The determinism dump's ordering is pinned to `collate "C"`, and the compose file pins the server encoding and the initdb locale explicitly | Composition, Proof harness |
| 84 | Comparison A asserts both dumps carry the fixture's expected row counts before it asserts they are equal | Proof harness |
| 85 | Contention is asserted twice — concurrently against one instance, and across two instances sharing one store — and each asserts exactly one `200` and one `409` carrying `concurrent_modification` | Proof harness |
| 86 | Four perturbations are asserted red: the guard removed produces two `200`s; an artificially stale version is rejected; an unreachable store produces `503` and not `409`; and a dump pointed at an empty schema fails comparison A | Proof harness |
| 87 | `readWritePauseMs` is `0` in every configuration but a perturbed harness's, and a test asserts the seam is inert at `0` | Store, Proof harness |
| 88 | After a conflict, the loser's action has left no trace in the winner's state; merging is never attempted | Proof harness |
| 89 | The port-conformance suite runs the same assertions over both targets and covers all seven port methods — `sessions.get/put`, `saves.get/put/delete`, `profiles.load/save`; `profiles.save` is the one declared divergence and its conditional conformance rests on an asserted property of the engine's caller | Proof harness |
| 90 | The two instances are anonymous: nothing keys on which instance served a request, and no instance identity is modelled, stored or logged as an identifier | Composition, Proof harness |
| 91 | Both instances bind loopback, and the two-instance harness introduces no public exposure | Proof harness |
| 92 | The edge's readiness check probes the workload's readiness endpoint; it plays no game operation and creates no session | Edge |
| 93 | The compose file provisions the store and starts no workload instance; the harness spawns the instances and provisions no store | Proof harness |
| 94 | The command the README names for running two instances is the same entry point the contention proof invokes | Proof harness |
| 95 | No project under `src/` or `samples/` references the workload, and Platform's `Persistence` package gains no consumer from G2 | Build |
| 96 | A lifecycle probe that fails is read as `absent`; Dispatch never converts a probe failure into `storage_failure`, and no probe failure changes a response's status | Dispatch |

### Amended invariants

Seven of G1's change under durable state. **Each is amended in G1's own numbering and nowhere else**,
so a citation still resolves to one statement.

| # | What changes |
|---|---|
| 12 | "The workload computes no sequence and stamps no field on a session or save record" now reads **no *engine-owned* field**. The store stamps `version`, `engine_version`, `row_created_at`, `row_updated_at` and `expires_at` — all host columns, none of which reaches a `StoredSessionRecord` (invariant 48) |
| 13 | Extends from the in-memory record to every durable row: no correlation, trace id or other host metadata is written into a session record, a save record, or any canonical serialization |
| 16 | Extends to the durable serialization handle, which reads `blob` columns and no host column |
| 17 | Grows a second forbidden target: neither surface's module graph reaches `StoreSerializationHandle` **or Store** (invariant 77) |
| 21 | "Both surfaces reach the store only through one `Dispatcher` instance over one `SessionStore`" becomes **over one `StoreProvider`**; the `SessionStore` instance is per request in the durable configuration |
| 39 | "Stage 1's single-hop replay remains in the suite and green" extends to the in-memory replay remaining green after the durable one lands |
| 46 | Unchanged in words, extended in reach: **both** instances bind loopback (invariant 91) |

---

## Unresolved

Values and signatures the design does not determine, or that a document above this contract still
contradicts. **Each blocks something concrete**, and none is guessed at above.

**Resolved items keep their number and are struck through rather than removed**, so nothing that
later cites one by number breaks. **1, 2 and 3 are resolved; 4 is open, and 5 is answered here
because this is the stage the design routed it to.**

### ~~1. What Dispatch answers when the lifecycle probe itself fails~~

**Resolved 2026-08-12, by Ben: a failed probe is read as `absent` and the engine's own code passes
through verbatim.** The design named three outcomes for three states and was silent on a fourth;
`LifecycleProbe` returns `Outcome<LifecycleState, StoreError>` because every error crossing a
TypeScript module boundary here is an `Outcome` failure, and what Dispatch does with the failure arm
was the undetermined half. It is now stated with `LifecycleProbe` above, carried as a row in
[*Dispatch — the two new outcomes*](#dispatch--the-two-new-outcomes), and asserted as invariant 96.

**Rejected: `storage_failure` → `503`.** The probe failed because the store failed, and `503` is what
that means — but it converts an honest `404` into an outage code on the one path that reaches the
probe, precisely when the store is degraded. That is the same mistake the design already forbids the
re-read classifier from making, and making it here would be inconsistent with the one rule the design
does state about a classifier's own failure. **Rejected: logging the failure and passing through.**
Identical on the wire, plus a log line; declined because the brief admits only the observability a
lost update needs, and the sweep's failure is already the one condition granted a log line of its own.

**The retained cost, recorded rather than dropped:** while the store is degraded, a session that had
merely expired is answered as one that never existed, and nothing on the wire says which. Readiness is
what surfaces the underlying condition.

### ~~2. One binding-document conflict remains~~

Design Open questions 9 and 10. **Both are now resolved by amendments to
[`00-brief.md`](00-brief.md)** — question 9 on 2026-08-12, question 10 on 2026-08-20. Both keep their
place here so a later citation resolves rather than disappearing. **No binding-document conflict
remains**, and this heading is kept rather than renumbered for the same reason.

- ~~**The tenant non-goal.**~~ **Resolved 2026-08-12, by Ben: the store supplies the implicit tenant
  as a constant in every key and statement, while no request resolves or carries a tenant and no
  behaviour varies by tenant.** The brief now permits invariant 51 directly rather than requiring
  the design to interpret around a binding non-goal.
- ~~**Save lifecycle.**~~ **Resolved 2026-08-20, by Ben: the brief admits saves to the lifecycle
  scope, on their own clock.** The criteria named *sessions* in every clause while this contract gave
  saves a 365-day absolute TTL, an `expires_at` column, a sweep that hard-deletes them, and a
  `save_expired` code widening a closed union in a published package. [`00-brief.md`](00-brief.md)
  now carries the clause; nothing above moves. **What would have reversed, recorded because the
  alternative was real:** `save.expires_at` and its index out of the schema, the sweep narrowed to
  `session`, `save_expired` out of `TransportErrorCode` and out of the status mapping, and
  `LifecycleProbe.save` left without a caller.

### ~~3. The engine's ratification of `concurrent_modification` and the brand's spelling~~

**Resolved 2026-08-20, by the tree rather than by a decision: the engine ratified both, at exactly
the names above.** Engine `0.8.0` — vendored at `workloads/game-service/vendor/` and the version
startup asserts the contract's recorded one against — exports `concurrent_modification` as the ninth
`SessionStoreErrorCode` member and `SESSION_PERSISTENCE_CONFLICT` as `"SessionPersistenceConflict"`,
and `writeSession`'s `catch` recognises the brand. There is no further ratification this repository
can observe: the tarball is the artifact it consumes.

**What was open, recorded because the next engine change will raise it again.** Every conflict
assertion above depended on a union member that existed in no published engine version, and the
design's position was that the engine pull request is the first deliverable with the proofs sequenced
behind it. The genuine exposure was a different name or a different brand shape — rework in one
slice, never a reshaping, because the adapter throws a branded value and Dispatch maps a code.

### 4. Design Open question 8 — what refreshes a session's idle clock

Open in the design and unchanged by this contract, which takes the design's reading: `expires_at`
advances on accepted writes only, so a session read continuously for the whole TTL still expires. **It
determines no signature above.** Refreshing on read would make every query operation a write, putting
it inside the compare-and-swap's blast radius and undoing *every read reaches the database* being a
read-only property; leaving it costs a name and a caller-visible surprise, both fixable by documenting
the TTL as "30 days since the last accepted write" wherever it appears.

### 5. Resolved here — design Open question 5, the contract package's release path

**Routed to this command by [`10-design.md`](10-design.md) and answered: G2 vendors the regenerated
tarball, exactly as G1 did.** This is a reading of the constraint rather than a preference. The
`@subzerodev` npm organisation is still unreserved — [Platform issue #81](https://github.com/The-Running-Dev/SubZeroDev.Platform/issues/81)
is open as of 2026-08-12 — so there is no registry to resolve `@subzerodev/service-contract` from, and
"switch to the registry" is not an available option rather than a rejected one. The regeneration is
forced (the error-coverage gate fails against a widened `SessionStoreErrorCode`, and
`TransportErrorCode` gains two members), it is a contract **minor** version, and
`workloads/game-service/vendor/` gains the new tarball while `package.json`'s `file:` dependency moves
to it. **The tarball is replaced wholesale, never edited in place.** When #81 closes, switching to the
registry is a one-line dependency change and no signature above moves.

---

## Additions requiring a decision-log entry

Six things above originated here rather than in the design, and none was derivable from it. Each is
small and named here rather than left for a reader to discover in the code.

1. **`SESSION_PERSISTENCE_CONFLICT` as the brand's spelling.** The design specifies a name-based
   duck-typed brand and names no string. The engine repository owns the final naming; this contract
   names it so the G2 slices and the engine pull request describe one thing — the same treatment G1
   gave `RecordIdSource`.
2. **The conformance suite's reference target is the workload's map-backed `SessionPersistence`, not
   the engine's.** This originated here: the design said "the engine's in-memory implementations",
   and the engine exports the `SessionPersistence` type and no implementation of it (verified at
   `0.8.0`). `ProfileStore` is literal — `createInMemoryProfileStore` is the engine's, and both of
   the reference target's degraded outcomes are provoked through its own `raw` and `onSave` seams
   rather than stubbed at the boundary. The suite is unchanged in value: G1's map-backed adapter is
   the implementation the byte-identity proof already trusts. **[`10-design.md`](10-design.md) has
   since been corrected to say the same**, so this item records where the correction came from
   rather than a live disagreement.
3. **`ComposedWorkload.close()` and `DurableStore.close()`.** The design gives the sweep a timer and
   the store a pool and says nothing about stopping either. A process that cannot stop its timer does
   not exit, which makes this mechanically forced rather than a design choice — but it is a signature
   the design does not contain.
4. **`GameEdgeOptions.LivenessTimeout` renamed to `ReadinessTimeout`.** The design changes which
   endpoint the check probes and says nothing about the option. A property named for liveness that
   bounds a readiness probe is the naming failure `agent.md` records; the rename is cosmetic and the
   `appsettings.json` key moves with it.
5. **The failed sweep reports a removed count of zero rather than a count of rows not removed.** The
   design asks for "the row count it did not remove", which a failed `delete` cannot supply without a
   second query against a store that has just failed one. The honest report is the failing statement
   and the fact that nothing was removed.
6. **`ReadVersionMap` as a named type with three methods.** The design describes a
   `Map<sessionId, version>` held by the per-request adapter. Naming it and giving `advance` a
   separate method from `record` is what makes "the map is advanced only after a write lands"
   (invariant 52's counterpart) checkable rather than a convention inside one function.
