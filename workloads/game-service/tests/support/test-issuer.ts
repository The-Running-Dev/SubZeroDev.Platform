/**
 * G3 S3 — the test issuer: an ES256 key pair, the JWKS the edge pins for it, and the tokens it
 * signs. The edge never discovers anything (S3.8): the public half travels as `SigningKeys`, so the
 * only keys the edge trusts are the ones a test put there. Never imported by `src/`.
 */
import { generateKeyPairSync, sign } from "node:crypto";
import type { KeyObject } from "node:crypto";

export const TOKEN_ISSUER = "https://issuer.test";
export const TOKEN_AUDIENCE = "game.test";
export const ISSUER_KEY_ID = "issuer-key-1";

export interface TestIssuer {
  readonly privateKey: KeyObject;
  /** The JWKS document the edge pins. */
  readonly jwks: string;
}

export function createTestIssuer(): TestIssuer {
  const pair = generateKeyPairSync("ec", { namedCurve: "prime256v1" });
  const jwk = pair.publicKey.export({ format: "jwk" });
  return {
    privateKey: pair.privateKey,
    jwks: JSON.stringify({ keys: [{ ...jwk, kid: ISSUER_KEY_ID, use: "sig", alg: "ES256" }] }),
  };
}

export interface TokenOptions {
  readonly key?: KeyObject;
  readonly issuer?: string;
  readonly audience?: string;
  /** The `kid` header; defaults to the one key the edge pins. */
  readonly keyId?: string;
  /** Seconds relative to now at which `iat` and `nbf` are set; negative is the past. Default 0. */
  readonly issuedOffsetSeconds?: number;
  /** Seconds after `iat` that the token lives. Default 300. */
  readonly lifetimeSeconds?: number;
}

function segment(value: unknown): string {
  return Buffer.from(JSON.stringify(value), "utf8").toString("base64url");
}

/** A five-minute bearer token for `subject`, signed by `issuer.privateKey` unless `options.key`
 *  stands in for a key the edge does not pin. */
export function signToken(issuer: TestIssuer, subject: string, options: TokenOptions = {}): string {
  const now = Math.floor(Date.now() / 1000) + (options.issuedOffsetSeconds ?? 0);
  const input = `${segment({ alg: "ES256", kid: options.keyId ?? ISSUER_KEY_ID, typ: "JWT" })}.${segment({
    iss: options.issuer ?? TOKEN_ISSUER,
    sub: subject,
    aud: options.audience ?? TOKEN_AUDIENCE,
    iat: now,
    nbf: now,
    exp: now + (options.lifetimeSeconds ?? 300),
  })}`;
  const signature = sign("sha256", Buffer.from(input, "ascii"), {
    key: options.key ?? issuer.privateKey,
    dsaEncoding: "ieee-p1363",
  });
  return `${input}.${signature.toString("base64url")}`;
}
