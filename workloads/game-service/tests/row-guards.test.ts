/**
 * S13 — the guards the durable slices declared and never enforced (`design/30-slices.md`, S13).
 * Row-shape validation (`StoreError.RowUndeserializable`), the sweep's statement timeout, and the
 * dependency-direction / conformance / composition-seam pieces live in their own files
 * (`dependency-direction.test.ts`, `conformance.test.ts`, `storage-seam.test.ts`) — this file
 * covers S13.1–S13.5, the ones that need a real, freshly migrated schema.
 */
import { describe, expect, it } from "vitest";
import type { StoredSaveRecord, StoredSessionRecord } from "@the-running-dev/game-engine";

import { configurationFor, createTestSchema, defaultBounds, RawSchemaClient } from "./support/database.js";
import { openDurableStore } from "../src/store.js";
import type { SemanticVersion } from "../src/types.js";

const ENGINE_VERSION = "1.0.0" as SemanticVersion;

function sessionRecord(id: string, overrides: Partial<StoredSessionRecord> = {}): StoredSessionRecord {
  return {
    sessionId: id,
    blob: "{}",
    audience: "player",
    attemptCounter: 0,
    replayCompatible: true,
    createdAt: "2026-01-01T00:00:00.000Z",
    updatedAt: "2026-01-01T00:00:00.000Z",
    ...overrides,
  };
}

function saveRecord(id: string, overrides: Partial<StoredSaveRecord> = {}): StoredSaveRecord {
  return {
    saveId: id,
    campaignId: "campaign-a",
    blob: "{}",
    savedAtSeq: 0,
    audience: "player",
    ...overrides,
  };
}

describe("S13.1 — a session row whose column cannot satisfy its declared type fails RowUndeserializable", () => {
  it("names the widened column, and never reclassifies as conflict (S13.2's session half)", async () => {
    const schema = await createTestSchema();
    const opened = await openDurableStore(configurationFor(schema.schema), ENGINE_VERSION);
    if (!opened.ok) throw new Error(`openDurableStore failed: ${JSON.stringify(opened.error)}`);
    const store = opened.value;
    try {
      const persistence = store.persistenceForRequest();
      await persistence.sessions.put(sessionRecord("s13-1"));

      const raw = await RawSchemaClient.connect(schema.schema);
      try {
        await raw.query("alter table session alter column attempt_counter type text");
        await raw.query("update session set attempt_counter = 'not-a-number' where session_id = $1", ["s13-1"]);
      } finally {
        await raw.close();
      }

      let caught: unknown;
      try {
        await store.persistenceForRequest().sessions.get("s13-1");
      } catch (error) {
        caught = error;
      }
      expect(caught).toBeInstanceOf(Error);
      expect(((caught as Error).cause as { code?: string; column?: string } | undefined)?.code).toBe(
        "RowUndeserializable",
      );
      expect(((caught as Error).cause as { code?: string; column?: string } | undefined)?.column).toBe(
        "attempt_counter",
      );
    } finally {
      await store.close();
      await schema.drop();
    }
  });
});

describe("S13.2 — the same seeding against a save row is classified the same way", () => {
  it("names the widened column on saves.get, and never affects the guarded session write path", async () => {
    const schema = await createTestSchema();
    const opened = await openDurableStore(configurationFor(schema.schema), ENGINE_VERSION);
    if (!opened.ok) throw new Error(`openDurableStore failed: ${JSON.stringify(opened.error)}`);
    const store = opened.value;
    try {
      const persistence = store.persistenceForRequest();
      await persistence.saves.put(saveRecord("s13-2"));

      const raw = await RawSchemaClient.connect(schema.schema);
      try {
        await raw.query("alter table save alter column saved_at_seq type text");
        await raw.query("update save set saved_at_seq = 'not-a-number' where save_id = $1", ["s13-2"]);
      } finally {
        await raw.close();
      }

      let caught: unknown;
      try {
        await store.persistenceForRequest().saves.get("s13-2");
      } catch (error) {
        caught = error;
      }
      expect(caught).toBeInstanceOf(Error);
      expect(((caught as Error).cause as { code?: string; column?: string } | undefined)?.code).toBe(
        "RowUndeserializable",
      );
      expect(((caught as Error).cause as { code?: string; column?: string } | undefined)?.column).toBe(
        "saved_at_seq",
      );

      // The guarded write path is unaffected: a re-read that lands on a corrupted-but-unrelated
      // session row still classifies zero-rows-affected as `conflict`/`expired` from
      // `version`/`expires_at` alone, never as `RowUndeserializable` (S13.2's own claim).
      await persistence.sessions.put(sessionRecord("s13-2-session"));
      const staleWrite = await store.persistenceForRequest().sessions.get("s13-2-session");
      expect(staleWrite).toBeDefined();
    } finally {
      await store.close();
      await schema.drop();
    }
  });

  // The save checker covers every column `saveSelectStatement` returns, not only the six
  // `toStoredSaveRecord` maps. `row_created_at` is the cleanest witness for that: it is a host
  // column that reaches no record and sits in no predicate, so before the widening it was selected
  // and then trusted. (`tenant_id` and `expires_at` cannot witness it — the statement's own
  // predicates fail the query first, which is why their checks are a backstop rather than a
  // reachable branch.)
  it("names a host column that no StoredSaveRecord field is mapped from", async () => {
    const schema = await createTestSchema();
    const opened = await openDurableStore(configurationFor(schema.schema), ENGINE_VERSION);
    if (!opened.ok) throw new Error(`openDurableStore failed: ${JSON.stringify(opened.error)}`);
    const store = opened.value;
    try {
      await store.persistenceForRequest().saves.put(saveRecord("s13-2-host"));

      const raw = await RawSchemaClient.connect(schema.schema);
      try {
        await raw.query("alter table save alter column row_created_at type text");
      } finally {
        await raw.close();
      }

      let caught: unknown;
      try {
        await store.persistenceForRequest().saves.get("s13-2-host");
      } catch (error) {
        caught = error;
      }
      expect(caught).toBeInstanceOf(Error);
      const cause = (caught as Error).cause as { code?: string; column?: string } | undefined;
      expect(cause?.code).toBe("RowUndeserializable");
      expect(cause?.column).toBe("row_created_at");
    } finally {
      await store.close();
      await schema.drop();
    }
  });
});

describe("S13.3 — the same seeding against profile/profile_achievement rows yields profile_corrupt, not RowUndeserializable", () => {
  it("reports profile_corrupt with an empty achievement set on a 200-shaped result, distinguished from S13.1/S13.2", async () => {
    const schema = await createTestSchema();
    const opened = await openDurableStore(configurationFor(schema.schema), ENGINE_VERSION);
    if (!opened.ok) throw new Error(`openDurableStore failed: ${JSON.stringify(opened.error)}`);
    const store = opened.value;
    try {
      const profileId = "s13-3-profile";
      await store.profiles.save({ formatVersion: 1, profileId, achievements: [{ campaignId: "c", achievementId: "a" }] });

      const raw = await RawSchemaClient.connect(schema.schema);
      try {
        await raw.query("alter table profile_achievement alter column achievement_id type integer using 0");
      } finally {
        await raw.close();
      }

      const loaded = await store.profiles.load(profileId);
      expect(loaded.warnings.some((warning) => warning.code === "profile_corrupt")).toBe(true);
      expect(loaded.profile.achievements).toHaveLength(0);
    } finally {
      await store.close();
      await schema.drop();
    }
  });
});

describe("S13.4/S13.5 — the sweep runs under its configured statement timeout", () => {
  it("fails with StatementFailed, releases its connection, and a serving request afterward still succeeds, on a pool sized to one", async () => {
    const schema = await createTestSchema();
    const configuration = configurationFor(schema.schema, {
      connection: { poolSize: 1 },
      bounds: defaultBounds({ sweepStatementTimeoutMs: 100, retentionHorizonSeconds: 0 }),
    });
    const opened = await openDurableStore(configuration, ENGINE_VERSION);
    if (!opened.ok) throw new Error(`openDurableStore failed: ${JSON.stringify(opened.error)}`);
    const store = opened.value;
    const blocker = await RawSchemaClient.connect(schema.schema);
    try {
      // Seeded already past the retention horizon (`expires_at` in the past), directly — not via
      // `sessions.put`, whose `expires_at` is always in the future — so the sweep's own delete
      // predicate matches this row and actually attempts to lock it.
      await blocker.query(
        "insert into session (tenant_id, session_id, blob, audience, attempt_counter, replay_compatible, engine_created_at, engine_updated_at, version, engine_version, row_updated_at, expires_at) " +
          "values ('implicit-tenant', 's13-4', '{}', 'player', 0, true, '2026-01-01T00:00:00.000Z', '2026-01-01T00:00:00.000Z', 1, '1.0.0', now(), now() - interval '1 day')",
      );

      // Lock the row on a second connection past the sweep's own bound, so its delete blocks and
      // times out — the sweep's own timeout is what fails it, not a driver-level connect timeout.
      await blocker.query("begin");
      await blocker.query("select * from session where session_id = $1 for update", ["s13-4"]);

      const result = await store.sweepOnce();
      expect(result.ok).toBe(false);
      if (!result.ok) {
        expect(result.error.code).toBe("StatementFailed");
        // The failing *statement*, not only its class. This log line is the whole of the sweep's
        // observability — it sits on neither the serving path nor the readiness check — so a bare
        // "StatementFailed" would not say which of the two deletes failed (`10-design.md`, "The
        // sweep fails").
        if (result.error.code === "StatementFailed") {
          expect(result.error.statement).toContain("delete from session");
        }
      }

      // With the pool sized to one, a serving request afterward could not succeed if the timed-out
      // sweep still held the only connection — proving the release happened.
      const stillServes = await store.check();
      expect(stillServes.ok).toBe(true);
    } finally {
      await blocker.query("rollback").catch(() => {});
      await blocker.close();
      await store.close();
      await schema.drop();
    }
  });

  it("names the save delete, distinctly from the session delete, when that is the statement that failed", async () => {
    const schema = await createTestSchema();
    const configuration = configurationFor(schema.schema, {
      connection: { poolSize: 1 },
      bounds: defaultBounds({ sweepStatementTimeoutMs: 100, retentionHorizonSeconds: 0 }),
    });
    const opened = await openDurableStore(configuration, ENGINE_VERSION);
    if (!opened.ok) throw new Error(`openDurableStore failed: ${JSON.stringify(opened.error)}`);
    const store = opened.value;
    const blocker = await RawSchemaClient.connect(schema.schema);
    try {
      // No session row at all, so the sweep's first delete matches nothing and completes; the
      // locked save row is what its second delete blocks on. Two runs, one label each, is what
      // makes the label load-bearing rather than decorative.
      await blocker.query(
        "insert into save (tenant_id, save_id, campaign_id, blob, saved_at_seq, audience, engine_version, expires_at) " +
          "values ('implicit-tenant', 's-sweep-label', 'c', '{}', 1, 'player', '1.0.0', now() - interval '1 day')",
      );
      await blocker.query("begin");
      await blocker.query("select * from save where save_id = $1 for update", ["s-sweep-label"]);

      const result = await store.sweepOnce();
      expect(result.ok).toBe(false);
      if (!result.ok && result.error.code === "StatementFailed") {
        expect(result.error.statement).toContain("delete from save");
      }
    } finally {
      await blocker.query("rollback").catch(() => {});
      await blocker.close();
      await store.close();
      await schema.drop();
    }
  });

  it("succeeds when the timeout is configured generously over the same locked row, and fails when it is not", async () => {
    const schema = await createTestSchema();
    const configuration = configurationFor(schema.schema, {
      connection: { poolSize: 1 },
      bounds: defaultBounds({ sweepStatementTimeoutMs: 5000, retentionHorizonSeconds: 0 }),
    });
    const opened = await openDurableStore(configuration, ENGINE_VERSION);
    if (!opened.ok) throw new Error(`openDurableStore failed: ${JSON.stringify(opened.error)}`);
    const store = opened.value;
    try {
      const raw = await RawSchemaClient.connect(schema.schema);
      try {
        await raw.query(
          "insert into session (tenant_id, session_id, blob, audience, attempt_counter, replay_compatible, engine_created_at, engine_updated_at, version, engine_version, row_updated_at, expires_at) " +
            "values ('implicit-tenant', 's13-5', '{}', 'player', 0, true, '2026-01-01T00:00:00.000Z', '2026-01-01T00:00:00.000Z', 1, '1.0.0', now(), now() - interval '1 day')",
        );
      } finally {
        await raw.close();
      }

      const result = await store.sweepOnce();
      expect(result.ok).toBe(true);
      if (result.ok) expect(result.value.sessionsRemoved).toBe(1);
    } finally {
      await store.close();
      await schema.drop();
    }
  });
});
