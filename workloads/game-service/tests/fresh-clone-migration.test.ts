/**
 * S11.3 — the documented migration command (`README.md`, "Migrate the schema to head"; `npm run
 * migrate`) brings a freshly provisioned schema to the same head any other caller in this
 * repository reaches, compared by the migrations table's applied set. Run as a real child
 * process, never by calling `migrateToHead` directly — S11.2's whole point is that CI executes
 * the documented command itself, not a private stand-in for it.
 */
import { describe, expect, it } from "vitest";
import { execFile } from "node:child_process";
import { promisify } from "node:util";
import { fileURLToPath } from "node:url";
import { randomBytes } from "node:crypto";

import { createTestSchema, RawSchemaClient, TEST_CONNECTION_STRING } from "./support/database.js";
import { dropSchemaByName } from "../src/migrations.js";
import type { SchemaName } from "../src/types.js";

const execFileAsync = promisify(execFile);

const TSX_CLI = fileURLToPath(new URL("../node_modules/tsx/dist/cli.mjs", import.meta.url));
const MIGRATE_SCRIPT = fileURLToPath(new URL("../scripts/migrate.ts", import.meta.url));
const REPO_ROOT = fileURLToPath(new URL("..", import.meta.url));

function freshSchemaName(): SchemaName {
  return `s11_migrate_${process.pid}_${randomBytes(4).toString("hex")}` as unknown as SchemaName;
}

async function appliedMigrations(schema: SchemaName): Promise<string[]> {
  const client = await RawSchemaClient.connect(schema);
  try {
    const result = await client.query<{ name: string }>("select name from pgmigrations order by name");
    return result.rows.map((row) => row.name);
  } finally {
    await client.close();
  }
}

describe("S11.3 — the documented migration command reaches the same head other callers reach", () => {
  it("applies the identical migration set the test harness's own migrateToHead reaches", async () => {
    const documentedSchema = freshSchemaName();

    await execFileAsync(process.execPath, [TSX_CLI, MIGRATE_SCRIPT], {
      cwd: REPO_ROOT,
      env: {
        ...process.env,
        GAME_SERVICE_DB_CONNECTION_STRING: TEST_CONNECTION_STRING,
        GAME_SERVICE_DB_SCHEMA: String(documentedSchema),
      },
    });

    const reference = await createTestSchema();
    try {
      const documented = await appliedMigrations(documentedSchema);
      const referenceApplied = await appliedMigrations(reference.schema);

      expect(documented.length).toBeGreaterThan(0);
      expect(documented).toEqual(referenceApplied);
    } finally {
      await reference.drop();
      await dropSchemaByName(TEST_CONNECTION_STRING, String(documentedSchema), 5000);
    }
  });
});
