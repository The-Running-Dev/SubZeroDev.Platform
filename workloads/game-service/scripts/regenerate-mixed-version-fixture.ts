/** Rebuild an immutable old adapter from Git, never from today's implementation. */
import { execFileSync } from "node:child_process";
import { createHash } from "node:crypto";
import { mkdirSync, writeFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import ts from "typescript";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const sourceRoot = "workloads/game-service/";
const adapter = ["src/store.ts", "src/types.ts", "src/migrations.ts"];
// Each fixture is pinned independently: #353's regressions keep the pre-upgrade adapter, and
// the previous-release fixture moves to the last vendored release on each engine upgrade.
const fixtures = {
  "pre-upgrade": {
    commit: "5b587d6990552b85dbf31cb9d82c22869754ac78",
    engineVersion: "0.10.0",
    sources: [
      ...adapter,
      "migrations/1787054400000_initial-schema.ts",
      "migrations/1787140800000_save-lifecycle-and-profile-terminals.ts",
      "vendor/the-running-dev-game-engine-0.10.0.tgz",
    ],
  },
  "previous-release": {
    commit: "7c82c565a6c23743a5914fa2bdfe7fca5feb87e8",
    engineVersion: "0.12.0",
    sources: [
      ...adapter,
      "migrations/1787054400000_initial-schema.ts",
      "migrations/1787140800000_save-lifecycle-and-profile-terminals.ts",
      "migrations/1787227200000_session-revision-and-profile-kind-data.ts",
      "migrations/1787313600000_legacy-session-revision.ts",
      "vendor/the-running-dev-game-engine-0.12.0.tgz",
    ],
  },
} as const;

const name = process.argv[2] as keyof typeof fixtures | undefined;
if (!name || !(name in fixtures)) {
  throw new Error(`Usage: regenerate-mixed-version-fixture.ts <${Object.keys(fixtures).join("|")}>`);
}
const { commit, engineVersion, sources } = fixtures[name];
const destination = resolve(root, "tests/fixtures", name);
const sha256 = (value: string | Buffer): string => createHash("sha256").update(value).digest("hex");
const files = sources.map((path) => {
  const source = execFileSync("git", ["show", `${commit}:${sourceRoot}${path}`], { cwd: root });
  const outputPath = path.replace(/\.ts$/, ".js");
  const output = path.endsWith(".ts")
    ? ts.transpileModule(source.toString("utf8"), {
      compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.ES2022 },
      fileName: path,
    }).outputText
    : source;
  mkdirSync(dirname(resolve(destination, outputPath)), { recursive: true });
  writeFileSync(resolve(destination, outputPath), output);
  return { source: sourceRoot + path, sourceSha256: sha256(source), path: outputPath, sha256: sha256(output) };
});
writeFileSync(resolve(destination, "manifest.json"), JSON.stringify({
  commit, engineVersion, typescriptVersion: ts.version, files,
}, null, 2) + "\n");
