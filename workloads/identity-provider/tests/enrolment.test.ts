/**
 * Enrolment and the owner reset (decided 2026-11-27): `invite` creates the account and Keycloak
 * emails it a link that registers a passkey and recovery codes; `reset` removes every credential,
 * ends every session and offline token, and sends a new link to the same account. The links arrive at
 * the compose mail catcher. Synthetic members only; test secrets only.
 */
import { afterAll, describe, expect, it } from "vitest";

import { invite, reset } from "../src/members.ts";
import { check, configure } from "../src/realm.ts";
import { Authenticator } from "./support/authenticator.ts";
import { addressOf, credentials, decode, describeWalk, enrol, environment, followLink, removeMembers, takeLink, walk } from "./support/members.ts";
import type { Environment } from "./support/members.ts";

const e = environment();
const PREFIX = `t4-${Date.now().toString(36)}`;

const refresh = async (env: Environment, token: string): Promise<number> =>
  (
    await fetch(`${env.issuer}/protocol/openid-connect/token`, {
      method: "POST",
      headers: { "content-type": "application/x-www-form-urlencoded" },
      body: new URLSearchParams({ grant_type: "refresh_token", refresh_token: token, client_id: "game-cli" }),
    })
  ).status;

const types = async (env: Environment, id: string): Promise<string[]> => (await credentials(env, id)).map((c) => c.type).sort();

describe.skipIf(!e)("enrolment and the owner reset", () => {
  const env = e as Environment;

  afterAll(async () => {
    await removeMembers(env, PREFIX);
  });

  it("an invited member follows the emailed link to a passkey and recovery codes, and signs in", async () => {
    const username = `${PREFIX}-new`;
    const id = await invite(env.kc, env.def, username, addressOf(username));
    expect(await credentials(env, id)).toEqual([]);
    const link = await takeLink(addressOf(username));
    const passkey = new Authenticator(env.base);
    const w = await followLink(env, link, passkey);
    expect(w.enrolled, describeWalk(w)).toBe(true);
    expect(w.trail.some((p) => p.startsWith("login-webauthn-register")), describeWalk(w)).toBe(true);
    expect(w.codes?.length).toBeGreaterThan(0);
    expect(await types(env, id)).toEqual(["recovery-authn-codes", "webauthn-passwordless"]);

    const signIn = await walk(env, "game-cli", username, { passkey });
    expect(signIn.tokens, describeWalk(signIn)).toBeDefined();
    expect(decode(signIn.tokens!.access_token).payload["sub"]).toBe(id);

    // The link is spent: following it again registers nothing.
    const again = await followLink(env, link, new Authenticator(env.base));
    expect(again.enrolled, describeWalk(again)).toBe(false);
    expect(again.trail.some((p) => p.startsWith("login-webauthn-register")), describeWalk(again)).toBe(false);
    expect(await types(env, id)).toEqual(["recovery-authn-codes", "webauthn-passwordless"]);
  });

  it("invite refuses an existing username, a malformed username and a malformed address", async () => {
    const member = await enrol(env, `${PREFIX}-dup`);
    await expect(invite(env.kc, env.def, member.username, addressOf(member.username))).rejects.toThrow(/already exists/);
    await expect(invite(env.kc, env.def, `${PREFIX}-Upper`, addressOf(`${PREFIX}-x`))).rejects.toThrow(/username/);
    await expect(invite(env.kc, env.def, `${PREFIX}-addr`, "not-an-address")).rejects.toThrow(/not an email address/);
  });

  it("reset keeps the account and its subject, removes every credential, ends sessions and offline tokens, and re-enrols by link", async () => {
    const member = await enrol(env, `${PREFIX}-reset`);
    const online = await walk(env, "game-cli", member.username, { passkey: member.passkey });
    const offline = await walk(env, "game-cli", member.username, { passkey: member.passkey, extra: { scope: "openid offline_access" } });
    expect(online.tokens?.refresh_token, describeWalk(online)).toBeDefined();
    expect(offline.tokens?.refresh_token, describeWalk(offline)).toBeDefined();
    expect(decode(offline.tokens!.refresh_token!).payload["typ"]).toBe("Offline");

    const done = await reset(env.kc, env.def, member.username);
    expect(done.id).toBe(member.id);
    expect(done.credentials).toBe(2);
    expect(done.clients).toContain("game-cli");
    expect(await credentials(env, member.id)).toEqual([]);
    expect(await refresh(env, online.tokens!.refresh_token!)).toBe(400);
    expect(await refresh(env, offline.tokens!.refresh_token!)).toBe(400);
    const old = await walk(env, "game-cli", member.username, { passkey: member.passkey });
    expect(old.tokens, describeWalk(old)).toBeUndefined();
    const oldCode = await walk(env, "game-cli", member.username, { choose: /recovery/i, code: (n) => member.codes[n - 1]! });
    expect(oldCode.tokens, describeWalk(oldCode)).toBeUndefined();

    const passkey = new Authenticator(env.base);
    const w = await followLink(env, await takeLink(addressOf(member.username)), passkey);
    expect(w.enrolled, describeWalk(w)).toBe(true);
    expect(await types(env, member.id)).toEqual(["recovery-authn-codes", "webauthn-passwordless"]);
    const back = await walk(env, "game-cli", member.username, { passkey });
    expect(back.tokens, describeWalk(back)).toBeDefined();
    expect(decode(back.tokens!.access_token).payload["sub"]).toBe(member.id);
  });

  it("reset refuses a member that does not exist", async () => {
    await expect(reset(env.kc, env.def, `${PREFIX}-nobody`)).rejects.toThrow(/does not exist/);
  });

  it("check names a drifted session lifetime and a drifted mail server, and configure puts them back", async () => {
    const path = `/realms/${env.realm}`;
    try {
      await env.kc.call("PUT", path, { ssoSessionMaxLifespan: 72000, smtpServer: { ...(env.def.realm["smtpServer"] as Record<string, string>), host: "elsewhere.test" } });
      const problems = await check(env.kc, env.def);
      expect(problems.some((p) => p.includes("ssoSessionMaxLifespan")), problems.join("\n")).toBe(true);
      expect(problems.some((p) => p.includes("mail setting host")), problems.join("\n")).toBe(true);
    } finally {
      await configure(env.kc, env.def);
    }
    expect(await check(env.kc, env.def)).toEqual([]);
  });
});
