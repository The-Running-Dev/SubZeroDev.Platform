import { defineConfig } from "vitest/config";
import { fileURLToPath } from "node:url";
import { resolve } from "node:path";

const siteRoot = fileURLToPath(new URL(".", import.meta.url));

export default defineConfig({
  server: { fs: { allow: [siteRoot, resolve(siteRoot, "..", "design")] } },
  test: {
    environment: "jsdom",
    setupFiles: "./src/test/setup.ts",
  },
});
