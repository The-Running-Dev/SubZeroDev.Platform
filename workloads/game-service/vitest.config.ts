/**
 * The suite's own concurrency bound and per-test timeout, and the only two things this file sets.
 *
 * Several proofs spawn genuine operating-system processes — the hosted replay target
 * (`tests/support/hosted-target.ts`), the .NET edge (`tests/support/hosted-edge.ts`), and, from
 * 2026-08-21, both instances of the two-instance contention proof (`src/harness.ts`, and
 * `design/90-decisions.md`, "The two-instance proof spawns real processes"). Each child pays a
 * `tsx` startup before it binds and reports ready, and each of those waits is bounded.
 *
 * Under vitest's default file parallelism — one worker per core, eleven on a twelve-core machine —
 * enough of those files run at once that children miss their own readiness bounds and the suite
 * fails for machine load rather than for a defect. **A bound a busy machine can blow is a flaky
 * gate, and a flaky gate is worse than a slow one**, because the one signal a proof must carry is
 * that a red run means the thing it proves is broken.
 *
 * Four workers, not one: the suite is dominated by process startup and database round trips rather
 * than by CPU, so serialising it outright would trade real wall-clock time for headroom nothing
 * needs. This bounds how many proofs may be starting processes at once; it is not a claim about
 * the right number of cores.
 *
 * **The per-test timeout has to sit above the harness's own bounds, or the harness's own errors
 * are unreachable through the runner.** `spawnInstances` waits `SPAWN_BOUND_MS` (15s) for an
 * instance to report ready and `SHUTDOWN_BOUND_MS` (10s) for it to exit, reporting
 * `InstanceSpawnFailed` / `InstanceShutdownFailed` when either elapses — variants
 * `20-contract.md` declares and S7.7 asserts. Under vitest's 5-second default the runner killed
 * the test first, so those variants could never be observed through it and the gate reported
 * "timed out" for every cause, including the ones the harness classifies precisely. 30 seconds
 * clears both bounds with margin.
 */
import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    maxWorkers: 4,
    minWorkers: 1,
    testTimeout: 30_000,
  },
});
