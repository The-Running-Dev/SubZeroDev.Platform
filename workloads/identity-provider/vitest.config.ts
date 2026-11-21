import { defineConfig } from "vitest/config";

// One file at a time: the suites share one Keycloak and change its realm's state. The timeout sits above
// the slowest sign-in walk with margin; Keycloak itself is started before the suite, not by it.
export default defineConfig({
  test: {
    fileParallelism: false,
    testTimeout: 60_000,
    hookTimeout: 120_000,
  },
});
