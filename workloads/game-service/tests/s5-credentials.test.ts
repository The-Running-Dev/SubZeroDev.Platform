/**
 * G3 S5 — every credential outcome is told apart, on the real composition (the .NET game-edge in
 * Terminate mode, the real game-service on a real PostgreSQL schema, a test issuer whose public keys
 * the edge pins, a recording tap between edge and workload). S5.6, a provider failure injected
 * mid-validation, needs a seam in the edge's own process and is proved in `TerminateModeTests.cs`;
 * every other criterion is here.
 *
 * Same prerequisites as `s3-edge.test.ts`: the edge is built, PostgreSQL is reachable. A missing one
 * fails the suite; it never skips it.
 */
import { afterAll, beforeAll, describe, expect, it } from "vitest";
import { request as httpRequest } from "node:http";

import { startConnectionCounter } from "./support/connection-counter.js";
import { TEST_CONNECTION_STRING, createTestSchema } from "./support/database.js";
import type { TestSchema } from "./support/database.js";
import { spawnTerminateEdge } from "./support/hosted-edge-terminate.js";
import type { TerminateEdge } from "./support/hosted-edge-terminate.js";
import { CAMPAIGN_ID } from "./support/real-workload.js";
import { TOKEN_AUDIENCE, createTestIssuer, signToken } from "./support/test-issuer.js";
import type { TestIssuer } from "./support/test-issuer.js";

const PLAYER = "player-s5-4be1";

interface Answer {
  readonly status: number;
  readonly text: string;
}

const code = (answer: Answer): unknown => (JSON.parse(answer.text) as Record<string, unknown>)["code"];

/** One `get-view` carrying `authorization` headers exactly as given (none, one or several). Node's
 *  `http` rather than `fetch`, which would merge or normalise the cases S5.3 is about. */
function rawCall(edge: TerminateEdge, authorization: readonly string[]): Promise<Answer> {
  const target = new URL(`${edge.baseAddress}/v1/get-view`);
  const body = JSON.stringify({ sessionId: "never-minted-session" });
  const headers: Array<[string, string]> = [
    // A raw header array replaces Node's defaults, and HTTP/1.1 requires Host.
    ["host", target.host],
    ["content-type", "application/json"],
    ["content-length", String(Buffer.byteLength(body))],
    ...authorization.map((value): [string, string] => ["authorization", value]),
  ];
  return new Promise((resolve, reject) => {
    const outgoing = httpRequest(
      { host: target.hostname, port: target.port, path: target.pathname, method: "POST", headers: headers.flat() },
      (incoming) => {
        const chunks: Buffer[] = [];
        incoming.on("data", (chunk: Buffer) => chunks.push(chunk));
        incoming.on("end", () => resolve({ status: incoming.statusCode ?? 0, text: Buffer.concat(chunks).toString("utf8") }));
      },
    );
    outgoing.once("error", reject);
    outgoing.end(body);
  });
}

async function call(edge: TerminateEdge, operation: string, body: unknown, token: string): Promise<Answer> {
  const response = await fetch(`${edge.baseAddress}/v1/${operation}`, {
    method: "POST",
    headers: { "content-type": "application/json", authorization: `Bearer ${token}` },
    body: JSON.stringify(body),
  });
  return { status: response.status, text: await response.text() };
}

/** An edge on its own schema, torn down by the caller. */
async function openEdge(
  overrides: Record<string, string> = {},
): Promise<{ edge: TerminateEdge; schema: TestSchema; close: () => Promise<void> }> {
  const schema = await createTestSchema();
  const edge = await spawnTerminateEdge(
    { connectionString: TEST_CONNECTION_STRING, schema: schema.schema },
    undefined,
    overrides,
  );
  return {
    edge,
    schema,
    close: async () => {
      await edge.stop();
      await schema.drop();
    },
  };
}

describe("S5.1–S5.3, S5.8 — a refused credential is told apart and never reaches the workload", () => {
  let open: Awaited<ReturnType<typeof openEdge>>;
  let edge: TerminateEdge;
  let issuer: TestIssuer;

  beforeAll(async () => {
    open = await openEdge();
    edge = open.edge;
    issuer = edge.issuer;
  }, 90_000);
  afterAll(async () => open?.close());

  /** Every case below ends here, so S5.8 is checked once for all of S5.1–S5.3 rather than in each. */
  async function refusedWithoutForwarding(send: () => Promise<Answer>, expected: string): Promise<void> {
    const before = edge.tap.requests.length;
    const answer = await send();
    expect(answer.status, answer.text).toBe(401);
    expect(code(answer)).toBe(expected);
    expect((JSON.parse(answer.text) as Record<string, unknown>)["correlation"]).toBeTruthy();
    expect(edge.tap.requests.length).toBe(before);
  }

  it("control: a valid token is accepted and does reach the workload", async () => {
    const before = edge.tap.requests.length;
    const created = await call(edge, "create-session", { campaignId: CAMPAIGN_ID, profileId: "default" }, signToken(issuer, PLAYER));
    expect(created.status, created.text).toBe(200);
    expect(edge.tap.requests.length).toBe(before + 1);
  });

  describe("S5.1 — a key the edge does not pin", () => {
    const stranger = createTestIssuer();
    const cases: ReadonlyArray<{ name: string; token: () => string }> = [
      { name: "another key under the pinned key id", token: () => signToken(issuer, PLAYER, { key: stranger.privateKey }) },
      { name: "another key under an id the edge has never been given (a rotation not yet pinned)", token: () => signToken(issuer, PLAYER, { key: stranger.privateKey, keyId: "issuer-key-2" }) },
      { name: "the pinned key under an id the edge has never been given", token: () => signToken(issuer, PLAYER, { keyId: "issuer-key-2" }) },
    ];
    it.each(cases)("$name is 401 CredentialRejected", async ({ token }) => {
      await refusedWithoutForwarding(() => call(edge, "get-view", { sessionId: "x" }, token()), "CredentialRejected");
    });
  });

  describe("S5.2 — an expired token, and one for another audience", () => {
    const cases: ReadonlyArray<{ name: string; token: () => string }> = [
      { name: "expired well past the edge's tolerance", token: () => signToken(issuer, PLAYER, { issuedOffsetSeconds: -900, lifetimeSeconds: 300 }) },
      { name: "expired an hour ago", token: () => signToken(issuer, PLAYER, { issuedOffsetSeconds: -7200, lifetimeSeconds: 3600 }) },
      { name: "valid for another audience", token: () => signToken(issuer, PLAYER, { audience: "elsewhere.test" }) },
      { name: "valid for no audience the edge accepts, though it names a prefix of its own", token: () => signToken(issuer, PLAYER, { audience: `${TOKEN_AUDIENCE}.evil` }) },
    ];
    it.each(cases)("$name is 401 CredentialRejected", async ({ token }) => {
      await refusedWithoutForwarding(() => call(edge, "get-view", { sessionId: "x" }, token()), "CredentialRejected");
    });
  });

  describe("S5.3 — a malformed Authorization is a rejected credential, never Anonymous", () => {
    const valid = (): string => signToken(issuer, PLAYER);
    const cases: ReadonlyArray<{ name: string; headers: () => readonly string[] }> = [
      { name: "the scheme alone", headers: () => ["Bearer"] },
      { name: "the scheme and a space", headers: () => ["Bearer "] },
      { name: "a token that is not a JWT", headers: () => ["Bearer not-a-token"] },
      { name: "two tokens", headers: () => [`Bearer ${valid()} ${valid()}`] },
      { name: "a token with a space inside it", headers: () => [`Bearer ${valid().replace(".", ". ")}`] },
      { name: "a JWT with an empty signature", headers: () => [`Bearer ${valid().slice(0, valid().lastIndexOf(".") + 1)}`] },
      { name: "a JWT with its signature cut short", headers: () => [`Bearer ${valid().slice(0, -8)}`] },
      { name: "an unsigned JWT (alg none)", headers: () => [`Bearer ${Buffer.from('{"alg":"none"}').toString("base64url")}.${Buffer.from(`{"iss":"https://issuer.test","sub":"${PLAYER}","aud":"${TOKEN_AUDIENCE}"}`).toString("base64url")}.`] },
      { name: "two Authorization headers, the first valid", headers: () => [`Bearer ${valid()}`, "Bearer not-a-token"] },
      { name: "two Authorization headers, the second valid", headers: () => ["Bearer not-a-token", `Bearer ${valid()}`] },
    ];
    it.each(cases)("$name is 401 CredentialRejected", async ({ headers }) => {
      await refusedWithoutForwarding(() => rawCall(edge, headers()), "CredentialRejected");
    });

    // The bearer scheme in a form the edge does not read is a presented credential, so it is rejected.
    it.each([
      { name: "a lowercase scheme", header: () => `bearer ${valid()}` },
      { name: "a tab after the scheme", header: () => `Bearer	${valid()}` },
    ])("$name is 401 CredentialRejected", async ({ header }) => {
      await refusedWithoutForwarding(() => rawCall(edge, [header()]), "CredentialRejected");
    });

    // A scheme the edge does not take is a credential it was handed and does not accept, and the
    // tolerated outcomes are exactly the two refusals: it must never be forwarded as anonymous.
    const otherSchemes: ReadonlyArray<{ name: string; header: () => string }> = [
      { name: "Basic", header: () => `Basic ${Buffer.from("player:secret").toString("base64")}` },
      { name: "a bearer token under another scheme name", header: () => `Token ${valid()}` },
      { name: "a lowercase scheme with no space before the token", header: () => `bearer${valid()}` },
    ];
    it.each(otherSchemes)("$name is a 401 refusal and is never forwarded", async ({ header }) => {
      const before = edge.tap.requests.length;
      const answer = await rawCall(edge, [header()]);
      expect(answer.status, answer.text).toBe(401);
      expect(["CredentialRejected", "CredentialRequired"]).toContain(code(answer));
      expect(edge.tap.requests.length).toBe(before);
    });
  });
});

describe("S5.7 — sign-out: a token the client discarded is good until it expires", () => {
  let open: Awaited<ReturnType<typeof openEdge>>;

  beforeAll(async () => {
    // No clock tolerance, so the token's own `exp` is the boundary the test crosses.
    open = await openEdge({ Platform__Identity__Bearer__game__ClockTolerance: "00:00:00" });
  }, 90_000);
  afterAll(async () => open?.close());

  it("succeeds repeatedly before exp and is 401 CredentialRejected after it", async () => {
    const { edge } = open;
    const kept = signToken(edge.issuer, PLAYER, { lifetimeSeconds: 10 });

    for (let attempt = 0; attempt < 2; attempt += 1) {
      const answer = await call(edge, "create-session", { campaignId: CAMPAIGN_ID, profileId: "default" }, kept);
      expect(answer.status, answer.text).toBe(200);
    }

    // The edge has no sign-out and no introspection: nothing the client does with its copy of the
    // token changes what the edge answers. Only the clock does.
    await new Promise((resolve) => setTimeout(resolve, 11_500));

    const before = edge.tap.requests.length;
    const after = await call(edge, "create-session", { campaignId: CAMPAIGN_ID, profileId: "default" }, kept);
    expect(after.status).toBe(401);
    expect(code(after)).toBe("CredentialRejected");
    expect(edge.tap.requests.length).toBe(before);
  }, 60_000);
});

describe("S5.9 — with the issuer stopped, valid tokens keep succeeding and the edge contacts only game-service", () => {
  it("never connects to the issuer's address, live or stopped, and still verifies tokens", async () => {
    // The issuer is a listener the edge is configured to know by address. Tokens carry that address as
    // `iss`, so the edge would reach it only by fetching keys, which it must never do.
    const counter = await startConnectionCounter();
    const issuerAddress = `http://127.0.0.1:${counter.port}`;
    const open = await openEdge({ Platform__Identity__Bearer__game__Issuer: issuerAddress });
    try {
      const { edge } = open;
      const token = (): string => signToken(edge.issuer, PLAYER, { issuer: issuerAddress });
      const play = async (): Promise<Answer> =>
        call(edge, "create-session", { campaignId: CAMPAIGN_ID, profileId: "default" }, token());

      // Issuer up: it answers nothing, which is what an issuer that hangs looks like. Were the edge to
      // depend on it, this request would stall; were it to contact it, the counter would show it.
      const whileUp = await play();
      expect(whileUp.status, whileUp.text).toBe(200);
      expect(counter.connections()).toBe(0);

      // Issuer stopped: nothing listens at that address any more.
      await counter.close();
      const whileStopped = await play();
      expect(whileStopped.status, whileStopped.text).toBe(200);

      // A bad token is still refused, not waved through for want of an issuer to ask.
      const rejected = await call(edge, "get-view", { sessionId: "x" }, "not-a-token");
      expect(rejected.status).toBe(401);
      expect(code(rejected)).toBe("CredentialRejected");

      // Everything the workload received came through the tap, which is the only address the edge was
      // given besides its own. The CI job `game-service-isolation` runs this file as a user whose every
      // non-loopback packet and DNS query is rejected and counted, and fails if any was.
      expect(edge.tap.requests.length).toBeGreaterThanOrEqual(2);
    } finally {
      await counter.close();
      await open.close();
    }
  }, 90_000);
});
