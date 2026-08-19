/**
 * S12.3–S12.7 — `compose()`'s durable branch brings a schema to head itself, before the first
 * connect, under the startup backoff — the gap `design/90-decisions.md`'s S12 entry named:
 * "nothing runs migrations at startup". Every test here runs against a real, freshly provisioned
 * (but deliberately *un*migrated) PostgreSQL schema, the same discipline `compose.test.ts` holds
 * to for the already-migrated case.
 */
import { describe, expect, it } from "vitest";
import { randomBytes } from "node:crypto";
import { createServer, connect as netConnect } from "node:net";
import type { AddressInfo, Socket } from "node:net";

import { compose, migrationNotReadyDetail } from "../src/compose.js";
import { startWorkload } from "../src/lifecycle.js";
import { dropSchemaByName, quoteIdentifier } from "../src/migrations.js";
import { contract } from "./support/harness.js";
import { CAMPAIGN_ID } from "./support/real-workload.js";
import { connectionFor, defaultBounds, RawSchemaClient, TEST_CONNECTION_STRING } from "./support/database.js";
import { DEFAULT_STORE_CONNECT_TIMEOUT_MS } from "../src/types.js";
import type { DurableStoreConfiguration, SchemaName, StorageProfile, WorkloadConfiguration } from "../src/types.js";

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

/** Unlike `tests/support/database.ts`'s own `freshSchemaName`, this never calls `migrateToHead` —
 *  every test in this file needs a schema `compose()` itself has never seen. */
function freshUnmigratedSchemaName(): SchemaName {
  return `s12_unmigrated_${process.pid}_${randomBytes(4).toString("hex")}` as unknown as SchemaName;
}

async function drop(schema: SchemaName): Promise<void> {
  await dropSchemaByName(TEST_CONNECTION_STRING, schema as unknown as string, DEFAULT_STORE_CONNECT_TIMEOUT_MS);
}

function durableConfigFor(schema: SchemaName, overrides: Partial<DurableStoreConfiguration> = {}): DurableStoreConfiguration {
  return {
    connection: connectionFor(schema),
    bounds: defaultBounds(),
    readWritePauseMs: 0,
    ...overrides,
  };
}

/** A raw TCP relay in front of the real PostgreSQL server — the same technique
 *  `compose.test.ts` uses for S4.4/S4.9, needed again here so S12.5 can flip reachability after
 *  the workload has already bound its listener. */
async function createPostgresProxy(): Promise<{
  readonly port: number;
  block(): void;
  unblock(): void;
  stop(): Promise<void>;
}> {
  const target = new URL(TEST_CONNECTION_STRING);
  const targetPort = Number(target.port || "5432");
  const targetHost = target.hostname;

  let blocked = true;
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

describe("S12.3 — a never-migrated schema is brought to head before the first connect", () => {
  it("reaches ready on the very first attempt and serves — no separate migration command is run first", async () => {
    const schema = freshUnmigratedSchemaName();
    const composed = await compose(
      baseConfiguration({ kind: "durable", store: durableConfigFor(schema) }),
      contract,
    );
    expect(composed.ok).toBe(true);
    if (!composed.ok) return;

    try {
      expect((await composed.value.readiness()).status).toBe("healthy");

      const raw = await RawSchemaClient.connect(schema);
      try {
        const migrations = await raw.query("select name from pgmigrations");
        expect(migrations.rows.length).toBeGreaterThan(0);
      } finally {
        await raw.close();
      }
    } finally {
      await composed.value.close();
      await drop(schema);
    }
  });
});

describe("S12.4 — two composed instances starting concurrently against one never-migrated schema both reach ready", () => {
  it("one applies, the other waits on the advisory lock, and no partial table is left", async () => {
    const schema = freshUnmigratedSchemaName();
    const config = durableConfigFor(schema);

    const [first, second] = await Promise.all([
      compose(baseConfiguration({ kind: "durable", store: config }), contract),
      compose(baseConfiguration({ kind: "durable", store: config }), contract),
    ]);
    expect(first.ok).toBe(true);
    expect(second.ok).toBe(true);
    if (!first.ok || !second.ok) return;

    try {
      expect((await first.value.readiness()).status).toBe("healthy");
      expect((await second.value.readiness()).status).toBe("healthy");

      const raw = await RawSchemaClient.connect(schema);
      try {
        const tables = await raw.query<{ table_name: string }>(
          "select table_name from information_schema.tables where table_schema = current_schema() and table_type = 'BASE TABLE'",
        );
        expect(tables.rows.map((row) => row.table_name).sort()).toEqual(
          ["pgmigrations", "profile", "profile_achievement", "save", "session"].sort(),
        );

        const migrationRows = await raw.query("select * from pgmigrations");
        expect(migrationRows.rows).toHaveLength(1);
      } finally {
        await raw.close();
      }
    } finally {
      await first.value.close();
      await second.value.close();
      await drop(schema);
    }
  });
});

describe("S12.5 — unreachable at startup: binds and reports live, not ready, then ready without a restart once reachable", () => {
  it(
    "the migration run and the first connect both retry under the startup backoff, and compose() never throws",
    { timeout: 20_000 },
    async () => {
      const schema = freshUnmigratedSchemaName();
      const proxy = await createPostgresProxy();

      const started = await startWorkload(
        baseConfiguration({
          kind: "durable",
          store: durableConfigFor(schema, {
            connection: { ...connectionFor(schema), connectionString: proxiedConnectionString(proxy.port), connectTimeoutMs: 500 },
          }),
        }),
      );
      expect(started.ok).toBe(true);
      if (!started.ok) {
        await proxy.stop();
        return;
      }

      try {
        expect(started.value.probes.liveness().status).toBe("healthy");
        expect((await started.value.probes.readiness()).status).toBe("unhealthy");

        proxy.unblock();
        // Past `DURABLE_RECONNECT_INTERVAL_MS` (5s) so at least one retry has landed.
        await sleep(7000);

        expect((await started.value.probes.readiness()).status).toBe("healthy");
      } finally {
        await started.value.shutdown();
        await proxy.stop();
        await drop(schema);
      }
    },
  );
});

describe("S12.6 — a migration whose SQL fails leaves the process up and not ready, naming the migration", () => {
  it("readiness reports unhealthy naming the failing migration, and serves nothing", async () => {
    const schema = freshUnmigratedSchemaName();

    // Pre-create a conflicting `session` table so the initial migration's own `createTable`
    // fails with "relation already exists" — a deterministic `MigrationError.MigrationFailed`
    // without waiting on any real bound.
    const seed = await RawSchemaClient.connect(schema);
    try {
      await seed.query(`create schema if not exists ${quoteIdentifier(schema as unknown as string)}`);
      await seed.query(`create table ${quoteIdentifier(schema as unknown as string)}.session (id int)`);
    } finally {
      await seed.close();
    }

    const composed = await compose(
      baseConfiguration({ kind: "durable", store: durableConfigFor(schema) }),
      contract,
    );
    expect(composed.ok).toBe(true);
    if (!composed.ok) return;

    try {
      const readiness = await composed.value.readiness();
      expect(readiness.status).toBe("unhealthy");
      expect((readiness as { detail?: string }).detail ?? "").toContain("migration failed");
      expect((readiness as { detail?: string }).detail ?? "").toContain("initial-schema");

      await expect(composed.value.stores.forRequest().createSession({ campaignId: CAMPAIGN_ID })).rejects.toThrow();
    } finally {
      await composed.value.close();
      await drop(schema);
    }
  });
});

describe("S12.7 — the readiness detail names a lock timeout, distinctly from an unreachable migration runner or a failed migration", () => {
  it("migrationNotReadyDetail maps each MigrationError variant to a distinguishable detail string", () => {
    expect(migrationNotReadyDetail({ code: "LockTimeout" })).toContain("lock timeout");
    expect(migrationNotReadyDetail({ code: "MigrationFailed", migration: "1787054400000_initial-schema" })).toBe(
      "migration failed: 1787054400000_initial-schema",
    );
    expect(migrationNotReadyDetail({ code: "Unreachable" })).not.toContain("lock timeout");
    expect(migrationNotReadyDetail({ code: "Unreachable" })).not.toContain("migration failed");

    // Reproducing the real 30-second advisory-lock bound end to end was tried and dropped
    // (`design/90-decisions.md`, S12): `node-pg-migrate`'s lock is one fixed id for the whole
    // database, not scoped per schema, so holding it that long blocked every other file's own
    // `migrateToHead` calls running concurrently under vitest's default file parallelism and
    // produced exactly the collateral `LockTimeout`s and readiness timeouts this note warns
    // against. S12.5's and S12.6's compose-level tests already prove the surrounding retry and
    // detail-surfacing machinery against the same code path this mapping feeds; what remains
    // untested end to end is `migrations.ts`'s own `isLockTimeout` classification of Postgres's
    // `55P03`/`57014`, which is unit-level, driver-facing logic outside this slice's `Touches`.
  });
});
