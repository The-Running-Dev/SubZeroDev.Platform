/** Shared shape and flattening logic for reading spans out of an OTLP/HTTP JSON export request —
 *  used both by `otlp-sink.ts` (one export request's own body) and `otel-collector.ts` (newline-
 *  delimited export requests read back from the collector's `file` exporter output). Kept in one
 *  place so the two test doubles can't quietly disagree about what a "collected span" is. */

export interface CollectedSpan {
  readonly name: string;
  readonly traceId: string;
  readonly spanId: string;
  readonly parentSpanId: string | null;
}

interface OtlpJsonSpan {
  readonly name: string;
  readonly traceId: string;
  readonly spanId: string;
  readonly parentSpanId?: string;
}

interface OtlpJsonExportRequest {
  readonly resourceSpans?: readonly {
    readonly scopeSpans?: readonly { readonly spans?: readonly OtlpJsonSpan[] }[];
  }[];
}

/** Every span nested inside one OTLP JSON export request. */
export function spansFromExportRequest(parsed: OtlpJsonExportRequest): CollectedSpan[] {
  const spans: CollectedSpan[] = [];
  for (const resourceSpan of parsed.resourceSpans ?? []) {
    for (const scopeSpan of resourceSpan.scopeSpans ?? []) {
      for (const span of scopeSpan.spans ?? []) {
        spans.push({
          name: span.name,
          traceId: span.traceId,
          spanId: span.spanId,
          parentSpanId: span.parentSpanId ?? null,
        });
      }
    }
  }
  return spans;
}
