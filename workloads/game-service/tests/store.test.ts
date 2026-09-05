/**
 * S3 — Migrations and the guarded store. Every test here runs against a real, freshly migrated
 * PostgreSQL schema (`tests/support/database.ts`), never a mock — S3's whole point is that the
 * compare-and-swap is provable in the database itself, not merely argued.
 */
import { describe, expect, it } from "vitest";
import { SESSION_PERSISTENCE_CONFLICT } from "@the-running-dev/game-engine";
import type { StoredSaveRecord, StoredSessionRecord } from "@the-running-dev/game-engine";

import { migrateToHead } from "../src/migrations.js";
import {
  IMPLICIT_TENANT_ID,
  createReadVersionMap,
  openDurableStore,
  saveDeleteStatement,
  saveLifecycleStatement,
  saveSelectStatement,
  saveUpsertStatement,
  sessionGuardedUpdateStatement,
  sessionInsertStatement,
  sessionLifecycleStatement,
  sessionReclassifyStatement,
  sessionSelectStatement,
  sweepStatements,
  writeSessionRow,
} from "../src/store.js";
import type { DurableWriteConflict, Queryable, SessionRowInput } from "../src/store.js";
import type {
  DurableStore,
  EngineInstant,
  SchemaName,
  SemanticVersion,
  SessionRowVersion,
  TenantId,
} from "../src/types.js";
import {
  RawSchemaClient,
  TEST_CONNECTION_STRING,
  configurationFor,
  connectionFor,
  createTestSchema,
  openTestPool,
} from "./support/database.js";
import type { TestSchema } from "./support/database.js";

const ENGINE_VERSION_A = "1.0.0" as SemanticVersion;
const ENGINE_VERSION_B = "2.0.0" as SemanticVersion;

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
    savedAt: "2026-01-01T00:00:00.000Z",
    savedAtSeq: 0,
    audience: "player",
    ...overrides,
  };
}

/** Every test in this file owns one schema, created fresh and dropped after — no state leaks
 *  between tests, and no test depends on another's ordering. */
async function withSchema(run: (schema: TestSchema, store: DurableStore) => Promise<void>): Promise<void> {
  const testSchema = await createTestSchema();
  const opened = await openDurableStore(configurationFor(testSchema.schema), ENGINE_VERSION_A);
  if (!opened.ok) {
    await testSchema.drop();
    throw new Error(`openDurableStore failed: ${JSON.stringify(opened.error)}`);
  }
  try {
    await run(testSchema, opened.value);
  } finally {
    await opened.value.close();
    await testSchema.drop();
  }
}

describe("S3.1 — insert then guarded update", () => {
  it("inserts version 1, then a second put supplying the read version updates to version 2", async () => {
    await withSchema(async (schema, store) => {
      const persistence = store.persistenceForRequest();
      await persistence.sessions.put(sessionRecord("s3-1"));

      const raw = await RawSchemaClient.connect(schema.schema);
      try {
        const first = await raw.query<{ version: bigint }>("select version from session where session_id = $1", [
          "s3-1",
        ]);
        expect(first.rows[0]?.version).toBe(1n);

        await persistence.sessions.get("s3-1"); // populates the read-version map with version 1
        await persistence.sessions.put(sessionRecord("s3-1", { blob: '{"changed":true}' }));

        const second = await raw.query<{ version: bigint }>("select version from session where session_id = $1", [
          "s3-1",
        ]);
        expect(second.rows[0]?.version).toBe(2n);
      } finally {
        await raw.close();
      }
    });
  });
});

describe("S3.2 — stale read-version is classified conflict", () => {
  it("re-reads and throws SessionPersistenceConflict when the row's version has moved on", async () => {
    await withSchema(async (schema, store) => {
      const persistence = store.persistenceForRequest();
      await persistence.sessions.put(sessionRecord("s3-2"));
      await persistence.sessions.get("s3-2"); // map now holds version 1

      const raw = await RawSchemaClient.connect(schema.schema);
      try {
        // A concurrent writer, bypassing this adapter, advances the row past what this
        // adapter's read-version map still believes.
        await raw.query("update session set version = 2, row_updated_at = now() where session_id = $1", ["s3-2"]);

        let caught: unknown;
        try {
          await persistence.sessions.put(sessionRecord("s3-2", { blob: '{"loser":true}' }));
        } catch (error) {
          caught = error;
        }
        expect(caught).toBeInstanceOf(Error);
        expect((caught as Error).name).toBe(SESSION_PERSISTENCE_CONFLICT);
        expect((caught as DurableWriteConflict).outcome).toBe("conflict");
      } finally {
        await raw.close();
      }
    });
  });
});

describe("S3.3 — same version but expired is classified expired, not conflict", () => {
  it("classifies a same-version, already-expired row as expired", async () => {
    await withSchema(async (schema, store) => {
      const persistence = store.persistenceForRequest();
      await persistence.sessions.put(sessionRecord("s3-3"));
      await persistence.sessions.get("s3-3"); // map now holds version 1

      const raw = await RawSchemaClient.connect(schema.schema);
      try {
        // Version is untouched; only expires_at is seeded into the past.
        await raw.query("update session set expires_at = now() - interval '1 hour' where session_id = $1", [
          "s3-3",
        ]);

        let caught: unknown;
        try {
          await persistence.sessions.put(sessionRecord("s3-3", { blob: '{"tooLate":true}' }));
        } catch (error) {
          caught = error;
        }
        expect((caught as Error).name).toBe(SESSION_PERSISTENCE_CONFLICT);
        expect((caught as DurableWriteConflict).outcome).toBe("expired");
      } finally {
        await raw.close();
      }
    });
  });
});

describe("S3.4 — a row absent from the store is classified conflict", () => {
  it("classifies a guarded update against a since-deleted row as conflict", async () => {
    await withSchema(async (schema, store) => {
      const persistence = store.persistenceForRequest();
      await persistence.sessions.put(sessionRecord("s3-4"));
      await persistence.sessions.get("s3-4"); // map now holds version 1

      const raw = await RawSchemaClient.connect(schema.schema);
      try {
        await raw.query("delete from session where session_id = $1", ["s3-4"]);

        let caught: unknown;
        try {
          await persistence.sessions.put(sessionRecord("s3-4", { blob: '{"ghost":true}' }));
        } catch (error) {
          caught = error;
        }
        expect((caught as Error).name).toBe(SESSION_PERSISTENCE_CONFLICT);
        expect((caught as DurableWriteConflict).outcome).toBe("conflict");
      } finally {
        await raw.close();
      }
    });
  });
});

describe("S3.5 — an expired row is not found, independent of the sweep", () => {
  it("reports an expired session and save as not found without ever running a sweep", async () => {
    await withSchema(async (schema, store) => {
      const persistence = store.persistenceForRequest();
      await persistence.sessions.put(sessionRecord("s3-5-session"));
      await persistence.saves.put(saveRecord("s3-5-save"));

      const raw = await RawSchemaClient.connect(schema.schema);
      try {
        await raw.query("update session set expires_at = now() - interval '1 hour' where session_id = $1", [
          "s3-5-session",
        ]);
        await raw.query("update save set expires_at = now() - interval '1 hour' where save_id = $1", [
          "s3-5-save",
        ]);

        // A fresh persistence instance: nothing about the earlier read survives to bias this one.
        const freshPersistence = store.persistenceForRequest();
        expect(await freshPersistence.sessions.get("s3-5-session")).toBeUndefined();
        expect(await freshPersistence.saves.get("s3-5-save")).toBeUndefined();

        // The rows are still physically present — "not found" is a read-time bound, not sweep state.
        const stillThere = await raw.query("select 1 from session where session_id = $1", ["s3-5-session"]);
        expect(stillThere.rows).toHaveLength(1);
      } finally {
        await raw.close();
      }
    });
  });
});

describe("S3.6 — a re-read that itself fails is classified conflict, never a StoreError", () => {
  it("swallows the re-read's own driver error and answers conflict", async () => {
    const schema = await createTestSchema();
    const pool = openTestPool(schema.schema);
    try {
      const versions = createReadVersionMap();
      const tenantId = IMPLICIT_TENANT_ID;
      const input: SessionRowInput = {
        sessionId: "s3-6",
        blob: "{}",
        audience: "player",
        attemptCounter: 0,
        replayCompatible: true,
        engineCreatedAt: "2026-01-01T00:00:00.000Z" as EngineInstant,
        engineUpdatedAt: "2026-01-01T00:00:00.000Z" as EngineInstant,
        profileId: null,
      };

      const inserted = await writeSessionRow(pool, tenantId, ENGINE_VERSION_A, 2_592_000, 0, versions, input);
      expect(inserted.ok && inserted.value).toBe("applied");

      // A concurrent writer moves the row's version on, exactly as in S3.2 — but this time the
      // *re-read* itself is made to fail, rather than the guarded update finding a live row.
      const raw = await RawSchemaClient.connect(schema.schema);
      await raw.query("update session set version = 2 where session_id = $1", ["s3-6"]);
      await raw.close();

      const reReadFails: Queryable = {
        async query(text, values) {
          if (text.startsWith("select version, expires_at > now() as live")) {
            throw new Error("simulated connection drop mid re-read");
          }
          return pool.query(text, values as unknown[]);
        },
      };

      const outcome = await writeSessionRow(
        reReadFails,
        tenantId,
        ENGINE_VERSION_A,
        2_592_000,
        0,
        versions,
        input,
      );
      expect(outcome.ok).toBe(true);
      expect(outcome.ok && outcome.value).toBe("conflict");
    } finally {
      await pool.end();
      await schema.drop();
    }
  });
});

describe("S3.7 — blob round-trips byte for byte", () => {
  it("preserves duplicate object keys and a number requiring exact round-trip", async () => {
    await withSchema(async (_schema, store) => {
      const blob = '{"a":1,"a":2,"n":1.10,"nested":{"x":1.10,"x":2}}';
      const persistence = store.persistenceForRequest();
      await persistence.sessions.put(sessionRecord("s3-7", { blob }));

      const fresh = store.persistenceForRequest();
      const record = await fresh.sessions.get("s3-7");
      expect(record?.blob).toBe(blob);
    });
  });
});

describe("S3.8 — engine instants stored verbatim; host timestamps are timestamptz", () => {
  it("round-trips engine_created_at/engine_updated_at exactly, and stores host timestamps as timestamptz", async () => {
    await withSchema(async (schema, store) => {
      const createdAt = "2026-01-01T00:00:00.123456789Z";
      const updatedAt = "2026-06-15T09:30:00.000000001Z";
      const persistence = store.persistenceForRequest();
      await persistence.sessions.put(sessionRecord("s3-8", { createdAt, updatedAt }));

      const fresh = store.persistenceForRequest();
      const record = await fresh.sessions.get("s3-8");
      expect(record?.createdAt).toBe(createdAt);
      expect(record?.updatedAt).toBe(updatedAt);

      const raw = await RawSchemaClient.connect(schema.schema);
      try {
        const rows = await raw.query<{
          row_created_at: Date;
          row_updated_at: Date;
          expires_at: Date;
          data_type: string;
        }>(
          "select row_created_at, row_updated_at, expires_at from session where session_id = $1",
          ["s3-8"],
        );
        expect(rows.rows[0]?.row_created_at).toBeInstanceOf(Date);
        expect(rows.rows[0]?.row_updated_at).toBeInstanceOf(Date);
        expect(rows.rows[0]?.expires_at).toBeInstanceOf(Date);

        const columnTypes = await raw.query<{ column_name: string; data_type: string }>(
          "select column_name, data_type from information_schema.columns " +
            "where table_name = 'session' and column_name in ('row_created_at', 'row_updated_at', 'expires_at')",
        );
        for (const row of columnTypes.rows) {
          expect(row.data_type).toBe("timestamp with time zone");
        }
      } finally {
        await raw.close();
      }
    });
  });
});

describe("S3.9 — tenant_id in every statement", () => {
  it("carries the implicit tenant in both the SQL text and the parameter list of every statement builder", () => {
    const tenantId = "t-static" as TenantId;
    const sessionInput: SessionRowInput = {
      sessionId: "s1",
      blob: "{}",
      audience: "player",
      attemptCounter: 0,
      replayCompatible: true,
      engineCreatedAt: "x" as EngineInstant,
      engineUpdatedAt: "x" as EngineInstant,
      profileId: null,
    };
    const saveInput = {
      saveId: "sv1",
      campaignId: "c1",
      blob: "{}",
      savedAt: "x" as EngineInstant,
      savedAtSeq: 0,
      audience: "player" as const,
      profileId: null,
    };
    const sweeps = sweepStatements(tenantId, 10);
    const statements = [
      sessionSelectStatement(tenantId, "s1"),
      sessionInsertStatement(tenantId, sessionInput, ENGINE_VERSION_A, 10),
      sessionGuardedUpdateStatement(tenantId, sessionInput, ENGINE_VERSION_A, 10, 1n as SessionRowVersion),
      sessionReclassifyStatement(tenantId, "s1"),
      saveSelectStatement(tenantId, "sv1"),
      saveUpsertStatement(tenantId, saveInput, ENGINE_VERSION_A, 10),
      saveDeleteStatement(tenantId, "sv1", "x"),
      sessionLifecycleStatement(tenantId, "s1"),
      saveLifecycleStatement(tenantId, "sv1"),
      sweeps.sessions,
      sweeps.saves,
    ];
    for (const statement of statements) {
      expect(statement.text).toMatch(/tenant_id/);
      expect(statement.values).toContain(tenantId);
    }
  });

  it("stamps the implicit tenant on every row a live write produces", async () => {
    await withSchema(async (schema, store) => {
      const persistence = store.persistenceForRequest();
      await persistence.sessions.put(sessionRecord("s3-9"));
      await persistence.saves.put(saveRecord("s3-9-save"));

      const raw = await RawSchemaClient.connect(schema.schema);
      try {
        const sessionTenants = await raw.query<{ tenant_id: string }>("select distinct tenant_id from session");
        const saveTenants = await raw.query<{ tenant_id: string }>("select distinct tenant_id from save");
        expect(sessionTenants.rows.map((r) => r.tenant_id)).toEqual([IMPLICIT_TENANT_ID]);
        expect(saveTenants.rows.map((r) => r.tenant_id)).toEqual([IMPLICIT_TENANT_ID]);
      } finally {
        await raw.close();
      }
    });
  });
});

describe("S3.10 — saves.put is a recomputing upsert", () => {
  it("recomputes expires_at and engine_version on a re-put rather than carrying them over", async () => {
    const schema = await createTestSchema();
    try {
      const openedA = await openDurableStore(configurationFor(schema.schema), ENGINE_VERSION_A);
      const openedB = await openDurableStore(configurationFor(schema.schema), ENGINE_VERSION_B);
      if (!openedA.ok || !openedB.ok) throw new Error("openDurableStore failed");

      try {
        await openedA.value.persistenceForRequest().saves.put(saveRecord("s3-10"));

        const raw = await RawSchemaClient.connect(schema.schema);
        try {
          const first = await raw.query<{ engine_version: string; expires_at: Date }>(
            "select engine_version, expires_at from save where save_id = $1",
            ["s3-10"],
          );
          expect(first.rows[0]?.engine_version).toBe(ENGINE_VERSION_A);

          await new Promise((resolve) => setTimeout(resolve, 20));
          await openedB.value.persistenceForRequest().saves.put(
            saveRecord("s3-10", { campaignId: "campaign-b", savedAtSeq: 7 }),
          );

          const second = await raw.query<{ engine_version: string; expires_at: Date; campaign_id: string; saved_at_seq: number }>(
            "select engine_version, expires_at, campaign_id, saved_at_seq from save where save_id = $1",
            ["s3-10"],
          );
          expect(second.rows[0]?.engine_version).toBe(ENGINE_VERSION_B);
          expect(second.rows[0]?.campaign_id).toBe("campaign-b");
          expect(second.rows[0]?.saved_at_seq).toBe(7);
          expect(second.rows[0]!.expires_at.getTime()).toBeGreaterThan(first.rows[0]!.expires_at.getTime());
        } finally {
          await raw.close();
        }
      } finally {
        await openedA.value.close();
        await openedB.value.close();
      }
    } finally {
      await schema.drop();
    }
  });
});

describe("S3.11 — the save table has no version column", () => {
  it("carries no column named version on save", async () => {
    const schema = await createTestSchema();
    try {
      const raw = await RawSchemaClient.connect(schema.schema);
      try {
        const columns = await raw.query<{ column_name: string }>(
          "select column_name from information_schema.columns where table_name = 'save'",
        );
        expect(columns.rows.map((row) => row.column_name)).not.toContain("version");
      } finally {
        await raw.close();
      }
    } finally {
      await schema.drop();
    }
  });
});

describe("S3.12 — a primary-key collision on insert is IdCollision, never the conflict brand", () => {
  it("throws an ordinary error carrying StoreError.IdCollision as its cause", async () => {
    await withSchema(async (_schema, store) => {
      const first = store.persistenceForRequest();
      await first.sessions.put(sessionRecord("s3-12"));

      // A second, independent persistence instance — its read-version map has never seen this id,
      // so `put` takes the insert path exactly as `createSession`/`loadGame` would for a duplicate.
      const second = store.persistenceForRequest();
      let caught: unknown;
      try {
        await second.sessions.put(sessionRecord("s3-12", { blob: '{"duplicate":true}' }));
      } catch (error) {
        caught = error;
      }
      expect(caught).toBeInstanceOf(Error);
      expect((caught as Error).name).not.toBe(SESSION_PERSISTENCE_CONFLICT);
      expect(((caught as Error).cause as { code?: string } | undefined)?.code).toBe("IdCollision");
    });
  });
});

describe("S3.13 — read committed is asserted on connect", () => {
  it("refuses a connection reporting serializable and issues no other statement", async () => {
    const schema = await createTestSchema();
    try {
      const configuration = configurationFor(schema.schema, {
        connection: {
          connectionString: `${TEST_CONNECTION_STRING}?options=-c%20default_transaction_isolation%3Dserializable`,
        },
      });
      const opened = await openDurableStore(configuration, ENGINE_VERSION_A);
      expect(opened.ok).toBe(false);
      if (!opened.ok) {
        expect(opened.error).toEqual({ code: "IsolationLevelUnsupported", isolationLevel: "serializable" });
      }
    } finally {
      await schema.drop();
    }
  });
});

describe("S3.16 — migrateToHead is idempotent and safe under concurrent callers", () => {
  it("is a no-op the second time run in sequence", async () => {
    const schema = await createTestSchema();
    try {
      const second = await migrateToHead(schema.connection);
      expect(second.ok).toBe(true);
    } finally {
      await schema.drop();
    }
  });

  it("lets two concurrent callers share one fresh schema, with no partial table left by either", async () => {
    const suffix = Math.random().toString(36).slice(2, 10);
    const schemaName = `s3_16_concurrent_${process.pid}_${suffix}` as unknown as SchemaName;
    const connection = connectionFor(schemaName);

    try {
      const [first, second] = await Promise.all([migrateToHead(connection), migrateToHead(connection)]);
      expect(first.ok).toBe(true);
      expect(second.ok).toBe(true);

      const raw = await RawSchemaClient.connect(schemaName);
      try {
        const tables = await raw.query<{ table_name: string }>(
          "select table_name from information_schema.tables where table_schema = current_schema() and table_type = 'BASE TABLE'",
        );
        const names = tables.rows.map((row) => row.table_name).sort();
        expect(names).toEqual(["pgmigrations", "profile", "profile_achievement", "save", "session"].sort());

        const migrationRows = await raw.query("select * from pgmigrations");
        expect(migrationRows.rows).toHaveLength(2);
      } finally {
        await raw.close();
      }
    } finally {
      const raw = await RawSchemaClient.connect(schemaName);
      await raw.query(`drop schema if exists "${schemaName}" cascade`);
      await raw.close();
    }
  });
});

describe("S3.17 — the version column round-trips as bigint", () => {
  it("ReadVersionMap.record stores a bigint", () => {
    const map = createReadVersionMap();
    map.record("s1", 42n as SessionRowVersion);
    expect(typeof map.observed("s1")).toBe("bigint");
  });

  it("the driver returns session.version as bigint, not a numeric-looking string", async () => {
    await withSchema(async (schema, store) => {
      const persistence = store.persistenceForRequest();
      await persistence.sessions.put(sessionRecord("s3-17"));

      const raw = await RawSchemaClient.connect(schema.schema);
      try {
        const rows = await raw.query<{ version: bigint }>("select version from session where session_id = $1", [
          "s3-17",
        ]);
        expect(typeof rows.rows[0]?.version).toBe("bigint");
      } finally {
        await raw.close();
      }
    });
  });
});
