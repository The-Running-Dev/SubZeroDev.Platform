/**
 * G3 S7 — a test OpenID Connect issuer: discovery, an authorize endpoint, a token endpoint and a
 * JWKS, signing ES256 id tokens. It stands where a real identity provider stands for Adventures
 * (`OIDC_ISSUER_URL`), so the edge carries a genuine authorization-code-with-PKCE round trip.
 *
 * It serves HTTPS with a certificate made for the run: Adventures' OIDC client refuses a plain
 * `http:` issuer, and that refusal is Adventures', not something this proof may switch off. The
 * certificate reaches Adventures through `NODE_EXTRA_CA_CERTS` (`caCertificatePath`) and the proof's
 * own requests through `ca`. Never imported by anything but the tests.
 */
import { createHash, generateKeyPairSync, randomBytes, sign } from "node:crypto";
import type { KeyObject } from "node:crypto";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { createServer, request as httpsRequest } from "node:https";
import type { IncomingMessage, Server, ServerResponse } from "node:http";
import { tmpdir } from "node:os";
import { join } from "node:path";
import selfsigned from "selfsigned";

import { freePort } from "./free-port.js";

export const CLIENT_ID = "adventures-proof-client";
export const CLIENT_SECRET = "adventuresproofsecret0123456789";
const KEY_ID = "issuer-key-1";

export interface Identity {
  readonly subject: string;
  readonly name: string;
  readonly email: string;
}

/** What the issuer saw reach its authorize endpoint — the redirect a player's browser followed. */
export interface AuthorizationRequest {
  readonly redirectUri: string;
  readonly state: string;
  readonly codeChallenge: string;
  readonly codeChallengeMethod: string;
  readonly clientId: string;
}

export interface TestOidcIssuer {
  /** The issuer identifier, `OIDC_ISSUER_URL` as Adventures reads it. */
  readonly issuer: string;
  /** Where `NODE_EXTRA_CA_CERTS` should point, so a process that dials this issuer trusts it. */
  readonly caCertificatePath: string;
  readonly caCertificate: string;
  readonly authorizations: readonly AuthorizationRequest[];
  /** How many token requests succeeded. */
  readonly tokensIssued: number;
  /** The identity the next authorize request will sign in as. */
  signInAs(identity: Identity): void;
  /** One request to the issuer, as the browser a player drives would make it. Never follows redirects. */
  get(url: string): Promise<{ status: number; location: string | undefined }>;
  close(): Promise<void>;
}

async function readBody(incoming: IncomingMessage): Promise<string> {
  const chunks: Buffer[] = [];
  for await (const chunk of incoming) chunks.push(chunk as Buffer);
  return Buffer.concat(chunks).toString("utf8");
}

function json(outgoing: ServerResponse, status: number, value: unknown): void {
  outgoing.writeHead(status, { "Content-Type": "application/json", "Cache-Control": "no-store" });
  outgoing.end(JSON.stringify(value));
}

function segment(value: unknown): string {
  return Buffer.from(JSON.stringify(value), "utf8").toString("base64url");
}

function idToken(privateKey: KeyObject, claims: Record<string, unknown>): string {
  const input = `${segment({ alg: "ES256", kid: KEY_ID, typ: "JWT" })}.${segment(claims)}`;
  const signature = sign("sha256", Buffer.from(input, "ascii"), { key: privateKey, dsaEncoding: "ieee-p1363" });
  return `${input}.${signature.toString("base64url")}`;
}

export async function startTestOidcIssuer(): Promise<TestOidcIssuer> {
  const port = await freePort();
  const issuer = `https://127.0.0.1:${port}`;

  const pems = await selfsigned.generate([{ name: "commonName", value: "127.0.0.1" }], {
    keyType: "ec",
    curve: "P-256",
    algorithm: "sha256",
    notAfterDate: new Date(Date.now() + 24 * 60 * 60 * 1000),
    extensions: [{ name: "subjectAltName", altNames: [{ type: 7, ip: "127.0.0.1" }] }],
  });
  const directory = mkdtempSync(join(tmpdir(), "adventures-proof-issuer-"));
  const caCertificatePath = join(directory, "issuer-ca.pem");
  writeFileSync(caCertificatePath, pems.cert);

  const pair = generateKeyPairSync("ec", { namedCurve: "prime256v1" });
  const jwks = { keys: [{ ...pair.publicKey.export({ format: "jwk" }), kid: KEY_ID, use: "sig", alg: "ES256" }] };

  const authorizations: AuthorizationRequest[] = [];
  const codes = new Map<string, { challenge: string; redirectUri: string; identity: Identity }>();
  let tokensIssued = 0;
  let signedInAs: Identity = { subject: "oidc-player-1", name: "Ada Lovelace", email: "ada@players.test" };

  const server: Server = createServer({ key: pems.private, cert: pems.cert }, (incoming, outgoing) => {
    void (async () => {
      const url = new URL(incoming.url ?? "/", issuer);

      if (url.pathname === "/.well-known/openid-configuration") {
        json(outgoing, 200, {
          issuer,
          authorization_endpoint: `${issuer}/authorize`,
          token_endpoint: `${issuer}/token`,
          jwks_uri: `${issuer}/jwks`,
          response_types_supported: ["code"],
          subject_types_supported: ["public"],
          id_token_signing_alg_values_supported: ["ES256"],
          token_endpoint_auth_methods_supported: ["client_secret_basic"],
          code_challenge_methods_supported: ["S256"],
          scopes_supported: ["openid", "profile", "email"],
        });
        return;
      }

      if (url.pathname === "/jwks") {
        json(outgoing, 200, jwks);
        return;
      }

      if (url.pathname === "/authorize") {
        const parameters = url.searchParams;
        const request: AuthorizationRequest = {
          redirectUri: parameters.get("redirect_uri") ?? "",
          state: parameters.get("state") ?? "",
          codeChallenge: parameters.get("code_challenge") ?? "",
          codeChallengeMethod: parameters.get("code_challenge_method") ?? "",
          clientId: parameters.get("client_id") ?? "",
        };
        authorizations.push(request);
        if (request.clientId !== CLIENT_ID || request.redirectUri === "" || request.codeChallengeMethod !== "S256") {
          json(outgoing, 400, { error: "invalid_request" });
          return;
        }
        const code = randomBytes(24).toString("base64url");
        codes.set(code, { challenge: request.codeChallenge, redirectUri: request.redirectUri, identity: signedInAs });
        const back = new URL(request.redirectUri);
        back.searchParams.set("code", code);
        back.searchParams.set("state", request.state);
        outgoing.writeHead(302, { Location: back.toString() });
        outgoing.end();
        return;
      }

      if (url.pathname === "/token" && incoming.method === "POST") {
        const credentials = /^Basic (.+)$/.exec(incoming.headers.authorization ?? "")?.[1];
        const decoded = credentials ? Buffer.from(credentials, "base64").toString("utf8") : "";
        if (decoded !== `${CLIENT_ID}:${CLIENT_SECRET}`) {
          json(outgoing, 401, { error: "invalid_client" });
          return;
        }
        const body = new URLSearchParams(await readBody(incoming));
        const code = body.get("code") ?? "";
        const issued = codes.get(code);
        const challenge = createHash("sha256")
          .update(body.get("code_verifier") ?? "")
          .digest("base64url");
        if (
          body.get("grant_type") !== "authorization_code" ||
          issued === undefined ||
          issued.redirectUri !== body.get("redirect_uri") ||
          issued.challenge !== challenge
        ) {
          json(outgoing, 400, { error: "invalid_grant" });
          return;
        }
        codes.delete(code);
        tokensIssued += 1;
        const now = Math.floor(Date.now() / 1000);
        json(outgoing, 200, {
          access_token: randomBytes(16).toString("base64url"),
          token_type: "Bearer",
          expires_in: 300,
          id_token: idToken(pair.privateKey, {
            iss: issuer,
            aud: CLIENT_ID,
            sub: issued.identity.subject,
            name: issued.identity.name,
            email: issued.identity.email,
            iat: now,
            exp: now + 300,
          }),
        });
        return;
      }

      json(outgoing, 404, { error: "not_found" });
    })();
  });

  await new Promise<void>((resolve, reject) => {
    server.once("error", reject);
    server.listen(port, "127.0.0.1", () => resolve());
  });

  return {
    issuer,
    caCertificatePath,
    caCertificate: pems.cert,
    authorizations,
    get tokensIssued() {
      return tokensIssued;
    },
    signInAs(identity) {
      signedInAs = identity;
    },
    get(target) {
      return new Promise((resolve, reject) => {
        const outgoing = httpsRequest(target, { ca: pems.cert, method: "GET" }, (answer) => {
          answer.resume();
          answer.once("end", () => resolve({ status: answer.statusCode ?? 0, location: answer.headers.location }));
        });
        outgoing.once("error", reject);
        outgoing.end();
      });
    },
    close: () =>
      new Promise<void>((resolve) => {
        server.closeAllConnections();
        server.close(() => {
          rmSync(directory, { recursive: true, force: true });
          resolve();
        });
      }),
  };
}
