/**
 * Composition — the engine, the content registry, the map-backed persistence and profile store,
 * and the engine-version assertion that decides whether any of it is built at all.
 *
 * `compose` owns that assertion, which is why it takes the contract: it uses nothing else from it.
 * A mismatch returns `EngineVersionMismatch` and no store is built, so the listener never binds.
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
  RecordIdSource,
  SessionPersistence,
  StoredSaveRecord,
  StoredSessionRecord,
} from "@the-running-dev/game-engine";
import type { ContractPackage } from "@subzerodev/service-contract";
import { renameSync, unlinkSync, writeFileSync } from "node:fs";
import { canonicalEncode } from "./canonical.js";
import { err, ok } from "./types.js";
import type {
  ComposedWorkload,
  CompositionError,
  DeterminismDump,
  JsonValue,
  Outcome,
  ReplayDeterminismProfile,
  StoreSerializationHandle,
  StoreSerializationSnapshot,
  WorkloadConfiguration,
} from "./types.js";

/** `KindRegistry` is a plain record keyed by kind id; kinds are engine-owned and are not ports. */
function kinds(): KindRegistry {
  return {
    [storyGraphKind.id]: storyGraphKind,
    [simulationKind.id]: simulationKind,
    [worldGraphKind.id]: worldGraphKind,
  } as KindRegistry;
}

/** Map-backed and total — G1 has no database, and `storage_failure` is consequently declared and
 *  unreachable. The maps are also what the serialization handle reads, which is why persistence is
 *  supplied explicitly rather than left to the engine's own default. */
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

  const registry = contentRegistry();
  if (!registry.ok) return registry;

  const { persistence, sessions, saves } = inMemoryPersistence();

  // The default profile supplies neither `ids` nor `recordIds` nor `clock`, so the engine's own
  // minting and the real wall clock apply unchanged (invariant 12a). The replay profile supplies
  // all three, each a counting or fixed source on the same terms as the engine's own fixture.
  const replay = configuration.determinism.kind === "replay" ? configuration.determinism : null;

  const engine = createEngine({
    kinds: kinds(),
    registry: registry.value,
    ...(replay ? { ids: createCountingIds() } : {}),
  });

  const store = createSessionLayer({
    engine,
    registry: registry.value,
    persistence,
    profiles: createInMemoryProfileStore(),
    ...(replay ? { clock: createFixedClock(replay.fixedInstant), recordIds: createCountingRecordIds() } : {}),
  });

  /** The blobs only, never the host-owned record fields around them (invariant 16). Exposed on
   *  `ComposedWorkload` and passed to the shutdown writer and the harness — never to a surface. */
  const serialization: StoreSerializationHandle = {
    async snapshot(): Promise<StoreSerializationSnapshot> {
      return {
        sessions: [...sessions.values()].map((record) => ({ id: record.sessionId, blob: record.blob })),
        saves: [...saves.values()].map((record) => ({ id: record.saveId, blob: record.blob })),
      };
    },
  };

  return ok({ store, serialization });
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
  const encoded = canonicalEncode(dump as unknown as JsonValue);
  if (!encoded.ok) {
    return err({ code: "DumpWriteFailed", path: profile.dumpPath });
  }

  // Written to a temporary path first and renamed into place, so a write that fails partway
  // through never leaves an empty or partial file at `dumpPath` for a later reader to mistake for
  // an empty store (S4.6).
  const temporaryPath = `${profile.dumpPath}.tmp-${process.pid}-${Date.now()}`;
  try {
    writeFileSync(temporaryPath, encoded.value as string, "utf8");
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
