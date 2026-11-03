/**
 * One file, one Adventures process, one edge process, one issuer: the proof is a single ordered
 * conversation, so nothing here runs in parallel. The timeouts sit above the harness's own
 * readiness bounds (`tests/support/adventures.ts`), so a slow start is reported as the harness
 * reports it rather than as a runner timeout.
 */
import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    // Only this project's own tests: the pinned Adventures checkout under `.adventures/` carries
    // thousands of tests of its own that are not this proof's to run.
    include: ["tests/**/*.test.ts"],
    fileParallelism: false,
    testTimeout: 60_000,
    hookTimeout: 120_000,
  },
});
