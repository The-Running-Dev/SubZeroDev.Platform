/**
 * Brings a schema to head — the fresh-clone documentation's own migration command
 * (`README.md`, "Migrate the schema to head"; `20-contract.md`'s `migrateToHead`). `npm run
 * migrate`. `GAME_SERVICE_DB_SCHEMA`, when set, targets that schema instead of the default
 * (`public`) one — every other caller in this repository (tests, the proof harness) migrates a
 * schema of its own, so this is the one entry point an operator's own shell reaches.
 */
import { migrateToHead } from "../src/migrations.js";
import { RUN_SCHEMA_CONNECT_TIMEOUT_MS, RUN_SCHEMA_POOL_SIZE } from "../src/harness.js";
import type { SchemaName } from "../src/types.js";

const connectionString =
  process.env["GAME_SERVICE_DB_CONNECTION_STRING"] ?? "postgresql://game_service:game_service@127.0.0.1:5432/game_service";
const schema = process.env["GAME_SERVICE_DB_SCHEMA"];

const outcome = await migrateToHead({
  connectionString,
  poolSize: RUN_SCHEMA_POOL_SIZE,
  connectTimeoutMs: RUN_SCHEMA_CONNECT_TIMEOUT_MS,
  schema: schema ? (schema as unknown as SchemaName) : null,
});

if (!outcome.ok) {
  process.stderr.write(`migrate: ${JSON.stringify(outcome.error)}\n`);
  process.exit(1);
}

process.stdout.write(`migrated ${schema || "public"} to head\n`);
