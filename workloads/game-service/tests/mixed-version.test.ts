/** #353: adapter compatibility on real PostgreSQL, not a production rollout proof. */
import { beforeAll, describe, expect, it } from "vitest";
import type { PlayerProfile, StoredSessionRecord } from "@the-running-dev/game-engine";
import { Client } from "pg";
import { runner } from "node-pg-migrate";
import { fileURLToPath } from "node:url";
import { migrateToHead } from "../src/migrations.js";
import { RawSchemaClient, configurationFor, createTestSchema } from "./support/database.js";
import type { TestSchema } from "./support/database.js";
import {
  candidateRuntime, G2_ROW_OWNER, loadOldRuntime, openCandidate, sessionLayer,
} from "./support/mixed-version.js";
import type { OldStore } from "./support/mixed-version.js";
import type { DurableStore } from "../src/types.js";

let old: Awaited<ReturnType<typeof loadOldRuntime>>;
beforeAll(async () => { old = await loadOldRuntime(); });

async function openOld(schema: TestSchema): Promise<OldStore> {
  const opened = await old.openDurableStore(configurationFor(schema.schema), old.engine.ENGINE_VERSION);
  if (!opened.ok) throw new Error(JSON.stringify(opened.error));
  return opened.value;
}

async function withMixed(run: (schema: TestSchema, previous: OldStore, next: DurableStore, raw: RawSchemaClient) => Promise<void>) {
  const schema = await createTestSchema();
  const stores: { close(): Promise<void> }[] = [];
  let raw: RawSchemaClient | undefined;
  try {
    const previous = await openOld(schema);
    stores.push(previous);
    const next = await openCandidate(configurationFor(schema.schema));
    stores.push(next);
    raw = await RawSchemaClient.connect(schema.schema);
    await run(schema, previous, next, raw);
  } finally {
    await raw?.close();
    await Promise.all(stores.map((store) => store.close()));
    await schema.drop();
  }
}

/** Applies exactly one pending migration with the runner's real bookkeeping, so a test can stop at
 *  the G2 head instead of `migrateToHead`, whose G3 migration deletes every row (invariant 128). */
async function migrateOneStep(schema: TestSchema): Promise<void> {
  const client = new Client({ connectionString: schema.connection.connectionString });
  await client.connect();
  try {
    await runner({
      dbClient: client, dir: fileURLToPath(new URL("../migrations", import.meta.url)),
      schema: String(schema.schema), migrationsSchema: String(schema.schema), migrationsTable: "pgmigrations",
      direction: "up", count: 1, singleTransaction: true, advisoryLockMode: "wait",
      logger: { info() {}, warn() {}, error() {} },
    });
  } finally {
    await client.end();
  }
}

const OWNED_TABLES = ["session", "save", "profile", "profile_achievement", "profile_terminal", "profile_kind_data"];

async function ownedRowCounts(raw: RawSchemaClient): Promise<Record<string, number>> {
  const counts: Record<string, number> = {};
  for (const table of OWNED_TABLES) {
    const [row] = (await raw.query(`select count(*)::int as count from ${table}`)).rows as { count: number }[];
    counts[table] = row?.count ?? -1;
  }
  return counts;
}

const NO_OWNED_ROWS = Object.fromEntries(OWNED_TABLES.map((table) => [table, 0]));

async function requireSession(store: DurableStore, id: string): Promise<StoredSessionRecord> {
  const record = await store.storage.persistence(G2_ROW_OWNER).sessions.get(id);
  if (!record) throw new Error(`Missing session ${id}`);
  return record;
}

const achievement = (id: string) => ({ campaignId: "mixed-version", achievementId: id });
const terminal = (id: string) => ({ campaignId: "mixed-version", terminalId: id });
const kindData: PlayerProfile["kindData"] = [
  { kindId: "simulation", dataVersion: 1, data: { visits: ["α", "世界"] } },
  { kindId: "world-graph", dataVersion: 2, data: { nested: { retained: true } } },
];

describe("#353 mixed-version PostgreSQL profiles", () => {
  it("old reads a new write without corruption; an old write after the G3 migration fails and changes nothing", async () => {
    await withMixed(async (_schema, previous, next, raw) => {
      const profileId = "new-profile";
      expect(await next.storage.profiles(G2_ROW_OWNER).save({
        formatVersion: 3, profileId, achievements: [achievement("new")], terminals: [terminal("new")], kindData,
      })).toEqual({ ok: true, warnings: [] });

      // This assertion must fail if the candidate starts storing format 3 prematurely. Checking
      // only the SQL format number would never exercise the previous adapter's decoder.
      const readByOld = await previous.profiles.load(profileId);
      expect(readByOld.warnings).toEqual([]);
      expect(readByOld.profile).toEqual({
        formatVersion: 2, profileId, achievements: [achievement("new")], terminals: [terminal("new")],
      });
      expect((await raw.query("select format_version from profile where profile_id = $1", [profileId])).rows)
        .toEqual([{ format_version: 2 }]);
      // The G2 upsert names `on conflict (tenant_id, profile_id)`, which the G3 key no longer is.
      expect(await previous.profiles.save({
        ...readByOld.profile,
        achievements: [...readByOld.profile.achievements, achievement("old")],
        terminals: [...readByOld.profile.terminals, terminal("old")],
      })).toEqual({ ok: false, warnings: [{ code: "profile_write_failed", profileId }] });
      const afterOld = await next.storage.profiles(G2_ROW_OWNER).load(profileId);
      expect(afterOld.warnings).toEqual([]);
      expect(afterOld.profile.kindData).toEqual(kindData);
      expect(afterOld.profile.achievements).toEqual([achievement("new")]);
      expect(afterOld.profile.terminals).toEqual([terminal("new")]);
      expect((await previous.profiles.load(profileId)).warnings).toEqual([]);
    });
  });

  it("refuses a first old profile write after the G3 migration and leaves no profile row", async () => {
    await withMixed(async (_schema, previous, next, raw) => {
      const profileId = "old-profile";
      expect(await previous.profiles.save({
        formatVersion: 2, profileId, achievements: [achievement("old")], terminals: [terminal("old")],
      })).toEqual({ ok: false, warnings: [{ code: "profile_write_failed", profileId }] });
      expect(await ownedRowCounts(raw)).toEqual(NO_OWNED_ROWS);
      const loaded = await next.storage.profiles(G2_ROW_OWNER).load(profileId);
      expect(loaded.warnings).toEqual([{ code: "profile_missing", profileId }]);
      expect(loaded.profile.achievements).toEqual([]);
    });
  });

  it("documents the intentional limit: a future stored format 3 is readable only by the new adapter", async () => {
    await withMixed(async (_schema, previous, next, raw) => {
      const profileId = "future-format";
      await next.storage.profiles(G2_ROW_OWNER).save({ formatVersion: 3, profileId, achievements: [achievement("kept")], terminals: [], kindData });
      await raw.query("update profile set format_version = 3 where profile_id = $1", [profileId]);
      expect((await previous.profiles.load(profileId)).warnings).toEqual([{ code: "profile_corrupt", profileId }]);
      const supported = await next.storage.profiles(G2_ROW_OWNER).load(profileId);
      expect(supported.warnings).toEqual([]);
      expect(supported.profile.kindData).toEqual(kindData);
      expect(supported.profile.achievements).toEqual([achievement("kept")]);
    });
  });
});

describe("#353 mixed-version PostgreSQL sessions and saves", () => {
  it("repairs revision drift written after the inspected upgrade migration and before the correction", async () => {
    const schema = await createTestSchema(old.migrateToHead);
    const previous = await openOld(schema);
    const raw = await RawSchemaClient.connect(schema.schema);
    try {
      // Apply only #353's original third migration, leaving the correction pending. Use the
      // runner's real migration bookkeeping rather than pretending a current schema is old.
      await migrateOneStep(schema);
      expect((await raw.query("select count(*)::int as count from pgmigrations")).rows).toEqual([{ count: 3 }]);
      const layer = sessionLayer(old, previous.persistenceForRequest());
      const { sessionId } = await layer.createSession({ campaignId: "mixed-version", seed: "shared" });
      await layer.submitAction(sessionId, "stay");
      await layer.submitAction(sessionId, "stay");
      expect((await raw.query("select version, revision from session")).rows).toEqual([{ version: 3n, revision: 0 }]);
      await migrateOneStep(schema);
      expect((await raw.query("select version, revision from session")).rows).toEqual([{ version: 3n, revision: 2 }]);
      await layer.submitAction(sessionId, "stay");
      expect((await raw.query("select version, revision from session")).rows).toEqual([{ version: 4n, revision: 3 }]);
      // The G3 migration then deletes the repaired row with every other pre-G3 row (invariant 128).
      expect((await migrateToHead(schema.connection)).ok).toBe(true);
      expect(await ownedRowCounts(raw)).toEqual(NO_OWNED_ROWS);
    } finally {
      await raw.close();
      await previous.close();
      await schema.drop();
    }
  });

  it("backfills revision on populated old rows at the G2 head, then the G3 migration deletes every one", async () => {
    const schema = await createTestSchema(old.migrateToHead);
    const previous = await openOld(schema);
    let next: DurableStore | undefined;
    const raw = await RawSchemaClient.connect(schema.schema);
    try {
      const layer = sessionLayer(old, previous.persistenceForRequest());
      const handle = await layer.createSession({ campaignId: "mixed-version", seed: "shared" });
      await layer.submitAction(handle.sessionId, "stay");
      await layer.submitAction(handle.sessionId, "stay");
      const saved = await layer.saveGame(handle.sessionId);
      expect((await previous.profiles.save({
        formatVersion: 2, profileId: "owner", achievements: [achievement("old")], terminals: [terminal("old")],
      })).ok).toBe(true);
      await migrateOneStep(schema);
      expect((await raw.query("select version, revision, initialization from session")).rows)
        .toEqual([{ version: 3n, revision: 2, initialization: null }]);
      expect((await raw.query("select count(*)::int as count from save")).rows).toEqual([{ count: 1 }]);

      // Rows written before players had owners cannot be attributed to one (invariant 128).
      expect(await migrateToHead(schema.connection)).toEqual({ ok: true, value: undefined });
      expect(await ownedRowCounts(raw)).toEqual(NO_OWNED_ROWS);
      next = await openCandidate(configurationFor(schema.schema));
      expect(await next.storage.persistence(G2_ROW_OWNER).sessions.get(handle.sessionId)).toBeUndefined();
      expect(await next.storage.persistence(G2_ROW_OWNER).saves.get(saved.saveId)).toBeUndefined();
      expect(await previous.persistenceForRequest().sessions.get(handle.sessionId)).toBeUndefined();
    } finally {
      await raw.close();
      await next?.close();
      await previous.close();
      await schema.drop();
    }
  });

  it("keeps revision in step with accepted writes made by the actual old adapter after migration", async () => {
    await withMixed(async (_schema, previous, next, raw) => {
      const layer = sessionLayer(old, previous.persistenceForRequest());
      const handle = await layer.createSession({ campaignId: "mixed-version", seed: "shared" });
      await layer.submitAction(handle.sessionId, "stay");
      await layer.submitAction(handle.sessionId, "stay");
      expect((await raw.query("select version, revision from session where session_id = $1", [handle.sessionId])).rows)
        .toEqual([{ version: 3n, revision: 2 }]);
      expect((await requireSession(next, handle.sessionId)).revision).toBe(2);
    });
  });

  it("alternates both real session runtimes and loads each runtime's saves through the other", async () => {
    await withMixed(async (_schema, previous, next, raw) => {
      const { sessionId } = await sessionLayer(old, previous.persistenceForRequest())
        .createSession({ campaignId: "mixed-version", seed: "shared" });
      for (let index = 0; index < 6; index += 1) {
        const candidateTurn = index % 2 === 0;
        await sessionLayer(
          candidateTurn ? candidateRuntime : old,
          candidateTurn ? next.storage.persistence(G2_ROW_OWNER) : previous.persistenceForRequest(),
        ).submitAction(sessionId, "stay");
        const state = await requireSession(next, sessionId);
        expect(state.revision).toBe(index + 1);
        expect(JSON.parse(state.blob).actionLog).toHaveLength(index + 1);
        expect((await raw.query("select engine_version from session where session_id = $1", [sessionId])).rows)
          .toEqual([{ engine_version: (candidateTurn ? candidateRuntime : old).engine.ENGINE_VERSION }]);
      }
      const newSave = await sessionLayer(candidateRuntime, next.storage.persistence(G2_ROW_OWNER)).saveGame(sessionId);
      const loadedByOld = await sessionLayer(old, previous.persistenceForRequest()).loadGame(newSave.saveId);
      expect(JSON.parse((await requireSession(next, loadedByOld.sessionId)).blob).actionLog).toHaveLength(6);
      const oldSave = await sessionLayer(old, previous.persistenceForRequest()).saveGame(sessionId);
      const loadedByNew = await sessionLayer(candidateRuntime, next.storage.persistence(G2_ROW_OWNER)).loadGame(oldSave.saveId);
      expect(JSON.parse((await requireSession(next, loadedByNew.sessionId)).blob).actionLog).toHaveLength(6);
    });
  });

  it("rejects stale old and new writers, then accepts fresh reads across alternating writers", async () => {
    await withMixed(async (_schema, previous, next, raw) => {
      const { sessionId } = await sessionLayer(old, previous.persistenceForRequest())
        .createSession({ campaignId: "mixed-version", seed: "shared" });
      for (let round = 0; round < 3; round += 1) {
        const oldWriter = previous.persistenceForRequest();
        const newWriter = next.storage.persistence(G2_ROW_OWNER);
        const oldRead = await oldWriter.sessions.get(sessionId);
        const newRead = await newWriter.sessions.get(sessionId);
        if (!oldRead || !newRead) throw new Error("Missing shared session");
        await oldWriter.sessions.put({ ...oldRead, attemptCounter: round * 2 + 1 });
        await expect(newWriter.sessions.put({ ...newRead, revision: newRead.revision + 1, attemptCounter: 999 }))
          .rejects.toMatchObject({ name: "SessionPersistenceConflict", outcome: "conflict" });

        const oldStale = previous.persistenceForRequest();
        const oldSnapshot = await oldStale.sessions.get(sessionId);
        const fresh = await newWriter.sessions.get(sessionId);
        if (!oldSnapshot || !fresh) throw new Error("Missing shared session");
        await newWriter.sessions.put({ ...fresh, revision: fresh.revision + 1, attemptCounter: round * 2 + 2 });
        await expect(oldStale.sessions.put({ ...oldSnapshot, attemptCounter: 999 }))
          .rejects.toMatchObject({ name: "SessionPersistenceConflict", outcome: "conflict" });
        expect((await raw.query("select version, revision, attempt_counter from session where session_id = $1", [sessionId])).rows)
          .toEqual([{ version: BigInt(round * 2 + 3), revision: round * 2 + 2, attempt_counter: round * 2 + 2 }]);
        expect((await requireSession(next, sessionId)).blob).toBe(oldRead.blob);
      }
    });
  });

  it("rejects a new writer's pre-old-write revision even after its row-version map was refreshed", async () => {
    await withMixed(async (_schema, previous, next) => {
      const { sessionId } = await sessionLayer(old, previous.persistenceForRequest())
        .createSession({ campaignId: "mixed-version", seed: "shared" });
      const newWriter = next.storage.persistence(G2_ROW_OWNER);
      const stale = await newWriter.sessions.get(sessionId);
      const oldWriter = previous.persistenceForRequest();
      const oldRead = await oldWriter.sessions.get(sessionId);
      if (!stale || !oldRead) throw new Error("Missing shared session");
      await oldWriter.sessions.put({ ...oldRead, attemptCounter: 1 });
      // Refresh only the adapter's row-version map, while attempting to write the stale engine
      // record. The engine revision guard must independently refuse it.
      await newWriter.sessions.get(sessionId);
      await expect(newWriter.sessions.put({ ...stale, revision: stale.revision + 1, attemptCounter: 999 }))
        .rejects.toMatchObject({ name: "SessionPersistenceConflict", outcome: "conflict" });
      expect((await requireSession(next, sessionId)).attemptCounter).toBe(1);
    });
  });

  it.each([undefined, {}, { kindProfileData: { visits: [1, 2] } }])(
    "preserves session/save initialization %j through old reads, updates and re-puts",
    async (initialization) => {
      await withMixed(async (_schema, previous, next) => {
        const { sessionId } = await sessionLayer(candidateRuntime, next.storage.persistence(G2_ROW_OWNER))
          .createSession({ campaignId: "mixed-version", seed: "shared" });
        const writer = next.storage.persistence(G2_ROW_OWNER);
        const initial = await writer.sessions.get(sessionId);
        if (!initial) throw new Error("Missing candidate session");
        const { initialization: _ignored, ...withoutInitialization } = initial;
        await writer.sessions.put({ ...withoutInitialization, revision: 1, ...(initialization === undefined ? {} : { initialization }) });
        const oldWriter = previous.persistenceForRequest();
        const readByOld = await oldWriter.sessions.get(sessionId);
        if (!readByOld) throw new Error("Missing old session");
        expect(readByOld).not.toHaveProperty("initialization");
        expect(readByOld).not.toHaveProperty("revision");
        await oldWriter.sessions.put({ ...readByOld, attemptCounter: 2 });
        const afterOld = await requireSession(next, sessionId);
        expect(afterOld.revision).toBe(2);
        expect(afterOld.initialization).toEqual(initialization);
        expect("initialization" in afterOld).toBe(initialization !== undefined);
        // The old runtime can actually resume this new-written story-graph envelope.
        await sessionLayer(old, previous.persistenceForRequest()).resumeSession(sessionId);

        const { saveId } = await sessionLayer(candidateRuntime, next.storage.persistence(G2_ROW_OWNER)).saveGame(sessionId);
        const savedByNew = await writer.saves.get(saveId);
        if (!savedByNew) throw new Error("Missing candidate save");
        expect(savedByNew.initialization).toEqual(initialization);
        await writer.saves.put({ ...savedByNew, savedAt: "2026-10-28T00:00:00.000Z", profileId: "owner" });
        const oldSave = await oldWriter.saves.get(saveId);
        if (!oldSave) throw new Error("Missing old save");
        expect(oldSave).not.toHaveProperty("initialization");
        await oldWriter.saves.put({ ...oldSave, savedAt: "2026-10-28T00:00:01.000Z" });
        const newSave = await next.storage.persistence(G2_ROW_OWNER).saves.get(saveId);
        expect(newSave?.initialization).toEqual(initialization);
        expect(newSave && "initialization" in newSave).toBe(initialization !== undefined);
        const loadedByNew = await sessionLayer(candidateRuntime, next.storage.persistence(G2_ROW_OWNER)).loadGame(saveId);
        const restored = await requireSession(next, loadedByNew.sessionId);
        expect(restored.initialization).toEqual(initialization);
        expect("initialization" in restored).toBe(initialization !== undefined);
        await sessionLayer(old, previous.persistenceForRequest()).loadGame(saveId);
        expect((await next.storage.persistence(G2_ROW_OWNER).saves.listByProfile("owner"))[0]).toEqual(newSave);
        expect(await oldWriter.saves.listByProfile("owner")).toEqual([{ ...oldSave, savedAt: "2026-10-28T00:00:01.000Z" }]);
        await expect(oldWriter.saves.delete(saveId, oldSave.savedAt)).rejects.toMatchObject({ name: "SessionPersistenceConflict" });
        await expect(writer.saves.delete(saveId, oldSave.savedAt)).rejects.toMatchObject({ name: "SessionPersistenceConflict" });
        await oldWriter.saves.delete(saveId, "2026-10-28T00:00:01.000Z");
        expect(await writer.saves.get(saveId)).toBeUndefined();
      });
    },
  );

  it("rolls code back to the old adapter/runtime while keeping the upgraded schema and new side data", async () => {
    const schema = await createTestSchema(old.migrateToHead);
    let next: DurableStore | undefined;
    let previous: OldStore | undefined;
    const raw = await RawSchemaClient.connect(schema.schema);
    try {
      expect((await migrateToHead(schema.connection)).ok).toBe(true);
      next = await openCandidate(configurationFor(schema.schema));
      const layer = sessionLayer(candidateRuntime, next.storage.persistence(G2_ROW_OWNER));
      const { sessionId } = await layer.createSession({ campaignId: "mixed-version", seed: "shared" });
      const saved = await layer.saveGame(sessionId);
      await next.storage.profiles(G2_ROW_OWNER).save({ formatVersion: 3, profileId: "rollback", achievements: [achievement("new")], terminals: [], kindData });
      await next.close();
      next = undefined;

      // Old startup does only UP migrations. No down migration and no new adapter is alive here.
      expect((await old.migrateToHead(schema.connection)).ok).toBe(true);
      previous = await openOld(schema);
      const rollback = sessionLayer(old, previous.persistenceForRequest());
      await rollback.resumeSession(sessionId);
      await rollback.submitAction(sessionId, "stay");
      await rollback.loadGame(saved.saveId);
      const readByOld = await previous.profiles.load("rollback");
      expect(readByOld.warnings).toEqual([]);
      expect(await previous.profiles.save({ ...readByOld.profile, achievements: [achievement("old")] }))
        .toEqual({ ok: false, warnings: [{ code: "profile_write_failed", profileId: "rollback" }] });
      expect((await raw.query("select version, revision, initialization from session where session_id = $1", [sessionId])).rows)
        .toEqual([{ version: 2n, revision: 1, initialization: "{}" }]);
      expect((await raw.query("select count(*)::int as count from profile_kind_data")).rows).toEqual([{ count: 2 }]);
      expect((await raw.query("select count(*)::int as count from pgmigrations")).rows).toEqual([{ count: 5 }]);
      await previous.close();
      previous = undefined;
      next = await openCandidate(configurationFor(schema.schema));
      const afterRollback = await next.storage.profiles(G2_ROW_OWNER).load("rollback");
      expect(afterRollback.warnings).toEqual([]);
      expect(afterRollback.profile.kindData).toEqual(kindData);
      expect(afterRollback.profile.achievements).toEqual([achievement("new")]);
    } finally {
      await raw.close();
      await previous?.close();
      await next?.close();
      await schema.drop();
    }
  });
});
