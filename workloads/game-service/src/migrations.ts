/**
 * Migrations — the ordered schema definitions under `../migrations/`, and `migrateToHead`, the one
 * call that brings a schema to them (`20-contract.md`, "Migrations — workload").
 *
 * `node-pg-migrate` owns the advisory lock (`PG_MIGRATE_LOCK_ID`), which is why it was taken over a
 * hand-rolled runner (`design/10-design.md`, "The store — PostgreSQL over plain `pg`"): two
 * instances starting together must not both apply the same migration, and the lock that makes that
 * safe is the tool's own rather than machinery this workload reimplements.
 */
import { Client } from "pg";
import { runner } from "node-pg-migrate";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { err, ok } from "./types.js";
import type { MigrationError, Outcome, StoreConnection } from "./types.js";

const MIGRATIONS_DIR = resolve(dirname(fileURLToPath(import.meta.url)), "../migrations");
const MIGRATIONS_TABLE = "pgmigrations";

// No contract field names a bound for this — `connectTimeoutMs` bounds the TCP connect, a separate
// concern. A lock held by a concurrent migrator is expected to clear in well under a second; this
// is generous headroom over that, not a tuned limit.
const LOCK_WAIT_TIMEOUT_MS = 30_000;

/** A `SchemaName` never carries anything but an identifier the harness itself minted — this quotes
 *  it defensively rather than trusting that. */
export function quoteIdentifier(identifier: string): string {
  return `"${identifier.replace(/"/g, '""')}"`;
}

function pgErrorCode(error: unknown): string | undefined {
  const code = (error as { code?: unknown } | null)?.code;
  return typeof code === "string" ? code : undefined;
}

function isConnectionError(error: unknown): boolean {
  const code = pgErrorCode(error);
  return code === "ECONNREFUSED" || code === "ENOTFOUND" || code === "ETIMEDOUT" || code === "EHOSTUNREACH";
}

/** `pg_advisory_lock` (`advisoryLockMode: "wait"`) blocks until the lock is free, which is what
 *  lets two concurrent callers cooperate (S3.16) rather than one failing outright. `lock_timeout`,
 *  set below, is the bound `MigrationError.LockTimeout` names — a lock held far longer than any
 *  migration should take surfaces as a classified failure rather than hanging the caller forever.
 *  Postgres raises this as `55P03 lock_not_available` (statement cancelled by `lock_timeout`) or,
 *  on some server versions, `57014 query_canceled`. */
function isLockTimeout(error: unknown): boolean {
  const code = pgErrorCode(error);
  return code === "55P03" || code === "57014";
}

export async function migrateToHead(connection: StoreConnection): Promise<Outcome<void, MigrationError>> {
  const client = new Client({
    connectionString: connection.connectionString,
    connectionTimeoutMillis: connection.connectTimeoutMs,
  });

  try {
    await client.connect();
  } catch {
    return err({ code: "Unreachable" });
  }

  // `node-pg-migrate` logs `### MIGRATION <name> (UP) ###` (`Migration._apply`, via `_getMarkAsRun`)
  // immediately before issuing that migration's SQL, so the last one captured here is the one a
  // subsequent catch attributes a failure to. Scoped to this call, never module-level — S3.16 runs
  // this function concurrently, and shared mutable state would let one caller's attribution bleed
  // into another's.
  let migrationInFlight: string | null = null;
  const captureMigrationName = (message: string): void => {
    const started = /^### MIGRATION (.+) \((?:UP|DOWN)\) ###$/.exec(message);
    if (started?.[1]) migrationInFlight = started[1];
  };

  try {
    await client.query(`SET lock_timeout = '${LOCK_WAIT_TIMEOUT_MS}ms'`);

    const schema = connection.schema as unknown as string | null;
    const schemaOptions = schema === null ? {} : { schema, migrationsSchema: schema, createSchema: true };

    await runner({
      dbClient: client,
      dir: MIGRATIONS_DIR,
      migrationsTable: MIGRATIONS_TABLE,
      direction: "up",
      advisoryLockMode: "wait",
      singleTransaction: true,
      logger: {
        info: captureMigrationName,
        warn: () => {},
        error: () => {},
      },
      ...schemaOptions,
    });

    return ok(undefined);
  } catch (error) {
    if (isLockTimeout(error)) return err({ code: "LockTimeout" });
    if (isConnectionError(error)) return err({ code: "Unreachable" });
    return err({ code: "MigrationFailed", migration: migrationInFlight ?? "unknown" });
  } finally {
    await client.end().catch(() => {});
  }
}
