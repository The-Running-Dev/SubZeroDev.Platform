/**
 * S6 — Contention, one instance. Two players' simultaneous actions against one session, dispatched
 * concurrently within this one test process against the real durable store, stop silently
 * overwriting each other: exactly one succeeds, and the other is told to re-read and decide.
 *
 * `readWritePauseMs` is what makes the race deterministic (`20-contract.md`, "Workload —
 * configuration") — it pauses the store adapter between a session read and the corresponding
 * write, so both concurrent requests are guaranteed to have read before either writes, rather than
 * merely likely to have.
 */
import { describe, expect, it } from "vitest";
import { ENGINE_VERSION } from "@the-running-dev/game-engine";

import { compose } from "../src/compose.js";
import { createDispatcher } from "../src/dispatch.js";
import { buildHttpSurface } from "../src/http-surface.js";
import {
  IMPLICIT_TENANT_ID,
  createReadVersionMap,
  openDurableStore,
  sessionInsertStatement,
  writeSessionRow,
} from "../src/store.js";
import type { Queryable } from "../src/store.js";
import type { EngineInstant, HttpSurface, SemanticVersion, SessionRowVersion, StorageProfile, TenantId, WorkloadConfiguration, WireResponse } from "../src/types.js";
import { contract, bodyJson, post } from "./support/harness.js";
import { CAMPAIGN_ID } from "./support/real-workload.js";
import {
  RawSchemaClient,
  configurationFor,
  createTestSchema,
  defaultBounds,
  openTestPool,
} from "./support/database.js";
import type { TestSchema } from "./support/database.js";

const ENGINE_VERSION_UNDER_TEST = ENGINE_VERSION as unknown as SemanticVersion;

function baseConfiguration(storage: StorageProfile): WorkloadConfiguration {
  return {
    listen: { host: "127.0.0.1", port: 0 },
    determinism: { kind: "default" },
    otlpEndpoint: null,
    storage,
  };
}

/** The same construction `compose()`'s durable branch performs, minus everything this file does
 *  not need — one dispatcher's `HttpSurface` over a real, freshly composed durable workload. */
async function durableSurface(
  schema: TestSchema,
  readWritePauseMs: number,
): Promise<{ surface: HttpSurface; close(): Promise<void> }> {
  const configuration = baseConfiguration({
    kind: "durable",
    store: configurationFor(schema.schema, { readWritePauseMs }),
  });
  const composed = await compose(configuration, contract);
  if (!composed.ok) {
    throw new Error(`compose() failed: ${JSON.stringify(composed.error)}`);
  }
  const built = buildHttpSurface(contract, createDispatcher(contract, composed.value.stores, composed.value.lifecycle));
  if (!built.ok) {
    throw new Error(`buildHttpSurface failed: ${JSON.stringify(built.error)}`);
  }
  return { surface: built.value, close: () => composed.value.close() };
}

describe("S6.1, S6.6 — two concurrent submissions resolve to one winner, and the loser leaves no trace", () => {
  it("produces exactly one 200 and one 409 carrying concurrent_modification, and the winner's state shows only its own action", async () => {
    const schema = await createTestSchema();
    const { surface, close } = await durableSurface(schema, 300);
    try {
      const created = await post(surface, "/v1/create-session", { campaignId: CAMPAIGN_ID });
      expect(created.status).toBe(200);
      const { sessionId } = bodyJson(created) as unknown as { sessionId: string };

      // Two distinguishable actions on the same session, fired together: `readWritePauseMs` above
      // guarantees both requests' reads land before either request's write is attempted, so which
      // one wins is a race the database settles, not an accident of scheduling in this process.
      const [ticksA, ticksB] = [1, 5];
      const [responseA, responseB] = await Promise.all([
        post(surface, "/v1/submit-action", { sessionId, actionId: "advance_ticks", params: { ticks: ticksA } }),
        post(surface, "/v1/submit-action", { sessionId, actionId: "advance_ticks", params: { ticks: ticksB } }),
      ]);

      const statuses = [responseA.status, responseB.status].sort();
      expect(statuses).toEqual([200, 409]);

      const winner = responseA.status === 200 ? { response: responseA, ticks: ticksA } : { response: responseB, ticks: ticksB };
      const loser = responseA.status === 200 ? responseB : responseA;

      expect(loser.status).toBe(409);
      expect(bodyJson(loser)["code"]).toBe("concurrent_modification");

      // Inspected directly rather than inferred from the status codes: the session's own state,
      // read back fresh, must show only the winner's ticks — never the loser's, and never both
      // summed, which is what a partially-applied write would look like.
      const queried = await post(surface, "/v1/get-scene", { sessionId });
      expect(queried.status).toBe(200);
      const scene = bodyJson(queried) as unknown as { body: { text: string } };
      expect(scene.body.text).toContain(`Tick ${winner.ticks} `);
      expect(scene.body.text).not.toContain(`Tick ${loser === responseA ? ticksA : ticksB} `);
    } finally {
      await close();
      await schema.drop();
    }
  });
});

describe("S6.2 — readWritePauseMs at its default of 0 is inert", () => {
  it("inserts no observable delay between a session read and its write", async () => {
    const schema = await createTestSchema();
    const opened = await openDurableStore(configurationFor(schema.schema, { readWritePauseMs: 0 }), ENGINE_VERSION_UNDER_TEST);
    if (!opened.ok) throw new Error(`openDurableStore failed: ${JSON.stringify(opened.error)}`);
    try {
      const persistence = opened.value.persistenceForRequest();
      await persistence.sessions.put({
        sessionId: "s6-2",
        blob: "{}",
        audience: "player",
        attemptCounter: 0,
        replayCompatible: true,
        createdAt: "2026-01-01T00:00:00.000Z" as unknown as string,
        updatedAt: "2026-01-01T00:00:00.000Z" as unknown as string,
      });
      await persistence.sessions.get("s6-2"); // records the read version the next put guards on

      const startedAt = performance.now();
      await persistence.sessions.put({
        sessionId: "s6-2",
        blob: '{"updated":true}',
        audience: "player",
        attemptCounter: 0,
        replayCompatible: true,
        createdAt: "2026-01-01T00:00:00.000Z" as unknown as string,
        updatedAt: "2026-01-01T00:00:00.000Z" as unknown as string,
      });
      const elapsedAtDefault = performance.now() - startedAt;

      // The same read/write pair, this time with a configured, non-zero pause — establishing that
      // this measurement method is sensitive to the seam at all before trusting it reported "0" as
      // meaningfully different from "absent".
      const pausedOpened = await openDurableStore(
        configurationFor(schema.schema, { readWritePauseMs: 200 }),
        ENGINE_VERSION_UNDER_TEST,
      );
      if (!pausedOpened.ok) throw new Error(`openDurableStore failed: ${JSON.stringify(pausedOpened.error)}`);
      try {
        const pausedPersistence = pausedOpened.value.persistenceForRequest();
        await pausedPersistence.sessions.get("s6-2");
        const pausedStartedAt = performance.now();
        await pausedPersistence.sessions.put({
          sessionId: "s6-2",
          blob: '{"updatedAgain":true}',
          audience: "player",
          attemptCounter: 0,
          replayCompatible: true,
          createdAt: "2026-01-01T00:00:00.000Z" as unknown as string,
          updatedAt: "2026-01-01T00:00:00.000Z" as unknown as string,
        });
        const elapsedWithPause = performance.now() - pausedStartedAt;

        expect(elapsedAtDefault).toBeLessThan(100);
        expect(elapsedWithPause).toBeGreaterThanOrEqual(200);
      } finally {
        await pausedOpened.value.close();
      }
    } finally {
      await opened.value.close();
      await schema.drop();
    }
  });
});

describe("S6.3 — perturbation: with the version predicate removed, both concurrent writes apply", () => {
  it("proves the S6.1 assertion can fail: two writers starting from the same observed version both succeed", async () => {
    const schema = await createTestSchema();
    const realPool = openTestPool(schema.schema);
    try {
      // The guarded update's own text, with only its version predicate stripped — everything else
      // about the statement, including the tenant and expiry predicates, is untouched. This is the
      // one substitution the design names as the gate's own red-run: "the update's `where` clause
      // not asserting the version" (`design/10-design.md`, *Control flow* 3).
      const unguardedPool: Queryable = {
        query: async <T extends Record<string, unknown> = Record<string, unknown>>(
          text: string,
          values?: readonly unknown[],
        ): Promise<{ rows: T[]; rowCount: number | null }> => {
          // The bind protocol rejects a value with no corresponding placeholder, so dropping the
          // predicate's `$11` from the text means dropping its trailing bind value too.
          const guarded = text.includes(" and version = $11 and expires_at > now()");
          const rewritten = guarded ? text.replace(" and version = $11 and expires_at > now()", " and expires_at > now()") : text;
          const forwarded = guarded && values ? values.slice(0, -1) : values;
          const result = await realPool.query(rewritten, forwarded as unknown[] | undefined);
          return { rows: result.rows as T[], rowCount: result.rowCount };
        },
      };

      const tenantId = IMPLICIT_TENANT_ID as unknown as TenantId;
      const insert = sessionInsertStatement(
        tenantId,
        {
          sessionId: "s6-3",
          blob: "{}",
          audience: "player",
          attemptCounter: 0,
          replayCompatible: true,
          engineCreatedAt: "2026-01-01T00:00:00.000Z" as EngineInstant,
          engineUpdatedAt: "2026-01-01T00:00:00.000Z" as EngineInstant,
          profileId: null,
        },
        ENGINE_VERSION_UNDER_TEST,
        2_592_000,
      );
      await realPool.query(insert.text, insert.values as unknown[]);

      // Two independent read-version maps, each believing it observed version 1 — exactly the
      // state two genuinely concurrent requests would each hold after their own read.
      const versionsA = createReadVersionMap();
      versionsA.record("s6-3", 1n as SessionRowVersion);
      const versionsB = createReadVersionMap();
      versionsB.record("s6-3", 1n as SessionRowVersion);

      const rowFor = (blob: string) => ({
        sessionId: "s6-3",
        blob,
        audience: "player" as const,
        attemptCounter: 0,
        replayCompatible: true,
        engineCreatedAt: "2026-01-01T00:00:00.000Z" as EngineInstant,
        engineUpdatedAt: "2026-01-01T00:00:00.000Z" as EngineInstant,
        profileId: null,
      });

      const [outcomeA, outcomeB] = await Promise.all([
        writeSessionRow(unguardedPool, tenantId, ENGINE_VERSION_UNDER_TEST, 2_592_000, 150, versionsA, rowFor('{"a":true}')),
        writeSessionRow(unguardedPool, tenantId, ENGINE_VERSION_UNDER_TEST, 2_592_000, 150, versionsB, rowFor('{"b":true}')),
      ]);

      expect(outcomeA).toEqual({ ok: true, value: "applied" });
      expect(outcomeB).toEqual({ ok: true, value: "applied" });
    } finally {
      await realPool.end();
      await schema.drop();
    }
  });
});

describe("S6.4 — a direct adapter call with a stale read-version is rejected as a conflict", () => {
  it("throws the conflict brand when a concurrent writer has already advanced the row", async () => {
    const schema = await createTestSchema();
    const opened = await openDurableStore(configurationFor(schema.schema), ENGINE_VERSION_UNDER_TEST);
    if (!opened.ok) throw new Error(`openDurableStore failed: ${JSON.stringify(opened.error)}`);
    try {
      const persistence = opened.value.persistenceForRequest();
      await persistence.sessions.put({
        sessionId: "s6-4",
        blob: "{}",
        audience: "player",
        attemptCounter: 0,
        replayCompatible: true,
        createdAt: "2026-01-01T00:00:00.000Z" as unknown as string,
        updatedAt: "2026-01-01T00:00:00.000Z" as unknown as string,
      });
      await persistence.sessions.get("s6-4"); // this adapter now believes version 1

      const raw = await RawSchemaClient.connect(schema.schema);
      try {
        await raw.query("update session set version = 2, row_updated_at = now() where session_id = $1", ["s6-4"]);

        let caught: unknown;
        try {
          await persistence.sessions.put({
            sessionId: "s6-4",
            blob: '{"loser":true}',
            audience: "player",
            attemptCounter: 0,
            replayCompatible: true,
            createdAt: "2026-01-01T00:00:00.000Z" as unknown as string,
            updatedAt: "2026-01-01T00:00:00.000Z" as unknown as string,
          });
        } catch (error) {
          caught = error;
        }

        expect(caught).toBeInstanceOf(Error);
        expect((caught as Error).name).toBe("SessionPersistenceConflict");
      } finally {
        await raw.close();
      }
    } finally {
      await opened.value.close();
      await schema.drop();
    }
  });
});

describe("S6.5 — perturbation: the same scenario against an unreachable store answers 503, never 409", () => {
  it("resolves both concurrent submissions to storage_failure at 503", async () => {
    const configuration = baseConfiguration({
      kind: "durable",
      store: {
        connection: {
          connectionString: "postgresql://game_service:game_service@127.0.0.1:1/game_service",
          poolSize: 1,
          connectTimeoutMs: 300,
          schema: null,
        },
        bounds: defaultBounds(),
        readWritePauseMs: 0,
      },
    });
    const composed = await compose(configuration, contract);
    if (!composed.ok) throw new Error(`compose() failed: ${JSON.stringify(composed.error)}`);
    try {
      const built = buildHttpSurface(contract, createDispatcher(contract, composed.value.stores, composed.value.lifecycle));
      if (!built.ok) throw new Error(`buildHttpSurface failed: ${JSON.stringify(built.error)}`);
      const surface = built.value;

      const [responseA, responseB]: [WireResponse, WireResponse] = await Promise.all([
        post(surface, "/v1/submit-action", { sessionId: "s6-5", actionId: "advance_ticks", params: { ticks: 1 } }),
        post(surface, "/v1/submit-action", { sessionId: "s6-5", actionId: "advance_ticks", params: { ticks: 1 } }),
      ]);

      for (const response of [responseA, responseB]) {
        expect(response.status).toBe(503);
        expect(bodyJson(response)["code"]).toBe("storage_failure");
        expect(response.status).not.toBe(409);
      }
    } finally {
      await composed.value.close();
    }
  });
});
