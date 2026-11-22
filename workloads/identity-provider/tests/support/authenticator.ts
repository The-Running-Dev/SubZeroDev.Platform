/**
 * A software WebAuthn authenticator: a P-256 key pair answering Keycloak's registration and
 * assertion challenges the way a platform passkey does (attestation "none", user present and
 * verified). It is what lets the sign-in proof run headless; it says nothing about real browsers or
 * real authenticators, which remain gate G3.
 */
import { createHash, createSign, generateKeyPairSync, randomBytes } from "node:crypto";
import type { KeyObject } from "node:crypto";

/** The CBOR subset WebAuthn needs: unsigned and negative integers, text, bytes and maps. */
function cbor(v: unknown): Buffer {
  const head = (major: number, n: number): Buffer => {
    if (n < 24) return Buffer.from([(major << 5) | n]);
    if (n < 256) return Buffer.from([(major << 5) | 24, n]);
    return Buffer.from([(major << 5) | 25, n >> 8, n & 255]);
  };
  if (typeof v === "number") return v >= 0 ? head(0, v) : head(1, -1 - v);
  if (typeof v === "string") {
    const b = Buffer.from(v, "utf8");
    return Buffer.concat([head(3, b.length), b]);
  }
  if (Buffer.isBuffer(v)) return Buffer.concat([head(2, v.length), v]);
  if (v instanceof Map) return Buffer.concat([head(5, v.size), ...[...v].flatMap(([k, x]) => [cbor(k), cbor(x)])]);
  throw new Error(`cbor: unsupported ${typeof v}`);
}

const FLAG_UP = 0x01;
const FLAG_UV = 0x04;
const FLAG_AT = 0x40;

export class Authenticator {
  readonly credentialId = randomBytes(16);
  readonly label = `soft-${randomBytes(3).toString("hex")}`;
  private readonly key: KeyObject = generateKeyPairSync("ec", { namedCurve: "prime256v1" }).privateKey;
  private readonly origin: string;
  private signCount = 0;
  private userHandle = "";

  /** `origin` is the page origin the browser would report, i.e. Keycloak's public origin. */
  constructor(origin: string) {
    this.origin = origin;
  }

  private authData(rpId: string, flags: number, attested?: Buffer): Buffer {
    const count = Buffer.alloc(4);
    count.writeUInt32BE(this.signCount);
    return Buffer.concat([createHash("sha256").update(rpId).digest(), Buffer.from([flags]), count, ...(attested ? [attested] : [])]);
  }

  /** The fields Keycloak's registration form posts. */
  create(challenge: string, rpId: string, userId: string): Record<string, string> {
    this.userHandle = userId;
    const jwk = this.key.export({ format: "jwk" }) as { x: string; y: string };
    const cose = cbor(new Map<number, unknown>([[1, 2], [3, -7], [-1, 1], [-2, Buffer.from(jwk.x, "base64url")], [-3, Buffer.from(jwk.y, "base64url")]]));
    const idLength = Buffer.alloc(2);
    idLength.writeUInt16BE(this.credentialId.length);
    const attested = Buffer.concat([Buffer.alloc(16), idLength, this.credentialId, cose]);
    const clientData = Buffer.from(JSON.stringify({ type: "webauthn.create", challenge, origin: this.origin, crossOrigin: false }));
    const attestationObject = cbor(new Map<string, unknown>([["fmt", "none"], ["attStmt", new Map()], ["authData", this.authData(rpId, FLAG_UP | FLAG_UV | FLAG_AT, attested)]]));
    return {
      clientDataJSON: clientData.toString("base64url"),
      attestationObject: attestationObject.toString("base64url"),
      publicKeyCredentialId: this.credentialId.toString("base64url"),
      transports: "internal",
      authenticatorAttachment: "platform",
      authenticatorLabel: this.label,
    };
  }

  /** The fields Keycloak's passkey sign-in form posts. */
  get(challenge: string, rpId: string): Record<string, string> {
    this.signCount += 1;
    const authData = this.authData(rpId, FLAG_UP | FLAG_UV);
    const clientData = Buffer.from(JSON.stringify({ type: "webauthn.get", challenge, origin: this.origin, crossOrigin: false }));
    const signer = createSign("SHA256");
    signer.update(Buffer.concat([authData, createHash("sha256").update(clientData).digest()]));
    return {
      clientDataJSON: clientData.toString("base64url"),
      authenticatorData: authData.toString("base64url"),
      signature: signer.sign(this.key).toString("base64url"),
      credentialId: this.credentialId.toString("base64url"),
      userHandle: this.userHandle,
    };
  }
}
