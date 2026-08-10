/**
 * S6.1–S6.3, S6.5–S6.7 — the MCP surface: same rows, same store, same `Dispatcher`, no
 * MCP-specific path. S6.4 (the table-is-the-only-source test) lives in `startup.test.ts` beside
 * the other startup-refusal criteria it shares a fixture shape with.
 */
import { describe, expect, it } from "vitest";
import type { McpToolName } from "@subzerodev/service-contract";
import { buildMcpSurface } from "../src/mcp-surface.js";
import { createDispatcher } from "../src/dispatch.js";
import { contract, bodyText, mcpSurfaceOver, post, recordingStore, surfaceOver, throwingStore } from "./support/harness.js";
import { CAMPAIGN_ID, realStore } from "./support/real-workload.js";

describe("S6.1 — listTools() has exactly as many entries as the table has rows", () => {
  it("counts one descriptor per row, names corresponding one-to-one with mcpTool", async () => {
    const mcp = mcpSurfaceOver(await realStore());
    const tools = mcp.listTools();

    expect(tools.length).toBe(contract.operations.length);
    expect([...tools.map((tool) => tool.name as string)].sort()).toEqual(
      [...contract.operations.map((row) => row.mcpTool as string)].sort(),
    );
  });
});

describe("S6.2 — one store, addressable from either surface", () => {
  it("a session created over HTTP is readable through an MCP tool call", async () => {
    const store = await realStore();
    const http = surfaceOver(store);
    const mcp = mcpSurfaceOver(store);

    const created = await post(http, "/v1/create-session", { campaignId: CAMPAIGN_ID });
    const { sessionId } = JSON.parse(bodyText(created)) as { sessionId: string };

    const viaMcp = await mcp.callTool("get_scene" as McpToolName, { sessionId }, null);
    expect(viaMcp.kind).toBe("result");
  });

  it("and the reverse — a session created through MCP is readable over HTTP", async () => {
    const store = await realStore();
    const http = surfaceOver(store);
    const mcp = mcpSurfaceOver(store);

    const created = await mcp.callTool("start_game" as McpToolName, { campaignId: CAMPAIGN_ID }, null);
    expect(created.kind).toBe("result");
    const { sessionId } = JSON.parse((created as { kind: "result"; value: string }).value) as { sessionId: string };

    const viaHttp = await post(http, "/v1/get-scene", { sessionId });
    expect(viaHttp.status).toBe(200);
  });
});

const FIXED_SCENE = {
  gameId: "g",
  status: "active",
  body: { textKey: "k", text: "t" },
  actions: [{ id: "advance_ticks", labelKey: "l", available: true }],
  view: { gameId: "g", status: "active", kindView: {} },
};

describe("S6.3 — one operation end to end through MCP, identical to the JSON wire's for the same arguments", () => {
  it("returns a canonically-encoded result identical to the wire's for create-session and submit-action", async () => {
    const fixedSession = { sessionId: "sess-1", scene: FIXED_SCENE };
    const fixedAction = {
      ok: true,
      scene: FIXED_SCENE,
      errors: [] as unknown[],
      warnings: [] as unknown[],
      changes: [] as unknown[],
      messages: [] as unknown[],
    };
    const { store } = recordingStore({
      createSession: () => fixedSession,
      submitAction: () => fixedAction,
    } as never);

    const http = surfaceOver(store);
    const mcp = mcpSurfaceOver(store);

    const httpCreated = await post(http, "/v1/create-session", { campaignId: CAMPAIGN_ID });
    const mcpCreated = await mcp.callTool("start_game" as McpToolName, { campaignId: CAMPAIGN_ID }, null);
    expect(mcpCreated.kind).toBe("result");
    expect((mcpCreated as { kind: "result"; value: string }).value).toBe(bodyText(httpCreated));

    const httpActed = await post(http, "/v1/submit-action", {
      sessionId: "sess-1",
      actionId: "advance_ticks",
      params: { ticks: 1 },
    });
    const mcpActed = await mcp.callTool(
      "choose" as McpToolName,
      { sessionId: "sess-1", actionId: "advance_ticks", params: { ticks: 1 } },
      null,
    );
    expect(mcpActed.kind).toBe("result");
    expect((mcpActed as { kind: "result"; value: string }).value).toBe(bodyText(httpActed));
  });
});

describe("S6.5 — a request-schema violation is an error outcome and the store is never called", () => {
  it("returns malformed_payload and records no call", async () => {
    const { store, calls } = recordingStore();
    const mcp = mcpSurfaceOver(store);

    const outcome = await mcp.callTool("get_scene" as McpToolName, {}, null);

    expect(outcome.kind).toBe("error");
    expect((outcome as { kind: "error"; error: { code: string } }).error.code).toBe("malformed_payload");
    expect(calls).toEqual([]);
  });
});

describe("S6.6 — an engine error carries the same code verbatim, and a rejected action is a successful result", () => {
  it("carries unknown_session verbatim through a tool call, matching the JSON wire", async () => {
    const { store } = throwingStore("submitAction", "unknown_session");
    const http = surfaceOver(store);
    const mcp = mcpSurfaceOver(store);

    const httpResponse = await post(http, "/v1/submit-action", { sessionId: "s", actionId: "a" });
    const mcpOutcome = await mcp.callTool("choose" as McpToolName, { sessionId: "s", actionId: "a" }, null);

    expect(mcpOutcome.kind).toBe("error");
    expect((mcpOutcome as { kind: "error"; error: { code: string } }).error.code).toBe(
      (JSON.parse(bodyText(httpResponse)) as { code: string }).code,
    );
  });

  it("returns a successful result for a rejected action, not an error outcome", async () => {
    const mcp = mcpSurfaceOver(await realStore());
    const created = await mcp.callTool("start_game" as McpToolName, { campaignId: CAMPAIGN_ID }, null);
    expect(created.kind).toBe("result");
    const { sessionId } = JSON.parse((created as { kind: "result"; value: string }).value) as { sessionId: string };

    const rejected = await mcp.callTool("choose" as McpToolName, { sessionId, actionId: "no-such-action" }, null);

    expect(rejected.kind).toBe("result");
    const value = JSON.parse((rejected as { kind: "result"; value: string }).value) as { ok: boolean };
    expect(value.ok).toBe(false);
  });
});

describe("#102 — every tool outcome carries the correlation, derived the same way the JSON wire's is", () => {
  const HEX32 = /^[0-9a-f]{32}$/;

  it("adopts a well-formed traceparent's trace-id as the correlation, on a result and on an error", async () => {
    const store = await realStore();
    const mcp = mcpSurfaceOver(store);
    const traceId = "4bf92f3577b34da6a3ce929d0e0e4736";
    const traceparent = `00-${traceId}-00f067aa0ba902b7-01`;

    const result = await mcp.callTool("start_game" as McpToolName, { campaignId: CAMPAIGN_ID }, traceparent);
    expect(result.kind).toBe("result");
    expect((result as { kind: "result"; correlation: string }).correlation).toBe(traceId);

    const error = await mcp.callTool("get_scene" as McpToolName, {}, traceparent);
    expect(error.kind).toBe("error");
    expect((error as { kind: "error"; error: { correlation: string } }).error.correlation).toBe(traceId);
  });

  it("mints a fresh correlation for a malformed or absent traceparent, on a result and on an error", async () => {
    const store = await realStore();
    const mcp = mcpSurfaceOver(store);

    const result = await mcp.callTool("start_game" as McpToolName, { campaignId: CAMPAIGN_ID }, "not-a-traceparent");
    expect(result.kind).toBe("result");
    expect((result as { kind: "result"; correlation: string }).correlation).toMatch(HEX32);

    const error = await mcp.callTool("get_scene" as McpToolName, {}, null);
    expect(error.kind).toBe("error");
    expect((error as { kind: "error"; error: { correlation: string } }).error.correlation).toMatch(HEX32);
  });
});

describe("S6.7 — two rows sharing an mcpTool fail startup before binding", () => {
  it("fails with DuplicateToolName naming both rows", () => {
    const first = contract.operations[0]!;
    const second = { ...contract.operations[1]!, mcpTool: first.mcpTool };
    const built = buildMcpSurface(
      { ...contract, operations: [first, second] },
      createDispatcher(contract, recordingStore().store),
    );

    expect(built.ok).toBe(false);
    if (built.ok) return;
    expect(built.error.code).toBe("DuplicateToolName");
    const rendered = JSON.stringify(built.error);
    expect(rendered).toContain(first.operation as string);
    expect(rendered).toContain(second.operation as string);
  });
});
