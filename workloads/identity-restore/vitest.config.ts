import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    maxWorkers: 4,
    minWorkers: 1,
    testTimeout: 30_000,
  },
});
