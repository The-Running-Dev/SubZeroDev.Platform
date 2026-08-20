/**
 * Composition — the engine, the content registry, the store provider (in-memory or durable), and
 * the engine-version assertion that decides whether any of it is built at all.
 *
 * `compose` owns that assertion, which is why it takes the contract: it uses nothing else from it.
 * A mismatch returns `EngineVersionMismatch` and no store is built, so the listener never binds.
 *
 * `compose` returns successfully even when a durable store is unreachable at startup (`20-contract.md`,
 * "Composition — workload"): the process comes up reporting not ready rather than refusing to start,
 * and this module retries the connection in the background from then on. The very first attempt is
 * still awaited here, bounded by `connectTimeoutMs`, so the listener does not bind until it settles
 * one way or the other — only the *retries* after that first attempt are pure background work.
 * `StoreUnavailable` is a readiness condition, never a `CompositionError` — the `CompositionError`
 * variants are the ones no retry can fix.
 */
import {
  buildContentRegistry,
  buildWorldGraphMvpCampaign,
  createCountingIds,
  createEngine,
  createInMemoryProfileStore,
  createSessionLayer,
  ENGINE_VERSION,
  simulationKind,
  storyGraphKind,
  worldGraphKind,
} from "@the-running-dev/game-engine";
import type {
  Clock,
  ContentRegistry,
  KindRegistry,
  ProfileStore,
  RecordIdSource,
  SessionPersistence,
  SessionStore,
  StoredSaveRecord,
  StoredSessionRecord,
} from "@the-running-dev/game-engine";
import type { ContractPackage } from "@subzerodev/service-contract";
import { renameSync, unlinkSync, writeFileSync } from "node:fs";
import { canonicalEncode } from "./canonical.js";
import { migrateToHead } from "./migrations.js";
import { corruptProfileResult, openDurableStore, profileWriteFailedResult } from "./store.js";
import { err, ok } from "./types.js";
import type {
  ComposedWorkload,
  CompositionError,
  DeterminismDump,
  DurableStore,
  LifecycleProbe,
  MigrationError,
  Outcome,
  ProbeResult,
  ReplayDeterminismProfile,
  SemanticVersion,
  StorageProfile,
  StoreError,
  StoreProvider,
  StoreSerializationHandle,
  StoreSerializationSnapshot,
  WorkloadConfiguration,
} from "./types.js";

/** Ascending by code unit — the same rule `canonicalEncode` applies to object members, restated
 *  here so the live snapshot and the dump read back from disk agree on order (S5 compares them). */
function byId(a: { readonly id: string }, b: { readonly id: string }): number {
  return a.id < b.id ? -1 : a.id > b.id ? 1 : 0;
}

/** `KindRegistry` is a plain record keyed by kind id; kinds are engine-owned and are not ports. */
function kinds(): KindRegistry {
  return {
    [storyGraphKind.id]: storyGraphKind,
    [simulationKind.id]: simulationKind,
    [worldGraphKind.id]: worldGraphKind,
  } as KindRegistry;
}

/** Map-backed and total — the in-memory configuration has no database, and `storage_failure` is
 *  consequently declared and unreachable. The maps are also what the serialization handle reads,
 *  which is why persistence is supplied explicitly rather than left to the engine's own default. */
function inMemoryPersistence(): {
  persistence: SessionPersistence;
  sessions: Map<string, StoredSessionRecord>;
  saves: Map<string, StoredSaveRecord>;
} {
  const sessions = new Map<string, StoredSessionRecord>();
  const saves = new Map<string, StoredSaveRecord>();
  return {
    sessions,
    saves,
    persistence: {
      sessions: {
        get: async (sessionId) => sessions.get(sessionId),
        put: async (record) => {
          sessions.set(record.sessionId, record);
        },
      },
      saves: {
        get: async (saveId) => saves.get(saveId),
        put: async (record) => {
          saves.set(record.saveId, record);
        },
        delete: async (saveId) => {
          saves.delete(saveId);
        },
      },
    },
  };
}

/** Every method throws a plain `Error` — the same shape S1's engine catch converts to
 *  `storage_failure` for a session write, and the shape every other `SessionPersistence` failure
 *  in this workload already takes. Handed to `createSessionLayer` only while a durable store has
 *  never yet connected, so a request that lands in that window gets an honest failure rather than
 *  a silent, unannounced fall-back to an in-memory store nobody asked for. */
function unavailablePersistence(): SessionPersistence {
  const fail = (): never => {
    throw new Error("durable store not yet connected");
  };
  return {
    sessions: { get: async () => fail(), put: async () => fail() },
    saves: { get: async () => fail(), put: async () => fail(), delete: async () => fail() },
  };
}

/** Same footing as `unavailablePersistence` above, but `ProfileStore` has its own warning
 *  vocabulary rather than an exception channel — every read reports `profile_corrupt` and every
 *  write reports `profile_write_failed`, exactly as a durable store already does for its own
 *  failures. */
function unavailableProfiles(): ProfileStore {
  return {
    load: async (profileId: string) => corruptProfileResult(profileId),
    save: async (profile) => profileWriteFailedResult(profile.profileId),
  };
}

/** The engine exports no counting `RecordIdSource` (S1's out-of-scope) — the replay profile's is
 *  the workload's own, on the same terms as the engine's own `createCountingIds()`: independent
 *  counters, each from zero, no argument (invariant 12b). */
function createCountingRecordIds(): RecordIdSource {
  let sessionCounter = 0;
  let saveCounter = 0;
  return {
    newSessionId: () => `counting-session-id-${sessionCounter++}`,
    newSaveId: () => `counting-save-id-${saveCounter++}`,
  };
}

/** The composition seam S4.5 is asserted against directly: a `Clock` that reports `fixedInstant`
 *  on every call, for the whole run. */
export function createFixedClock(fixedInstant: string): Clock {
  return { now: () => fixedInstant };
}

function contentRegistry(): Outcome<ContentRegistry, CompositionError> {
  const campaign = buildWorldGraphMvpCampaign();
  if (!campaign.ok || !campaign.value) {
    return err({ code: "ContentRegistryInvalid", campaignId: "world-graph-mvp" });
  }
  const registry = buildContentRegistry([campaign.value]);
  if (!registry.ok || !registry.value) {
    return err({ code: "ContentRegistryInvalid", campaignId: "world-graph-mvp" });
  }
  return ok(registry.value);
}

/** No contract field names a bound for "any request's duration" — `GameEdgeOptions.ForwardTimeout`
 *  (`design/20-contract.md`, "The edge — .NET") is the edge's own configured value, and it lives in
 *  a different repository and process; threading it through `WorkloadConfiguration` would need a
 *  signature the contract does not carry. This is a workload-owned constant, on the same footing
 *  as `migrations.ts`'s `LOCK_WAIT_TIMEOUT_MS` — chosen to exceed the edge's own configured value
 *  (10s in this repository's own hosted-edge test support) by a wide margin, so the production
 *  default (`retentionHorizonSeconds` of 30 days) validates and a deliberately short horizon is
 *  still caught before any connection is attempted (S4.6). Exported so a test can assert this still
 *  exceeds the real edge timeout used by `tests/support/hosted-edge.ts` — the one guard against this
 *  guess drifting out of sync with the edge's own configured value. */
export const ASSUMED_FORWARD_TIMEOUT_SECONDS = 60;

/** The interval between background reconnection attempts while a durable store has not yet
 *  connected. Not named by the contract — "retries with backoff" (`20-contract.md`, "Composition
 *  — workload") describes the requirement, not a value; this is the workload's own choice. */
const DURABLE_RECONNECT_INTERVAL_MS = 5_000;

/** What `readiness()`'s structural `detail` (see `notReadyDetail` below) names for each
 *  `MigrationError` variant — pulled out as its own pure function so the mapping S12.6/S12.7 need
 *  ("naming the migration", "naming a lock timeout") is checkable without reproducing the real
 *  30-second lock bound or a real failing migration against a live database. */
export function migrationNotReadyDetail(error: MigrationError): string {
  if (error.code === "LockTimeout") return "migration lock timeout";
  if (error.code === "MigrationFailed") return `migration failed: ${error.migration}`;
  return "migration runner unreachable";
}

/** The same mapping as `migrationNotReadyDetail` above, for the store's own side of startup.
 *  `20-contract.md`'s `StoreError` table requires an `IsolationLevelUnsupported` store to report
 *  not-ready **naming the isolation level found** — a `read committed` misconfiguration is the one
 *  store condition no amount of waiting clears, and reporting it as "store unreachable" tells an
 *  operator to wait for a recovery that cannot come. Pure, for the same reason its migration
 *  counterpart is: the mapping is checkable without provoking each condition against a live
 *  database. */
export function storeNotReadyDetail(error: StoreError): string {
  switch (error.code) {
    case "IsolationLevelUnsupported":
      return `store isolation level ${error.isolationLevel}, not read committed`;
    case "PoolExhausted":
      return "store connection pool exhausted";
    case "IdCollision":
    case "RowUndeserializable":
    case "StatementFailed":
      return "store statement failed";
    default:
      return "store unreachable";
  }
}

function validateStorageConfiguration(storage: StorageProfile): Outcome<void, CompositionError> {
  if (storage.kind === "in-memory") return ok(undefined);
  const { connection, bounds } = storage.store;

  // A leading `/` is `pg`'s own Unix-domain-socket form (`pg-connection-string`'s `parse()`
  // special-cases it before ever reaching a `URL`) and is never itself a valid absolute URL, so
  // `new URL(...)` would reject a connection string the driver accepts. Every other form is
  // URL-shaped, and that syntactic check is what `new URL(...)` below still validates.
  if (!connection.connectionString.startsWith("/")) {
    try {
      // eslint-disable-next-line no-new -- syntactic validity only; nothing here connects.
      new URL(connection.connectionString);
    } catch {
      return err({ code: "StorageConfigurationInvalid", setting: "storage.store.connection.connectionString" });
    }
  }
  if (!Number.isInteger(connection.poolSize) || connection.poolSize <= 0) {
    return err({ code: "StorageConfigurationInvalid", setting: "storage.store.connection.poolSize" });
  }
  if (bounds.retentionHorizonSeconds <= ASSUMED_FORWARD_TIMEOUT_SECONDS) {
    return err({ code: "StorageConfigurationInvalid", setting: "storage.store.bounds.retentionHorizonSeconds" });
  }
  return ok(undefined);
}

/**
 * S13.8/S13.9 — the seam `SessionPersistence`, `ProfileStore` and `LifecycleProbe` are composed
 * behind (`20-contract.md`: "the lifecycle probe is composed behind the same seam `SessionPersistence`
 * and `ProfileStore` are"; invariant 74). G2 applies no decorator — `IDENTITY_STORAGE_DECORATOR` is
 * the only one ever passed — but every one of the three ports this workload builds, for both storage
 * profiles, passes through this one function on its way to a caller, so a future authorization
 * decorator (G3) wraps all three by construction rather than by remembering to extend a two-port
 * wrapper to a third. Exported so S13.8/S13.9 can assert the structural claim directly, with a
 * counting decorator standing in for G3's real one, without needing `compose()`'s own fixed
 * signature to grow a parameter the contract does not carry.
 */
export interface StorageSeam {
  readonly persistence: SessionPersistence;
  readonly profiles: ProfileStore;
  readonly lifecycle: LifecycleProbe;
}

export type StorageDecorator = (seam: StorageSeam) => StorageSeam;

export const IDENTITY_STORAGE_DECORATOR: StorageDecorator = (seam) => seam;

export function composeStorageSeam(seam: StorageSeam, decorate: StorageDecorator = IDENTITY_STORAGE_DECORATOR): StorageSeam {
  return decorate(seam);
}

export async function compose(
  configuration: WorkloadConfiguration,
  contract: ContractPackage,
): Promise<Outcome<ComposedWorkload, CompositionError>> {
  // Invariant 11 — the resolved engine package's version equals the contract's recorded one, or
  // the process does not start. Asserted first: nothing below is worth building otherwise.
  if ((contract.engineVersion as string) !== ENGINE_VERSION) {
    return err({
      code: "EngineVersionMismatch",
      contractEngineVersion: contract.engineVersion as string,
      resolvedEngineVersion: ENGINE_VERSION,
    });
  }

  const validated = validateStorageConfiguration(configuration.storage);
  if (!validated.ok) return validated;

  const registry = contentRegistry();
  if (!registry.ok) return registry;

  // The default profile supplies neither `ids` nor `recordIds` nor `clock`, so the engine's own
  // minting and the real wall clock apply unchanged (invariant 12a). The replay profile supplies
  // all three, each a counting or fixed source on the same terms as the engine's own fixture.
  const replay = configuration.determinism.kind === "replay" ? configuration.determinism : null;

  const engine = createEngine({
    kinds: kinds(),
    registry: registry.value,
    ...(replay ? { ids: createCountingIds() } : {}),
  });

  if (configuration.storage.kind === "in-memory") {
    const { persistence: rawPersistence, sessions, saves } = inMemoryPersistence();

    // The no-op probe: every id classifies `absent`, so `unknown_session`/`unknown_save` pass
    // through Dispatch verbatim and Dispatch carries no branch on which store was composed
    // (`20-contract.md`, "Composition — workload").
    const rawLifecycle: LifecycleProbe = {
      session: async () => ok("absent"),
      save: async () => ok("absent"),
    };

    // S13.9: the in-memory configuration's no-op probe is composed behind the same seam as its
    // persistence and profiles, so invariant 74 is not a durable-only property.
    const seam = composeStorageSeam({ persistence: rawPersistence, profiles: createInMemoryProfileStore(), lifecycle: rawLifecycle });

    const store: SessionStore = createSessionLayer({
      engine,
      registry: registry.value,
      persistence: seam.persistence,
      profiles: seam.profiles,
      ...(replay ? { clock: createFixedClock(replay.fixedInstant), recordIds: createCountingRecordIds() } : {}),
    });

    const stores: StoreProvider = { forRequest: () => store };
    const lifecycle = seam.lifecycle;

    const serialization: StoreSerializationHandle = {
      async snapshot(): Promise<StoreSerializationSnapshot> {
        return {
          sessions: [...sessions.values()].map((record) => ({ id: record.sessionId, blob: record.blob })).sort(byId),
          saves: [...saves.values()].map((record) => ({ id: record.saveId, blob: record.blob })).sort(byId),
        };
      },
    };

    return ok({
      stores,
      lifecycle,
      serialization,
      async readiness(): Promise<ProbeResult> {
        return { status: "healthy" };
      },
      async close(): Promise<void> {
        // Nothing owns a connection or a timer under this profile.
      },
    });
  }

  const storeConfig = configuration.storage.store;

  let durableStore: DurableStore | null = null;
  let closed = false;
  let reconnectHandle: ReturnType<typeof setTimeout> | null = null;
  let sweepHandle: ReturnType<typeof setTimeout> | null = null;
  // Set only while a reconnect retry (never the first, awaited attempt) is in flight, so `close()`
  // can wait for it instead of returning while a stray connect is still running underneath it.
  let connecting: Promise<void> | null = null;
  // What `readiness()` reports as `ProbeResult.detail` while `durableStore` is still null — which
  // of "the lock is held past its bound", "a migration's SQL failed" (naming which one) or a store
  // condition is current (`design/30-slices.md`, S12.6/S12.7). A migration *still running* is not
  // among them and cannot be: the listener binds only once the first startup attempt settles, and
  // that attempt runs the migration inside itself (`20-contract.md`, "Workload — readiness").
  let notReadyDetail: string | null = null;
  // Once `migrateToHead` has succeeded once for this `compose()` call, the schema is at head and
  // every later retry (a store-connect failure, never a migration one) skips straight to
  // `openDurableStore` instead of paying a second full migration-runner invocation — including its
  // own connect and `node-pg-migrate`'s advisory-lock acquisition — for a schema already migrated.
  let migratedOnce = false;
  // Consecutive migration failures back this call's retry off — `node-pg-migrate`'s advisory lock
  // is one id for the whole database, not scoped per schema, so a schema whose migration keeps
  // failing must not keep re-requesting that lock every `DURABLE_RECONNECT_INTERVAL_MS` forever; a
  // healthy, unrelated schema's own migration attempt can otherwise queue behind it and time out.
  let migrationFailureStreak = 0;
  const MAX_RECONNECT_INTERVAL_MS = 60_000;

  function scheduleReconnect(delayMs: number): void {
    reconnectHandle = setTimeout(() => {
      connecting = attemptConnect().finally(() => {
        connecting = null;
      });
    }, delayMs);
  }

  /** `migrateToHead` runs before the first connection attempt, every attempt until it succeeds —
   *  the durable branch's own startup order (`design/30-slices.md`, S12): "no separate migration
   *  command is run first". A migration failure and a connection failure share one backoff loop,
   *  the same way `compose()` already retried a bare connection failure before this — the two are
   *  just two reasons the same retry exists for. */
  async function attemptConnect(): Promise<void> {
    if (!migratedOnce) {
      const migrated = await migrateToHead(storeConfig.connection);
      if (closed) return;
      if (!migrated.ok) {
        migrationFailureStreak += 1;
        notReadyDetail = migrationNotReadyDetail(migrated.error);
        console.error("[game-service] startup migration attempt failed", migrated.error);
        const delayMs = Math.min(
          DURABLE_RECONNECT_INTERVAL_MS * 2 ** (migrationFailureStreak - 1),
          MAX_RECONNECT_INTERVAL_MS,
        );
        scheduleReconnect(delayMs);
        return;
      }
      migratedOnce = true;
      migrationFailureStreak = 0;
    }

    const opened = await openDurableStore(storeConfig, ENGINE_VERSION as SemanticVersion);
    if (closed) {
      // `close()` ran while this attempt was in flight — a store that connected after the fact
      // must not be left dangling. Guarded the same way the sweep tick's own failure is: a rejection
      // here must not become an unhandled rejection this attempt was never awaited into.
      if (opened.ok) {
        try {
          await opened.value.close();
        } catch (closeError) {
          console.error("[game-service] closing a store that connected after shutdown failed", closeError);
        }
      }
      return;
    }
    if (opened.ok) {
      durableStore = opened.value;
      notReadyDetail = null;
      return;
    }
    notReadyDetail = storeNotReadyDetail(opened.error);
    console.error("[game-service] durable store connection attempt failed", opened.error);
    scheduleReconnect(DURABLE_RECONNECT_INTERVAL_MS);
  }

  // The one "is a durable store connected right now" branch `lifecycle.session`/`.save` and
  // `serialization.snapshot` each otherwise repeated on their own.
  function withDurableStore<T>(onUnavailable: () => T, fn: (store: DurableStore) => Promise<T>): Promise<T> {
    return durableStore ? fn(durableStore) : Promise.resolve(onUnavailable());
  }

  function scheduleSweep(): void {
    sweepHandle = setTimeout(() => {
      void (async () => {
        if (closed) return;
        // A sweep tick that fails is caught and logged; the next tick still runs on schedule
        // (S4.9) — never escaping this timer as an unhandled rejection.
        if (durableStore) {
          const result = await durableStore.sweepOnce();
          if (!result.ok) {
            console.error("[game-service] sweep tick failed", result.error);
          }
        }
        if (!closed) scheduleSweep();
      })();
    }, storeConfig.bounds.sweepIntervalSeconds * 1000);
  }

  await attemptConnect();
  scheduleSweep();

  // Built once, not per `forRequest()`. The counters are the whole point of a counting
  // `RecordIdSource`: minting one per request would restart them at zero every time, so every
  // created session under the replay profile would be handed `counting-session-id-0` and the second
  // one would overwrite the first. The in-memory branch above gets this for free — it composes one
  // long-lived layer — and the durable branch has to say it.
  const replaySources = replay
    ? { clock: createFixedClock(replay.fixedInstant), recordIds: createCountingRecordIds() }
    : null;

  // S13.8: the lifecycle probe is composed behind the same seam `SessionPersistence` and
  // `ProfileStore` are — the identical `composeStorageSeam` function, on the identical (identity,
  // in G2) decorator, at both call sites below.
  const rawLifecycle: LifecycleProbe = {
    session: (sessionId: string) =>
      withDurableStore(
        () => err({ code: "Unreachable" }),
        (store) => store.lifecycle.session(sessionId),
      ),
    save: (saveId: string) =>
      withDurableStore(
        () => err({ code: "Unreachable" }),
        (store) => store.lifecycle.save(saveId),
      ),
  };
  const lifecycle: LifecycleProbe = composeStorageSeam({
    persistence: unavailablePersistence(),
    profiles: unavailableProfiles(),
    lifecycle: rawLifecycle,
  }).lifecycle;

  const stores: StoreProvider = {
    forRequest(): SessionStore {
      const active = durableStore;
      const seam = composeStorageSeam({
        persistence: active ? active.persistenceForRequest() : unavailablePersistence(),
        profiles: active ? active.profiles : unavailableProfiles(),
        lifecycle: rawLifecycle,
      });
      return createSessionLayer({
        engine,
        registry: registry.value,
        persistence: seam.persistence,
        profiles: seam.profiles,
        ...(replaySources ?? {}),
      });
    },
  };

  const serialization: StoreSerializationHandle = {
    snapshot: () =>
      withDurableStore(
        () => {
          // Unlike `lifecycle`'s `Unreachable` and `unavailableProfiles`'s warnings, this port has
          // no error channel to report unavailability through (`snapshot()` always resolves) —
          // logged so an empty dump taken during an outage is not silently indistinguishable from a
          // genuinely empty store.
          console.error("[game-service] snapshot taken while the durable store is unavailable — returning empty");
          return { sessions: [], saves: [] };
        },
        (store) => store.serialization.snapshot(),
      ),
  };

  return ok({
    stores,
    lifecycle,
    serialization,
    async readiness(): Promise<ProbeResult> {
      // Evaluates the store on every call rather than replaying a memoized startup outcome — a
      // latch here would leave the workload reporting ready through the exact outage the readiness
      // probe exists to surface (S4.4).
      if (!durableStore) {
        // `detail`, part of `ProbeResult`'s declared shape (`design/20-contract.md`, "Workload —
        // readiness"), names which migration or lock condition is holding startup back
        // (S12.6/S12.7); omitted rather than `null` when there is none yet to report.
        return notReadyDetail ? { status: "unhealthy", detail: notReadyDetail } : { status: "unhealthy" };
      }
      const checked = await durableStore.check();
      return { status: checked.ok ? "healthy" : "unhealthy" };
    },
    async close(): Promise<void> {
      closed = true;
      if (reconnectHandle !== null) clearTimeout(reconnectHandle);
      if (sweepHandle !== null) clearTimeout(sweepHandle);
      // `clearTimeout` cannot abort a retry whose callback has already started running — wait for
      // it, so shutdown never returns while a stray `attemptConnect()` (and its own cleanup) is
      // still in flight underneath it.
      if (connecting) await connecting;
      if (durableStore) await durableStore.close();
    },
  });
}

/** Written under the replay profile only, at shutdown, before the listener stops accepting. The
 *  signature takes a `ReplayDeterminismProfile`, not a `DeterminismProfile` — it cannot be called
 *  with the default profile, which is how "with the default profile, nothing is written" is
 *  enforced by the type rather than left to a branch. */
export async function writeDeterminismDump(
  composed: ComposedWorkload,
  profile: ReplayDeterminismProfile,
): Promise<Outcome<void, CompositionError>> {
  const snapshot = await composed.serialization.snapshot();
  const dump: DeterminismDump = {
    sessions: Object.fromEntries(snapshot.sessions.map((blob) => [blob.id, blob.blob])),
    saves: Object.fromEntries(snapshot.saves.map((blob) => [blob.id, blob.blob])),
  };

  // `canonicalEncode` is what makes "keyed by id, in id order" a property of the encoding rather
  // than a step the writer must remember (`20-contract.md`, additions requiring a decision-log
  // entry, item 4).
  const encoded = canonicalEncode(dump);
  if (!encoded.ok) {
    return err({ code: "DumpWriteFailed", path: profile.dumpPath });
  }

  // Written to a temporary path first and renamed into place, so a write that fails partway
  // through never leaves an empty or partial file at `dumpPath` for a later reader to mistake for
  // an empty store (S4.6).
  const temporaryPath = `${profile.dumpPath}.tmp-${process.pid}-${Date.now()}`;
  try {
    writeFileSync(temporaryPath, encoded.value, "utf8");
    renameSync(temporaryPath, profile.dumpPath);
  } catch {
    try {
      unlinkSync(temporaryPath);
    } catch {
      // Nothing was left to clean up — the write itself never landed.
    }
    return err({ code: "DumpWriteFailed", path: profile.dumpPath });
  }

  return ok(undefined);
}
