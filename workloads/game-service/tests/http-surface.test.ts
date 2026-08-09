/**
 * S3.1–S3.8 — the request/response cycle. One slice, so the happy path and the defined answers to
 * asking wrongly are asserted together (`design/30-slices.md`, S3).
 */
import { describe, expect, it } from "vitest";
import { contract, post, bodyJson, bodyText, recordingStore, surfaceOver, throwingStore } from "./support/harness.js";
import { CAMPAIGN_ID, realStore } from "./support/real-workload.js";

describe("S3.1 — the whole table is routed and a game plays over it", () => {
  it("creates a session, submits an action against it, and a query returns that action's scene", async () => {
    const surface = surfaceOver(await realStore());

    const created = await post(surface, "/v1/create-session", { campaignId: CAMPAIGN_ID });
    expect(created.status).toBe(200);
    const session = bodyJson(created) as unknown as {
      sessionId: string;
      scene: { actions: { id: string; available: boolean }[] };
    };
    expect(typeof session.sessionId).toBe("string");

    // Requirements are shown-but-disabled rather than hidden, so an unavailable action is an
    // ordinary member of the list — S3.6 is what covers submitting one deliberately.
    expect(session.scene.actions.some((action) => action.id === "advance_ticks" && action.available)).toBe(true);

    const acted = await post(surface, "/v1/submit-action", {
      sessionId: session.sessionId,
      actionId: "advance_ticks",
      params: { ticks: 1 },
    });
    expect(acted.status).toBe(200);
    expect(bodyJson(acted)["ok"]).toBe(true);

    const queried = await post(surface, "/v1/get-scene", { sessionId: session.sessionId });
    expect(queried.status).toBe(200);
    const actedScene = (bodyJson(acted) as { scene: unknown }).scene;
    expect(bodyJson(queried)).toEqual(actedScene);
  });

  it("routes every row in the table — no row is missing a live path", async () => {
    const surface = surfaceOver(await realStore());
    const unrouted: string[] = [];

    for (const row of contract.operations) {
      // An empty body is a schema violation for most rows; what is asserted here is only that the
      // path resolves to a row at all, which `unknown_operation` would deny.
      const response = await post(surface, `/v1/${row.httpPath}`, {});
      const code = response.status === 200 ? null : (bodyJson(response)["code"] as string);
      if (code === "unknown_operation") {
        unrouted.push(row.operation as string);
      }
    }

    expect(unrouted).toEqual([]);
  });

  it("encodes canonically — members ascending by code unit, no insignificant whitespace", async () => {
    const surface = surfaceOver(await realStore());
    const created = await post(surface, "/v1/create-session", { campaignId: CAMPAIGN_ID });
    const text = bodyText(created);

    expect(text).not.toMatch(/[\n\t]|: | :/);
    const members = [...text.matchAll(/"([^"]+)":/g)].map((m) => m[1]!);
    const topLevel = Object.keys(JSON.parse(text) as object);
    expect(topLevel).toEqual([...topLevel].sort());
    expect(members.length).toBeGreaterThan(0);
  });
});

describe("S3.2 — every response is validated against its row's closed response schema", () => {
  it("fails the request as a 500 when a handler returns an added member, and never returns it", async () => {
    const { store } = recordingStore({
      getScene: () => ({ gameId: "g", status: "active", body: {}, actions: [], view: {}, smuggled: "x" }),
    } as never);
    const surface = surfaceOver(store);

    const response = await post(surface, "/v1/get-scene", { sessionId: "s" });

    expect(response.status).toBe(500);
    expect(bodyText(response)).not.toContain("smuggled");
    expect(bodyJson(response)["code"]).toBe("internal_failure");
  });
});

describe("S3.3 — unsupported version and unknown operation share a status and differ in code", () => {
  it("returns 404 unsupported_version for /v2, and nothing else in the body", async () => {
    const response = await post(surfaceOver(recordingStore().store), "/v2/create-session", {});
    expect(response.status).toBe(404);
    expect(Object.keys(bodyJson(response)).sort()).toEqual(["code", "correlation"]);
    expect(bodyJson(response)["code"]).toBe("unsupported_version");
  });

  it("returns 404 unknown_operation for an unrouted segment, and nothing else in the body", async () => {
    const response = await post(surfaceOver(recordingStore().store), "/v1/not-an-operation", {});
    expect(response.status).toBe(404);
    expect(Object.keys(bodyJson(response)).sort()).toEqual(["code", "correlation"]);
    expect(bodyJson(response)["code"]).toBe("unknown_operation");
  });

  it("returns 404 unknown_operation for a non-POST method on an otherwise-valid route, and never calls the store", async () => {
    const { store, calls } = recordingStore();
    const surface = surfaceOver(store);
    const response = await surface.handle({
      method: "GET",
      path: "/v1/create-session",
      headers: new Map(),
      body: new TextEncoder().encode(JSON.stringify({ campaignId: CAMPAIGN_ID })),
    });

    expect(response.status).toBe(404);
    expect(bodyJson(response)["code"]).toBe("unknown_operation");
    expect(calls).toEqual([]);
  });
});

describe("S3.4 — a malformed payload is a 400 the store never sees", () => {
  it("returns 400 malformed_payload for a missing required member and never calls the store", async () => {
    const { store, calls } = recordingStore();
    const response = await post(surfaceOver(store), "/v1/get-scene", {});

    expect(response.status).toBe(400);
    expect(bodyJson(response)["code"]).toBe("malformed_payload");
    expect(calls).toEqual([]);
  });

  it("returns 400 for an undeclared member — request schemas are closed", async () => {
    const { store, calls } = recordingStore();
    const response = await post(surfaceOver(store), "/v1/get-scene", { sessionId: "s", extra: 1 });

    expect(response.status).toBe(400);
    expect(bodyJson(response)["code"]).toBe("malformed_payload");
    expect(calls).toEqual([]);
  });

  it("carries no validation detail across the wire", async () => {
    const response = await post(surfaceOver(recordingStore().store), "/v1/get-scene", {});
    expect(Object.keys(bodyJson(response)).sort()).toEqual(["code", "correlation"]);
    expect(bodyText(response)).not.toMatch(/required|sessionId|schema/i);
  });
});

describe("S3.5 — engine codes travel verbatim, and status is the mapping's answer", () => {
  const cases = [
    { method: "submitAction", operation: "submit-action", code: "unknown_session", status: 404 },
    { method: "loadGame", operation: "load-game", code: "unknown_save", status: 404 },
    { method: "createSession", operation: "create-session", code: "unknown_campaign", status: 404 },
    { method: "submitAction", operation: "submit-action", code: "invalid_state", status: 409 },
    { method: "createSession", operation: "create-session", code: "unknown_kind", status: 409 },
    { method: "loadGame", operation: "load-game", code: "save_requires_migration", status: 409 },
    { method: "loadGame", operation: "load-game", code: "migration_failed", status: 409 },
  ] as const;

  const argumentsFor: Record<string, unknown> = {
    "submit-action": { sessionId: "s", actionId: "a" },
    "load-game": { saveId: "v" },
    "create-session": { campaignId: "c" },
  };

  for (const testCase of cases) {
    it(`returns ${testCase.status} carrying ${testCase.code} verbatim`, async () => {
      const { store } = throwingStore(testCase.method, testCase.code);
      const response = await post(surfaceOver(store), `/v1/${testCase.operation}`, argumentsFor[testCase.operation]);

      expect(response.status).toBe(testCase.status);
      expect(bodyJson(response)["code"]).toBe(testCase.code);
    });
  }
});

describe("S3.6 — a rejected action is a 200, not a 4xx", () => {
  it("returns 200 carrying the store's unsuccessful result for an unknown action id", async () => {
    const surface = surfaceOver(await realStore());
    const created = await post(surface, "/v1/create-session", { campaignId: CAMPAIGN_ID });
    const { sessionId } = bodyJson(created) as unknown as { sessionId: string };

    const response = await post(surface, "/v1/submit-action", { sessionId, actionId: "no-such-action" });

    expect(response.status).toBe(200);
    expect(bodyJson(response)["ok"]).toBe(false);
    expect(Array.isArray(bodyJson(response)["errors"])).toBe(true);
  });
});

describe("S3.7 — every response carries the correlation", () => {
  const HEX32 = /^[0-9a-f]{32}$/;

  it("adopts a well-formed traceparent's trace-id as the correlation", async () => {
    const traceId = "4bf92f3577b34da6a3ce929d0e0e4736";
    const headers = new Map([["traceparent", `00-${traceId}-00f067aa0ba902b7-01`]]);
    const response = await post(surfaceOver(recordingStore().store), "/v1/not-an-operation", {}, headers);

    expect(bodyJson(response)["correlation"]).toBe(traceId);
  });

  it("mints a fresh correlation for a malformed traceparent and still returns the ordinary status", async () => {
    const surface = surfaceOver(await realStore());
    const headers = new Map([["traceparent", "not-a-traceparent"]]);
    const response = await post(surface, "/v1/create-session", { campaignId: CAMPAIGN_ID }, headers);

    expect(response.status).toBe(200);
    const correlation = response.headers.get("x-correlation-id");
    expect(correlation).toMatch(HEX32);
    expect(correlation).not.toBe("0".repeat(32));
  });

  it("carries the correlation on a success as well as on a failure", async () => {
    const surface = surfaceOver(await realStore());
    const ok = await post(surface, "/v1/create-session", { campaignId: CAMPAIGN_ID });
    const bad = await post(surface, "/v1/get-scene", {});

    expect(ok.headers.get("x-correlation-id")).toMatch(HEX32);
    expect(bodyJson(bad)["correlation"]).toMatch(HEX32);
  });
});

describe("S3.8 — a thrown handler is a 500 carrying two members and nothing else", () => {
  it("returns exactly code and correlation, with no exception text or payload content", async () => {
    const secret = "unrepeatable-payload-marker";
    const { store } = recordingStore({
      getScene: () => {
        throw new Error(`boom at ${secret}`);
      },
    } as never);

    const response = await post(surfaceOver(store), "/v1/get-scene", { sessionId: secret });

    expect(response.status).toBe(500);
    expect(Object.keys(bodyJson(response)).sort()).toEqual(["code", "correlation"]);
    expect(bodyJson(response)["code"]).toBe("internal_failure");
    expect(bodyText(response)).not.toContain(secret);
    expect(bodyText(response)).not.toMatch(/boom|Error|at /);
  });
});
