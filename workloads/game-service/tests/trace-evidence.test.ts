/**
 * S8.1/S8.2/S8.4/S8.6 — one request through the edge produces spans from both processes in a real
 * collector, sharing one trace id, with the workload's span parented on the edge's — for a
 * well-formed inbound trace and, separately, for a malformed one arriving at the edge (S8.4's other
 * half, S3.7/`telemetry.test.ts` already covering it directly at the workload). Skipped unless
 * `OTEL_COLLECTOR_BIN` is set (`build.yml`'s `game-service` job sets it; nothing else does), so a
 * local `npm test` with no collector installed still runs everything else in this suite.
 *
 * The correlation invariant S8.3 asks for — the exported span's trace id equalling the response's
 * correlation — is asserted once already, per request, in `telemetry.test.ts`; `ForwardedResponse`
 * carries no headers at all (`20-contract.md`, "Edge — options, forwarding, and readiness"), so a
 * caller addressing the edge for a successful request has no header to read the workload's
 * correlation from in the first place. What this file adds beyond that is the fact only a real,
 * cross-process collector can show: that the two processes' spans are the *same* trace.
 */
import { describe, expect, it } from "vitest";

import { spawnHostedEdge } from "./support/hosted-edge.js";
import { readCollectedSpans, startCollector } from "./support/otel-collector.js";
import { CAMPAIGN_ID } from "./support/real-workload.js";

/** `edge.target.shutdown()` signals the edge (`SIGTERM`) without waiting for it to actually exit —
 *  every other caller only needs the workload's dump written, which it does wait for. This test
 *  needs the edge's own OTel provider to have flushed, which happens on process exit, so it polls
 *  liveness until the connection is refused rather than racing `collector.stop()` against it. */
async function waitForEdgeToExit(baseAddress: string, timeoutMs = 10_000): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try {
      await fetch(`${baseAddress}/health/live`, { signal: AbortSignal.timeout(500) });
    } catch {
      return;
    }
    await new Promise((resolve) => setTimeout(resolve, 100));
  }
  throw new Error(`edge did not exit within ${timeoutMs}ms`);
}

describe.skipIf(!process.env["OTEL_COLLECTOR_BIN"])(
  "S8.1/S8.2/S8.6 — one request through the edge, one trace, in a real collector",
  () => {
    it("the workload's span is a child of the edge's span, and both carry the same trace id", async () => {
      const collector = await startCollector();
      const edge = await spawnHostedEdge(collector.otlpEndpoint);

      try {
        const response = await fetch(`${edge.target.baseAddress}/v1/create-session`, {
          method: "POST",
          headers: { "content-type": "application/json" },
          body: JSON.stringify({ campaignId: CAMPAIGN_ID }),
        });
        expect(response.status).toBe(200);

        const shutdown = await edge.target.shutdown();
        expect(shutdown.ok).toBe(true);
        await waitForEdgeToExit(edge.target.baseAddress);
      } finally {
        edge.forceKill();
        await collector.stop();
      }

      const spans = readCollectedSpans(collector.outputPath);
      const workloadSpan = spans.find((span) => span.name === "game-service.request");
      expect(workloadSpan, `no workload span among: ${JSON.stringify(spans)}`).toBeDefined();
      expect(workloadSpan!.parentSpanId).not.toBeNull();

      const edgeSpan = spans.find((span) => span.spanId === workloadSpan!.parentSpanId);
      expect(edgeSpan, `no edge span with spanId ${workloadSpan!.parentSpanId} among: ${JSON.stringify(spans)}`)
        .toBeDefined();

      // S8.1 — one trace id, shared.
      expect(workloadSpan!.traceId).toBe(edgeSpan!.traceId);
      // S8.2 — the relationship is asserted directly, not inferred from the shared id alone: the
      // lookup above only succeeds because the workload span's own parentSpanId names it.
      expect(workloadSpan!.parentSpanId).toBe(edgeSpan!.spanId);
    });

    it("S8.4 — a malformed traceparent arriving at the edge still answers 200, under one fresh root shared by both hops", async () => {
      const collector = await startCollector();
      const edge = await spawnHostedEdge(collector.otlpEndpoint);

      try {
        const response = await fetch(`${edge.target.baseAddress}/v1/create-session`, {
          method: "POST",
          headers: { "content-type": "application/json", traceparent: "not-a-traceparent" },
          body: JSON.stringify({ campaignId: CAMPAIGN_ID }),
        });
        expect(response.status).toBe(200);

        const shutdown = await edge.target.shutdown();
        expect(shutdown.ok).toBe(true);
        await waitForEdgeToExit(edge.target.baseAddress);
      } finally {
        edge.forceKill();
        await collector.stop();
      }

      const spans = readCollectedSpans(collector.outputPath);
      const workloadSpan = spans.find((span) => span.name === "game-service.request");
      expect(workloadSpan, `no workload span among: ${JSON.stringify(spans)}`).toBeDefined();

      const edgeSpan = spans.find((span) => span.spanId === workloadSpan!.parentSpanId);
      expect(edgeSpan, `no edge span with spanId ${workloadSpan!.parentSpanId} among: ${JSON.stringify(spans)}`)
        .toBeDefined();

      // A malformed header at the edge still yields one shared, freshly minted root — same
      // criterion as the well-formed case above, over a header neither hop can adopt.
      expect(workloadSpan!.traceId).toBe(edgeSpan!.traceId);
      expect(workloadSpan!.parentSpanId).toBe(edgeSpan!.spanId);
    });
  },
);
