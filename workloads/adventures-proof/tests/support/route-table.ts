/**
 * G3 S11.3 — the reverse proxy's route table, as far as a cutover and its rollback need one. The
 * deployment's proxy keeps TLS and picks, for the API host, where requests go: straight to Adventures
 * (the direct route) or to the edge in front of it (the cutover route). This stands in for that choice
 * and nothing else — one public address that never changes, and a switch between named upstreams. Like
 * the production proxy it forwards a request unchanged, so a browser's cookies and `Host` survive a
 * switch. Never imported by anything but the tests.
 */
import { createServer, request as httpRequest } from "node:http";
import type { Server } from "node:http";

export interface RouteTable {
  /** The address players reach the API host at, whichever route is active. */
  readonly publicAddress: string;
  /** The name of the route that answers now. */
  readonly active: string;
  /** Every request the proxy forwarded, by the route that carried it. */
  readonly served: readonly string[];
  /** Points the host at another route. Requests already in flight are not moved. */
  use(route: string): void;
  close(): Promise<void>;
}

/** Listens on `port` and forwards to the upstream the active route names; `initial` is active first. */
export async function startRouteTable(
  port: number,
  upstreams: Readonly<Record<string, string>>,
  initial: string,
): Promise<RouteTable> {
  if (upstreams[initial] === undefined) throw new Error(`no route named ${initial}`);
  let active = initial;
  const served: string[] = [];

  const server: Server = createServer((incoming, outgoing) => {
    const route = active;
    const target = new URL(upstreams[route]!);
    served.push(route);
    const forwarded = httpRequest(
      { host: target.hostname, port: target.port, method: incoming.method, path: incoming.url, headers: incoming.headers },
      (answer) => {
        outgoing.writeHead(answer.statusCode ?? 502, answer.headers);
        answer.pipe(outgoing);
      },
    );
    // An upstream that is down is a gateway failure, not a hung socket: the player sees the outage.
    forwarded.once("error", () => {
      if (!outgoing.headersSent) outgoing.writeHead(502, { "content-type": "text/plain" });
      outgoing.end("bad gateway");
    });
    incoming.pipe(forwarded);
  });

  await new Promise<void>((resolve, reject) => {
    server.once("error", reject);
    server.listen(port, "127.0.0.1", () => resolve());
  });

  return {
    publicAddress: `http://127.0.0.1:${port}`,
    get active() {
      return active;
    },
    served,
    use(route) {
      if (upstreams[route] === undefined) throw new Error(`no route named ${route}`);
      active = route;
    },
    close: () =>
      new Promise<void>((resolve) => {
        server.closeAllConnections();
        server.close(() => resolve());
      }),
  };
}
