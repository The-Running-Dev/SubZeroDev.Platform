/**
 * G3 S7 — a tap between the edge and Adventures. It forwards every request unchanged and records
 * the request and the whole answer, so S7.3 can say Adventures itself produced the refusal: the `403`
 * and its `forbidden_origin` body were on the wire between the two, not made by the edge. It observes;
 * it decides nothing. Never imported by anything but the tests.
 */
import { createServer, request as httpRequest } from "node:http";
import type { IncomingHttpHeaders, Server } from "node:http";

import { freePort } from "./free-port.js";

export interface Exchange {
  readonly method: string;
  readonly pathAndQuery: string;
  readonly requestHeaders: IncomingHttpHeaders;
  readonly status: number;
  /** The answer's headers exactly as sent, one name/value pair per occurrence. */
  readonly responseRawHeaders: readonly string[];
  readonly body: string;
}

export interface RecordingProxy {
  readonly baseAddress: string;
  readonly exchanges: readonly Exchange[];
  close(): Promise<void>;
}

export async function startRecordingProxy(targetBaseAddress: string): Promise<RecordingProxy> {
  const target = new URL(targetBaseAddress);
  const exchanges: Exchange[] = [];
  const port = await freePort();

  const server: Server = createServer((incoming, outgoing) => {
    const forwarded = httpRequest(
      {
        host: target.hostname,
        port: target.port,
        method: incoming.method,
        path: incoming.url,
        headers: incoming.headers,
      },
      (answer) => {
        const chunks: Buffer[] = [];
        outgoing.writeHead(answer.statusCode ?? 502, answer.headers);
        answer.on("data", (chunk: Buffer) => {
          chunks.push(chunk);
          outgoing.write(chunk);
        });
        answer.once("end", () => {
          // Recorded before the caller sees the end of the body, so a test that has the whole
          // response can rely on the exchange being here.
          exchanges.push({
            method: incoming.method ?? "GET",
            pathAndQuery: incoming.url ?? "/",
            requestHeaders: { ...incoming.headers },
            status: answer.statusCode ?? 502,
            responseRawHeaders: [...answer.rawHeaders],
            body: Buffer.concat(chunks).toString("utf8"),
          });
          outgoing.end();
        });
      },
    );
    forwarded.on("error", () => outgoing.destroy());
    incoming.pipe(forwarded);
  });

  await new Promise<void>((resolve, reject) => {
    server.once("error", reject);
    server.listen(port, "127.0.0.1", () => resolve());
  });

  return {
    baseAddress: `http://127.0.0.1:${port}`,
    exchanges,
    close: () =>
      new Promise<void>((resolve) => {
        server.closeAllConnections();
        server.close(() => resolve());
      }),
  };
}
