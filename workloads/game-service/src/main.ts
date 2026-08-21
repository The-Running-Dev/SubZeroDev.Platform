/**
 * The process entry point. Every startup failure exits non-zero naming the variant — a service
 * that starts against a contract describing a different engine serves a wire its own schemas do
 * not describe, and every downstream assertion becomes conditional.
 */
import { startWorkload } from "./lifecycle.js";
import { determinismProfileFromEnv, durableStorageProfileFromEnv, err, ok } from "./types.js";
import type { Outcome, StartupError, StorageProfile, WorkloadConfiguration } from "./types.js";

/** `GAME_SERVICE_STORAGE=durable` selects the durable profile; anything else — unset or any other
 *  value — composes the in-memory profile exactly as before, reaching no database (S12.1). The one
 *  companion variable a real operator's process requires is the connection string: a missing one
 *  fails startup loudly, naming it, rather than degrading silently back to in-memory (S12.2) — a
 *  missing schema instead defaults to `public`, since an operator's process is not the proof
 *  harness's own per-run isolation (`durableStorageProfileFromEnv`'s own doc, `types.ts`). */
function storageProfile(): Outcome<StorageProfile, StartupError> {
  const profile = durableStorageProfileFromEnv(process.env, { requireSchema: false });
  if (!profile.ok) return err({ code: "ConfigurationInvalid", setting: profile.error.setting });
  return profile;
}

function configuration(): Outcome<WorkloadConfiguration, StartupError> {
  const storage = storageProfile();
  if (!storage.ok) return storage;

  const port = Number.parseInt(process.env["GAME_SERVICE_PORT"] ?? "8080", 10);
  const otlpEndpoint = process.env["GAME_SERVICE_OTLP_ENDPOINT"] ?? null;
  return ok({
    // An unset host is loopback, decided in `startWorkload` rather than here so every caller of it
    // gets the same answer (invariant 46).
    listen: { host: process.env["GAME_SERVICE_HOST"] ?? "", port },
    determinism: determinismProfileFromEnv(process.env),
    otlpEndpoint,
    storage: storage.value,
  });
}

const resolvedConfiguration = configuration();
if (!resolvedConfiguration.ok) {
  process.stderr.write(`${JSON.stringify(resolvedConfiguration.error)}\n`);
  process.exit(1);
}

const started = await startWorkload(resolvedConfiguration.value);

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
