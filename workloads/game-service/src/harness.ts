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
import { DEFAULT_LIFECYCLE_BOUNDS, err, ok } from "./types.js";
import type {
  HarnessError,
  Outcome,
  TwoInstanceOptions,
  WorkloadInstance,
} from "./types.js";

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
  try {
    const startPromise = startWorkload({
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
          bounds: DEFAULT_LIFECYCLE_BOUNDS,
          readWritePauseMs,
        },
      },
    });
    const started = await withBound(startPromise, SPAWN_BOUND_MS);

    if (started === TIMED_OUT) {
      // `startWorkload` has no cancellation of its own, so it keeps running after the bound
      // elapses. If it later succeeds, shut the process it built back down — otherwise a caller
      // that got `Err` here would still be leaving a live listener and store pool behind.
      void startPromise.then(
        (result) => {
          if (result.ok) void result.value.shutdown();
        },
        () => {},
      );
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
  } catch (error) {
    // `startWorkload` can throw synchronously rather than resolve to an `Outcome` (its contract
    // load path has no try/catch of its own). Caught here so that failure still resolves to `Err`
    // instead of rejecting `spawnInstances`'s `Promise.all` and skipping the sibling's cleanup.
    return err({
      code: "InstanceSpawnFailed",
      instance,
      detail: error instanceof Error ? error.message : String(error),
    });
  }
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
