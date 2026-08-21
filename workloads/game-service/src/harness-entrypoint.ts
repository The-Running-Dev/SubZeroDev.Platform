/**
 * The proof harness's own process entry point — S5's and S8's hosted replay targets, and S7's two
 * contending instances. Runs the identical startup path `src/main.ts` does — `startWorkload` over
 * the same `GAME_SERVICE_*` environment variables — but is shut down over a stdin byte rather than
 * a POSIX signal.
 *
 * `child.kill('SIGTERM')` only terminates a child process gracefully on POSIX; on Windows, libuv's
 * `uv_kill` calls `TerminateProcess` unconditionally for every signal name, so a portable harness
 * cannot rely on one. A parent-closed write to the child's stdin is delivered the same way on every
 * platform, so that is this entry point's one shutdown trigger. **`main.ts` itself is untouched** —
 * an operator still starts the workload the way S3/S4 built it, and a stdin trigger there would
 * shut down any service started with its stdin redirected from `/dev/null`.
 *
 * It lives beside `harness.ts` rather than under `tests/support/` because `harness.ts` spawns it
 * (`design/90-decisions.md`, 2026-08-21, "The two-instance proof spawns real processes"): a module
 * under `src/` reaching a path under `tests/` is the one dependency direction this workload does
 * not otherwise take. Nothing exports it from `index.ts`, on the same footing as `harness.ts`,
 * `conformance.ts` and `replay.ts` — test-scope modules that happen to live in `src/`.
 *
 * Both profiles it can compose are read from the environment by the same two functions `main.ts`
 * uses (`determinismProfileFromEnv`, `durableStorageProfileFromEnv`), so there is one reader per
 * profile rather than one per entry point. The single difference is `requireSchema`: this process
 * never defaults a missing schema, because the isolation a per-run schema gives every proof depends
 * on one always being named.
 */
import { startWorkload } from "./lifecycle.js";
import { determinismProfileFromEnv, durableStorageProfileFromEnv } from "./types.js";
import type { StorageProfile, WorkloadConfiguration } from "./types.js";

function storageProfile(): StorageProfile {
  // A caller that sets `GAME_SERVICE_STORAGE=durable` without both companion variables gets a
  // loud startup failure, not a silent in-memory run — the entire point of the durable replay
  // (S8) is proving persistence happened, so degrading to in-memory here would let it pass
  // without ever having exercised Postgres.
  const profile = durableStorageProfileFromEnv(process.env, { requireSchema: true });
  if (!profile.ok) {
    throw new Error(`GAME_SERVICE_STORAGE=durable is missing ${profile.error.setting}`);
  }
  return profile.value;
}

function configuration(): WorkloadConfiguration {
  return {
    listen: {
      host: process.env["GAME_SERVICE_HOST"] ?? "127.0.0.1",
      port: Number.parseInt(process.env["GAME_SERVICE_PORT"] ?? "0", 10),
    },
    // Env-selected rather than hardcoded to `replay`: S7's two instances run the default profile
    // against one durable store, and S5/S8's replay targets set `GAME_SERVICE_DETERMINISM=replay`
    // with its two companion variables. One entry point, both proofs.
    determinism: determinismProfileFromEnv(process.env),
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
