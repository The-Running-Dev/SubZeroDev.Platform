/**
 * Slice 3 proof: a signed-in member manages their own credentials and ends their session. Every
 * account action is an application-initiated action (`kc_action` on an authorization request), so
 * it runs only after a full sign-in: the members-browser flow has no cookie step, and a second
 * authorization in the same browser asks for the username and a credential again.
 *
 * Runs against the Keycloak in docker-compose.yml after `npm run cli -- configure`. Synthetic
 * members only; test secrets only.
 */
import { afterAll, beforeAll, describe, expect, it } from "vitest";

import { check } from "../src/realm.ts";
import { Authenticator } from "./support/authenticator.ts";
import { credentials, describeWalk, enrol, environment, removeMembers, walk } from "./support/members.ts";
import type { Environment, Member } from "./support/members.ts";

const e = environment();
const PREFIX = `t3-${Date.now().toString(36)}`;

const passkeys = async (env: Environment, m: Member) => (await credentials(env, m.id)).filter((c) => c.type === "webauthn-passwordless");

async function form(env: Environment, endpoint: string, fields: Record<string, string>, bearer?: string): Promise<{ status: number; body: string }> {
  const r = await fetch(`${env.issuer}/protocol/openid-connect/${endpoint}`, {
    method: "POST",
    headers: { "content-type": "application/x-www-form-urlencoded", ...(bearer ? { authorization: `Bearer ${bearer}` } : {}) },
    body: new URLSearchParams(fields),
  });
  return { status: r.status, body: await r.text() };
}
const refresh = (env: Environment, token: string) => form(env, "token", { grant_type: "refresh_token", refresh_token: token, client_id: "game-cli" });

describe.skipIf(!e)("member account actions (slice 3)", () => {
  const env = e as Environment;
  let keys: Member;
  let codes: Member;
  let other: Member;
  let session: Member;

  beforeAll(async () => {
    keys = await enrol(env, `${PREFIX}-keys`);
    codes = await enrol(env, `${PREFIX}-codes`);
    other = await enrol(env, `${PREFIX}-other`);
    session = await enrol(env, `${PREFIX}-session`);
  });

  afterAll(async () => {
    await removeMembers(env, PREFIX);
  });

  it("the realm enables only the passkey, recovery-code and delete-credential actions", async () => {
    expect([...env.def.requiredActions].sort()).toEqual(["CONFIGURE_RECOVERY_AUTHN_CODES", "delete_credential", "webauthn-register-passwordless"]);
    expect(await check(env.kc, env.def)).toEqual([]);
  });

  it("check fails when Keycloak's default UPDATE_PASSWORD action is enabled", async () => {
    const path = `/realms/${env.realm}/authentication/required-actions/UPDATE_PASSWORD`;
    const ra = await env.kc.get<Record<string, unknown>>(path);
    await env.kc.call("PUT", path, { ...ra, enabled: true });
    try {
      expect(await check(env.kc, env.def)).toContain("required action UPDATE_PASSWORD is enabled but not in the definition");
    } finally {
      await env.kc.call("PUT", path, { ...ra, enabled: false });
    }
  });

  it("a signed-in member cannot reach a password, OTP or profile page through kc_action", async () => {
    for (const action of ["UPDATE_PASSWORD", "CONFIGURE_TOTP", "UPDATE_PROFILE"]) {
      const w = await walk(env, "game-cli", session.username, { passkey: session.passkey, extra: { kc_action: action } });
      expect(w.tokens, `${action}: ${describeWalk(w)}`).toBeDefined();
      expect(w.trail.some((p) => /update-password|config-totp|update-profile/.test(p)), `${action}: ${describeWalk(w)}`).toBe(false);
    }
    expect((await credentials(env, session.id)).map((c) => c.type).sort()).toEqual(["recovery-authn-codes", "webauthn-passwordless"]);
  });

  it("a second authorization in the same browser asks for the username again: there is no SSO cookie step", async () => {
    const first = await walk(env, "game-cli", session.username, { passkey: session.passkey });
    expect(first.tokens, describeWalk(first)).toBeDefined();
    const again = await walk(env, "game-cli", undefined, { agent: first.agent });
    expect(again.tokens, describeWalk(again)).toBeUndefined();
    expect(again.status).toMatch(/^login-login-username/);
  });

  let added: Authenticator;

  it("a member adds a second passkey after signing in with the first, and the new one signs in", async () => {
    added = new Authenticator(env.base);
    const w = await walk(env, "game-cli", keys.username, { passkey: keys.passkey, register: added, extra: { kc_action: "webauthn-register-passwordless" } });
    expect(w.tokens, describeWalk(w)).toBeDefined();
    expect(w.trail.at(-1), describeWalk(w)).toBe("login-webauthn-register");
    expect((await passkeys(env, keys)).map((c) => c.userLabel).sort()).toEqual([keys.passkey.label, added.label].sort());

    const signIn = await walk(env, "game-cli", keys.username, { passkey: added });
    expect(signIn.tokens, describeWalk(signIn)).toBeDefined();
  });

  it("the delete page answered with Decline keeps the passkey", async () => {
    const target = (await passkeys(env, keys)).find((c) => c.userLabel === keys.passkey.label)!;
    const w = await walk(env, "game-cli", keys.username, { passkey: added, extra: { kc_action: `delete_credential:${target.id}` }, deletion: "decline" });
    expect(w.trail, describeWalk(w)).toContain("login-delete-credential");
    expect((await passkeys(env, keys)).map((c) => c.id)).toContain(target.id);
  });

  it("a member removes the first passkey; it is refused afterwards and the second still signs in", async () => {
    const target = (await passkeys(env, keys)).find((c) => c.userLabel === keys.passkey.label)!;
    const w = await walk(env, "game-cli", keys.username, { passkey: added, extra: { kc_action: `delete_credential:${target.id}` }, deletion: "accept" });
    expect(w.tokens, describeWalk(w)).toBeDefined();
    expect((await passkeys(env, keys)).map((c) => c.userLabel)).toEqual([added.label]);

    const removed = await walk(env, "game-cli", keys.username, { passkey: keys.passkey });
    expect(removed.tokens, describeWalk(removed)).toBeUndefined();
    const kept = await walk(env, "game-cli", keys.username, { passkey: added });
    expect(kept.tokens, describeWalk(kept)).toBeDefined();
  });

  it("a member cannot delete another member's credential by naming its id", async () => {
    const before = await credentials(env, other.id);
    const target = before.find((c) => c.type === "webauthn-passwordless")!;
    const w = await walk(env, "game-cli", keys.username, { passkey: added, extra: { kc_action: `delete_credential:${target.id}` }, deletion: "accept" });
    expect(w.trail, describeWalk(w)).not.toContain("login-delete-credential");
    expect((await credentials(env, other.id)).map((c) => c.id).sort()).toEqual(before.map((c) => c.id).sort());
    const still = await walk(env, "game-cli", other.username, { passkey: other.passkey });
    expect(still.tokens, describeWalk(still)).toBeDefined();
  });

  it("regenerating recovery codes replaces the set: an old code is refused and a new one signs in", async () => {
    const w = await walk(env, "game-cli", codes.username, { passkey: codes.passkey, extra: { kc_action: "CONFIGURE_RECOVERY_AUTHN_CODES" } });
    expect(w.tokens, describeWalk(w)).toBeDefined();
    const fresh = w.codes ?? [];
    expect(fresh.length).toBe(codes.codes.length);
    expect(fresh.some((c) => codes.codes.includes(c))).toBe(false);

    const old = await walk(env, "game-cli", codes.username, { choose: /recovery/i, code: (n) => codes.codes[n - 1]! });
    expect(old.tokens, describeWalk(old)).toBeUndefined();
    const current = await walk(env, "game-cli", codes.username, { choose: /recovery/i, code: (n) => fresh[n - 1]! });
    expect(current.tokens, describeWalk(current)).toBeDefined();
    expect((await credentials(env, codes.id)).filter((c) => c.type === "recovery-authn-codes")).toHaveLength(1);
  });

  it("signing out ends the session: its refresh token is refused", async () => {
    const w = await walk(env, "game-cli", session.username, { passkey: session.passkey });
    const out = await form(env, "logout", { client_id: "game-cli", refresh_token: w.tokens!.refresh_token! });
    expect(out.status, out.body).toBe(204);
    expect((await refresh(env, w.tokens!.refresh_token!)).status).toBe(400);
  });

  it("revoking the refresh token ends the session at Keycloak: refresh and userinfo are refused", async () => {
    const w = await walk(env, "game-cli", session.username, { passkey: session.passkey });
    const revoked = await form(env, "revoke", { client_id: "game-cli", token: w.tokens!.refresh_token!, token_type_hint: "refresh_token" });
    expect(revoked.status, revoked.body).toBe(200);
    expect((await refresh(env, w.tokens!.refresh_token!)).status).toBe(400);
    const userinfo = await fetch(`${env.issuer}/protocol/openid-connect/userinfo`, { headers: { authorization: `Bearer ${w.tokens!.access_token}` } });
    expect(userinfo.status).toBe(401);
  });

  it("a refresh token works once: reusing it is refused and ends the session", async () => {
    const w = await walk(env, "game-cli", session.username, { passkey: session.passkey });
    const first = await refresh(env, w.tokens!.refresh_token!);
    expect(first.status, first.body).toBe(200);
    const rotated = (JSON.parse(first.body) as { refresh_token: string }).refresh_token;
    const reuse = await refresh(env, w.tokens!.refresh_token!);
    expect(reuse.status).toBe(400);
    expect(reuse.body).toMatch(/reuse/i);
    expect((await refresh(env, rotated)).status).toBe(400);
  });

  // Keycloak's behaviour, pinned so that a change is noticed. Recorded, not accepted: whether a
  // member may remove their last way in belongs to the open recovery decisions (D3/D5).
  it("RECORDED: Keycloak lets a member delete their only credential, which leaves no way to sign in", async () => {
    const solo = await enrol(env, `${PREFIX}-solo`, { passkey: true });
    const [only] = await credentials(env, solo.id);
    const w = await walk(env, "game-cli", solo.username, { passkey: solo.passkey, extra: { kc_action: `delete_credential:${only!.id}` }, deletion: "accept" });
    expect(w.tokens, describeWalk(w)).toBeDefined();
    expect(await credentials(env, solo.id)).toEqual([]);
    const after = await walk(env, "game-cli", solo.username, { passkey: solo.passkey });
    expect(after.tokens, describeWalk(after)).toBeUndefined();
  });
});
