/**
 * G3 S7 — Adventures, the pinned commit, behind the real game edge in pass-through mode. Every
 * assertion is about what crosses the edge: the workload owns the session, the cookies and the
 * cross-origin policy, and the edge must carry all three without taking part in any of them.
 *
 * The stack is browser → edge → recording proxy → Adventures, with a test OIDC issuer the browser and
 * Adventures both reach directly. The proxy is how a test says Adventures, not the edge, made an
 * answer: the answer is on the wire between them.
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
import type { Exchange, RecordingProxy } from "./support/proxy.js";

const SITE_ORIGIN = "http://site.test:5173";
const PREVIEW_ORIGIN = "http://preview.test:4173";
const FOREIGN_ORIGIN = "http://sibling.test";
const SESSION_COOKIE = "sza_session";
const STATE_COOKIE = "sza_oauth_state";

let issuer: TestOidcIssuer;
let adventures: RunningAdventures;
let proxy: RecordingProxy;
let edge: RunningEdge;
let edgeAddress: string;

beforeAll(async () => {
  issuer = await startTestOidcIssuer();
  try {
    // The edge's port is chosen first: Adventures builds its OIDC redirect_uri from the address a
    // browser reaches it at, and that address is the edge's.
    const edgePort = await freePort();
    edgeAddress = `http://127.0.0.1:${edgePort}`;
    adventures = await startAdventures({
      apiUrl: edgeAddress,
      siteUrl: SITE_ORIGIN,
      previewOrigins: [PREVIEW_ORIGIN],
      oidcIssuer: issuer.issuer,
      caCertificatePath: issuer.caCertificatePath,
    });
    proxy = await startRecordingProxy(adventures.baseAddress);
    edge = await startEdge(edgePort, proxy.baseAddress);
  } catch (thrown) {
    await teardown();
    throw thrown;
  }
});

afterAll(teardown);

async function teardown(): Promise<void> {
  await edge?.stop();
  await proxy?.close();
  await adventures?.stop();
  await issuer?.close();
}

/** Every value of a header the workload sent, byte for byte, read off the wire between edge and workload. */
function sent(exchange: Exchange, name: string): string[] {
  const values: string[] = [];
  for (let at = 0; at < exchange.responseRawHeaders.length; at += 2) {
    if (exchange.responseRawHeaders[at]?.toLowerCase() === name) values.push(exchange.responseRawHeaders[at + 1] ?? "");
  }
  return values;
}

function exchangesFor(pathAndQuery: string, method?: string): Exchange[] {
  return proxy.exchanges.filter(
    (exchange) => exchange.pathAndQuery === pathAndQuery && (method === undefined || exchange.method === method),
  );
}

function cookieValue(setCookies: readonly string[], name: string): string | undefined {
  const line = setCookies.find((entry) => entry.startsWith(`${name}=`));
  return line?.slice(name.length + 1).split(";")[0];
}

async function firstCampaignId(browser: Browser): Promise<string> {
  const listing = (await browser.get("/api/campaigns")).json<{ campaigns: { campaignId: string; available?: boolean }[] }>();
  const startable = listing.campaigns.find((campaign) => campaign.available !== false);
  if (startable === undefined) throw new Error("Adventures listed no startable campaign");
  return startable.campaignId;
}

describe("Adventures through the edge", () => {
  it("S7.1 a guest session persists across three requests carrying the cookie the first response set", async () => {
    const browser = new Browser(edgeAddress);
    const campaignId = await firstCampaignId(browser);
    expect(browser.jar.has(SESSION_COOKIE), "reading the catalogue mints no guest").toBe(false);

    const started = await browser.post("/api/sessions", { json: { campaignId } });
    expect(started.status).toBe(200);
    const token = cookieValue(started.setCookies, SESSION_COOKIE);
    expect(token, "the first response sets the session cookie").toBeTruthy();
    const { sessionId } = started.json<{ sessionId: string }>();
    expect(sessionId).toBeTruthy();

    const answers = [
      await browser.get("/api/me"),
      await browser.get("/api/saves"),
      await browser.get(`/api/sessions/${sessionId}/scene`),
    ];

    for (const answer of answers) {
      expect(answer.status).toBe(200);
      expect(answer.setCookies, "a known session is not re-minted").toEqual([]);
    }
    const me = answers[0]!.json<{ kind: string; playerId: string }>();
    expect(me.kind).toBe("guest");
    expect(me.playerId).toBeTruthy();

    // Adventures received the very cookie it set: the edge carried it in, unchanged.
    for (const path of ["/api/me", "/api/saves", `/api/sessions/${sessionId}/scene`]) {
      const seen = exchangesFor(path).at(-1);
      expect(seen?.requestHeaders["cookie"], path).toBe(`${SESSION_COOKIE}=${token}`);
    }

    // And Adventures' own Set-Cookie reached the browser as it left the workload.
    const mint = exchangesFor("/api/sessions", "POST").at(-1)!;
    expect(started.setCookies).toEqual(sent(mint, "set-cookie"));
  });

  it("S7.2 an OIDC sign-in completes through the edge and the signed-in session is recognised", async () => {
    const browser = new Browser(edgeAddress);
    issuer.signInAs({ subject: "oidc-player-7", name: "Grace Hopper", email: "grace@players.test" });

    const started = await browser.get("/api/auth/oidc/start");
    expect(started.status).toBe(302);
    expect(started.location).toBeDefined();
    const authorize = new URL(started.location!);
    expect(`${authorize.origin}${authorize.pathname}`).toBe(`${issuer.issuer}/authorize`);
    expect(authorize.searchParams.get("redirect_uri")).toBe(`${edgeAddress}/api/auth/oidc/callback`);
    expect(authorize.searchParams.get("code_challenge_method")).toBe("S256");

    const guestToken = cookieValue(started.setCookies, SESSION_COOKIE);
    expect(guestToken, "a guest is minted for the sign-in to upgrade").toBeTruthy();
    const state = started.setCookies.find((entry) => entry.startsWith(`${STATE_COOKIE}=`));
    expect(state, "the oauth state cookie reaches the browser").toBeDefined();
    expect(state).toMatch(/HttpOnly/i);
    expect(state).toMatch(/SameSite=Lax/i);

    // The Location and the state cookie are what Adventures sent, verbatim.
    const startExchange = exchangesFor("/api/auth/oidc/start").at(-1)!;
    expect(sent(startExchange, "location")).toEqual([started.location]);
    expect(started.setCookies).toEqual(sent(startExchange, "set-cookie"));

    // The browser follows the redirect to the issuer, which sends it back to the edge with a code.
    const issued = await issuer.get(started.location!);
    expect(issued.status).toBe(302);
    const callback = new URL(issued.location!);
    expect(callback.origin).toBe(edgeAddress);
    expect(callback.searchParams.get("state")).toBe(authorize.searchParams.get("state"));

    const finished = await browser.get(callback.pathname + callback.search);
    expect(finished.status).toBe(302);
    expect(finished.location).toBe(SITE_ORIGIN);
    const finishExchange = exchangesFor(callback.pathname + callback.search).at(-1)!;
    expect(sent(finishExchange, "location")).toEqual([finished.location]);

    const cleared = finished.setCookies.find((entry) => entry.startsWith(`${STATE_COOKIE}=`));
    expect(cleared, "the state cookie is cleared on the way out").toMatch(/Expires=Thu, 01 Jan 1970|Max-Age=0/i);
    expect(browser.jar.has(STATE_COOKIE)).toBe(false);
    const signedInToken = cookieValue(finished.setCookies, SESSION_COOKIE);
    expect(signedInToken, "the session cookie is rotated at sign-in").toBeTruthy();
    expect(signedInToken).not.toBe(guestToken);

    const me = (await browser.get("/api/me")).json<{ kind: string; displayName: string | null }>();
    expect(me.kind).not.toBe("guest");
    expect(me.displayName).toBe("Grace Hopper");
    expect(issuer.tokensIssued).toBeGreaterThanOrEqual(1);
  });

  it("S7.3 a write from a second origin succeeds; a write from a disallowed origin is refused by Adventures", async () => {
    const browser = new Browser(edgeAddress);
    const campaignId = await firstCampaignId(browser);
    await browser.post("/api/sessions", { json: { campaignId } });
    expect(browser.jar.has(SESSION_COOKIE)).toBe(true);

    // The preflight is Adventures' to answer: it reaches the workload and its answer comes back.
    const preflight = await browser.send("OPTIONS", "/api/sessions", {
      headers: { Origin: PREVIEW_ORIGIN, "Access-Control-Request-Method": "POST" },
    });
    expect(preflight.status).toBeLessThan(300);
    expect(preflight.headers["access-control-allow-origin"]).toBe(PREVIEW_ORIGIN);
    expect(preflight.headers["access-control-allow-credentials"]).toBe("true");
    expect(exchangesFor("/api/sessions", "OPTIONS").at(-1)?.requestHeaders["origin"]).toBe(PREVIEW_ORIGIN);

    const allowed = await browser.post("/api/sessions", { json: { campaignId }, headers: { Origin: PREVIEW_ORIGIN } });
    expect(allowed.status).toBe(200);
    expect(allowed.headers["access-control-allow-origin"]).toBe(PREVIEW_ORIGIN);
    expect(allowed.headers["access-control-allow-credentials"]).toBe("true");
    expect(allowed.json<{ sessionId: string }>().sessionId).toBeTruthy();

    const before = proxy.exchanges.length;
    const refused = await browser.post("/api/sessions", { json: { campaignId }, headers: { Origin: FOREIGN_ORIGIN } });
    expect(refused.status).toBe(403);
    expect(refused.json<{ error: { operation: string; code: string } }>().error).toEqual({
      operation: "origin_check",
      code: "forbidden_origin",
    });

    // The refusal was made by Adventures: the request reached it, with the foreign Origin and the
    // cookie, and the 403 with its body was Adventures' own answer on the wire.
    const reached = proxy.exchanges.slice(before);
    expect(reached).toHaveLength(1);
    expect(reached[0]?.requestHeaders["origin"]).toBe(FOREIGN_ORIGIN);
    expect(reached[0]?.requestHeaders["cookie"]).toContain(`${SESSION_COOKIE}=`);
    expect(reached[0]?.status).toBe(403);
    expect(JSON.parse(reached[0]?.body ?? "{}")).toEqual({ error: { operation: "origin_check", code: "forbidden_origin" } });
  });
});
