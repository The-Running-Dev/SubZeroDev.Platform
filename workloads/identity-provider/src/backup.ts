/**
 * A backup of the member database: `pg_dump -Fc` of Keycloak's PostgreSQL, and beside it
 * `<dump>.meta.json` holding `startedAtMs`, `finishedAtMs`, `sha256` and `bytes`.
 *
 * The sidecar is the format the identity-restore workload reads (`BackupMeta` and `verifiedMeta` on
 * the PR #381 branch, which is not merged). Its restore replays recorded events from `startedAtMs`,
 * so `startedAtMs` is read before the dump command starts and can never be later than the snapshot
 * pg_dump takes. Host and database clocks are assumed to agree within the restore's margin.
 *
 * The dump is a secret. It holds the realm's private signing keys, client secrets and every member's
 * credentials. So `backup` writes only outside the repository, never overwrites a file, creates the
 * dump and the sidecar with mode 0600 (Windows ignores the mode), and never prints the dump.
 */
import { spawn, spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import { closeSync, existsSync, openSync, realpathSync, unlinkSync, writeSync } from "node:fs";
import { dirname, isAbsolute, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";

export interface BackupMeta {
  readonly startedAtMs: number;
  readonly finishedAtMs: number;
  readonly sha256: string;
  readonly bytes: number;
}

export const metaPath = (dump: string): string => `${dump}.meta.json`;

const REPOSITORY = resolve(fileURLToPath(new URL("../../../", import.meta.url)));

/** True when `path`, or the nearest directory of it that exists, lies inside this repository. */
export function insideRepository(path: string): boolean {
  let at = resolve(path);
  while (!existsSync(at) && dirname(at) !== at) at = dirname(at);
  const real = existsSync(at) ? realpathSync(at) : at;
  const tail = relative(resolve(at), resolve(path));
  const rel = relative(realpathSync(REPOSITORY), resolve(real, tail));
  return rel === "" || (!rel.startsWith("..") && !isAbsolute(rel));
}

/** The id of the running `db` container in compose project `project`. */
export function composeDb(project = process.env["IDP_COMPOSE_PROJECT"] ?? "identity-provider"): string {
  const ps = spawnSync("docker", ["ps", "-q", "--filter", `label=com.docker.compose.project=${project}`, "--filter", "label=com.docker.compose.service=db"], { encoding: "utf8" });
  const id = ps.stdout?.trim().split(/\s+/)[0];
  if (ps.status !== 0 || !id) throw new Error(`no running db container in compose project ${project}${ps.stderr ? `: ${ps.stderr.trim()}` : ""}`);
  return id;
}

/** The dump command for the compose database: pg_dump inside its `db` container. */
export const composeDumpCommand = (container = composeDb()): string[] =>
  ["docker", "exec", "-i", container, "pg_dump", "--format=custom", "--username=keycloak", "--dbname=keycloak"];

/**
 * Runs `command`, writes its stdout to `out`, and only when the command succeeds writes the sidecar.
 * A failed command leaves neither file behind.
 */
export async function backup(out: string, command: readonly string[], now: () => number = Date.now): Promise<BackupMeta> {
  const dump = resolve(out);
  if (insideRepository(dump)) throw new Error(`refusing to write a backup inside the repository (${dump}): a dump holds the realm's private keys and every member's credentials`);
  if (existsSync(metaPath(dump))) throw new Error(`${metaPath(dump)} already exists; backup never overwrites`);
  const [program, ...args] = command;
  if (!program) throw new Error("no dump command");

  const fd = openSync(dump, "wx", 0o600); // throws when the dump already exists
  const hash = createHash("sha256");
  let bytes = 0;
  const startedAtMs = now();
  const failure = await new Promise<string | undefined>((done) => {
    const child = spawn(program, args, { stdio: ["ignore", "pipe", "pipe"] });
    let stderr = "";
    child.stdout.on("data", (chunk: Buffer) => {
      hash.update(chunk);
      bytes += chunk.length;
      for (let off = 0; off < chunk.length;) off += writeSync(fd, chunk, off);
    });
    child.stderr.on("data", (chunk: Buffer) => { stderr = (stderr + chunk.toString("utf8")).slice(-2000); });
    child.on("error", (e) => done(e.message));
    child.on("close", (code) => done(code === 0 ? undefined : `exited with ${code}: ${stderr.trim()}`));
  });
  const finishedAtMs = now();
  closeSync(fd);
  if (failure || bytes === 0) {
    unlinkSync(dump);
    throw new Error(`the dump command failed, so no backup was written: ${failure ?? "it wrote nothing"}`);
  }
  const meta: BackupMeta = { startedAtMs, finishedAtMs, sha256: hash.digest("hex"), bytes };
  const mfd = openSync(metaPath(dump), "wx", 0o600);
  writeSync(mfd, JSON.stringify(meta, null, 2) + "\n");
  closeSync(mfd);
  return meta;
}
