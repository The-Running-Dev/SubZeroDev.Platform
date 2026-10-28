/** Rebuild the immutable old adapter from Git, never from today's implementation. */
import { execFileSync } from "node:child_process";
import { createHash } from "node:crypto";
import { mkdirSync, writeFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import ts from "typescript";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const commit = "5b587d6990552b85dbf31cb9d82c22869754ac78";
const destination = resolve(root, "tests/fixtures/pre-upgrade");
const sourceRoot = "workloads/game-service/";
const sources = [
  "src/store.ts", "src/types.ts", "src/migrations.ts",
  "migrations/1787054400000_initial-schema.ts",
  "migrations/1787140800000_save-lifecycle-and-profile-terminals.ts",
  "vendor/the-running-dev-game-engine-0.10.0.tgz",
];
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
  commit, engineVersion: "0.10.0", typescriptVersion: ts.version, files,
}, null, 2) + "\n");
