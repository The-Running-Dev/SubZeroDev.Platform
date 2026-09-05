/**
 * Test support for S5 — a real session layer over a persistence the test controls.
 *
 * S5.1, S5.2 and S5.7 are all stated in terms of "a stub store whose `sessions.put` throws", and
 * that is a `SessionPersistence`, not a `SessionStore`: the conflict brand is recognised by the
 * engine's own session layer (S1), so a fixture that stubbed the layer would assert Dispatch
 * against a mock of the very translation under test. The layer is built the way `compose.ts`
 * builds its in-memory one, so what these tests dispatch through is the engine's real write path.
 */
import {
  buildContentRegistry,
  buildWorldGraphMvpCampaign,
  createEngine,
  createInMemoryProfileStore,
  createSessionLayer,
  SESSION_PERSISTENCE_CONFLICT,
  simulationKind,
  storyGraphKind,
  worldGraphKind,
} from "@the-running-dev/game-engine";
import type {
  KindRegistry,
  SessionPersistence,
  SessionStore,
  StoredSaveRecord,
  StoredSessionRecord,
} from "@the-running-dev/game-engine";

/** The brand is a `name` property and never an `instanceof` — that is how the engine recognises a
 *  conflict across duplicated package copies, and a fixture that used a subclass would be testing
 *  a recognition path the durable adapter does not use. */
export function conflictError(): Error {
  const error = new Error("another writer changed this session");
  Object.defineProperty(error, "name", { value: SESSION_PERSISTENCE_CONFLICT });
  return error;
}

export interface ControlledPersistence {
  readonly persistence: SessionPersistence;
  /** Every `sessions.put` from here on throws what `make` returns. `null` restores the store. */
  failSessionPut(make: (() => Error) | null): void;
  /** How many times `sessions.put` has been called — S5.7 asserts exactly one per request. */
  sessionPutCalls(): number;
}

export function controlledPersistence(): ControlledPersistence {
  const sessions = new Map<string, StoredSessionRecord>();
  const saves = new Map<string, StoredSaveRecord>();
  let failure: (() => Error) | null = null;
  let putCalls = 0;

  return {
    failSessionPut(make: (() => Error) | null): void {
      failure = make;
    },
    sessionPutCalls(): number {
      return putCalls;
    },
    persistence: {
      sessions: {
        get: async (sessionId: string) => sessions.get(sessionId),
        put: async (record: StoredSessionRecord) => {
          putCalls += 1;
          if (failure) throw failure();
          sessions.set(record.sessionId, record);
        },
      },
      saves: {
        get: async (saveId: string) => saves.get(saveId),
        put: async (record: StoredSaveRecord) => {
          saves.set(record.saveId, record);
        },
        // `SaveRecordStore` declares `listByProfile`/`delete` and `compose.ts`'s in-memory
        // persistence implements both; omitting either here would leave an engine path that lists
        // or deletes a save throwing a `TypeError` — not a `SessionStoreError`, so Dispatch would
        // not catch it and the request would answer `internal_failure` instead of the code under
        // test.
        listByProfile: async (profileId: string) => [...saves.values()].filter((record) => record.profileId === profileId),
        delete: async (saveId: string) => {
          saves.delete(saveId);
        },
      },
    } satisfies SessionPersistence,
  };
}

function kinds(): KindRegistry {
  return {
    [storyGraphKind.id]: storyGraphKind,
    [simulationKind.id]: simulationKind,
    [worldGraphKind.id]: worldGraphKind,
  } as KindRegistry;
}

/** The same construction `compose.ts`'s in-memory branch performs, over a persistence the test
 *  owns — the one thing `compose()` does not let a caller supply. */
export function sessionLayerOver(persistence: SessionPersistence): SessionStore {
  const campaign = buildWorldGraphMvpCampaign();
  if (!campaign.ok || !campaign.value) {
    throw new Error("the world-graph campaign failed to build");
  }
  const registry = buildContentRegistry([campaign.value]);
  if (!registry.ok || !registry.value) {
    throw new Error("the world-graph campaign failed to build a content registry");
  }
  const engine = createEngine({ kinds: kinds(), registry: registry.value });
  return createSessionLayer({
    engine,
    registry: registry.value,
    persistence,
    profiles: createInMemoryProfileStore(),
  });
}
