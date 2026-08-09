/**
 * The harness's own process entry point for S5's hosted run. Runs the identical startup path
 * `src/main.ts` does — `startWorkload` under the replay profile — but is shut down over a stdin
 * byte rather than a POSIX signal.
 *
 * `child.kill('SIGTERM')` only terminates a child process gracefully on POSIX; on Windows, libuv's
 * `uv_kill` calls `TerminateProcess` unconditionally for every signal name, so a portable harness
 * cannot rely on one. A parent-closed write to the child's stdin is delivered the same way on every
 * platform, so that is this entry point's one shutdown trigger. `main.ts` itself is untouched — an
 * operator still starts the workload the way S3/S4 built it; this is the harness's own process.
 */
import { startWorkload } from "../../src/lifecycle.js";
import type { WorkloadConfiguration } from "../../src/types.js";

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
    otlpEndpoint: null,
  };
}

const started = await startWorkload(configuration());
if (!started.ok) {
  process.stderr.write(`${JSON.stringify(started.error)}\n`);
  process.exit(1);
}

process.stdout.write(`${JSON.stringify({ listening: started.value.listening })}\n`);

process.stdin.once("data", () => {
  void started.value.shutdown().then((outcome) => {
    if (!outcome.ok) {
      process.stderr.write(`${JSON.stringify(outcome.error)}\n`);
    }
    process.exit(outcome.ok ? 0 : 1);
  });
});
