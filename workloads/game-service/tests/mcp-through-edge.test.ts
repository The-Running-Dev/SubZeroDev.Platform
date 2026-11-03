/**
 * G3 S13 — an MCP client plays through the edge, on the real composition: the .NET game-edge in
 * Terminate mode, the real game-service on a real PostgreSQL schema, a test issuer whose public keys
 * the edge pins, and a recording tap between edge and workload. The MCP client here is the wire an MCP
 * client speaks (`POST /mcp/call-tool`, `{ name, arguments }`); MCP clients authenticate at the edge
 * with the same bearer an HTTP caller sends, and there is no MCP-specific authentication.
 *
 * The edge must be built first (`dotnet build workloads/game-edge -c Release`, or point
 * `GAME_EDGE_DLL` at a built `SubZeroDev.Platform.GameEdge.dll`), and PostgreSQL must be reachable at
 * `GAME_SERVICE_TEST_DATABASE_URL`. A missing prerequisite fails the suite; it never skips it.
 *
 * S13.4 (CI runs these proofs) is not a test: this file sits in `tests/`, which the `game-service`
 * job runs in full, and the `g3-composition` job fails unless that job succeeded.
 */
import { afterAll, beforeAll, describe, expect, it } from "vitest";

import { verifyHandoff } from "../src/handoff.js";
import { TEST_CONNECTION_STRING, createTestSchema } from "./support/database.js";
import type { TestSchema } from "./support/database.js";
import { ASSERTION_HEADER, TEST_HANDOFF } from "./support/handoff.js";
import { spawnTerminateEdge } from "./support/hosted-edge-terminate.js";
import type { TerminateEdge } from "./support/hosted-edge-terminate.js";
import type { RecordedRequest } from "./support/recording-proxy.js";
import { CAMPAIGN_ID } from "./support/real-workload.js";
import { TOKEN_ISSUER, signToken } from "./support/test-issuer.js";

const PLAYER_A = "player-a-7f3c";
const PLAYER_B = "player-b-91d2";
const CALL_TOOL = "/mcp/call-tool";

let schema: TestSchema;
let edge: TerminateEdge;

beforeAll(async () => {
  schema = await createTestSchema();
  edge = await spawnTerminateEdge({ connectionString: TEST_CONNECTION_STRING, schema: schema.schema });
}, 90_000);

afterAll(async () => {
  await edge?.stop();
  await schema?.drop();
});

interface Answer {
  readonly status: number;
  readonly text: string;
}

async function callTool(
  base: string,
  name: string,
  args: unknown,
  headers: Record<string, string>,
): Promise<Answer> {
  const response = await fetch(`${base}${CALL_TOOL}`, {
    method: "POST",
    headers: { "content-type": "application/json", ...headers },
    body: JSON.stringify({ name, arguments: args }),
  });
  return { status: response.status, text: await response.text() };
}

const bearer = (subject: string): Record<string, string> => ({
  authorization: `Bearer ${signToken(edge.issuer, subject)}`,
});

function json(answer: Answer): Record<string, unknown> {
  return JSON.parse(answer.text) as Record<string, unknown>;
}

function headerValues(request: RecordedRequest, name: string): string[] {
  const values: string[] = [];
  for (let index = 0; index < request.rawHeaders.length; index += 2) {
    if (request.rawHeaders[index]!.toLowerCase() === name) values.push(request.rawHeaders[index + 1]!);
  }
  return values;
}

/** The correlation id differs per request; everything else about a refusal must not. */
function withoutCorrelation(answer: Answer): { status: number; body: Record<string, unknown> } {
  const { correlation: _correlation, ...rest } = json(answer);
  return { status: answer.status, body: rest };
}

let sessionId = "";
let playedRequests: RecordedRequest[] = [];

describe("S13.1 — an MCP client signed in as A creates a session and acts on it through the edge", () => {
  it("start_game and choose with A's token both succeed", async () => {
    const before = edge.tap.requests.length;

    const created = await callTool(edge.baseAddress, "start_game", { campaignId: CAMPAIGN_ID }, bearer(PLAYER_A));
    expect(created.status).toBe(200);
    sessionId = json(created)["sessionId"] as string;
    expect(sessionId.length).toBeGreaterThan(0);

    const acted = await callTool(
      edge.baseAddress,
      "choose",
      { sessionId, actionId: "advance_ticks", params: { ticks: 1 } },
      bearer(PLAYER_A),
    );
    expect(acted.status).toBe(200);
    expect(json(acted)["ok"]).toBe(true);

    playedRequests = edge.tap.requests.slice(before);
    expect(playedRequests).toHaveLength(2);
  });

  it("the workload received both as MCP calls with the edge's assertion and no bearer", () => {
    for (const request of playedRequests) {
      expect(request.pathAndQuery).toBe(CALL_TOOL);
      expect(headerValues(request, "authorization")).toEqual([]);
      const assertions = headerValues(request, ASSERTION_HEADER);
      expect(assertions).toHaveLength(1);

      const verified = verifyHandoff(
        TEST_HANDOFF,
        { method: request.method, pathAndQuery: request.pathAndQuery, assertion: assertions[0] },
        new Date(),
      );
      expect(verified.ok).toBe(true);
      if (!verified.ok) return;
      expect(verified.value).toEqual({ issuer: TOKEN_ISSUER, subject: PLAYER_A });
    }
  });

  it("an MCP call with no credential is refused at the edge and the workload sees nothing", async () => {
    const before = edge.tap.requests.length;
    const answer = await callTool(edge.baseAddress, "get_scene", { sessionId }, {});
    expect(answer.status).toBe(401);
    expect(json(answer)["code"]).toBe("CredentialRequired");
    expect(edge.tap.requests.length).toBe(before);
  });
});

describe("S13.2 — B's MCP call on A's session is what a never-minted id returns", () => {
  it("get_scene and choose answer B as they answer an id nobody minted", async () => {
    for (const [tool, args] of [
      ["get_scene", (id: string) => ({ sessionId: id })],
      ["choose", (id: string) => ({ sessionId: id, actionId: "advance_ticks", params: { ticks: 1 } })],
    ] as const) {
      const foreign = await callTool(edge.baseAddress, tool, args(sessionId), bearer(PLAYER_B));
      const unknown = await callTool(edge.baseAddress, tool, args("never-minted-session"), bearer(PLAYER_B));

      expect(foreign.status, tool).toBe(404);
      expect(json(foreign)["code"], tool).toBe("unknown_session");
      expect(withoutCorrelation(foreign), tool).toStrictEqual(withoutCorrelation(unknown));
    }
  });

  it("A still reads its own session, so the refusals above were ownership and not a broken session", async () => {
    const own = await callTool(edge.baseAddress, "get_scene", { sessionId }, bearer(PLAYER_A));
    expect(own.status).toBe(200);
  });
});

describe("S13.3 — game-service refuses an MCP request that skips the edge", () => {
  it("a valid bearer and no assertion, straight to the workload, is 401 assertion_refused", async () => {
    const answer = await callTool(edge.tap.baseAddress, "get_scene", { sessionId }, bearer(PLAYER_A));
    expect(answer.status).toBe(401);
    expect(json(answer)["code"]).toBe("assertion_refused");
  });

  it("tool listing straight to the workload is refused the same way", async () => {
    const response = await fetch(`${edge.tap.baseAddress}/mcp/list-tools`, {
      method: "POST",
      headers: bearer(PLAYER_A),
    });
    expect(response.status).toBe(401);
    expect(JSON.parse(await response.text())["code"]).toBe("assertion_refused");
  });
});
