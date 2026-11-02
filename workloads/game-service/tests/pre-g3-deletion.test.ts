/** S9: rows saved before players had owners cannot be attributed to one, so the G3 migration
 *  deletes them (invariant 128). Seeded and read back at the previous release's own schema head. */
import { describe, expect, it } from "vitest";
import { migrateToHead } from "../src/migrations.js";
import { RawSchemaClient, configurationFor, createTestSchema } from "./support/database.js";
import { loadReleaseRuntime, sessionLayer } from "./support/mixed-version.js";

const SEEDED_TABLES = ["session", "save", "profile", "profile_achievement"];

async function rowCounts(raw: RawSchemaClient): Promise<Record<string, number>> {
  const counts: Record<string, number> = {};
  for (const table of SEEDED_TABLES) {
    const [row] = (await raw.query(`select count(*)::int as count from ${table}`)).rows as { count: number }[];
    counts[table] = row?.count ?? -1;
  }
  return counts;
}

describe("S9 pre-G3 rows", () => {
  it("S9.1 deletes every session, save, profile and achievement row a G2 schema held", async () => {
    const release = await loadReleaseRuntime();
    const schema = await createTestSchema(release.migrateToHead);
    const raw = await RawSchemaClient.connect(schema.schema);
    try {
      const opened = await release.openDurableStore(configurationFor(schema.schema), release.engine.ENGINE_VERSION);
      if (!opened.ok) throw new Error(JSON.stringify(opened.error));
      const previous = opened.value;
      try {
        const layer = sessionLayer(release, previous.persistenceForRequest());
        const { sessionId } = await layer.createSession({ campaignId: "mixed-version", seed: "g2" });
        await layer.submitAction(sessionId, "stay");
        await layer.saveGame(sessionId);
        const written = await previous.profiles.save({
          formatVersion: 3, profileId: "g2-player", kindData: [],
          achievements: [{ campaignId: "mixed-version", achievementId: "g2" }], terminals: [],
        });
        expect(written).toEqual({ ok: true, warnings: [] });
      } finally {
        await previous.close();
      }
      // Not vacuous: every table the criterion names holds a G2 row before the migration runs.
      expect(await rowCounts(raw)).toEqual({ session: 1, save: 1, profile: 1, profile_achievement: 1 });

      expect(await migrateToHead(schema.connection)).toEqual({ ok: true, value: undefined });
      expect(await rowCounts(raw)).toEqual({ session: 0, save: 0, profile: 0, profile_achievement: 0 });
    } finally {
      await raw.close();
      await schema.drop();
    }
  });
});
