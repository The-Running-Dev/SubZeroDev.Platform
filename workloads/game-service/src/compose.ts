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
  createEngine,
  createInMemoryProfileStore,
  createSessionLayer,
  ENGINE_VERSION,
  simulationKind,
  storyGraphKind,
  worldGraphKind,
} from "@the-running-dev/game-engine";
import type {
  ContentRegistry,
  KindRegistry,
  SessionPersistence,
  StoredSaveRecord,
  StoredSessionRecord,
} from "@the-running-dev/game-engine";
import type { ContractPackage } from "@subzerodev/service-contract";
import { err, ok } from "./types.js";
import type {
  ComposedWorkload,
  CompositionError,
  Outcome,
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
  const engine = createEngine({ kinds: kinds(), registry: registry.value });

  // The default determinism profile supplies no `RecordIdSource` and no fixed clock, so the
  // engine's own minting applies unchanged (invariant 12a). The replay profile's counting sources
  // are S4's, and nothing here reads `configuration.determinism` yet.
  void configuration;

  const store = createSessionLayer({
    engine,
    registry: registry.value,
    persistence,
    profiles: createInMemoryProfileStore(),
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
