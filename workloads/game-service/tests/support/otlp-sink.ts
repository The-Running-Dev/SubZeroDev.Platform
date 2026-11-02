/**
 * S8 — a loopback stand-in for an OTLP collector. Accepts the exporter's own POST and decodes the
 * OTLP/HTTP JSON body — trace and span ids are already lowercase hex on the wire (JSON_ENCODER in
 * `@opentelemetry/otlp-transformer` leaves them as-is; only byte-array attributes go through
 * base64), so this reads them straight through rather than decoding anything.
 */
import { createServer, type Server } from "node:http";

import { spansFromExportRequest } from "./otlp-json.js";
import type { CollectedSpan } from "./otlp-json.js";

export type { CollectedSpan } from "./otlp-json.js";

export interface OtlpSink {
  readonly url: string;
  readonly spans: CollectedSpan[];
  /** Every export body as received, undecoded — what a search for a string that must never be
   *  exported reads, since `spans` keeps only the fields the S8 assertions compare. */
  readonly bodies: string[];
  close(): Promise<void>;
}

export async function startOtlpSink(): Promise<OtlpSink> {
  const spans: CollectedSpan[] = [];
  const bodies: string[] = [];

  const server: Server = createServer((request, response) => {
    const chunks: Buffer[] = [];
    request.on("data", (chunk: Buffer) => chunks.push(chunk));
    request.on("end", () => {
      if (request.url === "/v1/traces") {
        const text = Buffer.concat(chunks).toString("utf8");
        bodies.push(text);
        try {
          const parsed = JSON.parse(text);
          spans.push(...spansFromExportRequest(parsed));
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
    bodies,
    async close() {
      await new Promise<void>((resolve) => server.close(() => resolve()));
    },
  };
}
