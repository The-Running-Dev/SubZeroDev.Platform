/**
 * G3 S3 — a signed-in player plays through the edge, on the real composition: the .NET game-edge in
 * Terminate mode under the AuthenticatedTransport profile, the real game-service on a real PostgreSQL
 * schema, a test issuer whose public keys the edge pins, and a recording tap between edge and
 * workload. The in-process edge tests (`TerminateModeTests`) keep their fake workload; nothing here
 * stands in for either end.
 *
 * The edge must be built first (`dotnet build workloads/game-edge -c Release`, or point
 * `GAME_EDGE_DLL` at a built `SubZeroDev.Platform.GameEdge.dll`), and PostgreSQL must be reachable at
 * `GAME_SERVICE_TEST_DATABASE_URL`. A missing prerequisite fails the suite; it never skips it.
 */
import { afterAll, beforeAll, describe, expect, it } from "vitest";
import { readFileSync } from "node:fs";
import { connect } from "node:net";

import { verifyHandoff } from "../src/handoff.js";
import { startConnectionCounter } from "./support/connection-counter.js";
import { TEST_CONNECTION_STRING, createTestSchema } from "./support/database.js";
import type { TestSchema } from "./support/database.js";
import { ASSERTION_HEADER, TEST_HANDOFF } from "./support/handoff.js";
import { spawnTerminateEdge, startEdgeExpectingExit } from "./support/hosted-edge-terminate.js";
import type { TerminateEdge } from "./support/hosted-edge-terminate.js";
import type { RecordedRequest } from "./support/recording-proxy.js";
import { CAMPAIGN_ID } from "./support/real-workload.js";
import { TOKEN_ISSUER, createTestIssuer, signToken } from "./support/test-issuer.js";
import { readCollectedSpans, startCollector } from "./support/otel-collector.js";

const PLAYER_A = "player-a-7f3c";
const PLAYER_B = "player-b-91d2";

let schema: TestSchema;
let edge: TerminateEdge;

/** Every secret a request in this file sent or received, for S3.7's scan. */
const secrets = new Set<string>();

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

function token(subject: string): string {
  const value = signToken(edge.issuer, subject);
  secrets.add(value);
  return value;
}

async function call(
  operation: string,
  body: unknown,
  headers: Record<string, string>,
): Promise<Answer> {
  const response = await fetch(`${edge.baseAddress}/v1/${operation}`, {
    method: "POST",
    headers: { "content-type": "application/json", ...headers },
    body: JSON.stringify(body),
  });
  return { status: response.status, text: await response.text() };
}

const bearer = (value: string): Record<string, string> => ({ authorization: `Bearer ${value}` });

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

let sessionId = "";
/** What the workload received for A's create and act — S3.4 reads these. */
let playedRequests: RecordedRequest[] = [];

describe("S3.1 — a signed-in player creates and plays a session through the edge", () => {
  it("A's token creates a session and performs an action on it, and both succeed", async () => {
    const before = edge.tap.requests.length;
    const created = await call("create-session", { campaignId: CAMPAIGN_ID, profileId: "default" }, bearer(token(PLAYER_A)));
    expect(created.status).toBe(200);
    sessionId = json(created)["sessionId"] as string;
    expect(sessionId.length).toBeGreaterThan(0);

    const acted = await call(
      "submit-action",
      { sessionId, actionId: "advance_ticks", params: { ticks: 1 } },
      bearer(token(PLAYER_A)),
    );
    expect(acted.status).toBe(200);

    playedRequests = edge.tap.requests.slice(before);
    expect(playedRequests).toHaveLength(2);
  });
});

describe("S3.2 — another player's valid token on A's session is an unknown session", () => {
  it("B's get-view and submit-action answer 404 unknown_session, as for an id nobody minted", async () => {
    for (const [operation, body] of [
      ["get-view", (id: string) => ({ sessionId: id })],
      ["submit-action", (id: string) => ({ sessionId: id, actionId: "advance_ticks", params: { ticks: 1 } })],
    ] as const) {
      const foreign = await call(operation, body(sessionId), bearer(token(PLAYER_B)));
      const unknown = await call(operation, body("never-minted-session"), bearer(token(PLAYER_B)));
      expect(foreign.status).toBe(404);
      expect(json(foreign)["code"]).toBe("unknown_session");
      expect(unknown.status).toBe(404);
      expect(json(unknown)["code"]).toBe("unknown_session");
    }

    const own = await call("get-view", { sessionId }, bearer(token(PLAYER_A)));
    expect(own.status).toBe(200);
  });
});

describe("S3.3 — no credential, and no usable credential, never reaches the workload", () => {
  it("no Authorization is 401 CredentialRequired with a correlation, and the workload sees nothing", async () => {
    const before = edge.tap.requests.length;
    const answer = await call("get-view", { sessionId }, {});
    expect(answer.status).toBe(401);
    const body = json(answer);
    expect(body["code"]).toBe("CredentialRequired");
    expect(String(body["correlation"]).length).toBeGreaterThan(0);
    expect(edge.tap.requests.length).toBe(before);
  });

  it("a forged assertion with no Authorization is still refused before anything is forwarded", async () => {
    const before = edge.tap.requests.length;
    const answer = await call("get-view", { sessionId }, { [ASSERTION_HEADER]: "caller.forged.value" });
    expect(answer.status).toBe(401);
    expect(json(answer)["code"]).toBe("CredentialRequired");
    expect(edge.tap.requests.length).toBe(before);
  });

  const stranger = createTestIssuer();
  const cases: ReadonlyArray<{ name: string; value: () => string }> = [
    { name: "garbage", value: () => "not-a-token" },
    { name: "signed by a key the edge does not pin", value: () => signToken(stranger, PLAYER_A, { key: stranger.privateKey }) },
    { name: "from another issuer", value: () => signToken(edge.issuer, PLAYER_A, { issuer: "https://other.test" }) },
    { name: "for another audience", value: () => signToken(edge.issuer, PLAYER_A, { audience: "elsewhere.test" }) },
  ];
  it.each(cases)("an invalid bearer ($name) is 401 CredentialRejected and never reaches the workload", async ({ value }) => {
    const credential = value();
    secrets.add(credential);
    const before = edge.tap.requests.length;
    const answer = await call("get-view", { sessionId }, bearer(credential));
    expect(answer.status).toBe(401);
    expect(json(answer)["code"]).toBe("CredentialRejected");
    expect(edge.tap.requests.length).toBe(before);
  });

  it("the workload itself refuses a request that skips the edge and carries no assertion", async () => {
    const response = await fetch(`${edge.tap.baseAddress}/v1/get-view`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ sessionId }),
    });
    expect(response.status).toBeGreaterThanOrEqual(400);
    expect(response.status).toBeLessThan(500);
    await response.text();
  });
});

describe("S3.4 — the workload receives the edge's assertion and no bearer", () => {
  it("each forwarded request carries no Authorization and exactly one assertion that verifies for A", () => {
    expect(playedRequests).toHaveLength(2);
    for (const request of playedRequests) {
      expect(headerValues(request, "authorization")).toEqual([]);
      const assertions = headerValues(request, ASSERTION_HEADER);
      expect(assertions).toHaveLength(1);
      secrets.add(assertions[0]!);

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
});

describe("S3.5 — a caller-supplied assertion never reaches the workload", () => {
  it("a forged assertion beside a valid token is replaced by the edge's", async () => {
    const forged = "forged.caller.assertion";
    const before = edge.tap.requests.length;
    const answer = await call("get-view", { sessionId }, { ...bearer(token(PLAYER_A)), [ASSERTION_HEADER]: forged });
    expect(answer.status).toBe(200);

    const received = edge.tap.requests.slice(before);
    expect(received).toHaveLength(1);
    const assertions = headerValues(received[0]!, ASSERTION_HEADER);
    expect(assertions).toHaveLength(1);
    expect(assertions[0]).not.toBe(forged);
    secrets.add(assertions[0]!);
    expect(JSON.stringify(received[0]!.rawHeaders)).not.toContain(forged);

    const verified = verifyHandoff(
      TEST_HANDOFF,
      { method: received[0]!.method, pathAndQuery: received[0]!.pathAndQuery, assertion: assertions[0] },
      new Date(),
    );
    expect(verified.ok).toBe(true);
  });

  it("a forged assertion naming B, beside A's token, still acts as A", async () => {
    const stranger = createTestIssuer();
    const before = edge.tap.requests.length;
    const forged = signToken(stranger, PLAYER_B, { key: stranger.privateKey });
    const answer = await call("get-view", { sessionId }, { ...bearer(token(PLAYER_A)), [ASSERTION_HEADER]: forged });
    expect(answer.status).toBe(200);
    expect(headerValues(edge.tap.requests[before]!, ASSERTION_HEADER)).not.toContain(forged);
  });
});

describe("S3.6 — a mode with the wrong settings fails startup, naming the setting", () => {
  const SIGNING = "GameEdge__Handoff__SigningKeyPath";
  const cases: ReadonlyArray<{
    name: string;
    base: "terminate" | "passThrough";
    overrides: Record<string, string | undefined>;
    named: string;
    /** The Platform error code a profile refusal carries, as the startup error prints it (`Code: ...`). */
    code?: string;
  }> = [
    { name: "no CredentialMode", base: "terminate", overrides: { GameEdge__CredentialMode: undefined }, named: "GameEdge:CredentialMode" },
    { name: "Terminate with no handoff section", base: "terminate", overrides: {
      GameEdge__Handoff__Issuer: undefined, GameEdge__Handoff__Audience: undefined, GameEdge__Handoff__KeyId: undefined,
      [SIGNING]: undefined, GameEdge__Handoff__Lifetime: undefined,
    }, named: "GameEdge:Handoff" },
    { name: "an unreadable signing key file", base: "terminate", overrides: { [SIGNING]: "/nonexistent/handoff.pem" }, named: "GameEdge:Handoff:SigningKeyPath" },
    { name: "a lifetime under one second", base: "terminate", overrides: { GameEdge__Handoff__Lifetime: "00:00:00.500" }, named: "GameEdge:Handoff:Lifetime" },
    { name: "PassThrough with handoff settings", base: "passThrough", overrides: { GameEdge__Handoff__Issuer: "https://edge.test/handoff" }, named: "GameEdge:Handoff" },
    { name: "no pinned bearer provider", base: "terminate", overrides: {
      Platform__Identity__Bearer__game__Issuer: undefined, Platform__Identity__Bearer__game__SigningKeys: undefined,
      Platform__Identity__Bearer__game__Audiences__0: undefined, Platform__Identity__Bearer__game__Algorithms__0: undefined,
    }, named: "", code: "AuthenticationProviderRequired" },
    { name: "the Local profile (it forbids an authentication provider)", base: "terminate", overrides: { Platform__CompositionProfile: "Local" }, named: "", code: "RegistrationForbiddenByProfile" },
    { name: "the Operated profile (it requires a durable audit sink)", base: "terminate", overrides: { Platform__CompositionProfile: "Operated" }, named: "", code: "DurableAuditSinkRequired" },
    { name: "PassThrough under the AuthenticatedTransport profile (it requires an authentication provider)", base: "passThrough", overrides: { Platform__CompositionProfile: "AuthenticatedTransport" }, named: "", code: "AuthenticationProviderRequired" },
  ];

  it.each(cases)("$name", async ({ base, overrides, named, code }) => {
    const refusal = await startEdgeExpectingExit(overrides, base);
    expect(refusal.exitCode, refusal.output).not.toBeUndefined();
    expect(refusal.exitCode, refusal.output).not.toBe(0);
    if (named.length > 0) expect(refusal.output).toContain(`'${named}'`);
    if (code !== undefined) expect(refusal.output).toContain(`${code}:`);
  }, 60_000);
});

describe("Invariant 125 — Terminate mode refuses issuer discovery before it contacts anything", () => {
  const PROVIDER = "Platform__Identity__Bearer__game";
  const PINNED_ONLY = {
    [`${PROVIDER}__SigningKeys`]: undefined,
    [`${PROVIDER}__Algorithms__0`]: undefined,
  } as const;

  it("the counter sees a connection made to it, so a zero below is a real absence", async () => {
    const counter = await startConnectionCounter();
    try {
      await new Promise<void>((resolve, reject) => {
        const socket = connect(counter.port, "127.0.0.1", () => {
          socket.destroy();
          resolve();
        });
        socket.once("error", reject);
      });
      await new Promise((resolve) => setTimeout(resolve, 100));
      expect(counter.connections()).toBe(1);
    } finally {
      await counter.close();
    }
  });

  const cases: ReadonlyArray<{
    name: string;
    overrides: (discovery: string) => Record<string, string | undefined>;
    named: string;
  }> = [
    {
      name: "a provider with a discovery address and no pinned keys",
      overrides: (discovery) => ({ ...PINNED_ONLY, [`${PROVIDER}__Discovery`]: discovery }),
      named: "Platform:Identity:Bearer:game:Discovery",
    },
    {
      name: "a discovery provider beside an otherwise valid pinned provider",
      overrides: (discovery) => ({
        Platform__Identity__Bearer__remote__Issuer: "https://remote-issuer.test",
        Platform__Identity__Bearer__remote__Discovery: discovery,
        Platform__Identity__Bearer__remote__Audiences__0: "game.test",
        Platform__Identity__Bearer__remote__Algorithms__0: "ES256",
      }),
      named: "Platform:Identity:Bearer:remote:Discovery",
    },
    {
      name: "a provider with both pinned keys and a discovery address",
      overrides: (discovery) => ({ [`${PROVIDER}__Discovery`]: discovery }),
      named: "Platform:Identity:Bearer:game:Discovery",
    },
    {
      name: "a provider with no pinned keys and no discovery address",
      overrides: () => PINNED_ONLY,
      named: "Platform:Identity:Bearer:game:SigningKeys",
    },
  ];

  it.each(cases)("$name fails startup, naming the setting, and the issuer address is never connected to", async ({ overrides, named }) => {
    const counter = await startConnectionCounter();
    try {
      const refusal = await startEdgeExpectingExit(overrides(`https://127.0.0.1:${counter.port}/jwks`));
      // Read first: a refused edge has exited, so any connection its startup would have made has been
      // made; one that was not refused is killed at the deadline, after it connected.
      expect(counter.connections(), refusal.output).toBe(0);
      expect(refusal.exitCode, refusal.output).not.toBeUndefined();
      expect(refusal.exitCode, refusal.output).not.toBe(0);
      expect(refusal.output).toContain(`'${named}'`);
    } finally {
      await counter.close();
    }
  }, 60_000);
});

describe("S3.7 — neither the bearer nor the assertion is logged, traced or exported", () => {
  it("no process output contains a token or an assertion this suite sent or saw", () => {
    expect(secrets.size).toBeGreaterThan(0);
    const outputs = { edge: edge.edgeOutput(), workload: edge.workloadOutput() };
    // A scan of nothing proves nothing: the edge writes its startup and request logs here.
    expect(outputs.edge.length).toBeGreaterThan(0);
    for (const [process, output] of Object.entries(outputs)) {
      for (const secret of secrets) {
        expect(output.includes(secret), `${process} output contains a secret`).toBe(false);
        // The signature alone is enough to be a credential.
        expect(output.includes(secret.slice(secret.lastIndexOf(".") + 1)), `${process} output contains a signature`).toBe(false);
      }
    }
  });
});

/** Spans only exist off-process, so this runs its own composition against a real collector (CI
 *  provides the binary through `OTEL_COLLECTOR_BIN`), shuts it down cleanly so the exporters flush,
 *  and reads what the collector wrote. */
describe.skipIf(!process.env["OTEL_COLLECTOR_BIN"])("S3.7 — no exported span carries a token or an assertion", () => {
  it("spans from an accepted and a refused request hold neither", async () => {
    const ownSchema = await createTestSchema();
    const collector = await startCollector();
    const traced = await spawnTerminateEdge(
      { connectionString: TEST_CONNECTION_STRING, schema: ownSchema.schema },
      collector.otlpEndpoint,
    );
    const sent: string[] = [];
    try {
      const good = signToken(traced.issuer, PLAYER_A);
      sent.push(good);
      const accepted = await fetch(`${traced.baseAddress}/v1/create-session`, {
        method: "POST",
        headers: { "content-type": "application/json", authorization: `Bearer ${good}` },
        body: JSON.stringify({ campaignId: CAMPAIGN_ID, profileId: "default" }),
      });
      expect(accepted.status).toBe(200);
      const bad = "not.a.token";
      const refused = await fetch(`${traced.baseAddress}/v1/create-session`, {
        method: "POST",
        headers: { "content-type": "application/json", authorization: `Bearer ${bad}` },
        body: "{}",
      });
      expect(refused.status).toBe(401);
      for (const request of traced.tap.requests) sent.push(...headerValues(request, ASSERTION_HEADER));
      await traced.shutdownGracefully();
    } finally {
      await traced.stop();
      await collector.stop();
      await ownSchema.drop();
    }

    expect(readCollectedSpans(collector.outputPath).length).toBeGreaterThan(0);
    const exported = readFileSync(collector.outputPath, "utf8");
    expect(sent.length).toBeGreaterThan(1);
    for (const secret of sent) expect(exported.includes(secret)).toBe(false);
  }, 90_000);
});

describe("S3.8 — the composition needs nothing beyond its own processes", () => {
  it("accepts tokens of an issuer whose host does not resolve, because the keys are pinned and never fetched", async () => {
    // https://issuer.test cannot be resolved anywhere, so a token that verifies here was verified
    // against the pinned keys. This test cannot see a connection attempt that something swallowed;
    // the CI job `game-service-isolation` (build.yml) runs this whole file as a user whose every
    // non-loopback packet, and every DNS query, is rejected and counted, and fails if any was.
    const answer = await call("get-view", { sessionId }, bearer(token(PLAYER_A)));
    expect(answer.status).toBe(200);
  });
});
