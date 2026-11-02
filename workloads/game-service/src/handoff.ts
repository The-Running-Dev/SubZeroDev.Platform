/**
 * The handoff verifier (`design/10-design.md`, "The handoff assertion"; `design/20-contract.md`,
 * "game-service (TypeScript)"). Every non-probe request presents a compact JWS the edge signed; this
 * module decides whether it verifies and, when it does, which owner it names.
 *
 * It reads nothing but `IncomingRequestLine` and its own settings, keeps no state between calls
 * (the design's "The verifier is stateless"), and has no setting that turns it off (invariant 110).
 * The checks run in the design's order: the key id, the signature against that key, then the
 * algorithm, issuer, audience, expiry, issued-at, lifetime, method, path and query, kind and owner.
 */
import { createPublicKey, verify } from "node:crypto";
import type { KeyObject } from "node:crypto";
import { readdirSync, readFileSync } from "node:fs";
import { basename, join } from "node:path";

import { err, ok } from "./types.js";
import type { HandoffRefusal, HandoffSettings, IncomingRequestLine, OwnerKey, Outcome } from "./types.js";

/** `SubZeroDev-Principal-Assertion`, as Node presents every inbound header name: lowercased. */
export const ASSERTION_HEADER = "subzerodev-principal-assertion";

export const ASSERTION_ALGORITHM = "ES256";
export const ASSERTION_TYPE = "szd-principal+jwt";
export const ACCOUNT_KIND = "Account";

const BASE64URL = /^[A-Za-z0-9_-]+$/;
/** ES256's signature is r‖s, 32 bytes each (RFC 7518 §3.4). */
const ES256_SIGNATURE_BYTES = 64;

interface Claims {
  readonly iss: string;
  readonly aud: string;
  readonly pis: string;
  readonly psub: string;
  readonly pkind: string;
  readonly htm: string;
  readonly htu: string;
  readonly iat: number;
  readonly exp: number;
}

function decodeObject(segment: string): Record<string, unknown> | null {
  try {
    const parsed: unknown = JSON.parse(Buffer.from(segment, "base64url").toString("utf8"));
    return typeof parsed === "object" && parsed !== null && !Array.isArray(parsed)
      ? (parsed as Record<string, unknown>)
      : null;
  } catch {
    return null;
  }
}

function claimsFrom(payload: Record<string, unknown>): Claims | null {
  const strings = ["iss", "aud", "pis", "psub", "pkind", "htm", "htu", "rid"] as const;
  for (const name of strings) {
    if (typeof payload[name] !== "string") return null;
  }
  for (const name of ["iat", "exp"] as const) {
    if (!Number.isSafeInteger(payload[name])) return null;
  }
  return payload as unknown as Claims;
}

export function verifyHandoff(
  settings: HandoffSettings,
  request: IncomingRequestLine,
  now: Date,
): Outcome<OwnerKey, HandoffRefusal> {
  const assertion = request.assertion;
  if (assertion === undefined || assertion.length === 0) return err("missing");

  const segments = assertion.split(".");
  if (segments.length !== 3 || !segments.every((segment) => BASE64URL.test(segment))) return err("malformed");
  const [encodedHeader, encodedPayload, encodedSignature] = segments as [string, string, string];

  const header = decodeObject(encodedHeader);
  if (header === null || typeof header["kid"] !== "string" || typeof header["alg"] !== "string") {
    return err("malformed");
  }
  // A token of some other type is not a handoff assertion at all, so it is malformed rather than
  // any of the named claim refusals.
  if (header["typ"] !== ASSERTION_TYPE) return err("malformed");

  const key = settings.keys.get(header["kid"]);
  if (key === undefined) return err("unknown_key");

  const signature = Buffer.from(encodedSignature, "base64url");
  let signed = false;
  if (signature.length === ES256_SIGNATURE_BYTES) {
    try {
      signed = verify(
        "sha256",
        Buffer.from(`${encodedHeader}.${encodedPayload}`, "ascii"),
        { key, dsaEncoding: "ieee-p1363" },
        signature,
      );
    } catch {
      signed = false;
    }
  }
  if (!signed) return err("bad_signature");

  // Checked after the signature, as the design orders it: the key only ever verifies ES256, so a
  // header naming another algorithm over bytes that key did sign is the one way to reach this.
  if (header["alg"] !== ASSERTION_ALGORITHM) return err("wrong_algorithm");

  const payload = decodeObject(encodedPayload);
  const claims = payload === null ? null : claimsFrom(payload);
  if (claims === null) return err("malformed");

  if (claims.iss !== settings.issuer) return err("wrong_issuer");
  if (claims.aud !== settings.audience) return err("wrong_audience");

  const nowSeconds = now.getTime() / 1000;
  const tolerance = settings.clockToleranceSeconds;
  if (nowSeconds >= claims.exp + tolerance) return err("expired");
  if (claims.iat > nowSeconds + tolerance) return err("not_yet_valid");
  // An expiry at or before issue is not a lifetime of any length.
  if (claims.exp <= claims.iat) return err("malformed");
  if (claims.exp - claims.iat > settings.maxLifetimeSeconds) return err("lifetime_exceeded");

  if (claims.htm !== request.method || claims.htu !== request.pathAndQuery) return err("request_mismatch");
  if (claims.pkind !== ACCOUNT_KIND) return err("unsupported_kind");
  // The reserved empty owner is never a principal's (invariant 117).
  if (claims.pis.length === 0 || claims.psub.length === 0) return err("empty_owner");

  return ok({ issuer: claims.pis, subject: claims.psub });
}

// ---------------------------------------------------------------------------- configuration

export const HANDOFF_KEYS_DIRECTORY_VARIABLE = "GAME_SERVICE_HANDOFF_KEYS_DIR";
export const HANDOFF_ISSUER_VARIABLE = "GAME_SERVICE_HANDOFF_ISSUER";
export const HANDOFF_AUDIENCE_VARIABLE = "GAME_SERVICE_HANDOFF_AUDIENCE";
export const HANDOFF_CLOCK_TOLERANCE_VARIABLE = "GAME_SERVICE_HANDOFF_CLOCK_TOLERANCE_SECONDS";
export const HANDOFF_MAX_LIFETIME_VARIABLE = "GAME_SERVICE_HANDOFF_MAX_LIFETIME_SECONDS";

const DEFAULT_CLOCK_TOLERANCE_SECONDS = 30;
const DEFAULT_MAX_LIFETIME_SECONDS = 60;

function seconds(raw: string | undefined, fallback: number): number | null {
  if (raw === undefined || raw.trim().length === 0) return fallback;
  return /^\d+$/.test(raw.trim()) ? Number.parseInt(raw.trim(), 10) : null;
}

/** One P-256 public key per `<key id>.pem` file in the named directory. Anything else in it — a key
 *  of another curve, a private key, an unreadable file — refuses the whole set rather than being
 *  skipped, because a skipped key is a rotation that silently did not happen. */
function readKeys(directory: string): ReadonlyMap<string, KeyObject> | null {
  let names: string[];
  try {
    names = readdirSync(directory).filter((name) => name.endsWith(".pem"));
  } catch {
    return null;
  }
  const keys = new Map<string, KeyObject>();
  for (const name of names.sort()) {
    try {
      const key = createPublicKey(readFileSync(join(directory, name)));
      if (key.asymmetricKeyType !== "ec" || key.asymmetricKeyDetails?.namedCurve !== "prime256v1") return null;
      keys.set(basename(name, ".pem"), key);
    } catch {
      return null;
    }
  }
  return keys;
}

/** The handoff settings from the environment, or the one setting that is missing or invalid. The
 *  key set is checked first, so a process given nothing names the setting invariant 110 is about. */
export function handoffSettingsFromEnv(
  env: NodeJS.ProcessEnv,
): Outcome<HandoffSettings, { readonly setting: string }> {
  const directory = env[HANDOFF_KEYS_DIRECTORY_VARIABLE];
  const keys = directory === undefined || directory.trim().length === 0 ? null : readKeys(directory);
  if (keys === null || keys.size === 0) return err({ setting: HANDOFF_KEYS_DIRECTORY_VARIABLE });

  const issuer = env[HANDOFF_ISSUER_VARIABLE] ?? "";
  if (issuer.trim().length === 0) return err({ setting: HANDOFF_ISSUER_VARIABLE });
  const audience = env[HANDOFF_AUDIENCE_VARIABLE] ?? "";
  if (audience.trim().length === 0) return err({ setting: HANDOFF_AUDIENCE_VARIABLE });

  const clockToleranceSeconds = seconds(env[HANDOFF_CLOCK_TOLERANCE_VARIABLE], DEFAULT_CLOCK_TOLERANCE_SECONDS);
  if (clockToleranceSeconds === null) return err({ setting: HANDOFF_CLOCK_TOLERANCE_VARIABLE });
  const maxLifetimeSeconds = seconds(env[HANDOFF_MAX_LIFETIME_VARIABLE], DEFAULT_MAX_LIFETIME_SECONDS);
  if (maxLifetimeSeconds === null || maxLifetimeSeconds === 0) return err({ setting: HANDOFF_MAX_LIFETIME_VARIABLE });

  return ok({ issuer, audience, keys, clockToleranceSeconds, maxLifetimeSeconds });
}
