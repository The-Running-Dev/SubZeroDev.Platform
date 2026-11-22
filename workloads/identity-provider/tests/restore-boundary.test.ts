/**
 * Slice 4 cross-boundary proof: the identity-restore recorder and dump check from PR #381 (not
 * merged) accept this workload's realm and backups. Runs only when IDENTITY_RESTORE_DIR points at a
 * checkout of that branch, for example a detached git worktree outside the repository; it is not in
 * CI, so this workload never depends on an unmerged branch. The PR's code runs in a child process
 * (tests/support/restore-boundary.ts); nothing from it is copied here.
 *
 * What it shows: on this realm's event settings, with the identity-recorder client's own token, a
 * recorder window after a bootstrap is "covered" (its canary found, no configuration violation), and
 * it holds the member's sign-in. PR #381's `verifiedMeta` accepts a dump written by `backup`.
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
import { clientSecret, describeWalk, enrol, environment, installBootstrapClient, removeBootstrapClient, removeMembers, walk } from "./support/members.ts";
import type { Environment, Member } from "./support/members.ts";

const e = environment();
const restoreDir = process.env["IDENTITY_RESTORE_DIR"];
const PREFIX = `t4r-${Date.now().toString(36)}`;
const SETTLE_MS = 1500;

describe.skipIf(!e || !restoreDir)("PR #381's recorder and dump check against this realm (slice 4, gated)", () => {
  const env = e as Environment;
  const dir = mkdtempSync(join(tmpdir(), "idp-boundary-"));
  let member: Member;

  beforeAll(async () => {
    await installBootstrapClient(env);
    try {
      member = await enrol(env, `${PREFIX}-m`);
    } finally {
      await removeBootstrapClient(env);
    }
  });

  afterAll(async () => {
    rmSync(dir, { recursive: true, force: true });
    await removeMembers(env, PREFIX);
  });

  it("a recorder window covers the member's sign-in, and the dump passes verifiedMeta", async () => {
    const dump = join(dir, "members.dump");
    await backup(dump, composeDumpCommand());
    const child = spawn(process.execPath, [fileURLToPath(new URL("./support/restore-boundary.ts", import.meta.url))], {
      env: { ...process.env, IDENTITY_RESTORE_DIR: restoreDir, RB_BASE_URL: env.base, RB_REALM: env.realm, RB_SECRET: await clientSecret(env, "identity-recorder"), RB_DUMP: dump, RB_SETTLE_MS: String(SETTLE_MS) },
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
