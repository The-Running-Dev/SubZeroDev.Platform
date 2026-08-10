/**
 * Trace-context adoption and OTLP export for the workload's own request handling — the OpenTelemetry
 * SDK, configured, not a wire format hand-rolled here.
 *
 * `correlation.ts` already derives the correlation the response carries, from the inbound
 * `traceparent`. Invariant 31 says the correlation *is* the trace-id of the adopted-or-minted trace
 * context — so the span this module starts must end up with that same trace id, not an independently
 * minted one. A parented request gets there for free: `parentContextFrom` parses the identical
 * header the same way `correlation.ts` does, so a valid parent's trace id already equals the
 * correlation. A root request would not — the SDK's own `IdGenerator` mints trace id and span id
 * together, with no parameter to hand it one — so `startRequestSpan` forces the next root trace id
 * through that generator, once, rather than minting twice and hoping the two agree.
 */
import { ROOT_CONTEXT, SpanKind, trace } from "@opentelemetry/api";
import type { Context, Span, SpanContext } from "@opentelemetry/api";
import { BasicTracerProvider, RandomIdGenerator, SimpleSpanProcessor } from "@opentelemetry/sdk-trace-base";
import type { IdGenerator } from "@opentelemetry/sdk-trace-base";
import { OTLPTraceExporter } from "@opentelemetry/exporter-trace-otlp-http";
import type { CorrelationId } from "./types.js";

/** `00-<32 hex trace-id>-<16 hex span-id>-<2 hex flags>` — the same shape `correlation.ts` parses. */
const TRACEPARENT = /^00-([0-9a-f]{32})-([0-9a-f]{16})-([0-9a-f]{2})$/;
const ALL_ZERO_TRACE = "0".repeat(32);
const ALL_ZERO_SPAN = "0".repeat(16);

/** A remote parent context when `inboundTraceParent` is well-formed and neither id is all-zero;
 *  `undefined` on anything else, which is what sends the span down the SDK's own root-span path. */
function parentContextFrom(inboundTraceParent: string | null): Context | undefined {
  if (inboundTraceParent === null) return undefined;
  const matched = TRACEPARENT.exec(inboundTraceParent.trim());
  if (!matched) return undefined;
  const [, traceId, spanId, flags] = matched as unknown as [string, string, string, string];
  if (traceId === ALL_ZERO_TRACE || spanId === ALL_ZERO_SPAN) return undefined;

  const parent: SpanContext = {
    traceId,
    spanId,
    traceFlags: Number.parseInt(flags, 16) & 0x1,
    isRemote: true,
  };
  return trace.setSpanContext(ROOT_CONTEXT, parent);
}

/** Delegates span-id generation to the SDK's own generator throughout, and trace-id generation too
 *  — except for the one root immediately after `forceNextTraceId`, which is how a root span's trace
 *  id is made to equal a correlation already committed elsewhere, rather than left to chance. */
function createIdGenerator(): { idGenerator: IdGenerator; forceNextTraceId: (traceId: string) => void } {
  const fallback = new RandomIdGenerator();
  let forcedTraceId: string | null = null;
  return {
    idGenerator: {
      generateTraceId: () => {
        const traceId = forcedTraceId ?? fallback.generateTraceId();
        forcedTraceId = null;
        return traceId;
      },
      generateSpanId: () => fallback.generateSpanId(),
    },
    forceNextTraceId: (traceId: string) => {
      forcedTraceId = traceId;
    },
  };
}

export interface Tracing {
  /** Starts a server span for one request. Parented on `inboundTraceParent` when it is well-formed;
   *  otherwise a fresh root minted *as* `correlation`, so the exported span's trace id and the
   *  response's correlation are the same value by construction rather than by coincidence. */
  startRequestSpan(name: string, inboundTraceParent: string | null, correlation: CorrelationId): Span;
  /** Flushes every span still queued. Called once, at graceful shutdown, so a request handled just
   *  before shutdown is not silently dropped from the collector's view. */
  shutdown(): Promise<void>;
}

/** Constructed only when `otlpEndpoint` is configured — no provider, no processor, no exporter, and
 *  no outbound connection when it is not (invariant 32, S8.5). The signal path matches the .NET
 *  side's own `ConfigureOtlp`: the endpoint is the base, `v1/traces` is appended here. */
export function createTracing(otlpEndpoint: string): Tracing {
  const exporter = new OTLPTraceExporter({ url: `${otlpEndpoint}/v1/traces` });
  const { idGenerator, forceNextTraceId } = createIdGenerator();
  const provider = new BasicTracerProvider({
    idGenerator,
    spanProcessors: [new SimpleSpanProcessor(exporter)],
  });
  const tracer = provider.getTracer("subzerodev.game-service");

  return {
    startRequestSpan(name, inboundTraceParent, correlation) {
      const parent = parentContextFrom(inboundTraceParent);
      if (parent === undefined) {
        forceNextTraceId(correlation as string);
      }
      return tracer.startSpan(name, { kind: SpanKind.SERVER }, parent);
    },
    async shutdown() {
      await provider.shutdown();
    },
  };
}
