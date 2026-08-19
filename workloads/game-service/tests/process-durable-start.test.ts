/**
 * S12.1/S12.2 — `src/main.ts`'s own env-driven durable configuration, exercised as a real child
 * process (never by importing `main.ts`'s internals — it runs a top-level `await` on import, so a
 * process is the only way to observe what it actually does with the environment it is handed).
 * The same technique `tests/fresh-clone-migration.test.ts` uses for `scripts/migrate.ts`.
 */
import { afterEach, describe, expect, it } from "vitest";
import { spawn } from "node:child_process";
import type { ChildProcessWithoutNullStreams } from "node:child_process";
import { fileURLToPath } from "node:url";
import { randomBytes } from "node:crypto";

import { dropSchemaByName } from "../src/migrations.js";
import { DEFAULT_STORE_CONNECT_TIMEOUT_MS } from "../src/types.js";
import type { SchemaName } from "../src/types.js";
import { RawSchemaClient, TEST_CONNECTION_STRING } from "./support/database.js";
import { CAMPAIGN_ID } from "./support/real-workload.js";
import { freePort } from "./support/free-port.js";

const TSX_CLI = fileURLToPath(new URL("../node_modules/tsx/dist/cli.mjs", import.meta.url));
const MAIN_ENTRY = fileURLToPath(new URL("../src/main.ts", import.meta.url));
const REPO_ROOT = fileURLToPath(new URL("..", import.meta.url));

function freshSchemaName(): SchemaName {
  return `s12_main_${process.pid}_${randomBytes(4).toString("hex")}` as unknown as SchemaName;
}

function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

interface SpawnedProcess {
  readonly child: ChildProcessWithoutNullStreams;
  stdout: string;
  stderr: string;
}

function spawnMain(env: Record<string, string | undefined>): SpawnedProcess {
  // `GAME_SERVICE_DB_CONNECTION_STRING: undefined` (S12.2) means "not set" — deleted from the
  // merged environment rather than passed through, so the child never inherits this test runner's
  // own value for it (there is none here, but the base environment is `process.env` verbatim).
  const merged: Record<string, string> = { ...(process.env as Record<string, string>) };
  for (const [key, value] of Object.entries(env)) {
    if (value === undefined) delete merged[key];
    else merged[key] = value;
  }
  const child = spawn(process.execPath, [TSX_CLI, MAIN_ENTRY], {
    cwd: REPO_ROOT,
    env: merged,
  });
  const spawned: SpawnedProcess = { child, stdout: "", stderr: "" };
  child.stdout.on("data", (chunk: Buffer) => {
    spawned.stdout += chunk.toString();
  });
  child.stderr.on("data", (chunk: Buffer) => {
    spawned.stderr += chunk.toString();
  });
  return spawned;
}

const TIMED_OUT = Symbol("timed-out");

/** `null` is a real, meaningful exit code (the process was killed by a signal rather than calling
 *  `process.exit`) — distinct from `TIMED_OUT`, which means the process never exited at all. */
async function waitForExit(child: ChildProcessWithoutNullStreams, timeoutMs: number): Promise<number | null | typeof TIMED_OUT> {
  return Promise.race([
    new Promise<number | null>((resolve) => child.once("exit", (code) => resolve(code))),
    new Promise<typeof TIMED_OUT>((resolve) => setTimeout(() => resolve(TIMED_OUT), timeoutMs)),
  ]);
}

async function waitForHealthy(baseUrl: string, path: string, timeoutMs: number): Promise<boolean> {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try {
      const response = await fetch(`${baseUrl}${path}`);
      if (response.ok) return true;
    } catch {
      // Not listening yet.
    }
    await sleep(300);
  }
  return false;
}

const spawnedByTest: ChildProcessWithoutNullStreams[] = [];

afterEach(async () => {
  for (const child of spawnedByTest.splice(0)) {
    if (child.exitCode === null && child.signalCode === null) {
      child.kill("SIGKILL");
    }
  }
});

describe("S12.1 — with none of the durable environment set, main.ts composes the in-memory profile exactly as today", () => {
  it("starts, reaches ready, and reaches no database", { timeout: 20_000 }, async () => {
    const port = await freePort();
    const spawned = spawnMain({ GAME_SERVICE_PORT: String(port) });
    spawnedByTest.push(spawned.child);

    const healthy = await waitForHealthy(`http://127.0.0.1:${port}`, "/readyz", 15_000);
    expect(healthy, `stdout: ${spawned.stdout}\nstderr: ${spawned.stderr}`).toBe(true);

    spawned.child.kill("SIGTERM");
    await waitForExit(spawned.child, 5000);
  });
});

describe("S12.1 — with the documented durable environment set to a reachable database, main.ts serves a game operation and persists it", () => {
  it("the created session is present in the database afterward", { timeout: 30_000 }, async () => {
    const port = await freePort();
    const schema = freshSchemaName();
    const spawned = spawnMain({
      GAME_SERVICE_PORT: String(port),
      GAME_SERVICE_STORAGE: "durable",
      GAME_SERVICE_DB_CONNECTION_STRING: TEST_CONNECTION_STRING,
      GAME_SERVICE_DB_SCHEMA: String(schema),
    });
    spawnedByTest.push(spawned.child);

    try {
      const healthy = await waitForHealthy(`http://127.0.0.1:${port}`, "/readyz", 20_000);
      expect(healthy, `stdout: ${spawned.stdout}\nstderr: ${spawned.stderr}`).toBe(true);

      const response = await fetch(`http://127.0.0.1:${port}/mcp/call-tool`, {
        method: "POST",
        body: JSON.stringify({ name: "start_game", arguments: { campaignId: CAMPAIGN_ID } }),
      });
      expect(response.status).toBe(200);
      const body = (await response.json()) as { sessionId?: string };
      const sessionId = body.sessionId;
      expect(typeof sessionId).toBe("string");

      const raw = await RawSchemaClient.connect(schema);
      try {
        const rows = await raw.query("select 1 from session where session_id = $1", [sessionId]);
        expect(rows.rows.length).toBe(1);
      } finally {
        await raw.close();
      }
    } finally {
      spawned.child.kill("SIGTERM");
      await waitForExit(spawned.child, 5000);
      await dropSchemaByName(TEST_CONNECTION_STRING, schema as unknown as string, DEFAULT_STORE_CONNECT_TIMEOUT_MS);
    }
  });
});

describe("S12.2 — with the durable flag set but the connection string missing, startup fails loudly naming what is missing", () => {
  it("exits non-zero, never binds a listener, and never degrades to in-memory", { timeout: 15_000 }, async () => {
    const port = await freePort();
    const spawned = spawnMain({
      GAME_SERVICE_PORT: String(port),
      GAME_SERVICE_STORAGE: "durable",
      GAME_SERVICE_DB_CONNECTION_STRING: undefined,
    });
    spawnedByTest.push(spawned.child);

    const code = await waitForExit(spawned.child, 10_000);
    expect(code).toBe(1);
    expect(spawned.stderr).toContain("GAME_SERVICE_DB_CONNECTION_STRING");
    expect(spawned.stderr).toContain("ConfigurationInvalid");

    const stillListening = await waitForHealthy(`http://127.0.0.1:${port}`, "/livez", 500);
    expect(stillListening).toBe(false);
  });
});
