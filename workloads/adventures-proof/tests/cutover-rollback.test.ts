/**
 * G3 S11.3 — the rollback procedure, rehearsed once in the reproducible setup. The procedure is the
 * operator's: the reverse proxy's route for Adventures' API host goes from the edge back to Adventures
 * directly. This rehearses it end to end against a route table that stands in for the proxy:
 *
 *   direct route:  browser → route table → Adventures
 *   cutover route: browser → route table → edge → tap → Adventures
 *
 * The tap sits behind the edge only, so a request is on the cutover route exactly when the tap saw it.
 * The public address never changes, which is why Adventures' OIDC `redirect_uri` and a player's
 * cookies survive a switch in either direction. Nothing here is a claim about a live deployment (S12).
 */
import { afterAll, beforeAll, describe, expect, it } from "vitest";

import { startAdventures } from "./support/adventures.js";
import type { RunningAdventures } from "./support/adventures.js";
import { Browser } from "./support/browser.js";
import { startEdge } from "./support/edge.js";
import type { RunningEdge } from "./support/edge.js";
import { freePort } from "./support/free-port.js";
import { startTestOidcIssuer } from "./support/issuer.js";
import type { TestOidcIssuer } from "./support/issuer.js";
import { startRecordingProxy } from "./support/proxy.js";
import type { RecordingProxy } from "./support/proxy.js";
import { startRouteTable } from "./support/route-table.js";
import type { RouteTable } from "./support/route-table.js";

const SITE_ORIGIN = "http://site.test:5173";
const SESSION_COOKIE = "sza_session";

let issuer: TestOidcIssuer;
let adventures: RunningAdventures;
let tap: RecordingProxy;
let edge: RunningEdge | undefined;
let front: RouteTable;

beforeAll(async () => {
  issuer = await startTestOidcIssuer();
  try {
    // The public address is chosen first: Adventures builds its OIDC redirect_uri from it, and it is
    // the address players keep using through the cutover and after the rollback.
    const publicPort = await freePort();
    adventures = await startAdventures({
      apiUrl: `http://127.0.0.1:${publicPort}`,
      siteUrl: SITE_ORIGIN,
      previewOrigins: [],
      oidcIssuer: issuer.issuer,
      caCertificatePath: issuer.caCertificatePath,
    });
    tap = await startRecordingProxy(adventures.baseAddress);
    edge = await startEdge(await freePort(), tap.baseAddress);
    front = await startRouteTable(publicPort, { direct: adventures.baseAddress, edge: edge.baseAddress }, "direct");
  } catch (thrown) {
    await teardown();
    throw thrown;
  }
});

afterAll(teardown);

async function teardown(): Promise<void> {
  await front?.close();
  await edge?.stop();
  await tap?.close();
  await adventures?.stop();
  await issuer?.close();
}

/** Requests the edge forwarded to Adventures so far. */
function throughEdge(): number {
  return tap.exchanges.length;
}

describe("Cutover and rollback", () => {
  it("S11.3 rolling the proxy back to the direct route restores service, signed-in players included", async () => {
    const browser = new Browser(front.publicAddress);

    // Before the cutover: the direct route. Nothing reaches the edge.
    const catalogue = (await browser.get("/api/campaigns")).json<{ campaigns: { campaignId: string; available?: boolean }[] }>();
    const campaignId = catalogue.campaigns.find((campaign) => campaign.available !== false)?.campaignId;
    expect(campaignId, "Adventures lists a startable campaign").toBeDefined();
    const started = await browser.post("/api/sessions", { json: { campaignId } });
    expect(started.status).toBe(200);
    const guest = (await browser.get("/api/me")).json<{ kind: string; playerId: string }>();
    expect(guest.kind).toBe("guest");
    expect(throughEdge(), "the direct route bypasses the edge").toBe(0);

    // The cutover: the same host now goes to the edge. The guest's cookie is still known.
    front.use("edge");
    const cutOver = (await browser.get("/api/me")).json<{ kind: string; playerId: string }>();
    expect(cutOver.playerId, "a session started before the cutover survives it").toBe(guest.playerId);
    expect(throughEdge(), "the cutover route reaches Adventures through the edge").toBe(1);

    // A player signs in on the cutover route, to the same public address the redirect_uri names.
    issuer.signInAs({ subject: "rollback-player", name: "Katherine Johnson", email: "katherine@players.test" });
    const begun = await browser.get("/api/auth/oidc/start");
    expect(begun.status).toBe(302);
    expect(new URL(begun.location!).searchParams.get("redirect_uri")).toBe(`${front.publicAddress}/api/auth/oidc/callback`);
    const issued = await issuer.get(begun.location!);
    const callback = new URL(issued.location!);
    expect(callback.origin).toBe(front.publicAddress);
    const finished = await browser.get(callback.pathname + callback.search);
    expect(finished.status).toBe(302);
    const signedIn = (await browser.get("/api/me")).json<{ kind: string; displayName: string | null; playerId: string }>();
    expect(signedIn.displayName).toBe("Katherine Johnson");
    expect(browser.jar.has(SESSION_COOKIE)).toBe(true);

    // The edge goes down while the host points at it: the players see the outage, nothing answers.
    await edge!.stop();
    edge = undefined;
    const outage = await browser.get("/api/me");
    expect(outage.status, "the cutover route is down with the edge").toBe(502);

    // The rollback: the one change the operator makes — the route goes back to Adventures directly.
    front.use("direct");
    const before = throughEdge();
    const restored = await browser.get("/api/me");
    expect(restored.status).toBe(200);
    expect(restored.json<{ displayName: string | null; playerId: string }>()).toMatchObject({
      displayName: "Katherine Johnson",
      playerId: signedIn.playerId,
    });
    expect(front.served.at(-1), "the rolled-back host serves from the direct route").toBe("direct");
    expect(throughEdge(), "the rolled-back route does not touch the edge").toBe(before);

    // And it is a working API, not only a readable one: a write succeeds.
    const another = await browser.post("/api/sessions", { json: { campaignId } });
    expect(another.status).toBe(200);
  });
});
