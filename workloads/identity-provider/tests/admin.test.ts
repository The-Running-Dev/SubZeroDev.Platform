/**
 * The admin client waits out a Keycloak that is still bootstrapping. Its readiness check can report
 * UP before the bootstrap admin exists, and the token endpoint then answers 503 "Bootstrap in
 * progress" (seen in CI). Runs against a local stand-in server; no Keycloak needed.
 */
import { createServer } from "node:http";
import type { AddressInfo } from "node:net";
import { afterEach, describe, expect, it } from "vitest";

import { AdminError, KeycloakAdmin } from "../src/admin.ts";

let close: (() => void) | undefined;
afterEach(() => close?.());

/** A token endpoint answering each status in turn, then 200; `/admin/...` answers `[]`. */
async function standIn(statuses: number[]): Promise<{ base: string; tokenCalls: () => number }> {
  let calls = 0;
  const server = createServer((req, res) => {
    if (req.url?.startsWith("/admin/")) return void res.writeHead(200, { "content-type": "application/json" }).end("[]");
    const status = statuses[calls++] ?? 200;
    if (status === 200) return void res.writeHead(200, { "content-type": "application/json" }).end(JSON.stringify({ access_token: "t" }));
    res.writeHead(status, { "retry-after": "1" }).end(status === 503 ? "Bootstrap in progress. Retry in 1 seconds." : "no");
  });
  await new Promise<void>((r) => server.listen(0, "127.0.0.1", r));
  close = () => server.close();
  return { base: `http://127.0.0.1:${(server.address() as AddressInfo).port}`, tokenCalls: () => calls };
}

describe("admin token while Keycloak bootstraps", () => {
  it("retries a 503 until the token is issued", async () => {
    const s = await standIn([503, 503]);
    const kc = new KeycloakAdmin({ baseUrl: s.base, username: "u", password: "p" });
    expect(await kc.get("/realms")).toEqual([]);
    expect(s.tokenCalls()).toBe(3);
  });

  it("does not retry any other failure", async () => {
    const s = await standIn([401]);
    const kc = new KeycloakAdmin({ baseUrl: s.base, username: "u", password: "p" });
    await expect(kc.get("/realms")).rejects.toThrow(AdminError);
    expect(s.tokenCalls()).toBe(1);
  });

  it("gives up on a 503 once the wait has passed", async () => {
    const s = await standIn([503, 503, 503, 503, 503]);
    const kc = new KeycloakAdmin({ baseUrl: s.base, username: "u", password: "p", bootstrapWaitMs: 500 });
    await expect(kc.get("/realms")).rejects.toThrow(/answered 503/);
    expect(s.tokenCalls()).toBe(2);
  });
});
