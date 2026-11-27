/**
 * Slice 4 cross-boundary proof: the identity-restore workload's recorder and dump check accept this
 * workload's realm and backups. Its code runs in a child process (tests/support/restore-boundary.ts),
 * imported from workloads/identity-restore at run time; nothing from it is copied here. Runs against
 * the compose Keycloak like the other suites, so it runs in CI.
 *
 * What it shows: on this realm's event settings, with the identity-recorder client's own token, a
 * recorder window after a bootstrap is "covered" (its canary found, no configuration violation), and
 * it holds the member's sign-in. identity-restore's `verifiedMeta` accepts a dump written by `backup`.
 * It does not show a restore, retention, or anything about production (R1, D6, OD-16 stay open).
 */
import { spawn } from "node:child_process";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { createInterface } from "node:readline";
import { fileURLToPath } from "node:url";
import { afterAll, beforeAll, describe, expect, it } from "vitest";

import { backup, composeDumpCommand } from "../src/backup.ts";
import { clientSecret, describeWalk, enrol, environment, removeMembers, walk } from "./support/members.ts";
import type { Environment, Member } from "./support/members.ts";

const e = environment();
const PREFIX = `t4r-${Date.now().toString(36)}`;
const SETTLE_MS = 1500;

describe.skipIf(!e)("identity-restore's recorder and dump check against this realm (slice 4)", () => {
  const env = e as Environment;
  const dir = mkdtempSync(join(tmpdir(), "idp-boundary-"));
  let member: Member;

  beforeAll(async () => {
    member = await enrol(env, `${PREFIX}-m`);
  });

  afterAll(async () => {
    rmSync(dir, { recursive: true, force: true });
    await removeMembers(env, PREFIX);
  });

  it("a recorder window covers the member's sign-in, and the dump passes verifiedMeta", async () => {
    const dump = join(dir, "members.dump");
    await backup(dump, composeDumpCommand());
    const child = spawn(process.execPath, [fileURLToPath(new URL("./support/restore-boundary.ts", import.meta.url))], {
      env: { ...process.env, RB_BASE_URL: env.base, RB_REALM: env.realm, RB_SECRET: await clientSecret(env, "identity-recorder"), RB_DUMP: dump, RB_SETTLE_MS: String(SETTLE_MS) },
      stdio: ["pipe", "pipe", "pipe"],
    });
    let stderr = "";
    child.stderr.on("data", (b: Buffer) => { stderr += b.toString(); });
    const lines = createInterface({ input: child.stdout })[Symbol.asyncIterator]();
    const next = async () => {
      const r = await lines.next();
      if (r.done) throw new Error(`the boundary process ended early: ${stderr}`);
      return JSON.parse(r.value) as Record<string, any>;
    };

    const first = await next();
    expect(first["step"]).toBe("bootstrapped");
    expect(first["window"]?.status, JSON.stringify(first)).toBe("gap");

    const signIn = await walk(env, "game-cli", member.username, { passkey: member.passkey });
    expect(signIn.tokens, describeWalk(signIn)).toBeDefined();
    child.stdin.write("signed in\n");

    const done = await next();
    const w = done["window"];
    expect(w?.status, JSON.stringify(done)).toBe("covered");
    expect(w.reason).toBeNull();
    expect(w.canaryId).not.toBeNull();
    expect(w.fromMs).toBe(first["window"].throughMs + 1);
    const inserted = done["inserted"] as { type: string; userId: string | null; clientId: string | null }[];
    expect(inserted.some((x) => x.type === "LOGIN" && x.userId === member.id && x.clientId === "game-cli"), JSON.stringify(inserted)).toBe(true);
    expect(done["meta"]?.bytes).toBeGreaterThan(0);
  });
});
