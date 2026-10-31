/** The last vendored release against the candidate, re-pinned on every engine upgrade. */
import { beforeAll, describe, expect, it } from "vitest";
import type { PlayerProfile, StoredSessionRecord } from "@the-running-dev/game-engine";
import { migrateToHead } from "../src/migrations.js";
import { RawSchemaClient, configurationFor, createTestSchema } from "./support/database.js";
import type { TestSchema } from "./support/database.js";
import {
  candidateRuntime, loadReleaseRuntime, openCandidate, sessionLayer,
} from "./support/mixed-version.js";
import type { ReleaseStore } from "./support/mixed-version.js";
import type { DurableStore } from "../src/types.js";

let release: Awaited<ReturnType<typeof loadReleaseRuntime>>;
beforeAll(async () => { release = await loadReleaseRuntime(); });

async function openRelease(schema: TestSchema): Promise<ReleaseStore> {
  const opened = await release.openDurableStore(configurationFor(schema.schema), release.engine.ENGINE_VERSION);
  if (!opened.ok) throw new Error(JSON.stringify(opened.error));
  return opened.value;
}

async function withMixed(run: (previous: ReleaseStore, next: DurableStore, raw: RawSchemaClient) => Promise<void>) {
  const schema = await createTestSchema();
  const stores: { close(): Promise<void> }[] = [];
  let raw: RawSchemaClient | undefined;
  try {
    const previous = await openRelease(schema);
    stores.push(previous);
    const next = await openCandidate(configurationFor(schema.schema));
    stores.push(next);
    raw = await RawSchemaClient.connect(schema.schema);
    await run(previous, next, raw);
  } finally {
    await raw?.close();
    await Promise.all(stores.map((store) => store.close()));
    await schema.drop();
  }
}

async function requireSession(store: DurableStore, id: string): Promise<StoredSessionRecord> {
  const record = await store.persistenceForRequest().sessions.get(id);
  if (!record) throw new Error(`Missing session ${id}`);
  return record;
}

const achievement = (id: string) => ({ campaignId: "mixed-version", achievementId: id });
const kindData: PlayerProfile["kindData"] = [
  { kindId: "simulation", dataVersion: 1, data: { visits: ["α", "世界"] } },
  { kindId: "world-graph", dataVersion: 2, data: { nested: { retained: true } } },
];

describe("previous release ↔ candidate on PostgreSQL", () => {
  it("alternates both session runtimes and loads each runtime's saves through the other", async () => {
    await withMixed(async (previous, next, raw) => {
      const { sessionId } = await sessionLayer(release, previous.persistenceForRequest())
        .createSession({ campaignId: "mixed-version", seed: "shared" });
      for (let index = 0; index < 6; index += 1) {
        const candidateTurn = index % 2 === 0;
        const runtime = candidateTurn ? candidateRuntime : release;
        await sessionLayer(runtime, (candidateTurn ? next : previous).persistenceForRequest())
          .submitAction(sessionId, "stay");
        const state = await requireSession(next, sessionId);
        expect(state.revision).toBe(index + 1);
        expect(JSON.parse(state.blob).actionLog).toHaveLength(index + 1);
        expect((await raw.query("select engine_version from session where session_id = $1", [sessionId])).rows)
          .toEqual([{ engine_version: runtime.engine.ENGINE_VERSION }]);
      }
      const newSave = await sessionLayer(candidateRuntime, next.persistenceForRequest()).saveGame(sessionId);
      const loadedByRelease = await sessionLayer(release, previous.persistenceForRequest()).loadGame(newSave.saveId);
      expect(JSON.parse((await requireSession(next, loadedByRelease.sessionId)).blob).actionLog).toHaveLength(6);
      const releaseSave = await sessionLayer(release, previous.persistenceForRequest()).saveGame(sessionId);
      const loadedByNew = await sessionLayer(candidateRuntime, next.persistenceForRequest()).loadGame(releaseSave.saveId);
      expect(JSON.parse((await requireSession(next, loadedByNew.sessionId)).blob).actionLog).toHaveLength(6);
    });
  });

  it("reads each side's profile writes without warnings, keeps kindData, and stores format 2", async () => {
    await withMixed(async (previous, next, raw) => {
      const profileId = "shared-profile";
      expect(await next.profiles.save({
        formatVersion: 3, profileId, achievements: [achievement("new")], terminals: [], kindData,
      })).toEqual({ ok: true, warnings: [] });
      const readByRelease = await previous.profiles.load(profileId);
      expect(readByRelease.warnings).toEqual([]);
      expect(readByRelease.profile.kindData).toEqual(kindData);
      expect((await previous.profiles.save({
        ...readByRelease.profile, achievements: [...readByRelease.profile.achievements, achievement("release")],
      })).ok).toBe(true);
      const readByNew = await next.profiles.load(profileId);
      expect(readByNew.warnings).toEqual([]);
      expect(readByNew.profile.kindData).toEqual(kindData);
      expect(readByNew.profile.achievements).toEqual(expect.arrayContaining([achievement("new"), achievement("release")]));
      expect((await raw.query("select format_version from profile where profile_id = $1", [profileId])).rows)
        .toEqual([{ format_version: 2 }]);
    });
  });

  it("rolls code back to the previous release over the candidate's schema and data", async () => {
    const schema = await createTestSchema(release.migrateToHead);
    let next: DurableStore | undefined;
    let previous: ReleaseStore | undefined;
    try {
      expect((await migrateToHead(schema.connection)).ok).toBe(true);
      next = await openCandidate(configurationFor(schema.schema));
      const layer = sessionLayer(candidateRuntime, next.persistenceForRequest());
      const { sessionId } = await layer.createSession({ campaignId: "mixed-version", seed: "shared" });
      await layer.submitAction(sessionId, "stay");
      const saved = await layer.saveGame(sessionId);
      await next.profiles.save({ formatVersion: 3, profileId: "rollback", achievements: [achievement("new")], terminals: [], kindData });
      await next.close();
      next = undefined;

      // The previous release's startup runs only UP migrations; over the head schema that is a no-op.
      expect((await release.migrateToHead(schema.connection)).ok).toBe(true);
      previous = await openRelease(schema);
      const rollback = sessionLayer(release, previous.persistenceForRequest());
      await rollback.resumeSession(sessionId);
      await rollback.submitAction(sessionId, "stay");
      await rollback.loadGame(saved.saveId);
      const readByRelease = await previous.profiles.load("rollback");
      expect(readByRelease.warnings).toEqual([]);
      expect(readByRelease.profile.kindData).toEqual(kindData);
      await previous.close();
      previous = undefined;

      next = await openCandidate(configurationFor(schema.schema));
      expect((await requireSession(next, sessionId)).revision).toBe(2);
      await sessionLayer(candidateRuntime, next.persistenceForRequest()).resumeSession(sessionId);
    } finally {
      await previous?.close();
      await next?.close();
      await schema.drop();
    }
  });
});
