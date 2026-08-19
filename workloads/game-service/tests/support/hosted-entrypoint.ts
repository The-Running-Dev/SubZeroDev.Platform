/**
 * The harness's own process entry point for S5's hosted run, and S8's durable one. Runs the
 * identical startup path `src/main.ts` does — `startWorkload` under the replay profile — but is
 * shut down over a stdin byte rather than a POSIX signal.
 *
 * `child.kill('SIGTERM')` only terminates a child process gracefully on POSIX; on Windows, libuv's
 * `uv_kill` calls `TerminateProcess` unconditionally for every signal name, so a portable harness
 * cannot rely on one. A parent-closed write to the child's stdin is delivered the same way on every
 * platform, so that is this entry point's one shutdown trigger. `main.ts` itself is untouched — an
 * operator still starts the workload the way S3/S4 built it; this is the harness's own process.
 *
 * A durable target (S8) is selected the same way `main.ts`'s own `determinismProfile()` selects
 * the replay profile: one flag plus its companion variables, anything else falling back to the
 * default this entry point already had. `bounds` is always `DEFAULT_LIFECYCLE_BOUNDS` — the
 * durable replay's own requirement that no TTL can elapse mid-run (S8.4) — so there is no env var
 * for it.
 */
import { RUN_SCHEMA_CONNECT_TIMEOUT_MS, RUN_SCHEMA_POOL_SIZE } from "../../src/harness.js";
import { startWorkload } from "../../src/lifecycle.js";
import { DEFAULT_LIFECYCLE_BOUNDS } from "../../src/types.js";
import type { SchemaName, StorageProfile, WorkloadConfiguration } from "../../src/types.js";

function storageProfile(): StorageProfile {
  if (process.env["GAME_SERVICE_STORAGE"] !== "durable") return { kind: "in-memory" };
  const connectionString = process.env["GAME_SERVICE_DB_CONNECTION_STRING"];
  const schema = process.env["GAME_SERVICE_DB_SCHEMA"];
  // A caller that sets `GAME_SERVICE_STORAGE=durable` without both companion variables gets a
  // loud startup failure, not a silent in-memory run — the entire point of the durable replay
  // (S8) is proving persistence happened, so degrading to in-memory here would let it pass
  // without ever having exercised Postgres.
  if (!connectionString || !schema) {
    throw new Error("GAME_SERVICE_STORAGE=durable requires GAME_SERVICE_DB_CONNECTION_STRING and GAME_SERVICE_DB_SCHEMA");
  }
  return {
    kind: "durable",
    store: {
      connection: {
        connectionString,
        poolSize: RUN_SCHEMA_POOL_SIZE,
        connectTimeoutMs: RUN_SCHEMA_CONNECT_TIMEOUT_MS,
        schema: schema as SchemaName,
      },
      bounds: DEFAULT_LIFECYCLE_BOUNDS,
      readWritePauseMs: 0,
    },
  };
}

function configuration(): WorkloadConfiguration {
  return {
    listen: {
      host: process.env["GAME_SERVICE_HOST"] ?? "127.0.0.1",
      port: Number.parseInt(process.env["GAME_SERVICE_PORT"] ?? "0", 10),
    },
    determinism: {
      kind: "replay",
      fixedInstant: process.env["GAME_SERVICE_FIXED_INSTANT"] ?? "",
      dumpPath: process.env["GAME_SERVICE_DUMP_PATH"] ?? "",
    },
    otlpEndpoint: process.env["GAME_SERVICE_OTLP_ENDPOINT"] ?? null,
    storage: storageProfile(),
  };
}

const started = await startWorkload(configuration());
if (!started.ok) {
  process.stderr.write(`${JSON.stringify(started.error)}\n`);
  process.exit(1);
}

const workload = started.value;
process.stdout.write(`${JSON.stringify({ listening: workload.listening })}\n`);

let shuttingDown = false;
function triggerShutdown(): void {
  if (shuttingDown) return;
  shuttingDown = true;
  void workload.shutdown().then((outcome) => {
    if (!outcome.ok) {
      process.stderr.write(`${JSON.stringify(outcome.error)}\n`);
    }
    // `shutdown()` already awaits the tracing provider's own flush (including its own
    // exit-race delay, paid only when tracing is configured — see `telemetry.ts`).
    process.exit(outcome.ok ? 0 : 1);
  });
}

process.stdin.once("data", triggerShutdown);
// A parent that dies without writing the shutdown byte (an interrupted test run, a killed CI job)
// still closes its end of the pipe, which delivers `end` here — the one signal every platform
// gives this process that its parent is gone, without relying on a POSIX signal reaching a Node
// child the way `hosted-target.ts`'s own note on `SIGTERM` explains it cannot.
process.stdin.once("end", triggerShutdown);
