/**
 * S3 test support. Every S3 test needs a real, freshly migrated PostgreSQL schema — no mock, no
 * in-memory stand-in, per this slice's own "an operator can point the workload at a real
 * PostgreSQL database" (`design/30-slices.md`, S3). `docker-compose.yml` under the workload root
 * provisions the server this connects to; nothing here starts one.
 *
 * Each test gets its own schema, created here rather than through `createRunSchema`
 * (`20-contract.md`'s proof-harness function, `../../src/harness.ts`) — this suite names and scopes
 * its schemas independently of the proof harness's own run-scoped naming. Teardown shares
 * `createRunSchema`'s own `dropSchemaByName` (`../../src/migrations.ts`) rather than a second
 * hand-rolled copy.
 */
import { Client, Pool } from "pg";
import { randomBytes } from "node:crypto";
import { RUN_SCHEMA_CONNECT_TIMEOUT_MS, RUN_SCHEMA_POOL_SIZE } from "../../src/harness.js";
import { dropSchemaByName, migrateToHead, quoteIdentifier } from "../../src/migrations.js";
import { BIGINT_VERSION_TYPES } from "../../src/store.js";
import { DEFAULT_LIFECYCLE_BOUNDS } from "../../src/types.js";
import type { DurableStoreConfiguration, LifecycleBounds, SchemaName, StoreConnection } from "../../src/types.js";

export const TEST_CONNECTION_STRING =
  process.env.GAME_SERVICE_TEST_DATABASE_URL ?? "postgresql://game_service:game_service@127.0.0.1:5432/game_service";

export function defaultBounds(overrides: Partial<LifecycleBounds> = {}): LifecycleBounds {
  return {
    ...DEFAULT_LIFECYCLE_BOUNDS,
    ...overrides,
  };
}

export function connectionFor(schema: SchemaName, overrides: Partial<StoreConnection> = {}): StoreConnection {
  return {
    connectionString: TEST_CONNECTION_STRING,
    poolSize: RUN_SCHEMA_POOL_SIZE,
    connectTimeoutMs: RUN_SCHEMA_CONNECT_TIMEOUT_MS,
    schema,
    ...overrides,
  };
}

export function configurationFor(
  schema: SchemaName,
  overrides: Partial<Omit<DurableStoreConfiguration, "connection">> & { readonly connection?: Partial<StoreConnection> } = {},
): DurableStoreConfiguration {
  const { connection, bounds, readWritePauseMs, ...rest } = overrides;
  void rest;
  return {
    connection: connectionFor(schema, connection),
    bounds: defaultBounds(bounds),
    readWritePauseMs: readWritePauseMs ?? 0,
  };
}

/** A raw `pg.Pool` scoped to the schema's `search_path`, for tests that call `writeSessionRow`
 *  directly rather than through `openDurableStore` — S3.6's fault injection needs a pool it can
 *  wrap, which `DurableStore` keeps private. */
export function openTestPool(schema: SchemaName): Pool {
  return new Pool({
    connectionString: TEST_CONNECTION_STRING,
    options: `-c search_path=${quoteIdentifier(schema as unknown as string)},public`,
    types: BIGINT_VERSION_TYPES,
  });
}

export interface TestSchema {
  readonly schema: SchemaName;
  readonly connection: StoreConnection;
  drop(): Promise<void>;
}

let counter = 0;

/** A schema name unique to this process and this call — collision-safe under vitest's own
 *  parallelism without needing a lock of its own. */
function freshSchemaName(): SchemaName {
  counter += 1;
  const suffix = randomBytes(4).toString("hex");
  return `s3_test_${process.pid}_${counter}_${suffix}` as unknown as SchemaName;
}

/** Creates a fresh schema, migrates it to head, and returns a `StoreConnection` scoped to it.
 *  Throws if migration fails — every S3 test needs a schema at head to mean anything, so there is
 *  no useful partial state for a caller to inspect. */
export async function createTestSchema(): Promise<TestSchema> {
  const schema = freshSchemaName();
  const connection = connectionFor(schema);

  const migrated = await migrateToHead(connection);
  if (!migrated.ok) {
    throw new Error(`migrateToHead failed for test schema ${String(schema)}: ${JSON.stringify(migrated.error)}`);
  }

  return {
    schema,
    connection,
    async drop(): Promise<void> {
      await dropSchemaByName(TEST_CONNECTION_STRING, schema as unknown as string, RUN_SCHEMA_CONNECT_TIMEOUT_MS);
    },
  };
}

/** Direct, store-bypassing access to the schema under test — for seeding a race the adapter itself
 *  cannot produce (an artificially stale version, a row whose `expires_at` has already passed) and
 *  for inspecting column-level state the ports never expose (S3.7, S3.8, S3.11). */
export class RawSchemaClient {
  private readonly client: Client;

  private constructor(client: Client) {
    this.client = client;
  }

  static async connect(schema: SchemaName): Promise<RawSchemaClient> {
    const client = new Client({
      connectionString: TEST_CONNECTION_STRING,
      options: `-c search_path=${quoteIdentifier(schema as unknown as string)},public`,
      types: BIGINT_VERSION_TYPES,
    });
    await client.connect();
    return new RawSchemaClient(client);
  }

  async query<T extends Record<string, unknown> = Record<string, unknown>>(
    text: string,
    values: readonly unknown[] = [],
  ): Promise<{ rows: T[]; rowCount: number | null }> {
    const result = await this.client.query(text, values as unknown[]);
    return { rows: result.rows as T[], rowCount: result.rowCount };
  }

  async close(): Promise<void> {
    await this.client.end();
  }
}
