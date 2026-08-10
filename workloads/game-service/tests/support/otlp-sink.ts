/**
 * S8 — a loopback stand-in for an OTLP collector. Accepts the exporter's own POST and decodes the
 * OTLP/HTTP JSON body — trace and span ids are already lowercase hex on the wire (JSON_ENCODER in
 * `@opentelemetry/otlp-transformer` leaves them as-is; only byte-array attributes go through
 * base64), so this reads them straight through rather than decoding anything.
 */
import { createServer, type Server } from "node:http";

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

export interface OtlpSink {
  readonly url: string;
  readonly spans: CollectedSpan[];
  close(): Promise<void>;
}

export async function startOtlpSink(): Promise<OtlpSink> {
  const spans: CollectedSpan[] = [];

  const server: Server = createServer((request, response) => {
    const chunks: Buffer[] = [];
    request.on("data", (chunk: Buffer) => chunks.push(chunk));
    request.on("end", () => {
      if (request.url === "/v1/traces") {
        try {
          const parsed = JSON.parse(Buffer.concat(chunks).toString("utf8")) as OtlpJsonExportRequest;
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
        } catch {
          // A body this sink cannot parse fails the test's own assertions on `spans`, not this
          // handler — there is nothing useful to do with the parse error here.
        }
      }
      response.writeHead(200, { "content-type": "application/json" });
      response.end("{}");
    });
  });

  const port = await new Promise<number>((resolve) => {
    server.listen(0, "127.0.0.1", () => {
      const address = server.address();
      resolve(typeof address === "object" && address !== null ? address.port : 0);
    });
  });

  return {
    url: `http://127.0.0.1:${port}`,
    spans,
    async close() {
      await new Promise<void>((resolve) => server.close(() => resolve()));
    },
  };
}
