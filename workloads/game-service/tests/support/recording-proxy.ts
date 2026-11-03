/**
 * G3 S3 — a tap between the edge and the workload. It forwards every request unchanged and records
 * what it saw, so S3.3 can say the workload received nothing and S3.4/S3.5 can say what it received.
 * The workload still verifies the assertion itself: this observes the wire, it does not stand in for
 * either end. Never imported by `src/`.
 */
import { createServer, request as httpRequest } from "node:http";
import type { IncomingHttpHeaders, Server } from "node:http";

export interface RecordedRequest {
  readonly method: string;
  readonly pathAndQuery: string;
  readonly headers: IncomingHttpHeaders;
  /** Header names and values exactly as sent, one pair per occurrence — a repeated header shows
   *  twice here and once, comma-joined, in `headers`. */
  readonly rawHeaders: readonly string[];
}

export interface RecordingProxy {
  readonly baseAddress: string;
  readonly requests: readonly RecordedRequest[];
  close(): Promise<void>;
}

export async function startRecordingProxy(targetBaseAddress: string): Promise<RecordingProxy> {
  const target = new URL(targetBaseAddress);
  const requests: RecordedRequest[] = [];

  const server: Server = createServer((incoming, outgoing) => {
    requests.push({
      method: incoming.method ?? "GET",
      pathAndQuery: incoming.url ?? "/",
      headers: { ...incoming.headers },
      rawHeaders: [...incoming.rawHeaders],
    });

    const forwarded = httpRequest(
      {
        host: target.hostname,
        port: target.port,
        method: incoming.method,
        path: incoming.url,
        headers: incoming.headers,
      },
      (answer) => {
        outgoing.writeHead(answer.statusCode ?? 502, answer.headers);
        answer.pipe(outgoing);
      },
    );
    forwarded.on("error", () => outgoing.destroy());
    incoming.pipe(forwarded);
  });

  const port = await new Promise<number>((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", () => {
      const address = server.address();
      resolve(typeof address === "object" && address !== null ? address.port : 0);
    });
  });

  return {
    baseAddress: `http://127.0.0.1:${port}`,
    requests,
    close: () =>
      new Promise<void>((resolve) => {
        server.closeAllConnections();
        server.close(() => resolve());
      }),
  };
}
