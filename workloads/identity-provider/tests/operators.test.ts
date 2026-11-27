/**
 * The operators realm (decided 2026-11-27): the people who run the platform are enrolled by
 * invitation into a realm of their own, sign in with a passkey or a recovery code and nothing else,
 * and reach the Dashboard through its own confidential client. Nothing crosses between the realms:
 * an operator is not a member, a member cannot sign in here, and no operators client is a game
 * client. The realm is configured by `npm run cli -- configure` with the members realm. Synthetic
 * accounts only; test secrets only.
 */
import { afterAll, beforeAll, describe, expect, it } from "vitest";

import { findMember } from "../src/members.ts";
import { check, configure, publishedKeys } from "../src/realm.ts";
import { Authenticator } from "./support/authenticator.ts";
import { clientSecret, decode, describeWalk, enrol, environment, removeMembers, walk } from "./support/members.ts";
import type { Environment, Member } from "./support/members.ts";

const ops = environment(process.env, "operators");
const members = environment(process.env, "members");
const PREFIX = `t5-${Date.now().toString(36)}`;

describe.skipIf(!ops || !members)("the operators realm", () => {
  const env = ops as Environment;
  const membersEnv = members as Environment;
  const redirect = (env.def.clients.find((c) => c.clientId === "dashboard")!["redirectUris"] as string[])[0]!;
  const dashboard = async () => ({ secret: await clientSecret(env, "dashboard"), redirect });
  let operator: Member;

  beforeAll(async () => {
    operator = await enrol(env, `${PREFIX}-op`);
  });

  afterAll(async () => {
    await removeMembers(env, PREFIX);
    await removeMembers(membersEnv, PREFIX);
  });

  it("check passes on the configured realm", async () => {
    expect(await check(env.kc, env.def)).toEqual([]);
  });

  it("an invited operator signs in to the Dashboard with a passkey, on the operators issuer", async () => {
    const w = await walk(env, "dashboard", operator.username, { passkey: operator.passkey, client: await dashboard() });
    expect(w.tokens, describeWalk(w)).toBeDefined();
    const access = decode(w.tokens!.access_token);
    expect(access.header["alg"]).toBe("ES256");
    expect((await publishedKeys(env.base, env.realm)).map((k) => k.kid)).toContain(access.header["kid"]);
    expect((await publishedKeys(membersEnv.base, membersEnv.realm)).map((k) => k.kid)).not.toContain(access.header["kid"]);
    expect(access.payload["iss"]).toBe(env.issuer);
    expect(access.payload["azp"]).toBe("dashboard");
    expect(access.payload["sub"]).toBe(operator.id);
    expect(([] as unknown[]).concat(access.payload["aud"] ?? [])).not.toContain(membersEnv.def.gameAudience);

    // The Dashboard reads the ID token: its subject and issuer, and no address.
    expect(w.tokens!.id_token).toBeDefined();
    const id = decode(w.tokens!.id_token!).payload;
    expect(id["iss"]).toBe(env.issuer);
    expect(id["sub"]).toBe(operator.id);
    expect(id["email"]).toBeUndefined();
  });

  it("a recovery code signs the operator in, once", async () => {
    let used: string | undefined;
    const w = await walk(env, "dashboard", operator.username, { choose: /recovery/i, code: (n) => (used = operator.codes[n - 1]!), client: await dashboard() });
    expect(w.tokens, describeWalk(w)).toBeDefined();
    const again = await walk(env, "dashboard", operator.username, { choose: /recovery/i, code: () => used!, client: await dashboard() });
    expect(again.tokens, describeWalk(again)).toBeUndefined();
  });

  it("the Dashboard's code is refused without its secret", async () => {
    const w = await walk(env, "dashboard", operator.username, { passkey: operator.passkey, client: { secret: "not-the-secret", redirect } });
    expect(w.tokens, describeWalk(w)).toBeUndefined();
    expect(w.status).toMatch(/exchange 401/);
  });

  it("the Dashboard cannot ask for an offline session", async () => {
    const w = await walk(env, "dashboard", operator.username, {
      passkey: operator.passkey,
      client: await dashboard(),
      extra: { scope: "openid offline_access" },
    });
    expect(w.tokens, describeWalk(w)).toBeUndefined();
    expect(w.status).toMatch(/error=invalid_scope/);
  });

  it("nothing crosses between the realms", async () => {
    // An operator is not a member, and a member cannot sign in to the Dashboard.
    expect(await findMember(membersEnv.kc, membersEnv.realm, operator.username)).toBeUndefined();
    const member = await enrol(membersEnv, `${PREFIX}-member`);
    expect(await findMember(env.kc, env.realm, member.username)).toBeUndefined();
    const w = await walk(env, "dashboard", member.username, { passkey: member.passkey, client: await dashboard() });
    expect(w.tokens, describeWalk(w)).toBeUndefined();

    // An operator's passkey is not a member credential either.
    const asMember = await walk(membersEnv, "game-cli", operator.username, { passkey: operator.passkey });
    expect(asMember.tokens, describeWalk(asMember)).toBeUndefined();

    // No game client lives here, and the Dashboard's client does not live in members.
    expect(await env.kc.get(`/realms/${env.realm}/clients?clientId=game-cli`)).toEqual([]);
    expect(await membersEnv.kc.get(`/realms/${membersEnv.realm}/clients?clientId=dashboard`)).toEqual([]);
  });

  it("a passkey the operator does not hold is refused", async () => {
    const w = await walk(env, "dashboard", operator.username, { passkey: new Authenticator(env.base), client: await dashboard() });
    expect(w.tokens, describeWalk(w)).toBeUndefined();
  });

  it("check names a brokered identity provider", async () => {
    const path = `/realms/${env.realm}/identity-provider/instances`;
    try {
      await env.kc.call("POST", path, {
        alias: `${PREFIX}-github`,
        providerId: "github",
        enabled: true,
        config: { clientId: "test-only", clientSecret: "test-only" },
      });
      const problems = await check(env.kc, env.def);
      expect(problems.some((p) => p.includes(`identity provider ${PREFIX}-github`)), problems.join("\n")).toBe(true);
    } finally {
      await env.kc.call("DELETE", `${path}/${PREFIX}-github`);
    }
    expect(await check(env.kc, env.def)).toEqual([]);
  });

  it("check names a drifted token lifetime, and configure puts it back", async () => {
    try {
      await env.kc.call("PUT", `/realms/${env.realm}`, { accessTokenLifespan: 3600 });
      const problems = await check(env.kc, env.def);
      expect(problems.some((p) => p.includes("accessTokenLifespan")), problems.join("\n")).toBe(true);
    } finally {
      await configure(env.kc, env.def);
    }
    expect(await check(env.kc, env.def)).toEqual([]);
  });
});
