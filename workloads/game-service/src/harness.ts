/**
 * Proof harness — S7 and S8. Two workload instances against one shared durable store, each a
 * **genuine operating-system process** running `harness-entrypoint.ts` — the same `startWorkload`
 * call `src/main.ts` makes, over the same `GAME_SERVICE_*` variables, addressed over a real bound
 * socket.
 *
 * **That the instances are separate processes is the proof, not a detail of it.** The failure
 * `engine-hosting-contract.md` §6.1 describes is between processes; two compositions sharing one
 * event loop, one module registry and one heap cannot establish that the compare-and-swap survives
 * the separation the failure is defined by. They ran in-process until 2026-08-21, which the
 * design specified against throughout (`design/90-decisions.md`, "The two-instance proof spawns
 * real processes").
 *
 * The two instances are anonymous and interchangeable (`20-contract.md`, "Proof harness"): nothing
 * distinguishes them beyond which end of `readWritePauseMs` each is configured with, which reaches
 * the child as `GAME_SERVICE_READ_WRITE_PAUSE_MS`.
 *
 * `createRunSchema` (S8) is the durable replay's own prerequisite, on the same footing: a per-run
 * schema, created and migrated to head here, dropped by the caller once its run is done.
 */
import { randomBytes } from "node:crypto";
import { spawn, type ChildProcessWithoutNullStreams } from "node:child_process";
import { createInterface } from "node:readline";
import { fileURLToPath } from "node:url";
import { dropSchemaByName, migrateToHead } from "./migrations.js";
import { DEFAULT_STORE_CONNECT_TIMEOUT_MS, DEFAULT_STORE_POOL_SIZE, err, ok } from "./types.js";
import type {
  HarnessError,
  Outcome,
  RunSchema,
  SchemaName,
  StoreConnection,
  TwoInstanceOptions,
  WorkloadInstance,
} from "./types.js";

const ENTRYPOINT = fileURLToPath(new URL("./harness-entrypoint.ts", import.meta.url));
const TSX_CLI = fileURLToPath(new URL("../node_modules/tsx/dist/cli.mjs", import.meta.url));
const WORKING_DIRECTORY = fileURLToPath(new URL("..", import.meta.url));

let runSchemaCounter = 0;

/** Unique to this process and this call, the same way `spawnInstances`' two instances need no
 *  coordination — collision-safe under concurrent runs without a lock of its own. */
function freshRunSchemaName(): SchemaName {
  runSchemaCounter += 1;
  const suffix = randomBytes(4).toString("hex");
  return `run_${process.pid}_${runSchemaCounter}_${suffix}` as unknown as SchemaName;
}

export async function createRunSchema(connectionString: string): Promise<Outcome<RunSchema, HarnessError>> {
  const name = freshRunSchemaName();
  const connection: StoreConnection = {
    connectionString,
    poolSize: DEFAULT_STORE_POOL_SIZE,
    connectTimeoutMs: DEFAULT_STORE_CONNECT_TIMEOUT_MS,
    schema: name,
  };

  const migrated = await migrateToHead(connection);
  if (!migrated.ok) {
    // `createSchema: true` (`migrations.ts`) issues its `CREATE SCHEMA IF NOT EXISTS` before the
    // migration transaction, so a failure here can still leave a pristine, empty schema behind —
    // best-effort cleanup, since there is no `RunSchema` handle to hand the caller for this path.
    await dropSchemaByName(connectionString, name as unknown as string, DEFAULT_STORE_CONNECT_TIMEOUT_MS).catch(() => {});
    return err({ code: "SchemaCreateFailed", detail: JSON.stringify(migrated.error) });
  }

  return ok({
    name,
    async drop(): Promise<Outcome<void, HarnessError>> {
      try {
        await dropSchemaByName(connectionString, name as unknown as string, DEFAULT_STORE_CONNECT_TIMEOUT_MS);
        return ok(undefined);
      } catch (error) {
        return err({
          code: "SchemaDropFailed",
          schema: name,
          detail: error instanceof Error ? error.message : String(error),
        });
      }
    },
  });
}

const SPAWN_BOUND_MS = 15_000;
const SHUTDOWN_BOUND_MS = 10_000;

const TIMED_OUT = Symbol("timed-out");

async function withBound<T>(promise: Promise<T>, ms: number): Promise<T | typeof TIMED_OUT> {
  return Promise.race([
    promise,
    new Promise<typeof TIMED_OUT>((resolve) => {
      setTimeout(() => resolve(TIMED_OUT), ms);
    }),
  ]);
}

interface Listening {
  readonly listening: { readonly host: string; readonly port: number };
}

/**
 * One instance, as its own operating-system process: `process.execPath` running `tsx`'s CLI over
 * `harness-entrypoint.ts`, exactly as `tests/support/hosted-target.ts` spawns the replay's target.
 * Every input the child needs travels as an environment variable, because that is the only channel
 * a separate process has — which is also what makes the two instances demonstrably independent.
 *
 * Shutdown is a byte on the child's stdin, never a signal: on Windows libuv's `uv_kill` calls
 * `TerminateProcess` for every signal name, so a signal cannot be relied on to reach a Node child
 * gracefully (`harness-entrypoint.ts`'s own note). A hard kill would leave the child's pool
 * connections open and race the caller's `DROP SCHEMA ... CASCADE`.
 */
async function spawnOne(
  options: TwoInstanceOptions,
  readWritePauseMs: number,
  instance: 0 | 1,
): Promise<Outcome<WorkloadInstance, HarnessError>> {
  const env: NodeJS.ProcessEnv = { ...process.env };
  // An inherited override would point the child at a different contract than the one this proof
  // is asserted against — the same reason `hosted-target.ts` deletes it.
  delete env["GAME_SERVICE_CONTRACT"];
  delete env["GAME_SERVICE_OTLP_ENDPOINT"];
  // The default determinism profile: the contention proofs mint real ids, and a counting
  // `RecordIdSource` across two instances would collide on the primary key by construction
  // (`10-design.md`, "The replay is strictly sequential and single-instance").
  delete env["GAME_SERVICE_DETERMINISM"];
  env["GAME_SERVICE_HOST"] = "127.0.0.1";
  env["GAME_SERVICE_PORT"] = "0";
  env["GAME_SERVICE_STORAGE"] = "durable";
  env["GAME_SERVICE_DB_CONNECTION_STRING"] = options.connectionString;
  env["GAME_SERVICE_DB_SCHEMA"] = String(options.schema);
  env["GAME_SERVICE_READ_WRITE_PAUSE_MS"] = String(readWritePauseMs);

  let child: ChildProcessWithoutNullStreams;
  try {
    child = spawn(process.execPath, [TSX_CLI, ENTRYPOINT], { cwd: WORKING_DIRECTORY, env });
  } catch (error) {
    return err({
      code: "InstanceSpawnFailed",
      instance,
      detail: error instanceof Error ? error.message : String(error),
    });
  }

  let stderr = "";
  child.stderr.on("data", (chunk: Buffer) => {
    stderr += chunk.toString("utf8");
  });

  // A write to a child that has already exited emits EPIPE; with no listener Node escalates it to
  // an uncaught exception, taking the test runner down instead of failing this instance.
  let stdinError: Error | null = null;
  child.stdin.on("error", (thrown: Error) => {
    stdinError = thrown;
  });

  let hasExited = false;
  child.once("exit", () => {
    hasExited = true;
  });
  const exited = new Promise<number | null>((resolve) => {
    if (hasExited) {
      resolve(child.exitCode);
      return;
    }
    child.once("exit", (code) => resolve(code));
  });

  const listening = await new Promise<Listening | null>((resolve) => {
    const lines = createInterface({ input: child.stdout });
    const timeout = setTimeout(() => {
      lines.close();
      // Nothing else will ever reclaim a child that never reported ready, so the timeout is the
      // one place that kills it.
      child.kill("SIGKILL");
      resolve(null);
    }, SPAWN_BOUND_MS);

    lines.on("line", (line) => {
      try {
        const parsed = JSON.parse(line) as Partial<Listening>;
        if (parsed.listening) {
          clearTimeout(timeout);
          lines.close();
          resolve(parsed as Listening);
        }
      } catch {
        // Not the one JSON status line the entry point writes — a dependency's stray stdout must
        // not fail the wait.
      }
    });

    child.once("exit", () => {
      clearTimeout(timeout);
      lines.close();
      resolve(null);
    });
  });

  if (listening === null) {
    await exited;
    return err({
      code: "InstanceSpawnFailed",
      instance,
      detail: `did not report ready within ${SPAWN_BOUND_MS}ms; stderr:\n${stderr}`,
    });
  }

  return ok({
    baseAddress: `http://${listening.listening.host}:${listening.listening.port}`,
    async shutdown(): Promise<Outcome<void, HarnessError>> {
      if (!hasExited) child.stdin.write("shutdown\n");
      const code = await withBound(exited, SHUTDOWN_BOUND_MS);
      if (code === TIMED_OUT) {
        child.kill("SIGKILL");
        await exited;
        return err({ code: "InstanceShutdownFailed", instance, detail: `did not exit within ${SHUTDOWN_BOUND_MS}ms` });
      }
      if (code !== 0 || stdinError) {
        return err({
          code: "InstanceShutdownFailed",
          instance,
          detail: `exited ${String(code)}; stderr:\n${stderr}`,
        });
      }
      return ok(undefined);
    },
  });
}

/** Spawns both instances concurrently, against the same connection string and schema. A failure on
 *  either shuts the other back down before reporting — a caller that got `Err` never holds a leaked
 *  instance. */
export async function spawnInstances(
  options: TwoInstanceOptions,
): Promise<Outcome<readonly [WorkloadInstance, WorkloadInstance], HarnessError>> {
  const [first, second] = await Promise.all([
    spawnOne(options, options.readWritePauseMs[0], 0),
    spawnOne(options, options.readWritePauseMs[1], 1),
  ]);

  if (!first.ok) {
    if (second.ok) await second.value.shutdown();
    return first;
  }
  if (!second.ok) {
    await first.value.shutdown();
    return second;
  }

  return ok([first.value, second.value] as const);
}
