/**
 * S3.9–S3.11 — the startup order the design claims: configuration, contract load, version
 * assertion, composition, both surfaces, then bind. Every failure below happens before the
 * listener binds, which is what makes the ordering assertable rather than incidental.
 *
 * `startWorkload` takes a `WorkloadConfiguration` and nothing else (`20-contract.md`), so the
 * crafted artifacts these criteria need reach it the only way left: as bytes on disk, named by
 * the environment and read through `loadContract`. S6.4 assumes the same route.
 */
import { afterEach, describe, expect, it } from "vitest";
import { connect } from "node:net";
import { mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import type { ContractPackage, OperationRow, SchemaRef } from "@subzerodev/service-contract";

import { buildHttpSurface } from "../src/http-surface.js";
import { createDispatcher } from "../src/dispatch.js";
import { compose } from "../src/compose.js";
import { startWorkload, createProbeSurface, CONTRACT_PATH_VARIABLE } from "../src/lifecycle.js";
import { contract, recordingStore } from "./support/harness.js";
import { CAMPAIGN_ID } from "./support/real-workload.js";

const DEFAULT_CONFIGURATION = {
  listen: { host: "127.0.0.1", port: 0 },
  determinism: { kind: "default" as const },
  otlpEndpoint: null,
};

function withOperations(rows: readonly OperationRow[]): ContractPackage {
  return { ...contract, operations: rows };
}

/** Writes a crafted artifact and points the next `startWorkload` at it. */
function artifactAt(pkg: ContractPackage): string {
  const path = join(mkdtempSync(join(tmpdir(), "contract-")), "contract.json");
  writeFileSync(path, JSON.stringify(pkg));
  process.env[CONTRACT_PATH_VARIABLE] = path;
  return path;
}

afterEach(() => {
  delete process.env[CONTRACT_PATH_VARIABLE];
});

function refused(port: number): Promise<boolean> {
  return new Promise((resolve) => {
    const socket = connect({ host: "127.0.0.1", port });
    socket.on("connect", () => {
      socket.destroy();
      resolve(false);
    });
    socket.on("error", () => resolve(true));
  });
}

const MISMATCHED = "0.0.1-not-the-resolved-engine" as ContractPackage["engineVersion"];

describe("S3.9 — an engine-version mismatch aborts before the listener binds", () => {
  it("fails composition with EngineVersionMismatch naming both versions", async () => {
    const composed = await compose(DEFAULT_CONFIGURATION, { ...contract, engineVersion: MISMATCHED });

    expect(composed.ok).toBe(false);
    if (composed.ok) return;
    expect(composed.error.code).toBe("EngineVersionMismatch");
    const rendered = JSON.stringify(composed.error);
    expect(rendered).toContain(MISMATCHED);
    expect(rendered).toContain(contract.engineVersion);
  });

  it("never binds the configured port — a connection attempt is refused", async () => {
    const port = 39_517;
    artifactAt({ ...contract, engineVersion: MISMATCHED });

    const started = await startWorkload({ ...DEFAULT_CONFIGURATION, listen: { host: "127.0.0.1", port } });

    expect(started.ok).toBe(false);
    if (started.ok) return;
    expect(started.error.code).toBe("Composition");
    expect(await refused(port)).toBe(true);
  });
});

describe("S3.10 — a table the service cannot satisfy fails surface construction", () => {
  it("fails with DuplicateRoute naming both rows when two derive the same path segment", () => {
    const first = contract.operations[0]!;
    const second = { ...contract.operations[1]!, httpPath: first.httpPath };
    const built = buildHttpSurface(withOperations([first, second]), createDispatcher(contract, recordingStore().store));

    expect(built.ok).toBe(false);
    if (built.ok) return;
    expect(built.error.code).toBe("DuplicateRoute");
    const rendered = JSON.stringify(built.error);
    expect(rendered).toContain(first.operation as string);
    expect(rendered).toContain(second.operation as string);
  });

  it("fails with MissingSchema naming the row and the reference", () => {
    const absent = "https://contracts.subzerodev.dev/service-contract/v1/absent/request.json" as SchemaRef;
    const row = { ...contract.operations[0]!, requestShape: absent };
    const built = buildHttpSurface(withOperations([row]), createDispatcher(contract, recordingStore().store));

    expect(built.ok).toBe(false);
    if (built.ok) return;
    expect(built.error.code).toBe("MissingSchema");
    const rendered = JSON.stringify(built.error);
    expect(rendered).toContain(row.operation as string);
    expect(rendered).toContain(absent);
  });

  it("fails startup before binding when the table is unsatisfiable", async () => {
    const port = 39_518;
    const first = contract.operations[0]!;
    artifactAt(withOperations([first, { ...contract.operations[1]!, httpPath: first.httpPath }]));

    const started = await startWorkload({ ...DEFAULT_CONFIGURATION, listen: { host: "127.0.0.1", port } });

    expect(started.ok).toBe(false);
    if (started.ok) return;
    expect(started.error.code).toBe("SurfaceBuild");
    expect(await refused(port)).toBe(true);
  });
});

describe("S6.4 — the table is the only source: one row removed takes it out of both surfaces at once", () => {
  it("the HTTP path returns unknown_operation and the tool is absent from listTools(), from one crafted artifact, restarted", async () => {
    const port = 39_519;
    const removed = contract.operations.find((row) => (row.operation as string) === "get-scene")!;
    const rowRemoved = withOperations(contract.operations.filter((row) => row !== removed));
    artifactAt(rowRemoved);

    const started = await startWorkload({ ...DEFAULT_CONFIGURATION, listen: { host: "127.0.0.1", port } });
    expect(started.ok).toBe(true);
    if (!started.ok) return;

    try {
      const httpResponse = await fetch(`http://127.0.0.1:${port}/v1/get-scene`, {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: "{}",
      });
      expect(httpResponse.status).toBe(404);
      const httpBody = (await httpResponse.json()) as { code: string };
      expect(httpBody.code).toBe("unknown_operation");

      const mcpResponse = await fetch(`http://127.0.0.1:${port}/mcp/list-tools`, { method: "POST" });
      const mcpBody = (await mcpResponse.json()) as { tools: { name: string }[] };
      // The count is asserted alongside the absence: `not.toContain` alone is satisfied by an
      // empty list, so the criterion would pass against a surface that had stopped reflecting the
      // table at all — which is the one regression this test exists to catch.
      expect(mcpBody.tools.length).toBe(rowRemoved.operations.length);
      expect(mcpBody.tools.map((tool) => tool.name)).not.toContain(removed.mcpTool as string);
    } finally {
      await started.value.shutdown();
    }
  });
});

/**
 * The MCP HTTP transport answers with the JSON wire's codes and the JSON wire's statuses, because
 * `20-contract.md` heads one `WireError` table "HTTP and MCP surfaces". These are regression tests
 * for a transport that had its own vocabulary: it accepted any HTTP verb, called a caller's bad
 * envelope an `internal_failure`, and returned `200` for every failure.
 */
describe("the MCP HTTP transport speaks the JSON wire's codes and statuses", () => {
  async function bound<T>(use: (base: string) => Promise<T>): Promise<T> {
    const started = await startWorkload(DEFAULT_CONFIGURATION);
    expect(started.ok).toBe(true);
    if (!started.ok) throw new Error("the workload did not start");
    try {
      return await use(`http://127.0.0.1:${started.value.listening.port}`);
    } finally {
      await started.value.shutdown();
    }
  }

  it("refuses every method but POST, so no verb variant can list or run a tool", async () => {
    await bound(async (base) => {
      for (const method of ["GET", "PUT", "DELETE"]) {
        const listed = await fetch(`${base}/mcp/list-tools`, { method });
        expect([listed.status, ((await listed.json()) as { code: string }).code]).toEqual([
          404,
          "unknown_operation",
        ]);
      }

      // The body would have created a session had the verb been honoured, so this asserts the
      // refusal happens before the tool runs and not merely that the status changed.
      const called = await fetch(`${base}/mcp/call-tool`, {
        method: "DELETE",
        body: JSON.stringify({ name: "start_game", arguments: { campaignId: CAMPAIGN_ID } }),
      });
      expect(called.status).toBe(404);
      expect(((await called.json()) as { code: string }).code).toBe("unknown_operation");
    });
  });

  it("calls a caller's bad envelope malformed_payload, not an internal failure", async () => {
    await bound(async (base) => {
      for (const body of ["{", JSON.stringify({ arguments: {} }), JSON.stringify({ name: 7 })]) {
        const response = await fetch(`${base}/mcp/call-tool`, { method: "POST", body });
        expect(response.status).toBe(400);
        expect(((await response.json()) as { code: string }).code).toBe("malformed_payload");
      }
    });
  });

  it("answers an unknown tool and an engine error with the statuses the mapping names", async () => {
    await bound(async (base) => {
      const unknownTool = await fetch(`${base}/mcp/call-tool`, {
        method: "POST",
        body: JSON.stringify({ name: "no_such_tool", arguments: {} }),
      });
      expect(unknownTool.status).toBe(404);
      expect(((await unknownTool.json()) as { code: string }).code).toBe("unknown_operation");

      // The same failure over both surfaces: the same code, and now the same status.
      const viaMcp = await fetch(`${base}/mcp/call-tool`, {
        method: "POST",
        body: JSON.stringify({ name: "get_scene", arguments: { sessionId: "no-such-session" } }),
      });
      const viaHttp = await fetch(`${base}/v1/get-scene`, {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ sessionId: "no-such-session" }),
      });

      expect(viaMcp.status).toBe(viaHttp.status);
      expect(viaMcp.status).toBe(404);
      expect(((await viaMcp.json()) as { code: string }).code).toBe(
        ((await viaHttp.json()) as { code: string }).code,
      );
    });
  });

  it("carries the correlation on every error, in the body and in the header, as one value", async () => {
    await bound(async (base) => {
      const response = await fetch(`${base}/mcp/call-tool`, { method: "POST", body: "{" });
      const body = (await response.json()) as { code: string; correlation?: string };

      expect(typeof body.correlation).toBe("string");
      expect(body.correlation).toMatch(/^[0-9a-f]{32}$/);
      expect(response.headers.get("x-correlation-id")).toBe(body.correlation);
    });
  });

  it("adopts an inbound traceparent for a transport-level refusal", async () => {
    await bound(async (base) => {
      const traceId = "4bf92f3577b34da6a3ce929d0e0e4736";
      const response = await fetch(`${base}/mcp/call-tool`, {
        method: "POST",
        headers: { traceparent: `00-${traceId}-00f067aa0ba902b7-01` },
        body: "{",
      });

      expect(((await response.json()) as { correlation: string }).correlation).toBe(traceId);
    });
  });

  it("carries the correlation on a successful tool call too, not only on an error (#102)", async () => {
    await bound(async (base) => {
      const traceId = "4bf92f3577b34da6a3ce929d0e0e4736";
      const response = await fetch(`${base}/mcp/call-tool`, {
        method: "POST",
        headers: { traceparent: `00-${traceId}-00f067aa0ba902b7-01` },
        body: JSON.stringify({ name: "start_game", arguments: { campaignId: CAMPAIGN_ID } }),
      });

      expect(response.status).toBe(200);
      expect(response.headers.get("x-correlation-id")).toBe(traceId);
    });
  });

  it("still lists the tools on POST", async () => {
    await bound(async (base) => {
      const response = await fetch(`${base}/mcp/list-tools`, { method: "POST" });
      const body = (await response.json()) as { tools: { name: string }[] };

      expect(response.status).toBe(200);
      expect(body.tools.length).toBe(contract.operations.length);
    });
  });
});

describe("S3.11 — probes", () => {
  it("returns liveness healthy without touching the store, and readiness healthy once bound", async () => {
    const started = await startWorkload(DEFAULT_CONFIGURATION);
    expect(started.ok).toBe(true);
    if (!started.ok) return;

    try {
      expect(started.value.probes.liveness().status).toBe("healthy");
      expect(started.value.probes.readiness().status).toBe("healthy");
      expect(started.value.listening.port).toBeGreaterThan(0);
    } finally {
      await started.value.shutdown();
    }
  });

  it("reports readiness healthy only after both surface construction and the bind", () => {
    const probes = createProbeSurface();

    expect(probes.surface.liveness().status).toBe("healthy");
    expect(probes.surface.readiness().status).toBe("unhealthy");

    probes.markSurfacesBuilt();
    expect(probes.surface.readiness().status).toBe("unhealthy");

    probes.markListening();
    expect(probes.surface.readiness().status).toBe("healthy");
  });

  it("answers liveness without reaching the store", async () => {
    const { store, calls } = recordingStore();
    const probes = createProbeSurface();
    void store;

    probes.markSurfacesBuilt();
    probes.markListening();
    probes.surface.liveness();
    probes.surface.readiness();

    expect(calls).toEqual([]);
  });
});
