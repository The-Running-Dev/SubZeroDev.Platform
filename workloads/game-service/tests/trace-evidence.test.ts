/**
 * S8.1/S8.2/S8.4/S8.6 — one request through the edge produces spans from both processes in a real
 * collector, sharing one trace id, with the workload's span parented on the edge's — for a
 * well-formed inbound trace and, separately, for a malformed one arriving at the edge (S8.4's other
 * half, S3.7/`telemetry.test.ts` already covering it directly at the workload). Skipped unless
 * `OTEL_COLLECTOR_BIN` is set (`build.yml`'s `game-service` job sets it; nothing else does), so a
 * local `npm test` with no collector installed still runs everything else in this suite.
 *
 * Both requests go through **one** spawned edge-plus-workload pair and **one** collector, not one
 * of each per request: two full process-spawn cycles back to back in the same CI job proved flaky
 * in practice (a second edge/workload pair starting immediately after the first's shutdown
 * intermittently lost its own export), and nothing about what S8.1/S8.2/S8.4/S8.6 assert requires
 * fresh processes per request — only that each request's own pair of spans is found and related
 * correctly, which grouping by trace id gives for free.
 *
 * The correlation invariant S8.3 asks for — the exported span's trace id equalling the response's
 * correlation — is asserted once already, per request, in `telemetry.test.ts`; `ForwardedResponse`
 * carries no headers at all (`20-contract.md`, "Edge — options, forwarding, and readiness"), so a
 * caller addressing the edge for a successful request has no header to read the workload's
 * correlation from in the first place. What this file adds beyond that is the fact only a real,
 * cross-process collector can show: that the two processes' spans are the *same* trace.
 */
import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";

import { spawnHostedEdge } from "./support/hosted-edge.js";
import { readCollectedSpans, startCollector } from "./support/otel-collector.js";
import type { CollectedSpan } from "./support/otel-collector.js";
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
    } catch (thrown) {
      // A slow-but-alive edge (GC pause, CI contention, still draining its own OTel exporter)
      // can trip the 500ms abort before the connection is ever refused — only a genuine refusal
      // is evidence the process actually exited, so a mere timeout keeps polling instead.
      const isTimeout = thrown instanceof DOMException && thrown.name === "TimeoutError";
      if (!isTimeout) return;
    }
    await new Promise((resolve) => setTimeout(resolve, 100));
  }
  throw new Error(`edge did not exit within ${timeoutMs}ms`);
}

async function requestThroughEdge(baseAddress: string, traceparent: string | null): Promise<void> {
  const headers: Record<string, string> = { "content-type": "application/json" };
  if (traceparent !== null) headers["traceparent"] = traceparent;

  const response = await fetch(`${baseAddress}/v1/create-session`, {
    method: "POST",
    headers,
    body: JSON.stringify({ campaignId: CAMPAIGN_ID }),
  });
  expect(response.status).toBe(200);
}

/** Every span the collector holds for one trace id, asserted as one edge span parenting one
 *  workload span — the pairing S8.1/S8.2 exist to prove, over whichever trace id the caller names. */
function assertOneSharedTrace(spans: readonly CollectedSpan[], traceId: string): void {
  const inTrace = spans.filter((span) => span.traceId === traceId);
  const workloadSpan = inTrace.find((span) => span.name === "game-service.request");
  expect(workloadSpan, `no workload span for trace ${traceId} among: ${JSON.stringify(inTrace)}`).toBeDefined();
  expect(workloadSpan!.parentSpanId).not.toBeNull();

  const edgeSpan = inTrace.find((span) => span.spanId === workloadSpan!.parentSpanId);
  expect(
    edgeSpan,
    `no edge span with spanId ${workloadSpan!.parentSpanId} for trace ${traceId} among: ${JSON.stringify(inTrace)}`,
  ).toBeDefined();

  // S8.2 — the relationship is asserted directly, not inferred from the shared id alone: the
  // lookup above only succeeds because the workload span's own parentSpanId names it.
  expect(workloadSpan!.parentSpanId).toBe(edgeSpan!.spanId);
}

/** `true` when `traceId` has a complete edge-parenting-workload pair in `spans` — the same shape
 *  `assertOneSharedTrace` requires, but as a predicate rather than an assertion. */
function hasCompleteSharedTrace(spans: readonly CollectedSpan[], traceId: string): boolean {
  const inTrace = spans.filter((span) => span.traceId === traceId);
  const workloadSpan = inTrace.find((span) => span.name === "game-service.request");
  if (workloadSpan === undefined || workloadSpan.parentSpanId === null) return false;
  return inTrace.some((span) => span.spanId === workloadSpan.parentSpanId);
}

// The edge's own sampler (`PlatformSampler`) applies a 10% trace-id ratio to every genuinely
// unparented root — which is exactly what a malformed `traceparent` produces, since there is no
// valid header to adopt. That ratio is the one fact this count rests on; the sampler is not changed
// for the test (S25).
const EDGE_ROOT_SAMPLING_RATIO = 0.1;

// The largest chance this case may have of failing on a healthy build. Below one in a million the
// case is not a flake anyone will meet; a red run means pairing broke.
const MAX_HEALTHY_MISS_PROBABILITY = 1e-6;

// Derivation (S25.1). Each attempt's fresh root carries a new random trace id, and the ratio
// sampler's decision is a function of that id alone, so the n attempts are n independent draws,
// each recorded by the edge with probability p = EDGE_ROOT_SAMPLING_RATIO. The case misses only if
// every draw is unrecorded:
//
//   P(miss) = (1 - p)^n ≤ MAX_HEALTHY_MISS_PROBABILITY
//   ⇔ n ≥ ln(MAX_HEALTHY_MISS_PROBABILITY) / ln(1 - p) = ln(1e-6) / ln(0.9) ≈ 131.1
//
// so n = 132, and P(miss) = 0.9^132 ≈ 9.1e-7. (The 40 attempts this replaced missed one run in
// ~68 — issue #298.)
//
// The count only bounds the healthy case. With pairing broken — the edge's span not propagated as
// the workload's parent — no trace id carries a complete edge→workload pair, whatever the edge
// samples, so the case fails on every run (S25.2).
const MALFORMED_TRACEPARENT_ATTEMPTS = Math.ceil(
  Math.log(MAX_HEALTHY_MISS_PROBABILITY) / Math.log(1 - EDGE_ROOT_SAMPLING_RATIO),
);

describe.skipIf(!process.env["OTEL_COLLECTOR_BIN"])(
  "S8.1/S8.2/S8.4/S8.6 — requests through the edge, each its own shared trace, in a real collector",
  () => {
    it("a well-formed traceparent and a malformed one each produce one shared trace, parented correctly", async () => {
      const collector = await startCollector();
      const edge = await spawnHostedEdge(collector.otlpEndpoint);

      const traceId = "1234567890abcdef1234567890abcdef";
      const parentSpanId = "1234567890abcdef";

      let spans: readonly CollectedSpan[];
      try {
        // S8.1/S8.2 — a well-formed inbound trace is adopted end to end.
        await requestThroughEdge(edge.target.baseAddress, `00-${traceId}-${parentSpanId}-01`);
        // S8.4 — a malformed one still answers 200, under a fresh root shared by both hops. The
        // edge's own sampler only records ~10% of these (see `MALFORMED_TRACEPARENT_ATTEMPTS`'s
        // comment), so several independent attempts are made rather than relying on exactly one.
        for (let attempt = 0; attempt < MALFORMED_TRACEPARENT_ATTEMPTS; attempt++) {
          await requestThroughEdge(edge.target.baseAddress, "not-a-traceparent");
        }

        const shutdown = await edge.target.shutdown();
        expect(shutdown.ok).toBe(true);
        await waitForEdgeToExit(edge.target.baseAddress);
      } finally {
        edge.forceKill();
        await collector.stop();
      }

      const raw = readFileSync(collector.outputPath, "utf8");
      spans = readCollectedSpans(collector.outputPath);
      if (spans.length === 0) {
        throw new Error(
          `collector captured no spans at all. output file bytes: ${raw.length}, content: ${JSON.stringify(raw)}\n`
            + `collector stderr:\n${collector.stderr()}`,
        );
      }

      // S8.1 — one trace id, shared, for the well-formed request specifically.
      assertOneSharedTrace(spans, traceId);

      // Each malformed-traceparent attempt's root is whichever trace id its pair of spans actually
      // landed under — not knowable in advance, so it is found rather than asserted as a literal.
      // The edge's own sampler decides independently per attempt (see `MALFORMED_TRACEPARENT_ATTEMPTS`'s
      // comment): an attempt the edge did not sample still surfaces a lone workload span here (the
      // workload always records), with no edge span to pair it against, so only trace ids with a
      // *complete* pairing are asserted on — the rest are attempts the edge itself dropped, not a
      // pairing failure. At least one complete pairing is required so the S8.4 guarantee is still
      // exercised, not merely attempted.
      const traceIds = new Set(spans.map((span) => span.traceId));
      traceIds.delete(traceId);
      const completeFreshRoots = [...traceIds].filter((id) => hasCompleteSharedTrace(spans, id));
      expect(
        completeFreshRoots.length,
        `expected at least one fully-sampled fresh-root trace among ${MALFORMED_TRACEPARENT_ATTEMPTS} attempts `
          + `(a healthy build misses with probability below ${MAX_HEALTHY_MISS_PROBABILITY}, so this is a pairing `
          + `failure, not sampling); `
          + `spans: ${JSON.stringify(spans)}`,
      ).toBeGreaterThan(0);
      for (const freshRootTraceId of completeFreshRoots) {
        assertOneSharedTrace(spans, freshRootTraceId);
      }
    });
  },
);
