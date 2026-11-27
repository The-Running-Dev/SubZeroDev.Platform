/**
 * Slice 1 proof: a member signs in to the member realm with a passkey through `game-cli` and gets
 * an ES256 access token for the game audience; recovery codes are the only other way in; a
 * username alone gets nothing. Runs against the Keycloak in docker-compose.yml after
 * `npm run cli -- configure`. Synthetic members only; test secrets only.
 */
import { afterAll, beforeAll, describe, expect, it } from "vitest";

import { check, publishedKeys } from "../src/realm.ts";
import { Authenticator } from "./support/authenticator.ts";
import { ADVENTURES_CALLBACK, clientSecret, credentials, decode, describeWalk, enrol, environment, installOverrideClient, removeClient, removeMembers, walk } from "./support/members.ts";
import type { Environment, Member } from "./support/members.ts";

const e = environment();
const PREFIX = `t1-${Date.now().toString(36)}`;
const OVERRIDE = `${PREFIX}-override`;

describe.skipIf(!e)("member sign-in (slice 1)", () => {
  const env = e as Environment;
  let member: Member;

  beforeAll(async () => {
    member = await enrol(env, `${PREFIX}-m`);
  });

  afterAll(async () => {
    await removeMembers(env, PREFIX);
    await removeClient(env, OVERRIDE);
  });

  it("check passes on the configured realm, and names a client that overrides the browser flow", async () => {
    expect(await check(env.kc, env.def)).toEqual([]);
    await installOverrideClient(env, OVERRIDE);
    try {
      expect((await check(env.kc, env.def)).some((p) => p.includes(OVERRIDE))).toBe(true);
    } finally {
      await removeClient(env, OVERRIDE);
    }
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

  it("a sign-in through adventures yields a token for the adventures audience and never the game one", async () => {
    const w = await walk(env, "adventures", member.username, {
      passkey: member.passkey,
      client: { secret: await clientSecret(env, "adventures"), redirect: ADVENTURES_CALLBACK },
    });
    expect(w.tokens, describeWalk(w)).toBeDefined();
    const { payload } = decode(w.tokens!.access_token);
    const aud = ([] as unknown[]).concat(payload["aud"]);
    expect(aud).toContain("https://adventures.identity-provider.test/");
    expect(aud).not.toContain(env.def.gameAudience);
    expect(payload["azp"]).toBe("adventures");
  });

  it("adventures' code is refused without its secret", async () => {
    const w = await walk(env, "adventures", member.username, {
      passkey: member.passkey,
      client: { secret: "not-the-secret", redirect: ADVENTURES_CALLBACK },
    });
    expect(w.tokens, describeWalk(w)).toBeUndefined();
    expect(w.status).toMatch(/exchange 401/);
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
});
