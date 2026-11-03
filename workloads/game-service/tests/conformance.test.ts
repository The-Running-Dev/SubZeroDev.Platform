/**
 * S9 — Port conformance, both implementations. `runPortConformance` runs the identical assertion
 * set against the in-memory reference target and the durable one; both must come back `ok: true`
 * (S9.1–S9.6, S9.8, and S9.7's non-firing path). S9.7's firing path and S9.9 are failure paths —
 * proven by deliberately broken fixtures, since neither real target ever violates them: a real
 * target's `profiles`/seed methods behave correctly, so there is nothing for either check to
 * catch there.
 */
import { afterAll, describe, expect, it } from "vitest";
import type { AchievementRecord } from "@the-running-dev/game-engine";

import {
  CONFORMANCE_OWNERS,
  inMemoryConformanceTarget,
  openDurableConformanceTarget,
  runPortConformance,
  verifyCallerProfileWrites,
} from "../src/conformance.js";
import type { DurableConformanceTarget } from "../src/conformance.js";
import type { ConformanceTarget, OwnerKey, OwnerScopedStorage, SchemaName, SemanticVersion } from "../src/types.js";
import { configurationFor, createTestSchema, openTestPool } from "./support/database.js";
import type { TestSchema } from "./support/database.js";

const ENGINE_VERSION = "1.0.0" as SemanticVersion;

describe("S9 — port conformance", () => {
  describe("the in-memory reference target", () => {
    it("S9.1–S9.8: passes the whole conformance suite", async () => {
      const target = inMemoryConformanceTarget();
      expect(target.label).toBe("in-memory");
      const result = await runPortConformance(target);
      expect(result).toEqual({ ok: true, value: undefined });
    });
  });

  describe("the durable target", () => {
    let schema: TestSchema;
    let target: DurableConformanceTarget;

    afterAll(async () => {
      await target?.close();
      await schema?.drop();
    });

    it("S9.1–S9.8: passes the whole conformance suite", async () => {
      schema = await createTestSchema();
      const opened = await openDurableConformanceTarget(configurationFor(schema.schema), ENGINE_VERSION);
      if (!opened.ok) {
        throw new Error(`openDurableConformanceTarget failed: ${JSON.stringify(opened.error)}`);
      }
      target = opened.value;
      expect(target.label).toBe("durable");
      const result = await runPortConformance(target);
      expect(result).toEqual({ ok: true, value: undefined });
    });

    it("S4.4: the run left rows under both owners, read back with SQL, and under no other", async () => {
      const pool = openTestPool(schema.schema);
      try {
        const expected = CONFORMANCE_OWNERS.map((owner) => `${owner.issuer}/${owner.subject}`).sort();
        for (const table of ["session", "save", "profile"]) {
          const rows = await pool.query(`select distinct owner_issuer, owner_subject from ${table}`);
          const seen = rows.rows.map((row: { owner_issuer: string; owner_subject: string }) => `${row.owner_issuer}/${row.owner_subject}`).sort();
          expect(seen, table).toEqual(expected);
        }
      } finally {
        await pool.end();
      }
    });
  });

  // -------------------------------------------------------------------------- S4.4: two owners

  describe("S4.4 — port conformance under two owners", () => {
    it("names two distinct owners", () => {
      expect(CONFORMANCE_OWNERS).toHaveLength(2);
      expect(CONFORMANCE_OWNERS[0]).not.toEqual(CONFORMANCE_OWNERS[1]);
    });

    it("the in-memory target answers each owner only its own rows, through the ports and the lifecycle probe", async () => {
      const target = inMemoryConformanceTarget();
      expect(await runPortConformance(target)).toEqual({ ok: true, value: undefined });
      const [a, b] = CONFORMANCE_OWNERS;
      const probe = async (owner: OwnerKey, other: OwnerKey): Promise<void> => {
        const id = `s4-4-after-${owner.subject}`;
        await target.storage.persistence(owner).sessions.put({
          sessionId: id,
          blob: "{}",
          audience: "player",
          attemptCounter: 1,
          replayCompatible: true,
          createdAt: "2026-01-01T00:00:00.000Z",
          updatedAt: "2026-01-01T00:00:00.000Z",
          revision: 0,
        });
        expect(await target.storage.persistence(other).sessions.get(id)).toBeUndefined();
        expect(await target.storage.lifecycle.session(owner, id)).toEqual({ ok: true, value: "live" });
        expect(await target.storage.lifecycle.session(other, id)).toEqual({ ok: true, value: "absent" });
      };
      await probe(a, b);
      await probe(b, a);
    });

    // One mutant per port method: each answers its own owner correctly, so every single-owner
    // assertion still passes, and leaks across owners in exactly one place. Each must fail the suite.
    type Leak =
      | "sessions.get"
      | "sessions.put"
      | "saves.get"
      | "saves.put"
      | "saves.listByProfile"
      | "saves.delete"
      | "profiles.load"
      | "profiles.save";

    function leaking(leak: Leak): ConformanceTarget {
      const real = inMemoryConformanceTarget();
      const peerOf = (owner: OwnerKey): OwnerKey =>
        owner.subject === CONFORMANCE_OWNERS[0].subject ? CONFORMANCE_OWNERS[1] : CONFORMANCE_OWNERS[0];
      const storage: OwnerScopedStorage = {
        lifecycle: real.storage.lifecycle,
        persistence(owner) {
          const own = real.storage.persistence(owner);
          const peer = real.storage.persistence(peerOf(owner));
          return {
            sessions: {
              get: async (id) => (leak === "sessions.get" ? ((await own.sessions.get(id)) ?? (await peer.sessions.get(id))) : own.sessions.get(id)),
              put: async (record) => {
                await own.sessions.put(record);
                if (leak === "sessions.put") await peer.sessions.put(record);
              },
            },
            saves: {
              get: async (id) => (leak === "saves.get" ? ((await own.saves.get(id)) ?? (await peer.saves.get(id))) : own.saves.get(id)),
              put: async (record) => {
                await own.saves.put(record);
                if (leak === "saves.put") await peer.saves.put(record);
              },
              listByProfile: async (profileId) =>
                leak === "saves.listByProfile"
                  ? [...(await own.saves.listByProfile(profileId)), ...(await peer.saves.listByProfile(profileId))]
                  : own.saves.listByProfile(profileId),
              delete: async (id, expectedSavedAt) => {
                await own.saves.delete(id, expectedSavedAt);
                if (leak === "saves.delete") await peer.saves.delete(id, expectedSavedAt);
              },
            },
          };
        },
        profiles(owner) {
          const own = real.storage.profiles(owner);
          const peer = real.storage.profiles(peerOf(owner));
          return {
            load: async (profileId) => {
              const loaded = await own.load(profileId);
              return leak === "profiles.load" && loaded.warnings.some((warning) => warning.code === "profile_missing")
                ? peer.load(profileId)
                : loaded;
            },
            save: async (profile) => {
              const result = await own.save(profile);
              if (leak === "profiles.save") await peer.save(profile);
              return result;
            },
          };
        },
      };
      return { ...real, storage };
    }

    const LEAKS: readonly Leak[] = [
      "sessions.get",
      "sessions.put",
      "saves.get",
      "saves.put",
      "saves.listByProfile",
      "saves.delete",
      "profiles.load",
      "profiles.save",
    ];

    it.each(LEAKS)("fails the suite when %s lets one owner see the other's rows", async (leak) => {
      const result = await runPortConformance(leaking(leak));
      expect(result.ok, `a target leaking through ${leak} passed the suite`).toBe(false);
      if (result.ok) return;
      expect(result.error.code).toBe("MethodDiverged");
    });
  });

  // -------------------------------------------------------------------------- S9.9: SeamUnavailable

  describe("S9.9 — a target that cannot honour a seed method", () => {
    function unseedableTarget(): ConformanceTarget {
      const real = inMemoryConformanceTarget();
      return {
        ...real,
        async seedCorruptProfile(_owner: OwnerKey, _profileId: string): Promise<void> {
          throw new Error("this target cannot seed a corrupt profile");
        },
        async seedProfileWriteFailure(_owner: OwnerKey, _profileId: string): Promise<void> {
          throw new Error("this target cannot seed a profile write failure");
        },
      };
    }

    it("fails the suite with SeamUnavailable naming seedCorruptProfile, rather than silently skipping the assertion", async () => {
      const result = await runPortConformance(unseedableTarget());
      expect(result.ok).toBe(false);
      if (result.ok) return;
      expect(result.error).toEqual({ code: "SeamUnavailable", method: "seedCorruptProfile" });
    });

    it("openDurableConformanceTarget itself reports SeamUnavailable when it cannot even connect to establish the seams", async () => {
      // An unreachable database is the simplest way to make target construction fail before
      // either seed method exists at all — `openDurableStore` itself is what surfaces first in
      // that case, and this builder folds that into the same `SeamUnavailable` vocabulary rather
      // than a bare `StoreError` the contract doesn't name for this function.
      let unreachableCounter = 0;
      unreachableCounter += 1;
      const schemaName = `s9_unreachable_${process.pid}_${unreachableCounter}` as unknown as SchemaName;
      const badConfiguration = configurationFor(schemaName, {
        connection: { connectionString: "postgresql://game_service:game_service@127.0.0.1:59999/game_service", connectTimeoutMs: 200 },
      });
      const result = await openDurableConformanceTarget(badConfiguration, ENGINE_VERSION);
      expect(result.ok).toBe(false);
      if (result.ok) return;
      expect(result.error.code).toBe("SeamUnavailable");
    });
  });

  // -------------------------------------------------------------------------- S9.7: CallerPropertyViolated

  describe("S9.7 — the engine caller property", () => {
    it("verifyCallerProfileWrites passes a save that carries the loaded set plus an addition", () => {
      const loaded: readonly AchievementRecord[] = [
        { campaignId: "conformance-fixture-campaign", achievementId: "first-flag" },
      ];
      const saved: readonly AchievementRecord[] = [
        ...loaded,
        { campaignId: "conformance-fixture-campaign", achievementId: "second-flag" },
      ];
      const result = verifyCallerProfileWrites([{ profileId: "p1", loaded, saved }]);
      expect(result).toEqual({ ok: true, value: undefined });
    });

    it("verifyCallerProfileWrites raises CallerPropertyViolated, naming the method and the observed payload, when a save is observed carrying less than the loaded set plus additions", () => {
      const loaded: readonly AchievementRecord[] = [
        { campaignId: "conformance-fixture-campaign", achievementId: "first-flag" },
      ];
      // A deliberately short save: the loaded achievement it should still carry is missing
      // entirely — the shape a broken caller (or a regression in the vendored engine's own
      // `upsertAchievements`) would produce. `runPortConformance`'s S9.7 step feeds this exact
      // function real `{loaded, saved}` pairs recorded off the actual engine; this drives it
      // directly to prove the failure path fires, since the real engine cannot itself be
      // provoked into violating it (`src/conformance.ts`'s `verifyCallerProfileWrites` doc
      // comment explains why).
      const shortSave: readonly AchievementRecord[] = [];
      const result = verifyCallerProfileWrites([{ profileId: "p1", loaded, saved: shortSave }]);
      expect(result.ok).toBe(false);
      if (result.ok) return;
      expect(result.error).toEqual({
        code: "CallerPropertyViolated",
        method: "profiles.save",
        observed: expect.stringContaining("first-flag") as unknown as string,
      });
    });

    it("the real engine's own upsertAchievements call sequence never violates it, driven through the engine against both targets", async () => {
      // `runPortConformance`'s own S9.7 step (`checkEngineCallerProperty` in `src/conformance.ts`)
      // drives a two-achievement story-graph campaign through a real session layer and calls
      // this exact function against what it recorded — already covered by the two
      // "passes the whole conformance suite" tests above returning `ok: true`. Restated here as
      // its own named expectation, against a freshly built in-memory target, so a future change
      // that broke only this step would fail a test whose name says so.
      const result = await runPortConformance(inMemoryConformanceTarget());
      expect(result).toEqual({ ok: true, value: undefined });
    });
  });
});
