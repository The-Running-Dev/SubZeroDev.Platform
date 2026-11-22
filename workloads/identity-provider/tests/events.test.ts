/**
 * Slice 4 proof: the realm stores the security events a restore needs, and the identity-recorder
 * client can read them and nothing else.
 *
 * The event types and the 30-day expiries are what the identity-restore recorder
 * (`workloads/identity-restore`) checks for. The expiries are test-only values; retention is not decided here.
 *
 * Runs against the Keycloak in docker-compose.yml after `npm run cli -- configure`. Synthetic
 * members only; test secrets only.
 */
import { afterAll, beforeAll, describe, expect, it } from "vitest";

import { check } from "../src/realm.ts";
import { clientSecret, credentials, describeWalk, enrol, environment, installBootstrapClient, removeBootstrapClient, removeMembers, walk } from "./support/members.ts";
import type { Environment, Member } from "./support/members.ts";

const e = environment();
const PREFIX = `t4-${Date.now().toString(36)}`;
const RECORDER = "identity-recorder";

interface UserEvent { type: string; clientId?: string; userId?: string; time: number; details?: Record<string, string> }
interface AdminEvent { operationType: string; resourceType: string; resourcePath: string; representation?: string }

/** The recorder's own client-credentials token, as the identity-restore recorder would get it. */
async function recorderToken(env: Environment): Promise<string> {
  const r = await fetch(`${env.issuer}/protocol/openid-connect/token`, {
    method: "POST",
    headers: { "content-type": "application/x-www-form-urlencoded" },
    body: new URLSearchParams({ grant_type: "client_credentials", client_id: RECORDER, client_secret: await clientSecret(env, RECORDER) }),
  });
  expect(r.status).toBe(200);
  return ((await r.json()) as { access_token: string }).access_token;
}

const asRecorder = (env: Environment, token: string, path: string, init: RequestInit = {}) =>
  fetch(`${env.base}/admin/realms/${env.realm}${path}`, { ...init, headers: { authorization: `Bearer ${token}`, "content-type": "application/json", ...init.headers } });

const eventsOf = (env: Environment, m: Member) => env.kc.get<UserEvent[]>(`/realms/${env.realm}/events?user=${m.id}&max=100`);

describe.skipIf(!e)("security events and the recorder client (slice 4)", () => {
  const env = e as Environment;
  let member: Member;

  beforeAll(async () => {
    await installBootstrapClient(env);
    try {
      member = await enrol(env, `${PREFIX}-ev`);
    } finally {
      await removeBootstrapClient(env);
    }
  });

  afterAll(async () => {
    await removeMembers(env, PREFIX);
  });

  it("the realm stores user and admin events, with details, for the defined types and expiries", async () => {
    expect(await check(env.kc, env.def)).toEqual([]);
    const config = await env.kc.get<Record<string, unknown>>(`/realms/${env.realm}/events/config`);
    expect(config["eventsEnabled"]).toBe(true);
    expect(config["adminEventsEnabled"]).toBe(true);
    expect(config["adminEventsDetailsEnabled"]).toBe(true);
    expect([...(config["enabledEventTypes"] as string[])].sort()).toEqual([...env.def.events.enabledEventTypes].sort());
    expect(String(config["eventsExpiration"])).toBe(String(env.def.events.eventsExpiration));
    const realm = await env.kc.get<{ attributes: Record<string, string> }>(`/realms/${env.realm}`);
    expect(realm.attributes["adminEventsExpiration"]).toBe(String(env.def.events.adminEventsExpiration));
  });

  it("check fails when a defined event type is no longer stored", async () => {
    const path = `/realms/${env.realm}/events/config`;
    const config = await env.kc.get<{ enabledEventTypes: string[] }>(path);
    await env.kc.call("PUT", path, { ...config, enabledEventTypes: config.enabledEventTypes.filter((t) => t !== "REMOVE_CREDENTIAL") });
    try {
      expect(await check(env.kc, env.def)).toContain("events: REMOVE_CREDENTIAL is not stored");
    } finally {
      await env.kc.call("PUT", path, config);
    }
    expect(await check(env.kc, env.def)).toEqual([]);
  });

  it("check fails when the admin-event expiry drifts", async () => {
    const path = `/realms/${env.realm}`;
    const realm = await env.kc.get<{ attributes: Record<string, string> }>(path);
    await env.kc.call("PUT", path, { realm: env.realm, attributes: { ...realm.attributes, adminEventsExpiration: "3600" } });
    try {
      expect((await check(env.kc, env.def)).some((p) => p.startsWith("events: adminEventsExpiration is 3600"))).toBe(true);
    } finally {
      await env.kc.call("PUT", path, { realm: env.realm, attributes: realm.attributes });
    }
    expect(await check(env.kc, env.def)).toEqual([]);
  });

  it("the recorder reads events, admin events, the event config and the realm, and nothing more", async () => {
    const token = await recorderToken(env);
    expect((await asRecorder(env, token, "/events/config")).status).toBe(200);
    expect((await asRecorder(env, token, "/admin-events?max=1")).status).toBe(200);
    const realm = await asRecorder(env, token, "");
    expect(realm.status).toBe(200);
    expect(((await realm.json()) as { attributes: Record<string, string> }).attributes["adminEventsExpiration"]).toBe(String(env.def.events.adminEventsExpiration));
    // Its own sign-in is the canary the recorder looks for after each window.
    const own = await asRecorder(env, token, "/events?type=CLIENT_LOGIN&client=identity-recorder&max=5");
    expect(own.status).toBe(200);
    expect(((await own.json()) as UserEvent[]).length).toBeGreaterThan(0);

    expect((await asRecorder(env, token, "/users?max=1")).status).toBe(403);
    expect((await asRecorder(env, token, "/clients?max=1")).status).toBe(403);
    expect((await asRecorder(env, token, `/users/${member.id}/credentials`)).status).toBe(403);
    const put = await asRecorder(env, token, "/events/config", { method: "PUT", body: JSON.stringify({ eventsEnabled: false }) });
    expect(put.status).toBe(403);
    expect((await env.kc.get<{ eventsEnabled: boolean }>(`/realms/${env.realm}/events/config`)).eventsEnabled).toBe(true);
  });

  it("a passkey sign-in and a credential deletion are recorded against the member", async () => {
    const signIn = await walk(env, "game-cli", member.username, { passkey: member.passkey });
    expect(signIn.tokens, describeWalk(signIn)).toBeDefined();
    const codes = (await credentials(env, member.id)).find((c) => c.type === "recovery-authn-codes");
    expect(codes).toBeDefined();
    const deletion = await walk(env, "game-cli", member.username, { passkey: member.passkey, extra: { kc_action: `delete_credential:${codes!.id}` }, deletion: "accept" });
    expect(deletion.tokens, describeWalk(deletion)).toBeDefined();
    expect((await credentials(env, member.id)).map((c) => c.type)).toEqual(["webauthn-passwordless"]);

    const events = await eventsOf(env, member);
    const passkeyLogin = events.find((x) => x.type === "LOGIN" && x.clientId === "game-cli" && x.details?.["credential_type"] === "webauthn-passwordless");
    expect(passkeyLogin, events.map((x) => x.type).join(",")).toBeDefined();
    expect(events.some((x) => x.type === "CODE_TO_TOKEN" && x.clientId === "game-cli")).toBe(true);
    const removed = events.find((x) => x.type === "REMOVE_CREDENTIAL");
    expect(removed?.details?.["credential_type"]).toBe("recovery-authn-codes");
    // Enrolment is recorded too: each credential the member set up is an UPDATE_CREDENTIAL.
    expect(events.filter((x) => x.type === "UPDATE_CREDENTIAL").map((x) => x.details?.["credential_type"]).sort()).toEqual(["recovery-authn-codes", "webauthn-passwordless"]);
  });

  it("admin events carry the representation, and a created member's representation holds no credential", async () => {
    const events = await env.kc.get<AdminEvent[]>(`/realms/${env.realm}/admin-events?resourcePath=${encodeURIComponent(`users/${member.id}`)}&operationTypes=CREATE&max=5`);
    const created = events.find((x) => x.resourceType === "USER" && x.operationType === "CREATE");
    expect(created?.representation).toBeDefined();
    const rep = JSON.parse(created!.representation!) as Record<string, unknown>;
    expect(rep["username"]).toBe(member.username);
    // enrol() creates the member with a throwaway password; Keycloak strips it from the stored event.
    expect(rep).not.toHaveProperty("credentials");
    expect(created!.representation).not.toMatch(/"(credentials|value|secretData|password)"/i);
  });
});
