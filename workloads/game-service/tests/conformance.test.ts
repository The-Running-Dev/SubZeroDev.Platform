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

import { inMemoryConformanceTarget, openDurableConformanceTarget, runPortConformance, verifyCallerProfileWrites } from "../src/conformance.js";
import type { DurableConformanceTarget } from "../src/conformance.js";
import type { ConformanceTarget, SchemaName, SemanticVersion } from "../src/types.js";
import { configurationFor, createTestSchema } from "./support/database.js";
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
  });

  // -------------------------------------------------------------------------- S9.9: SeamUnavailable

  describe("S9.9 — a target that cannot honour a seed method", () => {
    function unseedableTarget(): ConformanceTarget {
      const real = inMemoryConformanceTarget();
      return {
        ...real,
        async seedCorruptProfile(): Promise<void> {
          throw new Error("this target cannot seed a corrupt profile");
        },
        async seedProfileWriteFailure(): Promise<void> {
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
