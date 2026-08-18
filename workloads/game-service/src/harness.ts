/**
 * Proof harness — S7. Two workload instances, spawned by direct `startWorkload` calls against one
 * shared durable store — the path `src/main.ts`'s own note anticipates: "the two-instance proof
 * drives `startWorkload` with a durable profile directly, through the proof harness's own
 * `WorkloadConfiguration`, not through this process entry point." There is no env var for a schema
 * or a read/write pause, so this cannot go through the process entry point the way `hosted-target.ts`
 * does for the replay proof.
 *
 * The two instances are anonymous and interchangeable (`20-contract.md`, "Proof harness"): nothing
 * distinguishes them beyond which end of `readWritePauseMs` each is configured with.
 */
import { startWorkload } from "./lifecycle.js";
import { err, ok } from "./types.js";
import type {
  HarnessError,
  LifecycleBounds,
  Outcome,
  TwoInstanceOptions,
  WorkloadInstance,
} from "./types.js";

// Generous enough that the schema's own `sessionIdleTtlSeconds`/`saveTtlSeconds` never bound a
// proof run, and comfortably clear of `ASSUMED_FORWARD_TIMEOUT_SECONDS` for the
// `retentionHorizonSeconds` check `compose()` performs (`compose.ts`).
const HARNESS_BOUNDS: LifecycleBounds = {
  sessionIdleTtlSeconds: 2_592_000,
  saveTtlSeconds: 31_536_000,
  retentionHorizonSeconds: 31_536_000,
  sweepIntervalSeconds: 3600,
  sweepStatementTimeoutMs: 5000,
};

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

async function spawnOne(
  options: TwoInstanceOptions,
  readWritePauseMs: number,
  instance: 0 | 1,
): Promise<Outcome<WorkloadInstance, HarnessError>> {
  const started = await withBound(
    startWorkload({
      listen: { host: "127.0.0.1", port: 0 },
      determinism: { kind: "default" },
      otlpEndpoint: null,
      storage: {
        kind: "durable",
        store: {
          connection: {
            connectionString: options.connectionString,
            poolSize: 5,
            connectTimeoutMs: 5000,
            schema: options.schema,
          },
          bounds: HARNESS_BOUNDS,
          readWritePauseMs,
        },
      },
    }),
    SPAWN_BOUND_MS,
  );

  if (started === TIMED_OUT) {
    return err({ code: "InstanceSpawnFailed", instance, detail: `did not report ready within ${SPAWN_BOUND_MS}ms` });
  }
  if (!started.ok) {
    return err({ code: "InstanceSpawnFailed", instance, detail: JSON.stringify(started.error) });
  }

  const process = started.value;
  return ok({
    baseAddress: `http://${process.listening.host}:${process.listening.port}`,
    async shutdown(): Promise<Outcome<void, HarnessError>> {
      const stopped = await withBound(process.shutdown(), SHUTDOWN_BOUND_MS);
      if (stopped === TIMED_OUT) {
        return err({ code: "InstanceShutdownFailed", instance, detail: `did not exit within ${SHUTDOWN_BOUND_MS}ms` });
      }
      if (!stopped.ok) {
        return err({ code: "InstanceShutdownFailed", instance, detail: JSON.stringify(stopped.error) });
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
