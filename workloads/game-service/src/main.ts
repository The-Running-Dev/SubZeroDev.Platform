/**
 * The process entry point. Every startup failure exits non-zero naming the variant — a service
 * that starts against a contract describing a different engine serves a wire its own schemas do
 * not describe, and every downstream assertion becomes conditional.
 */
import { startWorkload } from "./lifecycle.js";
import type { DeterminismProfile, WorkloadConfiguration } from "./types.js";

/** `GAME_SERVICE_DETERMINISM=replay` plus both companion variables selects the replay profile;
 *  anything else — unset, any other value, or either companion variable missing — is the default
 *  profile. Without this, the replay profile S4 delivers is reachable only from a programmatic
 *  `startWorkload` caller (tests), never from the process an operator actually starts. */
function determinismProfile(): DeterminismProfile {
  if (process.env["GAME_SERVICE_DETERMINISM"] !== "replay") {
    return { kind: "default" };
  }
  const fixedInstant = process.env["GAME_SERVICE_FIXED_INSTANT"];
  const dumpPath = process.env["GAME_SERVICE_DUMP_PATH"];
  if (!fixedInstant || !dumpPath) {
    return { kind: "default" };
  }
  return { kind: "replay", fixedInstant, dumpPath };
}

function configuration(): WorkloadConfiguration {
  const port = Number.parseInt(process.env["GAME_SERVICE_PORT"] ?? "8080", 10);
  const otlpEndpoint = process.env["GAME_SERVICE_OTLP_ENDPOINT"] ?? null;
  return {
    // An unset host is loopback, decided in `startWorkload` rather than here so every caller of it
    // gets the same answer (invariant 46).
    listen: { host: process.env["GAME_SERVICE_HOST"] ?? "", port },
    determinism: determinismProfile(),
    otlpEndpoint,
    // Env-driven durable configuration is not part of this slice (`design/30-slices.md`, S4) — the
    // two-instance proof drives `startWorkload` with a durable profile directly, through the proof
    // harness's own `WorkloadConfiguration`, not through this process entry point.
    storage: { kind: "in-memory" },
  };
}

const started = await startWorkload(configuration());

if (!started.ok) {
  process.stderr.write(`${JSON.stringify(started.error)}\n`);
  process.exit(1);
}

process.stdout.write(
  `${JSON.stringify({ listening: started.value.listening, readiness: (await started.value.probes.readiness()).status })}\n`,
);

let shuttingDown = false;
for (const signal of ["SIGINT", "SIGTERM"] as const) {
  process.on(signal, () => {
    if (shuttingDown) return;
    shuttingDown = true;
    void started.value.shutdown().then((outcome) => {
      if (!outcome.ok) {
        process.stderr.write(`${JSON.stringify(outcome.error)}\n`);
      }
      // `shutdown()` already awaits the tracing provider's own flush (including its own
      // exit-race delay, paid only when tracing is configured — see `telemetry.ts`).
      process.exit(outcome.ok ? 0 : 1);
    });
  });
}
