/**
 * The proof harness's half of the handoff (`design/20-contract.md`, "Proof harness"): a test key
 * pair, the assertion minter G2's harnesses and replay present (invariant 124), and the settings a
 * workload under test verifies against. Never imported by `src/`.
 */
import { generateKeyPairSync, sign } from "node:crypto";
import { mkdtempSync, writeFileSync } from "node:fs";
import { request as httpRequest, createServer } from "node:http";
import type { IncomingHttpHeaders, Server } from "node:http";
import { tmpdir } from "node:os";
import { join } from "node:path";

import { ASSERTION_HEADER } from "../../src/handoff.js";
import type { HandoffSettings, IncomingRequestLine, OwnerKey } from "../../src/types.js";

export { ASSERTION_HEADER };

export interface TestSigningKey {
  readonly keyId: string;
  readonly privateKey: import("node:crypto").KeyObject;
}

export interface AssertionClaims {
  readonly iss: string;
  readonly aud: string;
  readonly pis: string;
  readonly psub: string;
  readonly pkind: string;
  readonly htm: string;
  readonly htu: string;
  readonly rid: string;
  readonly iat: number;
  readonly exp: number;
}

export const TEST_ISSUER = "https://edge.test/handoff";
export const TEST_AUDIENCE = "game-service.test";
export const TEST_OWNER: OwnerKey = { issuer: "https://issuer.test", subject: "player-1" };

const pair = generateKeyPairSync("ec", { namedCurve: "prime256v1" });
export const TEST_KEY: TestSigningKey = { keyId: "test-key-1", privateKey: pair.privateKey };
export const TEST_PUBLIC_KEY = pair.publicKey;

/** A second pair no workload under test is configured with — what a forged signature is made of. */
const stranger = generateKeyPairSync("ec", { namedCurve: "prime256v1" });
export const STRANGER_KEY: TestSigningKey = { keyId: TEST_KEY.keyId, privateKey: stranger.privateKey };

export const TEST_HANDOFF: HandoffSettings = {
  issuer: TEST_ISSUER,
  audience: TEST_AUDIENCE,
  keys: new Map([[TEST_KEY.keyId, TEST_PUBLIC_KEY]]),
  clockToleranceSeconds: 30,
  maxLifetimeSeconds: 60,
};

const DEFAULT_LIFETIME_SECONDS = 30;

function segment(value: unknown): string {
  return Buffer.from(JSON.stringify(value), "utf8").toString("base64url");
}

/** A compact JWS over any header and payload, signed with ES256 whatever the header claims — the
 *  building block for the refusal cases a well-formed assertion cannot reach. */
export function signCompact(key: TestSigningKey, header: unknown, payload: unknown): string {
  const signingInput = `${segment(header)}.${segment(payload)}`;
  const signature = sign("sha256", Buffer.from(signingInput, "ascii"), {
    key: key.privateKey,
    dsaEncoding: "ieee-p1363",
  });
  return `${signingInput}.${signature.toString("base64url")}`;
}

export function claimsFor(
  owner: OwnerKey,
  request: IncomingRequestLine,
  now: Date,
  overrides: Partial<AssertionClaims> = {},
): AssertionClaims {
  const iat = Math.floor(now.getTime() / 1000);
  return {
    iss: TEST_ISSUER,
    aud: TEST_AUDIENCE,
    pis: owner.issuer,
    psub: owner.subject,
    pkind: "Account",
    htm: request.method,
    htu: request.pathAndQuery,
    rid: "test-request",
    iat,
    exp: iat + DEFAULT_LIFETIME_SECONDS,
    ...overrides,
  };
}

export function mintTestAssertion(
  key: TestSigningKey,
  owner: OwnerKey,
  request: IncomingRequestLine,
  now: Date,
  overrides?: Partial<AssertionClaims>,
): string {
  return signCompact(
    key,
    { alg: "ES256", kid: key.keyId, typ: "szd-principal+jwt" },
    claimsFor(owner, request, now, overrides),
  );
}

/** The one header a harness request adds: a fresh assertion bound to exactly this request line. */
export function assertionFor(method: string, pathAndQuery: string, owner: OwnerKey = TEST_OWNER): string {
  return mintTestAssertion(TEST_KEY, owner, { method, pathAndQuery, assertion: undefined }, new Date());
}

export function signedHeaders(
  method: string,
  pathAndQuery: string,
  headers: Record<string, string> = {},
): Record<string, string> {
  return { ...headers, [ASSERTION_HEADER]: assertionFor(method, pathAndQuery) };
}

/** `fetch` with an assertion bound to the request's own method, path and query — for the tests
 *  that address a workload's socket directly, where no edge stands to sign for them. */
export function signedFetch(url: string, init: RequestInit = {}): Promise<Response> {
  const parsed = new URL(url);
  const headers = new Headers(init.headers);
  headers.set(ASSERTION_HEADER, assertionFor(init.method ?? "GET", `${parsed.pathname}${parsed.search}`));
  return fetch(url, { ...init, headers });
}

let keysDirectory: string | null = null;

/** The environment a spawned workload reads its handoff settings from. The public key is written
 *  once per test process, under the operating system's temporary directory. */
export function handoffEnvironment(): Record<string, string> {
  if (keysDirectory === null) {
    keysDirectory = mkdtempSync(join(tmpdir(), "game-service-handoff-"));
    writeFileSync(
      join(keysDirectory, `${TEST_KEY.keyId}.pem`),
      TEST_PUBLIC_KEY.export({ type: "spki", format: "pem" }),
    );
  }
  return {
    GAME_SERVICE_HANDOFF_KEYS_DIR: keysDirectory,
    GAME_SERVICE_HANDOFF_ISSUER: TEST_ISSUER,
    GAME_SERVICE_HANDOFF_AUDIENCE: TEST_AUDIENCE,
  };
}

const HOP_BY_HOP = new Set(["connection", "keep-alive", "transfer-encoding", "host", ASSERTION_HEADER]);

export interface SigningProxy {
  readonly baseAddress: string;
  close(): Promise<void>;
}

/**
 * Stands where S3's handoff signer will: between the edge and the workload, adding an assertion
 * bound to each forwarded request line and passing everything else through unchanged. The edge
 * signs nothing until S3, so G2's edge-fronted proofs reach the verifier through this instead.
 * Any caller-supplied assertion is dropped, as the edge's own signer will drop it.
 */
export async function startSigningProxy(targetBaseAddress: string): Promise<SigningProxy> {
  const target = new URL(targetBaseAddress);
  const server: Server = createServer((incoming, outgoing) => {
    const headers: IncomingHttpHeaders = {};
    for (const [name, value] of Object.entries(incoming.headers)) {
      if (!HOP_BY_HOP.has(name) && value !== undefined) headers[name] = value;
    }
    const method = incoming.method ?? "GET";
    const pathAndQuery = incoming.url ?? "/";
    headers[ASSERTION_HEADER] = assertionFor(method, pathAndQuery);

    const forwarded = httpRequest(
      { host: target.hostname, port: target.port, method, path: pathAndQuery, headers },
      (answer) => {
        const answered: IncomingHttpHeaders = {};
        for (const [name, value] of Object.entries(answer.headers)) {
          if (!HOP_BY_HOP.has(name) && value !== undefined) answered[name] = value;
        }
        outgoing.writeHead(answer.statusCode ?? 502, answered);
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
    close: () =>
      new Promise<void>((resolve) => {
        server.closeAllConnections();
        server.close(() => resolve());
      }),
  };
}
