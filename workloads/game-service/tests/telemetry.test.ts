/**
 * S8.1–S8.5 — the workload's own half of "one trace across two languages": a well-formed inbound
 * `traceparent` is adopted as the exported span's parent, a malformed or absent one still yields a
 * `200` under a fresh root, the exported span's trace id always equals the response's correlation,
 * and with no endpoint configured nothing is exported and nothing is dialled (already covered by
 * S3.13's process.test.ts; this file is additive, not a replacement).
 */
import { randomBytes } from "node:crypto";
import { afterEach, describe, expect, it } from "vitest";

import { startWorkload } from "../src/lifecycle.js";
import type { WorkloadProcess } from "../src/types.js";
import { startOtlpSink } from "./support/otlp-sink.js";
import type { OtlpSink } from "./support/otlp-sink.js";
import { CAMPAIGN_ID } from "./support/real-workload.js";

function wellFormedTraceParent(): { traceparent: string; traceId: string; spanId: string } {
  const traceId = randomBytes(16).toString("hex");
  const spanId = randomBytes(8).toString("hex");
  return { traceparent: `00-${traceId}-${spanId}-01`, traceId, spanId };
}

async function waitFor(predicate: () => boolean, timeoutMs = 5_000): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (predicate()) return;
    await new Promise((resolve) => setTimeout(resolve, 25));
  }
  if (!predicate()) throw new Error(`condition not met within ${timeoutMs}ms`);
}

describe("S8 — the workload's span shares the response's correlation, and adopts a valid parent", () => {
  let sink: OtlpSink | undefined;
  let workload: WorkloadProcess | undefined;

  afterEach(async () => {
    await workload?.shutdown();
    await sink?.close();
    sink = undefined;
    workload = undefined;
  });

  it("S8.2/S8.3/S8.4 — a well-formed traceparent is adopted as the span's parent, and the trace id equals the correlation", async () => {
    sink = await startOtlpSink();
    const started = await startWorkload({
      listen: { host: "127.0.0.1", port: 0 },
      determinism: { kind: "default" },
      otlpEndpoint: sink.url,
    });
    expect(started.ok).toBe(true);
    if (!started.ok) return;
    workload = started.value;

    const { traceparent, traceId, spanId } = wellFormedTraceParent();
    const response = await fetch(`http://127.0.0.1:${started.value.listening.port}/v1/create-session`, {
      method: "POST",
      headers: { "content-type": "application/json", traceparent },
      body: JSON.stringify({ campaignId: CAMPAIGN_ID }),
    });
    expect(response.status).toBe(200);
    const correlation = response.headers.get("x-correlation-id");
    expect(correlation).toBe(traceId);

    await waitFor(() => sink!.spans.length > 0);
    const [span] = sink.spans;
    expect(span!.traceId).toBe(traceId);
    expect(span!.parentSpanId).toBe(spanId);
  });

  it("S8.4 — a malformed traceparent still answers 200 under a fresh root, and the span's trace id is that root", async () => {
    sink = await startOtlpSink();
    const started = await startWorkload({
      listen: { host: "127.0.0.1", port: 0 },
      determinism: { kind: "default" },
      otlpEndpoint: sink.url,
    });
    expect(started.ok).toBe(true);
    if (!started.ok) return;
    workload = started.value;

    const response = await fetch(`http://127.0.0.1:${started.value.listening.port}/v1/create-session`, {
      method: "POST",
      headers: { "content-type": "application/json", traceparent: "not-a-traceparent" },
      body: JSON.stringify({ campaignId: CAMPAIGN_ID }),
    });
    expect(response.status).toBe(200);
    const correlation = response.headers.get("x-correlation-id");
    expect(correlation).toMatch(/^[0-9a-f]{32}$/);

    await waitFor(() => sink!.spans.length > 0);
    const [span] = sink.spans;
    expect(span!.traceId).toBe(correlation);
    expect(span!.parentSpanId).toBeNull();
  });

  it("flushes a request handled just before graceful shutdown", async () => {
    sink = await startOtlpSink();
    const started = await startWorkload({
      listen: { host: "127.0.0.1", port: 0 },
      determinism: { kind: "default" },
      otlpEndpoint: sink.url,
    });
    expect(started.ok).toBe(true);
    if (!started.ok) return;
    workload = started.value;

    await fetch(`http://127.0.0.1:${started.value.listening.port}/v1/create-session`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ campaignId: CAMPAIGN_ID }),
    });

    const outcome = await workload.shutdown();
    workload = undefined;
    expect(outcome.ok).toBe(true);
    expect(sink.spans.length).toBeGreaterThan(0);
  });
});
