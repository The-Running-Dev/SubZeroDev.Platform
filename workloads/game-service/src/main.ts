/**
 * The process entry point. Every startup failure exits non-zero naming the variant — a service
 * that starts against a contract describing a different engine serves a wire its own schemas do
 * not describe, and every downstream assertion becomes conditional.
 */
import { startWorkload } from "./lifecycle.js";
import type { WorkloadConfiguration } from "./types.js";

function configuration(): WorkloadConfiguration {
  const port = Number.parseInt(process.env["GAME_SERVICE_PORT"] ?? "8080", 10);
  const otlpEndpoint = process.env["GAME_SERVICE_OTLP_ENDPOINT"] ?? null;
  return {
    // An unset host is loopback, decided in `startWorkload` rather than here so every caller of it
    // gets the same answer (invariant 46).
    listen: { host: process.env["GAME_SERVICE_HOST"] ?? "", port },
    determinism: { kind: "default" },
    otlpEndpoint,
  };
}

const started = await startWorkload(configuration());

if (!started.ok) {
  process.stderr.write(`${JSON.stringify(started.error)}\n`);
  process.exit(1);
}

process.stdout.write(
  `${JSON.stringify({ listening: started.value.listening, readiness: started.value.probes.readiness().status })}\n`,
);

for (const signal of ["SIGINT", "SIGTERM"] as const) {
  process.on(signal, () => {
    void started.value.shutdown().then((outcome) => {
      process.exit(outcome.ok ? 0 : 1);
    });
  });
}
