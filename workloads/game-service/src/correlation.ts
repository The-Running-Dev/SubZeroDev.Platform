/**
 * The correlation is derived and never supplied: it is the trace-id of the adopted-or-minted trace
 * context. A malformed inbound `traceparent` yields a fresh root and a fresh correlation, and never
 * a failed request (invariant 31) — which is why nothing here has a failure channel.
 *
 * 32 lowercase hexadecimal characters, never all-zero — the same constraint Platform puts on its
 * own, so the two processes name one value the same way.
 */
import { randomBytes } from "node:crypto";
import type { CorrelationId } from "./types.js";

/** `00-<32 hex trace-id>-<16 hex span-id>-<2 hex flags>`, W3C's own shape. */
const TRACEPARENT = /^00-([0-9a-f]{32})-[0-9a-f]{16}-[0-9a-f]{2}$/;
const ALL_ZERO = "0".repeat(32);

export function mintCorrelation(): CorrelationId {
  let candidate = randomBytes(16).toString("hex");
  while (candidate === ALL_ZERO) {
    candidate = randomBytes(16).toString("hex");
  }
  return candidate as CorrelationId;
}

export function correlationFrom(inboundTraceParent: string | null): CorrelationId {
  if (inboundTraceParent === null) return mintCorrelation();
  const matched = TRACEPARENT.exec(inboundTraceParent.trim());
  if (!matched) return mintCorrelation();
  const traceId = matched[1]!;
  return traceId === ALL_ZERO ? mintCorrelation() : (traceId as CorrelationId);
}
