/**
 * Slice 4 proof: `backup` writes a pg_dump of the member database with a sidecar that describes it,
 * outside the repository, without overwriting, and leaves nothing behind when the dump fails. The
 * dump restores into a scratch database that holds the members it was taken with.
 *
 * The guard tests run anywhere. The dump and restore tests need the compose stack and skip without
 * it, like the other suites (and fail under CI).
 */
import { spawnSync } from "node:child_process";
import { createHash, randomBytes } from "node:crypto";
import { closeSync, existsSync, mkdtempSync, openSync, readFileSync, readSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { afterAll, beforeAll, describe, expect, it } from "vitest";

import { backup, composeDb, composeDumpCommand, insideRepository, metaPath } from "../src/backup.ts";
import type { BackupMeta } from "../src/backup.ts";
import { enrol, environment, installBootstrapClient, removeBootstrapClient, removeMembers } from "./support/members.ts";
import type { Environment, Member } from "./support/members.ts";

const e = environment();
const PREFIX = `t4b-${Date.now().toString(36)}`;
const dir = mkdtempSync(join(tmpdir(), "idp-backup-"));
const node = (script: string) => [process.execPath, "-e", script];

afterAll(() => rmSync(dir, { recursive: true, force: true }));

const sha256 = (path: string) => createHash("sha256").update(readFileSync(path)).digest("hex");

describe("backup guards", () => {
  it("refuses a path inside the repository, even one that does not exist yet", async () => {
    const inRepo = fileURLToPath(new URL("../never/members.dump", import.meta.url));
    expect(insideRepository(inRepo)).toBe(true);
    expect(insideRepository(join(dir, "members.dump"))).toBe(false);
    await expect(backup(inRepo, node("process.stdout.write('x')"))).rejects.toThrow(/inside the repository/);
    expect(existsSync(inRepo)).toBe(false);
  });

  it("never overwrites a dump or its sidecar", async () => {
    const out = join(dir, "once.dump");
    await backup(out, node("process.stdout.write('first')"));
    await expect(backup(out, node("process.stdout.write('second')"))).rejects.toThrow(/already exists/);
    expect(readFileSync(out, "utf8")).toBe("first");
    rmSync(metaPath(out));
    await expect(backup(out, node("process.stdout.write('second')"))).rejects.toThrow(/EEXIST/);
    expect(readFileSync(out, "utf8")).toBe("first");
  });

  it("a failed or empty dump leaves neither file behind", async () => {
    for (const [name, script] of [["fails", "process.stdout.write('partial'); process.exit(3)"], ["empty", ""]] as const) {
      const out = join(dir, `${name}.dump`);
      await expect(backup(out, node(script))).rejects.toThrow(/no backup was written/);
      expect(existsSync(out), name).toBe(false);
      expect(existsSync(metaPath(out)), name).toBe(false);
    }
    const missing = join(dir, "missing.dump");
    await expect(backup(missing, ["no-such-dump-command-on-path"])).rejects.toThrow(/no backup was written/);
    expect(existsSync(missing)).toBe(false);
  });
});

describe.skipIf(!e)("backup of the compose member database (slice 4)", () => {
  const env = e as Environment;
  const scratch = `restore_${randomBytes(4).toString("hex")}`;
  const out = join(dir, "members.dump");
  let member: Member;
  let meta: BackupMeta;
  let before: number;
  let container: string;

  const psql = (db: string, sql: string) => spawnSync("docker", ["exec", "-i", container, "psql", "-U", "keycloak", "-d", db, "-tA", "-c", sql], { encoding: "utf8" });

  beforeAll(async () => {
    container = composeDb();
    await installBootstrapClient(env);
    try {
      member = await enrol(env, `${PREFIX}-m`);
    } finally {
      await removeBootstrapClient(env);
    }
    before = Date.now();
    meta = await backup(out, composeDumpCommand(container));
  });

  afterAll(async () => {
    spawnSync("docker", ["exec", "-i", container, "dropdb", "-U", "keycloak", "--if-exists", scratch]);
    await removeMembers(env, PREFIX);
  });

  it("writes a custom-format dump and a sidecar that describes it exactly", () => {
    const head = Buffer.alloc(5);
    const fd = openSync(out, "r");
    readSync(fd, head, 0, 5, 0);
    closeSync(fd);
    expect(head.toString("latin1")).toBe("PGDMP");
    const sidecar = JSON.parse(readFileSync(metaPath(out), "utf8")) as Record<string, unknown>;
    expect(Object.keys(sidecar).sort()).toEqual(["bytes", "finishedAtMs", "sha256", "startedAtMs"]);
    expect(sidecar).toEqual(meta);
    expect(meta.sha256).toBe(sha256(out));
    expect(meta.bytes).toBe(readFileSync(out).length);
    expect(meta.startedAtMs).toBeGreaterThanOrEqual(before);
    expect(meta.finishedAtMs).toBeGreaterThanOrEqual(meta.startedAtMs);
  });

  it("restores into a scratch database holding the member and the realm's signing keys", () => {
    expect(spawnSync("docker", ["exec", "-i", container, "createdb", "-U", "keycloak", scratch]).status).toBe(0);
    const restored = spawnSync("docker", ["exec", "-i", container, "pg_restore", "--no-owner", "--exit-on-error", "-U", "keycloak", "-d", scratch], { input: readFileSync(out) });
    expect(restored.status, restored.stderr?.toString()).toBe(0);
    const user = psql(scratch, `select count(*) from user_entity u join realm r on r.id = u.realm_id where r.name = '${env.realm}' and u.username = '${member.username}'`);
    expect(user.stdout.trim(), user.stderr).toBe("1");
    const creds = psql(scratch, `select string_agg(type, ',' order by type) from credential where user_id = '${member.id}'`);
    expect(creds.stdout.trim(), creds.stderr).toBe("recovery-authn-codes,webauthn-passwordless");
    const keys = psql(scratch, `select count(*) from component c join realm r on r.id = c.realm_id where r.name = '${env.realm}' and c.provider_type = 'org.keycloak.keys.KeyProvider'`);
    expect(Number(keys.stdout.trim()), keys.stderr).toBeGreaterThan(0);
  });
});
