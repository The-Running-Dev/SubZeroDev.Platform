/**
 * S5.7 — a real operating-system process with a bound socket, addressed over that socket. Spawns
 * `hosted-entrypoint.ts` under the replay profile — the same `startWorkload` call `src/main.ts`
 * makes, over the same `GAME_SERVICE_*` environment variables, with a bound port reported the same
 * way. Implements `HostedTarget` over it: `shutdown` writes to the child's stdin to trigger the
 * graceful path (see `hosted-entrypoint.ts`'s own note on why that, and not `SIGTERM`, is portable),
 * and `readDump` reads the file that shutdown wrote — never a value read out of the child's memory.
 */
import { spawn, type ChildProcessWithoutNullStreams } from "node:child_process";
import { createInterface } from "node:readline";
import { mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";

import { REPLAY_FIXED_INSTANT } from "../../src/replay.js";
import { readDeterminismDumpFile } from "../../src/dump.js";
import { err, ok } from "../../src/types.js";
import type { HostedTarget, Outcome, SchemaName, ShutdownError } from "../../src/types.js";

const ENTRYPOINT = fileURLToPath(new URL("./hosted-entrypoint.ts", import.meta.url));
const TSX_CLI = fileURLToPath(new URL("../../node_modules/tsx/dist/cli.mjs", import.meta.url));

interface Listening {
  readonly listening: { readonly host: string; readonly port: number };
}

export interface SpawnedHostedTarget {
  readonly target: HostedTarget;
  readonly dumpPath: string;
  /** Only for a test that needs to abandon the process without a clean shutdown — every S5 test
   *  goes through `target.shutdown()` instead, which is what writes the dump. */
  forceKill(): void;
}

function freshDumpPath(): string {
  return join(mkdtempSync(join(tmpdir(), "s5-hosted-dump-")), "dump.json");
}

/** S8's durable target: which schema, in which database, the spawned process should compose its
 *  storage profile from — the harness-owned counterpart to `TwoInstanceOptions`. */
export interface DurableHostedTarget {
  readonly connectionString: string;
  readonly schema: SchemaName;
}

/** Spawns the workload as a genuine child process — `process.execPath` running `tsx`'s own CLI
 *  entry point, so this exercises the compiled-equivalent startup path rather than an in-process
 *  stand-in, over a real bound socket. `otlpEndpoint`, when given, is S8's own hook: every other
 *  caller omits it and gets today's `otlpEndpoint: null` behaviour unchanged. `durable`, when
 *  given, is S8's other hook — the durable replay's own target, composed against the named schema
 *  instead of the in-memory default. */
export async function spawnHostedWorkload(
  otlpEndpoint?: string,
  durable?: DurableHostedTarget,
): Promise<SpawnedHostedTarget> {
  const dumpPath = freshDumpPath();

  const env: NodeJS.ProcessEnv = { ...process.env };
  // The published contract is the one `runInProcess` loads (`loadPublishedContract()`, unconditional);
  // an inherited override here would let the hosted run compare against a different contract than the
  // in-process run, defeating comparisons A and B without either failing loudly.
  delete env["GAME_SERVICE_CONTRACT"];
  env["GAME_SERVICE_HOST"] = "127.0.0.1";
  env["GAME_SERVICE_PORT"] = "0";
  // The same fixed instant `runInProcess` uses, so the one clock input the byte-identity proof
  // holds constant is stated once rather than duplicated as a literal that could drift from it.
  env["GAME_SERVICE_FIXED_INSTANT"] = REPLAY_FIXED_INSTANT;
  env["GAME_SERVICE_DUMP_PATH"] = dumpPath;
  if (otlpEndpoint) {
    env["GAME_SERVICE_OTLP_ENDPOINT"] = otlpEndpoint;
  } else {
    delete env["GAME_SERVICE_OTLP_ENDPOINT"];
  }
  if (durable) {
    env["GAME_SERVICE_STORAGE"] = "durable";
    env["GAME_SERVICE_DB_CONNECTION_STRING"] = durable.connectionString;
    env["GAME_SERVICE_DB_SCHEMA"] = String(durable.schema);
  } else {
    delete env["GAME_SERVICE_STORAGE"];
    delete env["GAME_SERVICE_DB_CONNECTION_STRING"];
    delete env["GAME_SERVICE_DB_SCHEMA"];
  }

  const child: ChildProcessWithoutNullStreams = spawn(process.execPath, [TSX_CLI, ENTRYPOINT], {
    cwd: fileURLToPath(new URL("../..", import.meta.url)),
    env,
  });

  let stderr = "";
  child.stderr.on("data", (chunk: Buffer) => {
    stderr += chunk.toString("utf8");
  });

  // A write to `child.stdin` after the child has already exited (a crash, an OOM kill) emits
  // EPIPE; with no listener, Node escalates that to an uncaught exception. This keeps the failure
  // inside `shutdown()`'s own `Outcome` instead of taking the process down.
  let stdinError: Error | null = null;
  child.stdin.on("error", (thrown: Error) => {
    stdinError = thrown;
  });

  let hasExited = false;
  child.once("exit", () => {
    hasExited = true;
  });

  const listening = await new Promise<Listening>((resolve, reject) => {
    const lines = createInterface({ input: child.stdout });
    const timeout = setTimeout(() => {
      lines.close();
      // The child never reported readiness within budget — nothing else will ever reclaim it, so
      // the timeout itself is the one place that kills it.
      child.kill("SIGKILL");
      reject(new Error(`hosted workload did not report readiness in time; stderr:\n${stderr}`));
    }, 15_000);

    lines.on("line", (line) => {
      try {
        const parsed = JSON.parse(line) as Partial<Listening>;
        if (parsed.listening) {
          clearTimeout(timeout);
          lines.close();
          resolve(parsed as Listening);
        }
      } catch {
        // A line that is not the one JSON status line — the entry point writes nothing else to
        // stdout, but a dependency's own stray output must not fail the wait.
      }
    });

    child.once("exit", (code) => {
      clearTimeout(timeout);
      reject(new Error(`hosted workload exited before reporting readiness (code ${code}); stderr:\n${stderr}`));
    });
  });

  const baseAddress = `http://${listening.listening.host}:${listening.listening.port}`;

  const exited = new Promise<number | null>((resolve) => {
    if (hasExited) {
      resolve(child.exitCode);
      return;
    }
    child.once("exit", (code) => resolve(code));
  });

  const target: HostedTarget = {
    baseAddress,

    async shutdown(): Promise<Outcome<void, ShutdownError>> {
      if (!hasExited) {
        // A byte on stdin, not a signal — `hosted-entrypoint.ts`'s own note explains why a signal
        // cannot be relied on to reach a Node child process gracefully on every platform.
        child.stdin.write("shutdown\n");
      }
      const code = await exited;
      if (code === 0 && !stdinError) return ok(undefined);

      // The child's own last stderr line is a `ShutdownError` (`hosted-entrypoint.ts` writes
      // `outcome.error` verbatim), not a bare `CompositionError` — parsed and unwrapped here
      // rather than cast wholesale into `cause`, or the reported error nests one layer too deep
      // and its declared fields (e.g. `cause.path`) come back `undefined`.
      let fromChild: ShutdownError | undefined;
      try {
        fromChild = JSON.parse(stderr.trim().split("\n").pop() ?? "") as ShutdownError;
      } catch {
        fromChild = undefined;
      }
      const cause = fromChild?.cause ?? { code: "DumpWriteFailed" as const, path: dumpPath };
      return err({ code: "DumpWriteFailed", cause });
    },

    async readDump() {
      return readDeterminismDumpFile(dumpPath);
    },
  };

  return {
    target,
    dumpPath,
    forceKill(): void {
      if (!hasExited) child.kill("SIGKILL");
    },
  };
}
