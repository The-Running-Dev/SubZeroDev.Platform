/**
 * G3 S7.4 — makes the one Adventures checkout the proof runs against: the commit named in
 * `adventures-pin.json`, with its `engine` submodule built and its server's dependencies installed,
 * cloned from the repository itself rather than from anything on this machine.
 *
 * Idempotent: a checkout already prepared for the pinned commit is left alone. `ADVENTURES_DIR`
 * overrides where the checkout lives; the default is `.adventures/` beside this project.
 *
 * The steps are Adventures' own CI recipe (`.github/workflows/ci.yml`, job `server`): build the
 * engine submodule with `npm run setup`, install with `npm ci --prefix server`, and link
 * `node_modules` so `shared/` resolves the server's modules.
 */
import { execFileSync } from "node:child_process";
import { existsSync, readFileSync, symlinkSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const project = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const pin = JSON.parse(readFileSync(join(project, "adventures-pin.json"), "utf8"));
if (!/^[0-9a-f]{40}$/.test(pin.commit)) {
  throw new Error(`adventures-pin.json: "commit" must be a full 40-character commit hash, got "${pin.commit}"`);
}

const directory = resolve(process.env.ADVENTURES_DIR ?? join(project, ".adventures"));
const marker = join(directory, `.prepared-${pin.commit}`);
if (existsSync(marker)) {
  console.log(`Adventures ${pin.commit} is already prepared in ${directory}`);
  process.exit(0);
}

const git = (args, cwd = directory) =>
  execFileSync("git", args, { cwd, stdio: "inherit" });
const capture = (args, cwd = directory) =>
  execFileSync("git", args, { cwd, encoding: "utf8" }).trim();

if (!existsSync(join(directory, ".git"))) {
  console.log(`Cloning ${pin.repository} into ${directory}`);
  git(["clone", "--no-checkout", pin.repository, directory], project);
}

try {
  capture(["cat-file", "-e", `${pin.commit}^{commit}`]);
} catch {
  git(["fetch", "origin", pin.commit]);
}
git(["checkout", "--detach", "--force", pin.commit]);
git(["submodule", "update", "--init", "--recursive", "--force"]);

const head = capture(["rev-parse", "HEAD"]);
if (head !== pin.commit) {
  throw new Error(`Adventures checkout is at ${head}, the pin is ${pin.commit}`);
}

// npm is run through its own JS entry point, as Adventures' `scripts/setup-engine.mjs` does:
// `npm.cmd` cannot be spawned without a shell on Windows.
const npmCli = process.env.npm_execpath;
const npm = (args) =>
  npmCli
    ? execFileSync(process.execPath, [npmCli, ...args], { cwd: directory, stdio: "inherit" })
    : execFileSync(process.platform === "win32" ? "npm.cmd" : "npm", args, {
        cwd: directory,
        stdio: "inherit",
        shell: process.platform === "win32",
      });

npm(["run", "setup"]);
npm(["ci", "--prefix", "server"]);

const link = join(directory, "node_modules");
if (!existsSync(link)) {
  symlinkSync(join(directory, "server", "node_modules"), link, "junction");
}

writeFileSync(marker, `${pin.commit}\n`);
console.log(`Adventures ${pin.commit} is prepared in ${directory}`);
