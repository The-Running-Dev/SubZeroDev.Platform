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

import { readDeterminismDumpFile } from "../../src/dump.js";
import { err, ok } from "../../src/types.js";
import type { HostedTarget, Outcome, ShutdownError } from "../../src/types.js";

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

/** Spawns the workload as a genuine child process — `process.execPath` running `tsx`'s own CLI
 *  entry point, so this exercises the compiled-equivalent startup path rather than an in-process
 *  stand-in, over a real bound socket. */
export async function spawnHostedWorkload(): Promise<SpawnedHostedTarget> {
  const dumpPath = freshDumpPath();

  const env: NodeJS.ProcessEnv = { ...process.env };
  env["GAME_SERVICE_HOST"] = "127.0.0.1";
  env["GAME_SERVICE_PORT"] = "0";
  env["GAME_SERVICE_FIXED_INSTANT"] = "2026-01-01T00:00:00.000Z";
  env["GAME_SERVICE_DUMP_PATH"] = dumpPath;

  const child: ChildProcessWithoutNullStreams = spawn(process.execPath, [TSX_CLI, ENTRYPOINT], {
    cwd: fileURLToPath(new URL("../..", import.meta.url)),
    env,
  });

  let stderr = "";
  child.stderr.on("data", (chunk: Buffer) => {
    stderr += chunk.toString("utf8");
  });

  const listening = await new Promise<Listening>((resolve, reject) => {
    const lines = createInterface({ input: child.stdout });
    const timeout = setTimeout(() => {
      lines.close();
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
    child.once("exit", (code) => resolve(code));
  });

  let shutdownRequested = false;

  const target: HostedTarget = {
    baseAddress,

    async shutdown(): Promise<Outcome<void, ShutdownError>> {
      shutdownRequested = true;
      // A byte on stdin, not a signal — `hosted-entrypoint.ts`'s own note explains why a signal
      // cannot be relied on to reach a Node child process gracefully on every platform.
      child.stdin.write("shutdown\n");
      const code = await exited;
      if (code === 0) return ok(undefined);

      let cause: ShutdownError["cause"] | undefined;
      try {
        cause = JSON.parse(stderr.trim().split("\n").pop() ?? "") as ShutdownError["cause"];
      } catch {
        cause = { code: "DumpWriteFailed", path: dumpPath };
      }
      return err({ code: "DumpWriteFailed", cause: cause ?? { code: "DumpWriteFailed", path: dumpPath } });
    },

    async readDump() {
      return readDeterminismDumpFile(dumpPath);
    },
  };

  return {
    target,
    dumpPath,
    forceKill(): void {
      if (!shutdownRequested) child.kill("SIGKILL");
    },
  };
}
