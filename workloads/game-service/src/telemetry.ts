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
import { ROOT_CONTEXT, SpanKind, TraceFlags, trace } from "@opentelemetry/api";
import type { Context, Span, SpanContext } from "@opentelemetry/api";
import { BasicTracerProvider, RandomIdGenerator, SimpleSpanProcessor } from "@opentelemetry/sdk-trace-base";
import type { IdGenerator } from "@opentelemetry/sdk-trace-base";
import { OTLPTraceExporter } from "@opentelemetry/exporter-trace-otlp-http";
import { TRACEPARENT } from "./correlation.js";
import type { CorrelationId } from "./types.js";

const ALL_ZERO_TRACE = "0".repeat(32);
const ALL_ZERO_SPAN = "0".repeat(16);

/** A remote parent context when `inboundTraceParent` is well-formed and neither id is all-zero;
 *  `undefined` on anything else, which is what sends the span down the SDK's own root-span path.
 *  The parent is always marked sampled, regardless of the inbound flags byte: this workload
 *  unconditionally records one span per request when tracing is on, and correlation.ts's own
 *  `correlationFrom` makes the same promise independent of that byte — a spec-legal unsampled
 *  parent must not silently produce a correlation with no matching span behind it. */
function parentContextFrom(inboundTraceParent: string | null): Context | undefined {
  if (inboundTraceParent === null) return undefined;
  const matched = TRACEPARENT.exec(inboundTraceParent.trim());
  if (!matched) return undefined;
  const [, traceId, spanId] = matched as unknown as [string, string, string, string];
  if (traceId === ALL_ZERO_TRACE || spanId === ALL_ZERO_SPAN) return undefined;

  const parent: SpanContext = {
    traceId,
    spanId,
    traceFlags: TraceFlags.SAMPLED,
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
   *  response's correlation are the same value by construction rather than by coincidence.
   *  `startedAt`, when given, backdates the span's recorded start time to when the request actually
   *  began — the span object itself is still constructed after the work finishes (its trace id can
   *  depend on that work's outcome), but its exported timestamps should not lie about when the
   *  request started just because the object was built late. */
  startRequestSpan(
    name: string,
    inboundTraceParent: string | null,
    correlation: CorrelationId,
    startedAt?: number,
  ): Span;
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
    startRequestSpan(name, inboundTraceParent, correlation, startedAt) {
      const parent = parentContextFrom(inboundTraceParent);
      if (parent === undefined) {
        forceNextTraceId(correlation as string);
      }
      return tracer.startSpan(name, { kind: SpanKind.SERVER, ...(startedAt !== undefined ? { startTime: startedAt } : {}) }, parent);
    },
    async shutdown() {
      // `SimpleSpanProcessor.shutdown()` alone does not wait for exports already in flight — only
      // `forceFlush()` does (checked against the installed package's own source: `shutdown()` calls
      // the exporter's `shutdown()` directly, never draining `_pendingExports`). A request handled
      // just before this runs would otherwise race the process exit and sometimes lose.
      await provider.forceFlush();
      await provider.shutdown();
      // `forceFlush()` resolving is not the same guarantee as the underlying OS socket having
      // finished writing that last export — an immediate `process.exit()` right after this method
      // returns can still race it on some platforms. Paid only here, so a shutdown with no tracing
      // configured (`tracing` is `null` and this method is never called) never pays it.
      await new Promise((resolve) => setTimeout(resolve, 50));
    },
  };
}
