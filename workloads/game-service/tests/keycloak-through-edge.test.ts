/**
 * Identity slice 2: a member's real sign-in reaches game-service through the edge. The member
 * realm's Keycloak (`workloads/identity-provider`) issues the tokens; the .NET edge in Terminate
 * mode pins that realm's published key set, its issuer and the game audience, exactly as it pins the
 * test issuer in `mcp-through-edge.test.ts`; the real game-service runs on a real PostgreSQL schema.
 * Nothing here signs a token: every bearer below came out of a passkey sign-in.
 *
 * The tokens come from `workloads/identity-provider/tests/support/mint.ts`, run as a child process,
 * so the Keycloak client code keeps one owner and this file owns only the composition.
 *
 * Prerequisites: the identity-provider compose stack up and configured
 * (`npm run cli -- configure` there), `KC_BOOTSTRAP_ADMIN_PASSWORD` set, the edge built, and
 * PostgreSQL at `GAME_SERVICE_TEST_DATABASE_URL`. Without `KC_BOOTSTRAP_ADMIN_PASSWORD` the suite
 * skips locally; under CI (`CI` set) it fails instead.
 */
import { execFile } from "node:child_process";
import { fileURLToPath } from "node:url";
import { promisify } from "node:util";

import { afterAll, beforeAll, describe, expect, it } from "vitest";

import { verifyHandoff } from "../src/handoff.js";
import { TEST_CONNECTION_STRING, createTestSchema } from "./support/database.js";
import type { TestSchema } from "./support/database.js";
import { ASSERTION_HEADER, TEST_HANDOFF } from "./support/handoff.js";
import { spawnTerminateEdge } from "./support/hosted-edge-terminate.js";
import type { TerminateEdge } from "./support/hosted-edge-terminate.js";
import type { RecordedRequest } from "./support/recording-proxy.js";
import { CAMPAIGN_ID } from "./support/real-workload.js";

const MINT = fileURLToPath(new URL("../../identity-provider/tests/support/mint.ts", import.meta.url));
const PREFIX = `t2-${Date.now().toString(36)}`;
const CALL_TOOL = "/mcp/call-tool";

interface Minted {
  issuer: string;
  jwks: string;
  gameAudience: string;
  subject: string;
  game: string;
  adventures: string;
}

const configured = Boolean(process.env["KC_BOOTSTRAP_ADMIN_PASSWORD"]);
if (!configured && process.env["CI"]) {
  throw new Error("CI run without KC_BOOTSTRAP_ADMIN_PASSWORD: the Keycloak-through-edge proof cannot be skipped in CI");
}

const run = promisify(execFile);
const mint = async (command: "mint" | "remove"): Promise<string> =>
  (await run(process.execPath, [MINT, command, PREFIX], { env: process.env, timeout: 60_000 })).stdout;

async function callTool(base: string, name: string, args: unknown, token?: string): Promise<{ status: number; body: Record<string, unknown> }> {
  const response = await fetch(`${base}${CALL_TOOL}`, {
    method: "POST",
    headers: { "content-type": "application/json", ...(token ? { authorization: `Bearer ${token}` } : {}) },
    body: JSON.stringify({ name, arguments: args }),
  });
  return { status: response.status, body: JSON.parse(await response.text()) as Record<string, unknown> };
}

function headerValues(request: RecordedRequest, name: string): string[] {
  const values: string[] = [];
  for (let index = 0; index < request.rawHeaders.length; index += 2) {
    if (request.rawHeaders[index]!.toLowerCase() === name) values.push(request.rawHeaders[index + 1]!);
  }
  return values;
}

describe.skipIf(!configured)("identity slice 2: a Keycloak member token through the edge", () => {
  let minted: Minted;
  let schema: TestSchema;
  let edge: TerminateEdge;

  beforeAll(async () => {
    minted = JSON.parse(await mint("mint")) as Minted;
    schema = await createTestSchema();
    edge = await spawnTerminateEdge({ connectionString: TEST_CONNECTION_STRING, schema: schema.schema }, undefined, {
      Platform__Identity__Bearer__game__Issuer: minted.issuer,
      Platform__Identity__Bearer__game__SigningKeys: minted.jwks,
      Platform__Identity__Bearer__game__Audiences__0: minted.gameAudience,
      Platform__Identity__Bearer__game__Algorithms__0: "ES256",
    });
  }, 120_000);

  afterAll(async () => {
    await edge?.stop();
    await schema?.drop();
    if (configured) await mint("remove");
  });

  let sessionId = "";

  it("a game-cli token from a passkey sign-in starts a game and acts on it", async () => {
    const before = edge.tap.requests.length;
    const created = await callTool(edge.baseAddress, "start_game", { campaignId: CAMPAIGN_ID }, minted.game);
    expect(created.status, JSON.stringify(created.body)).toBe(200);
    sessionId = created.body["sessionId"] as string;
    expect(sessionId.length).toBeGreaterThan(0);

    const acted = await callTool(edge.baseAddress, "choose", { sessionId, actionId: "advance_ticks", params: { ticks: 1 } }, minted.game);
    expect(acted.status, JSON.stringify(acted.body)).toBe(200);
    expect(edge.tap.requests.length - before).toBe(2);
  });

  it("the workload saw the member as the realm's issuer and the token's subject, and never the bearer", () => {
    const played = edge.tap.requests.slice(-2);
    for (const request of played) {
      expect(headerValues(request, "authorization")).toEqual([]);
      const assertions = headerValues(request, ASSERTION_HEADER);
      expect(assertions).toHaveLength(1);
      const verified = verifyHandoff(TEST_HANDOFF, { method: request.method, pathAndQuery: request.pathAndQuery, assertion: assertions[0] }, new Date());
      expect(verified.ok).toBe(true);
      if (!verified.ok) return;
      expect(verified.value).toEqual({ issuer: minted.issuer, subject: minted.subject });
    }
  });

  it("the same member's adventures token is refused at the edge and the workload sees nothing", async () => {
    const before = edge.tap.requests.length;
    const answer = await callTool(edge.baseAddress, "get_scene", { sessionId }, minted.adventures);
    expect(answer.status, JSON.stringify(answer.body)).toBe(401);
    expect(edge.tap.requests.length).toBe(before);
  });

  it("the game-cli token still reads the session, so the refusal above was the audience", async () => {
    const own = await callTool(edge.baseAddress, "get_scene", { sessionId }, minted.game);
    expect(own.status, JSON.stringify(own.body)).toBe(200);
  });
});
