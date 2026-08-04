import { execFile as execFileCallback } from "node:child_process";
import { mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { promisify } from "node:util";

const execFile = promisify(execFileCallback);
const siteRoot = dirname(fileURLToPath(import.meta.url));
const packageCli = join(
  siteRoot,
  "..",
  "node_modules",
  "subzerodev-platform-ui-landing-page",
  "dist",
  "cli.js",
);
const fixtureRoot = await mkdtemp(join(tmpdir(), "platform-landing-merge-"));

async function makeDocs(name, withProtectedDocs = true) {
  const output = join(fixtureRoot, name);
  await mkdir(output, { recursive: true });
  await writeFile(join(output, "index.html"), "documentation home", "utf8");
  if (withProtectedDocs) {
    await mkdir(join(output, "docs"), { recursive: true });
    await writeFile(join(output, "docs", "safe.html"), "safe", "utf8");
  }
  return output;
}

async function makeLanding(name, { index = true, docs = false } = {}) {
  const output = join(fixtureRoot, name);
  await mkdir(output, { recursive: true });
  if (index)
    await writeFile(join(output, "index.html"), "landing home", "utf8");
  if (docs) await mkdir(join(output, "docs"), { recursive: true });
  return output;
}

async function merge(landingDist, docsOutput) {
  return execFile(process.execPath, [
    packageCli,
    "merge",
    "--landing-dist",
    landingDist,
    "--docs-output",
    docsOutput,
    "--protected-path",
    "docs",
  ]);
}

try {
  const docs = await makeDocs("docs");
  const landing = await makeLanding("landing");
  await merge(landing, docs);
  if ((await readFile(join(docs, "index.html"), "utf8")) !== "landing home") {
    throw new Error("Package merge did not overlay the landing page.");
  }
  if ((await readFile(join(docs, "docs", "safe.html"), "utf8")) !== "safe") {
    throw new Error("Package merge changed the protected docs subtree.");
  }

  await expectMergeFailure(
    makeLanding("no-index", { index: false }),
    makeDocs("docs-for-no-index"),
    "has no index.html",
  );
  await expectMergeFailure(
    makeLanding("landing-with-docs", { docs: true }),
    makeDocs("docs-for-landing-docs"),
    "must not contain protected path",
  );
  await expectMergeFailure(
    makeLanding("landing-for-no-docs"),
    makeDocs("no-protected-docs", false),
    "has no protected 'docs' subtree",
  );

  console.log(
    "Package merge preserves docs and rejects all protected-boundary violations.",
  );
} finally {
  await rm(fixtureRoot, { recursive: true, force: true });
}

async function expectMergeFailure(landing, docs, expectedMessage) {
  await expectFailure(
    async () => merge(await landing, await docs),
    expectedMessage,
  );
}

async function expectFailure(action, expectedMessage) {
  try {
    await action();
  } catch (error) {
    const output = `${error.stdout ?? ""}${error.stderr ?? ""}${error.message ?? ""}`;
    if (output.includes(expectedMessage)) return;
    throw new Error(
      `Package merge failed with an unexpected message; expected '${expectedMessage}', got '${output}'.`,
    );
  }
  throw new Error(`Package merge unexpectedly accepted '${expectedMessage}'.`);
}
