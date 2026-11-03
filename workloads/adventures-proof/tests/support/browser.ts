/**
 * A player's browser, as far as this proof needs one: it sends requests to one address with a cookie
 * jar, never follows a redirect (the proof reads each `Location` itself), and returns every
 * `Set-Cookie` a response carried as its own value. Built on `node:http` rather than `fetch` because
 * `fetch` forbids a caller-set `Origin` and `Cookie`, and the proof sets both on purpose.
 */
import { request } from "node:http";

import { CookieJar } from "./cookie-jar.js";

export interface Answer {
  readonly status: number;
  /** Header values keyed by lower-cased name; `set-cookie` is always an array. */
  readonly headers: Readonly<Record<string, string | readonly string[] | undefined>>;
  readonly setCookies: readonly string[];
  readonly location: string | undefined;
  readonly body: string;
  json<T = unknown>(): T;
}

export interface RequestOptions {
  readonly headers?: Readonly<Record<string, string>>;
  readonly json?: unknown;
}

export class Browser {
  readonly jar = new CookieJar();

  constructor(private readonly baseAddress: string) {}

  get(path: string, options: RequestOptions = {}): Promise<Answer> {
    return this.send("GET", path, options);
  }

  post(path: string, options: RequestOptions = {}): Promise<Answer> {
    return this.send("POST", path, options);
  }

  send(method: string, path: string, options: RequestOptions = {}): Promise<Answer> {
    const target = new URL(path, this.baseAddress);
    const payload = options.json === undefined ? undefined : Buffer.from(JSON.stringify(options.json), "utf8");
    const headers: Record<string, string> = { ...options.headers };
    const cookie = this.jar.header();
    if (cookie !== undefined && headers["Cookie"] === undefined) headers["Cookie"] = cookie;
    if (payload !== undefined) {
      headers["Content-Type"] = "application/json";
      headers["Content-Length"] = String(payload.length);
    }

    return new Promise((resolve, reject) => {
      const outgoing = request(
        { host: target.hostname, port: target.port, path: target.pathname + target.search, method, headers },
        (incoming) => {
          const chunks: Buffer[] = [];
          incoming.on("data", (chunk: Buffer) => chunks.push(chunk));
          incoming.once("end", () => {
            const raw = incoming.headers["set-cookie"] ?? [];
            const body = Buffer.concat(chunks).toString("utf8");
            this.jar.absorb(raw);
            resolve({
              status: incoming.statusCode ?? 0,
              headers: incoming.headers,
              setCookies: raw,
              location: incoming.headers.location,
              body,
              json: <T>() => JSON.parse(body) as T,
            });
          });
        },
      );
      outgoing.once("error", reject);
      outgoing.end(payload);
    });
  }
}
