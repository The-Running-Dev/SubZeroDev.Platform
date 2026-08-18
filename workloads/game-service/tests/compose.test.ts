/**
 * S4 — Composition: per-request, and the sweep. Every durable-facing test here runs against a
 * real, freshly migrated PostgreSQL schema (`tests/support/database.ts`), the same discipline S3's
 * own suite holds to — this slice's whole point is that "per request" and "the sweep reclaims what
 * it should and nothing else" are provable against the database itself, not merely argued.
 */
import { describe, expect, it } from "vitest";
import { createServer, connect as netConnect } from "node:net";
import type { AddressInfo, Socket } from "node:net";

import { compose } from "../src/compose.js";
import { createProbeSurface } from "../src/lifecycle.js";
import { contract } from "./support/harness.js";
import { CAMPAIGN_ID } from "./support/real-workload.js";
import {
  TEST_CONNECTION_STRING,
  configurationFor,
  createTestSchema,
  defaultBounds,
  RawSchemaClient,
} from "./support/database.js";
import type { DurableStoreConfiguration, StorageProfile, WorkloadConfiguration } from "../src/types.js";

function baseConfiguration(storage: StorageProfile): WorkloadConfiguration {
  return {
    listen: { host: "127.0.0.1", port: 0 },
    determinism: { kind: "default" },
    otlpEndpoint: null,
    storage,
  };
}

function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

/** A raw TCP relay in front of the real PostgreSQL server, so a test can simulate the store going
 *  unreachable mid-life (S4.4, S4.9) without touching the shared container other tests depend on.
 *  `block()` refuses new connections and severs every one already open; `stop()` does the same and
 *  releases the listener. */
async function createPostgresProxy(): Promise<{
  readonly port: number;
  block(): void;
  unblock(): void;
  stop(): Promise<void>;
}> {
  const target = new URL(TEST_CONNECTION_STRING);
  const targetPort = Number(target.port || "5432");
  const targetHost = target.hostname;

  let blocked = false;
  const sockets = new Set<Socket>();
  const server = createServer((client) => {
    if (blocked) {
      client.destroy();
      return;
    }
    const upstream = netConnect(targetPort, targetHost);
    sockets.add(client);
    sockets.add(upstream);
    client.pipe(upstream);
    upstream.pipe(client);
    const cleanup = () => {
      sockets.delete(client);
      sockets.delete(upstream);
    };
    client.on("close", cleanup);
    upstream.on("close", cleanup);
    client.on("error", () => upstream.destroy());
    upstream.on("error", () => client.destroy());
  });

  await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", resolve));

  function severAll(): void {
    for (const socket of sockets) socket.destroy();
  }

  return {
    get port() {
      return (server.address() as AddressInfo).port;
    },
    block() {
      blocked = true;
      severAll();
    },
    unblock() {
      blocked = false;
    },
    async stop() {
      blocked = true;
      severAll();
      await new Promise<void>((resolve) => server.close(() => resolve()));
    },
  };
}

function proxiedConnectionString(proxyPort: number): string {
  const url = new URL(TEST_CONNECTION_STRING);
  url.hostname = "127.0.0.1";
  url.port = String(proxyPort);
  return url.toString();
}

describe("S4.1 — durable forRequest() shares no cache across calls", () => {
  it("returns two distinct instances, and a write through the first is visible to the second once it reads", async () => {
    const schema = await createTestSchema();
    const composed = await compose(
      baseConfiguration({ kind: "durable", store: configurationFor(schema.schema) }),
      contract,
    );
    expect(composed.ok).toBe(true);
    if (!composed.ok) return;

    try {
      const storeA = composed.value.stores.forRequest();
      const storeB = composed.value.stores.forRequest();
      expect(storeA).not.toBe(storeB);

      const handle = await storeA.createSession({ campaignId: CAMPAIGN_ID });
      const scene = await storeB.resumeSession(handle.sessionId);
      expect(scene).toBeTruthy();
    } finally {
      await composed.value.close();
      await schema.drop();
    }
  });
});

describe("S4.2 — in-memory forRequest() returns G1's same long-lived layer", () => {
  it("returns identical instances, and a write on one is visible to the other with no store access", async () => {
    const composed = await compose(baseConfiguration({ kind: "in-memory" }), contract);
    expect(composed.ok).toBe(true);
    if (!composed.ok) return;

    try {
      const storeA = composed.value.stores.forRequest();
      const storeB = composed.value.stores.forRequest();
      expect(storeA).toBe(storeB);

      const handle = await storeA.createSession({ campaignId: CAMPAIGN_ID });
      const scene = await storeB.resumeSession(handle.sessionId);
      expect(scene).toBeTruthy();
    } finally {
      await composed.value.close();
    }
  });
});

describe("S4.3 — compose() against an unreachable durable store", () => {
  it("returns successfully; readiness reports unhealthy; compose() never throws", async () => {
    const configuration: DurableStoreConfiguration = {
      connection: {
        connectionString: "postgresql://game_service:game_service@127.0.0.1:1/game_service",
        poolSize: 1,
        connectTimeoutMs: 300,
        schema: null,
      },
      bounds: defaultBounds(),
      readWritePauseMs: 0,
    };

    const composed = await compose(baseConfiguration({ kind: "durable", store: configuration }), contract);
    expect(composed.ok).toBe(true);
    if (!composed.ok) return;

    try {
      const readiness = await composed.value.readiness();
      expect(readiness.status).toBe("unhealthy");
    } finally {
      await composed.value.close();
    }
  });
});

describe("S4.4 — readiness evaluates the store on every call", () => {
  it("reports healthy then unhealthy once the store is made unreachable in between", async () => {
    const schema = await createTestSchema();
    const proxy = await createPostgresProxy();
    const configuration = configurationFor(schema.schema, {
      connection: { connectionString: proxiedConnectionString(proxy.port), connectTimeoutMs: 2000 },
    });

    const composed = await compose(baseConfiguration({ kind: "durable", store: configuration }), contract);
    expect(composed.ok).toBe(true);
    if (!composed.ok) {
      await proxy.stop();
      await schema.drop();
      return;
    }

    try {
      expect((await composed.value.readiness()).status).toBe("healthy");

      proxy.block();
      expect((await composed.value.readiness()).status).toBe("unhealthy");
    } finally {
      await composed.value.close();
      await proxy.stop();
      await schema.drop();
    }
  });
});

describe("S4.5 — liveness never calls into the store", () => {
  it("reports healthy without ever invoking the readiness thunk", () => {
    const probes = createProbeSurface(async () => {
      throw new Error("readiness thunk must not be invoked by liveness()");
    });
    probes.markSurfacesBuilt();
    probes.markListening();

    expect(probes.surface.liveness().status).toBe("healthy");
  });
});

describe("S4.6 — an invalid retention horizon fails before any connection is attempted", () => {
  it("returns StorageConfigurationInvalid naming the setting, immediately", async () => {
    const configuration: DurableStoreConfiguration = {
      connection: {
        connectionString: "postgresql://game_service:game_service@127.0.0.1:1/game_service",
        poolSize: 1,
        connectTimeoutMs: 5000,
        schema: null,
      },
      bounds: defaultBounds({ retentionHorizonSeconds: 10 }),
      readWritePauseMs: 0,
    };

    const startedAt = Date.now();
    const composed = await compose(baseConfiguration({ kind: "durable", store: configuration }), contract);
    const elapsedMs = Date.now() - startedAt;

    expect(composed.ok).toBe(false);
    if (composed.ok) {
      await composed.value.close();
      return;
    }
    expect(composed.error).toEqual({
      code: "StorageConfigurationInvalid",
      setting: "storage.store.bounds.retentionHorizonSeconds",
    });
    // No connection was attempted — an unreachable address with a 5s timeout would otherwise make
    // this slow rather than immediate.
    expect(elapsedMs).toBeLessThan(1000);
  });
});

describe("S4.7 — the sweep reclaims past-horizon rows and retains merely-expired ones", () => {
  it("removes a row past the retention horizon and keeps one merely expired within it", async () => {
    const schema = await createTestSchema();
    const composed = await compose(
      baseConfiguration({
        kind: "durable",
        store: configurationFor(schema.schema, {
          bounds: defaultBounds({ retentionHorizonSeconds: 61, sweepIntervalSeconds: 1 }),
        }),
      }),
      contract,
    );
    expect(composed.ok).toBe(true);
    if (!composed.ok) return;

    try {
      const store = composed.value.stores.forRequest();
      const pastHorizon = await store.createSession({ campaignId: CAMPAIGN_ID });
      const merelyExpired = await store.createSession({ campaignId: CAMPAIGN_ID });

      const raw = await RawSchemaClient.connect(schema.schema);
      try {
        await raw.query("update session set expires_at = now() - interval '2 days' where session_id = $1", [
          pastHorizon.sessionId,
        ]);
        await raw.query("update session set expires_at = now() - interval '5 seconds' where session_id = $1", [
          merelyExpired.sessionId,
        ]);
      } finally {
        await raw.close();
      }

      // Two sweep intervals (1s each), plus headroom for the tick itself.
      await sleep(2400);

      const after = await RawSchemaClient.connect(schema.schema);
      try {
        const gone = await after.query("select 1 from session where session_id = $1", [pastHorizon.sessionId]);
        const kept = await after.query("select 1 from session where session_id = $1", [merelyExpired.sessionId]);
        expect(gone.rows.length).toBe(0);
        expect(kept.rows.length).toBe(1);
      } finally {
        await after.close();
      }
    } finally {
      await composed.value.close();
      await schema.drop();
    }
  });
});

describe("S4.8 — the sweep never removes a profile or profile_achievement row", () => {
  it("leaves both untouched even seeded far in the past", async () => {
    const schema = await createTestSchema();
    const composed = await compose(
      baseConfiguration({
        kind: "durable",
        store: configurationFor(schema.schema, { bounds: defaultBounds({ retentionHorizonSeconds: 61, sweepIntervalSeconds: 1 }) }),
      }),
      contract,
    );
    expect(composed.ok).toBe(true);
    if (!composed.ok) return;

    try {
      const raw = await RawSchemaClient.connect(schema.schema);
      try {
        await raw.query(
          "insert into profile (tenant_id, profile_id, format_version, row_created_at, row_updated_at) " +
            "values ('implicit-tenant', $1, 1, now() - interval '400 days', now() - interval '400 days')",
          ["s4-8-profile"],
        );
        await raw.query(
          "insert into profile_achievement (tenant_id, profile_id, campaign_id, achievement_id, row_created_at) " +
            "values ('implicit-tenant', $1, $2, $3, now() - interval '400 days')",
          ["s4-8-profile", "campaign-a", "achievement-a"],
        );
      } finally {
        await raw.close();
      }

      await sleep(2400);

      const after = await RawSchemaClient.connect(schema.schema);
      try {
        const profile = await after.query("select 1 from profile where profile_id = $1", ["s4-8-profile"]);
        const achievement = await after.query("select 1 from profile_achievement where profile_id = $1", [
          "s4-8-profile",
        ]);
        expect(profile.rows.length).toBe(1);
        expect(achievement.rows.length).toBe(1);
      } finally {
        await after.close();
      }
    } finally {
      await composed.value.close();
      await schema.drop();
    }
  });
});

describe("S4.9 — a failing sweep tick is caught and logged; the next tick still runs", () => {
  it("recovers on the tick after the store becomes reachable again, with no unhandled rejection", async () => {
    const schema = await createTestSchema();
    const proxy = await createPostgresProxy();
    const composed = await compose(
      baseConfiguration({
        kind: "durable",
        store: configurationFor(schema.schema, {
          connection: { connectionString: proxiedConnectionString(proxy.port), connectTimeoutMs: 2000 },
          bounds: defaultBounds({ retentionHorizonSeconds: 61, sweepIntervalSeconds: 1 }),
        }),
      }),
      contract,
    );
    expect(composed.ok).toBe(true);
    if (!composed.ok) {
      await proxy.stop();
      await schema.drop();
      return;
    }

    const unhandled: unknown[] = [];
    const onUnhandled = (reason: unknown) => unhandled.push(reason);
    process.on("unhandledRejection", onUnhandled);

    try {
      const store = composed.value.stores.forRequest();
      const pastHorizon = await store.createSession({ campaignId: CAMPAIGN_ID });
      const raw = await RawSchemaClient.connect(schema.schema);
      try {
        await raw.query("update session set expires_at = now() - interval '2 days' where session_id = $1", [
          pastHorizon.sessionId,
        ]);
      } finally {
        await raw.close();
      }

      // Break the store for the first tick or two, then restore it — the row must still be gone
      // once a tick lands with the store reachable again, and nothing above must have thrown.
      proxy.block();
      await sleep(1300);
      proxy.unblock();
      await sleep(1300);

      const after = await RawSchemaClient.connect(schema.schema);
      try {
        const gone = await after.query("select 1 from session where session_id = $1", [pastHorizon.sessionId]);
        expect(gone.rows.length).toBe(0);
      } finally {
        await after.close();
      }
      expect(unhandled).toEqual([]);
    } finally {
      process.off("unhandledRejection", onUnhandled);
      await composed.value.close();
      await proxy.stop();
      await schema.drop();
    }
  });
});

describe("S4.10 — a slowed tick blocks the next one from starting concurrently", () => {
  it("never runs two sweep ticks' session-deletes at once", { timeout: 15_000 }, async () => {
    const schema = await createTestSchema();
    const raw = await RawSchemaClient.connect(schema.schema);
    // A statement-level guard on `session`'s own delete: the first delete to land holds a global
    // advisory lock across a deliberate `pg_sleep`; any delete that lands while it is held records
    // the overlap rather than failing, so the evidence survives even though S4.9's own contract
    // means a failed sweep tick is swallowed silently.
    await raw.query("create table sweep_probe (run_count integer not null default 0, overlap_detected boolean not null default false)");
    await raw.query("insert into sweep_probe default values");
    await raw.query(`
      create function sweep_probe_guard() returns trigger as $$
      begin
        update sweep_probe set run_count = run_count + 1;
        if not pg_try_advisory_lock(987654321) then
          update sweep_probe set overlap_detected = true;
        else
          perform pg_sleep(1.2);
          perform pg_advisory_unlock(987654321);
        end if;
        return null;
      end;
      $$ language plpgsql
    `);
    await raw.query(
      "create trigger sweep_probe_guard_trigger before delete on session for each statement execute function sweep_probe_guard()",
    );
    await raw.close();

    const composed = await compose(
      baseConfiguration({
        kind: "durable",
        store: configurationFor(schema.schema, { bounds: defaultBounds({ retentionHorizonSeconds: 61, sweepIntervalSeconds: 1 }) }),
      }),
      contract,
    );
    expect(composed.ok).toBe(true);
    if (!composed.ok) return;

    try {
      // Every tick's `delete from session` fires the guard even with nothing to delete — a
      // statement-level trigger runs once per statement, whether or not it matches a row.
      await sleep(5600);

      const after = await RawSchemaClient.connect(schema.schema);
      try {
        const probe = await after.query<{ run_count: number; overlap_detected: boolean }>(
          "select run_count, overlap_detected from sweep_probe",
        );
        expect(probe.rows[0]?.run_count ?? 0).toBeGreaterThanOrEqual(2);
        expect(probe.rows[0]?.overlap_detected).toBe(false);
      } finally {
        await after.close();
      }
    } finally {
      await composed.value.close();
      await schema.drop();
    }
  });
});

describe("S4.11 — close() stops the sweep timer and closes the pool", () => {
  it("leaves no timer running: a row seeded past the horizon after close() is never swept", async () => {
    const schema = await createTestSchema();
    const composed = await compose(
      baseConfiguration({
        kind: "durable",
        store: configurationFor(schema.schema, { bounds: defaultBounds({ retentionHorizonSeconds: 61, sweepIntervalSeconds: 1 }) }),
      }),
      contract,
    );
    expect(composed.ok).toBe(true);
    if (!composed.ok) return;

    const store = composed.value.stores.forRequest();
    const handle = await store.createSession({ campaignId: CAMPAIGN_ID });

    await composed.value.close();

    const raw = await RawSchemaClient.connect(schema.schema);
    try {
      await raw.query("update session set expires_at = now() - interval '2 days' where session_id = $1", [
        handle.sessionId,
      ]);

      await sleep(2400);

      const remaining = await raw.query("select 1 from session where session_id = $1", [handle.sessionId]);
      expect(remaining.rows.length).toBe(1);
    } finally {
      await raw.close();
      await schema.drop();
    }
  });
});
