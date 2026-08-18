/**
 * Composition — the engine, the content registry, the store provider (in-memory or durable), and
 * the engine-version assertion that decides whether any of it is built at all.
 *
 * `compose` owns that assertion, which is why it takes the contract: it uses nothing else from it.
 * A mismatch returns `EngineVersionMismatch` and no store is built, so the listener never binds.
 *
 * `compose` returns successfully even when a durable store is unreachable at startup (`20-contract.md`,
 * "Composition — workload"): the process stays up, reports live, reports not ready, and this module
 * retries the connection in the background. `StoreUnavailable` is a readiness condition, never a
 * `CompositionError` — the `CompositionError` variants are the ones no retry can fix.
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
import { openDurableStore } from "./store.js";
import { err, ok } from "./types.js";
import type {
  ComposedWorkload,
  CompositionError,
  DeterminismDump,
  DurableStore,
  LifecycleProbe,
  Outcome,
  ProbeResult,
  ReplayDeterminismProfile,
  SemanticVersion,
  StorageProfile,
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
    async load(profileId: string) {
      return { profile: { formatVersion: 1, profileId, achievements: [] }, warnings: [{ code: "profile_corrupt" as const, profileId }] };
    },
    async save(profile) {
      return { ok: false, warnings: [{ code: "profile_write_failed" as const, profileId: profile.profileId }] };
    },
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
 *  (`design/20-contract.md` line 767) is the edge's own configured value, and it lives in a
 *  different repository and process; threading it through `WorkloadConfiguration` would need a
 *  signature the contract does not carry. This is a workload-owned constant, on the same footing
 *  as `migrations.ts`'s `LOCK_WAIT_TIMEOUT_MS` — chosen to exceed the edge's own configured value
 *  (10s in this repository's own hosted-edge test support) by a wide margin, so the production
 *  default (`retentionHorizonSeconds` of 30 days) validates and a deliberately short horizon is
 *  still caught before any connection is attempted (S4.6). */
const ASSUMED_FORWARD_TIMEOUT_SECONDS = 60;

/** The interval between background reconnection attempts while a durable store has not yet
 *  connected. Not named by the contract — "retries with backoff" (`20-contract.md`, "Composition
 *  — workload") describes the requirement, not a value; this is the workload's own choice. */
const DURABLE_RECONNECT_INTERVAL_MS = 5_000;

function validateStorageConfiguration(storage: StorageProfile): Outcome<void, CompositionError> {
  if (storage.kind === "in-memory") return ok(undefined);
  const { connection, bounds } = storage.store;

  try {
    // eslint-disable-next-line no-new -- syntactic validity only; nothing here connects.
    new URL(connection.connectionString);
  } catch {
    return err({ code: "StorageConfigurationInvalid", setting: "storage.store.connection.connectionString" });
  }
  if (!Number.isInteger(connection.poolSize) || connection.poolSize <= 0) {
    return err({ code: "StorageConfigurationInvalid", setting: "storage.store.connection.poolSize" });
  }
  if (bounds.retentionHorizonSeconds <= ASSUMED_FORWARD_TIMEOUT_SECONDS) {
    return err({ code: "StorageConfigurationInvalid", setting: "storage.store.bounds.retentionHorizonSeconds" });
  }
  return ok(undefined);
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
    const { persistence, sessions, saves } = inMemoryPersistence();

    const store: SessionStore = createSessionLayer({
      engine,
      registry: registry.value,
      persistence,
      profiles: createInMemoryProfileStore(),
      ...(replay ? { clock: createFixedClock(replay.fixedInstant), recordIds: createCountingRecordIds() } : {}),
    });

    const stores: StoreProvider = { forRequest: () => store };

    // The no-op probe: every id classifies `absent`, so `unknown_session`/`unknown_save` pass
    // through Dispatch verbatim and Dispatch carries no branch on which store was composed
    // (`20-contract.md`, "Composition — workload").
    const lifecycle: LifecycleProbe = {
      session: async () => ok("absent"),
      save: async () => ok("absent"),
    };

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

  async function attemptConnect(): Promise<void> {
    const opened = await openDurableStore(storeConfig, ENGINE_VERSION as SemanticVersion);
    if (closed) {
      // `close()` ran while this attempt was in flight — a store that connected after the fact
      // must not be left dangling.
      if (opened.ok) await opened.value.close();
      return;
    }
    if (opened.ok) {
      durableStore = opened.value;
      return;
    }
    reconnectHandle = setTimeout(() => {
      void attemptConnect();
    }, DURABLE_RECONNECT_INTERVAL_MS);
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

  const stores: StoreProvider = {
    forRequest(): SessionStore {
      const active = durableStore;
      return createSessionLayer({
        engine,
        registry: registry.value,
        persistence: active ? active.persistenceForRequest() : unavailablePersistence(),
        profiles: active ? active.profiles : unavailableProfiles(),
        ...(replay ? { clock: createFixedClock(replay.fixedInstant), recordIds: createCountingRecordIds() } : {}),
      });
    },
  };

  const lifecycle: LifecycleProbe = {
    async session(sessionId: string) {
      if (!durableStore) return err({ code: "Unreachable" });
      return durableStore.lifecycle.session(sessionId);
    },
    async save(saveId: string) {
      if (!durableStore) return err({ code: "Unreachable" });
      return durableStore.lifecycle.save(saveId);
    },
  };

  const serialization: StoreSerializationHandle = {
    async snapshot(): Promise<StoreSerializationSnapshot> {
      if (!durableStore) return { sessions: [], saves: [] };
      return durableStore.serialization.snapshot();
    },
  };

  return ok({
    stores,
    lifecycle,
    serialization,
    async readiness(): Promise<ProbeResult> {
      // Evaluates the store on every call rather than replaying a memoized startup outcome — a
      // latch here would leave the workload reporting ready through the exact outage the readiness
      // probe exists to surface (S4.4).
      if (!durableStore) return { status: "unhealthy" };
      const checked = await durableStore.check();
      return { status: checked.ok ? "healthy" : "unhealthy" };
    },
    async close(): Promise<void> {
      closed = true;
      if (reconnectHandle !== null) clearTimeout(reconnectHandle);
      if (sweepHandle !== null) clearTimeout(sweepHandle);
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
