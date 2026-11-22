import { appendFileSync, existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { afterEach, describe, expect, it } from "vitest";
import { backup, metaPath, verifiedMeta } from "../src/backup.ts";

const dirs: string[] = [];
const dump = () => { const d = mkdtempSync(join(tmpdir(), "idr-backup-")); dirs.push(d); return join(d, "kc.dump"); };
afterEach(() => { for (const d of dirs.splice(0)) rmSync(d, { recursive: true, force: true }); });
const node = (code: string) => [process.execPath, "-e", code];

describe("backup", () => {
  it("writes the dump and a sidecar the verifier accepts", async () => {
    const out = dump();
    let t = 1_000;
    const meta = await backup(out, node("process.stdout.write('x'.repeat(200000))"), () => (t += 500));
    expect(meta).toMatchObject({ startedAtMs: 1_500, finishedAtMs: 2_000, bytes: 200_000 });
    expect(await verifiedMeta(out)).toEqual(meta);
  });

  it("rejects a dump changed after it was taken", async () => {
    const out = dump();
    await backup(out, node("process.stdout.write('dump')"));
    appendFileSync(out, "!");
    await expect(verifiedMeta(out)).rejects.toThrow(/does not match/);
  });

  it("rejects a dump with no sidecar", async () => {
    const out = dump();
    writeFileSync(out, "dump");
    await expect(verifiedMeta(out)).rejects.toThrow(/start time is unknown/);
  });

  it("rejects a sidecar missing its start time", async () => {
    const out = dump();
    await backup(out, node("process.stdout.write('dump')"));
    const m = JSON.parse(readFileSync(metaPath(out), "utf8"));
    delete m.startedAtMs;
    writeFileSync(metaPath(out), JSON.stringify(m));
    await expect(verifiedMeta(out)).rejects.toThrow(/startedAtMs is missing/);
  });

  it("writes no sidecar when the dump command fails", async () => {
    const out = dump();
    await expect(backup(out, node("process.stdout.write('partial'); process.exit(3)"))).rejects.toThrow(/exited 3/);
    expect(existsSync(metaPath(out))).toBe(false);
  });
});
