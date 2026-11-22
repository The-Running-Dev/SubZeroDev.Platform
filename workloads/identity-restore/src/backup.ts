/**
 * Wraps the operator's database dump so the restore knows when the dump began. The replay window starts
 * at the dump's start, less a margin: a dump is not a point in time, and an event during the dump may or
 * may not be in it. Starting early can only re-apply a removal that the dump already holds, which is a
 * no-op, or drop a recovery-code set that was in use during the margin, which is the margin's cost.
 */
import { spawn } from "node:child_process";
import { createHash } from "node:crypto";
import { createReadStream, createWriteStream, existsSync, readFileSync, writeFileSync } from "node:fs";
import type { BackupMeta } from "./types.ts";

export const metaPath = (dumpPath: string) => `${dumpPath}.meta.json`;

/** Runs `command` with its stdout written to `outPath`. Writes the sidecar only if the command succeeds. */
export async function backup(outPath: string, command: readonly string[], now: () => number = Date.now): Promise<BackupMeta> {
  if (command.length === 0) throw new Error("backup needs the dump command after --");
  const startedAtMs = now();
  const hash = createHash("sha256");
  let bytes = 0;
  const out = createWriteStream(outPath);
  const written = new Promise<void>((resolve, reject) => { out.on("finish", resolve); out.on("error", reject); });
  const code = await new Promise<number>((resolve, reject) => {
    const p = spawn(command[0]!, command.slice(1), { stdio: ["ignore", "pipe", "inherit"] });
    p.stdout.on("data", (b: Buffer) => { hash.update(b); bytes += b.length; });
    p.stdout.pipe(out);
    p.on("error", reject);
    p.on("close", (c) => resolve(c ?? 1));
  });
  await written;
  if (code !== 0) throw new Error(`dump command exited ${code}; no backup metadata written`);
  const meta: BackupMeta = { startedAtMs, finishedAtMs: now(), sha256: hash.digest("hex"), bytes };
  writeFileSync(metaPath(outPath), JSON.stringify(meta, null, 2) + "\n");
  return meta;
}

export async function sha256File(path: string): Promise<{ sha256: string; bytes: number }> {
  const h = createHash("sha256");
  let bytes = 0;
  for await (const b of createReadStream(path)) { h.update(b as Buffer); bytes += (b as Buffer).length; }
  return { sha256: h.digest("hex"), bytes };
}

/** Reads the sidecar and checks it describes `dumpPath`. Any mismatch is fatal: the replay window would
 *  start at the wrong time. */
export async function verifiedMeta(dumpPath: string): Promise<BackupMeta> {
  const mp = metaPath(dumpPath);
  if (!existsSync(mp)) throw new Error(`no backup metadata at ${mp}: the dump was not taken with \`backup\`, so its start time is unknown`);
  const meta = JSON.parse(readFileSync(mp, "utf8")) as BackupMeta;
  for (const k of ["startedAtMs", "finishedAtMs", "bytes"] as const) if (!Number.isFinite(meta[k])) throw new Error(`backup metadata ${k} is missing`);
  const actual = await sha256File(dumpPath);
  if (actual.sha256 !== meta.sha256 || actual.bytes !== meta.bytes) throw new Error(`the dump at ${dumpPath} does not match its metadata (sha256 or size differs)`);
  if (meta.finishedAtMs < meta.startedAtMs) throw new Error("backup metadata finishes before it starts");
  return meta;
}
