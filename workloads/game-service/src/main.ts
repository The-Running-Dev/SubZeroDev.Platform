/**
 * The process entry point. Every startup failure exits non-zero naming the variant — a service
 * that starts against a contract describing a different engine serves a wire its own schemas do
 * not describe, and every downstream assertion becomes conditional.
 */
import { startWorkload } from "./lifecycle.js";
import { DEFAULT_LIFECYCLE_BOUNDS, DEFAULT_STORE_CONNECT_TIMEOUT_MS, DEFAULT_STORE_POOL_SIZE, err, ok } from "./types.js";
import type { DeterminismProfile, Outcome, SchemaName, StartupError, StorageProfile, WorkloadConfiguration } from "./types.js";

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

/** `GAME_SERVICE_STORAGE=durable` selects the durable profile; anything else — unset or any other
 *  value — composes the in-memory profile exactly as before, reaching no database (S12.1). The
 *  one companion variable the durable profile requires is the connection string: a missing one
 *  fails startup loudly, naming it, rather than degrading silently back to in-memory (S12.2) —
 *  the same discipline `tests/support/hosted-entrypoint.ts`'s own `storageProfile()` already holds
 *  the harness's durable target to, and the reason `main.ts` was named there as not yet reaching
 *  it (`design/90-decisions.md`, S12). Pool size and connect timeout are the one stated, un-tuned
 *  default (`DEFAULT_STORE_POOL_SIZE`, `DEFAULT_STORE_CONNECT_TIMEOUT_MS`) — the brief's
 *  performance non-goal forbids exposing either as its own setting. */
function storageProfile(): Outcome<StorageProfile, StartupError> {
  if (process.env["GAME_SERVICE_STORAGE"] !== "durable") {
    return ok({ kind: "in-memory" });
  }
  const connectionString = process.env["GAME_SERVICE_DB_CONNECTION_STRING"];
  if (!connectionString) {
    return err({ code: "ConfigurationInvalid", setting: "GAME_SERVICE_DB_CONNECTION_STRING" });
  }
  const schema = process.env["GAME_SERVICE_DB_SCHEMA"];
  return ok({
    kind: "durable",
    store: {
      connection: {
        connectionString,
        poolSize: DEFAULT_STORE_POOL_SIZE,
        connectTimeoutMs: DEFAULT_STORE_CONNECT_TIMEOUT_MS,
        schema: schema ? (schema as unknown as SchemaName) : null,
      },
      bounds: DEFAULT_LIFECYCLE_BOUNDS,
      readWritePauseMs: 0,
    },
  });
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
    determinism: determinismProfile(),
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
