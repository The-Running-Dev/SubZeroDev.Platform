/**
 * Slice 1 proof: a member signs in to the member realm with a passkey through `game-cli` and gets
 * an ES256 access token for the game audience; recovery codes are the only other way in; a
 * username alone gets nothing. Runs against the Keycloak in docker-compose.yml after
 * `npm run cli -- configure`. Synthetic members only; test secrets only.
 */
import { afterAll, beforeAll, describe, expect, it } from "vitest";

import { check, publishedKeys } from "../src/realm.ts";
import { Authenticator } from "./support/authenticator.ts";
import { BOOTSTRAP_CLIENT, credentials, decode, describeWalk, enrol, environment, installBootstrapClient, removeBootstrapClient, removeMembers, walk } from "./support/members.ts";
import type { Environment, Member } from "./support/members.ts";

const e = environment();
const PREFIX = `t1-${Date.now().toString(36)}`;

describe.skipIf(!e)("member sign-in (slice 1)", () => {
  const env = e as Environment;
  let member: Member;

  beforeAll(async () => {
    await installBootstrapClient(env);
    member = await enrol(env, `${PREFIX}-m`);
  });

  afterAll(async () => {
    await removeMembers(env, PREFIX);
    await removeBootstrapClient(env);
  });

  it("check names the test-only bootstrap client while it exists", async () => {
    const problems = await check(env.kc, env.def);
    expect(problems.some((p) => p.includes(BOOTSTRAP_CLIENT))).toBe(true);
  });

  it("enrolment leaves the member with a passkey and a recovery set, and no password", async () => {
    const types = (await credentials(env, member.id)).map((c) => c.type).sort();
    expect(types).toEqual(["recovery-authn-codes", "webauthn-passwordless"]);
    expect(member.codes.length).toBeGreaterThan(0);
  });

  it("a passkey sign-in through game-cli yields an ES256 token for the game audience", async () => {
    const w = await walk(env, "game-cli", member.username, { passkey: member.passkey });
    expect(w.tokens, describeWalk(w)).toBeDefined();
    const { header, payload } = decode(w.tokens!.access_token);
    expect(header["alg"]).toBe("ES256");
    const kids = (await publishedKeys(env.base, env.realm)).map((k) => k.kid);
    expect(kids).toContain(header["kid"]);
    expect(payload["iss"]).toBe(env.issuer);
    const aud = ([] as unknown[]).concat(payload["aud"]);
    expect(aud).toContain(env.def.gameAudience);
    expect(payload["azp"]).toBe("game-cli");
    expect(typeof payload["sub"]).toBe("string");
    expect(payload["email"]).toBeUndefined();
    expect(payload["preferred_username"]).toBeUndefined();
  });

  it("a recovery code signs the member in through \"try another way\", once", async () => {
    let used: string | undefined;
    const w = await walk(env, "game-cli", member.username, {
      choose: /recovery/i,
      code: (n) => (used = member.codes[n - 1]!),
    });
    expect(w.tokens, describeWalk(w)).toBeDefined();
    expect(([] as unknown[]).concat(decode(w.tokens!.access_token).payload["aud"])).toContain(env.def.gameAudience);

    const again = await walk(env, "game-cli", member.username, { choose: /recovery/i, code: () => used! });
    expect(again.tokens, describeWalk(again)).toBeUndefined();
  });

  it("a passkey the member does not hold is refused", async () => {
    const w = await walk(env, "game-cli", member.username, { passkey: new Authenticator(env.base) });
    expect(w.tokens, describeWalk(w)).toBeUndefined();
  });

  it("a username with no credential gets no token and no enrolment page", async () => {
    const bare = await enrol(env, `${PREFIX}-bare`, {});
    const w = await walk(env, "game-cli", bare.username, { passkey: new Authenticator(env.base), choose: /recovery/i, code: () => "AAAAAAAAAAAA", register: new Authenticator(env.base) });
    expect(w.tokens, describeWalk(w)).toBeUndefined();
    expect(w.trail.some((p) => /webauthn-register|recovery-authn-code-config/.test(p)), describeWalk(w)).toBe(false);
    expect((await credentials(env, bare.id)).length).toBe(0);
  });

  it("an unknown username gets no token", async () => {
    const w = await walk(env, "game-cli", `${PREFIX}-nobody`, { passkey: new Authenticator(env.base) });
    expect(w.tokens, describeWalk(w)).toBeUndefined();
  });

  it("check passes once the bootstrap client is gone", async () => {
    await removeBootstrapClient(env);
    expect(await check(env.kc, env.def)).toEqual([]);
    await installBootstrapClient(env);
  });
});
