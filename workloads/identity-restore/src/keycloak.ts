/**
 * A minimal Keycloak admin-API client over an injectable transport, so the recorder and the restore run
 * unchanged against Keycloak or against the in-memory realm the unit tests use. TLS trust for a private
 * CA comes from `NODE_EXTRA_CA_CERTS`, which Node's `fetch` honours.
 */

export interface Reply { readonly status: number; readonly body: string }
export type Transport = (method: string, url: string, headers: Record<string, string>, body?: string) => Promise<Reply>;

export const fetchTransport: Transport = async (method, url, headers, body) => {
  const r = await fetch(url, { method, headers, ...(body === undefined ? {} : { body }) });
  return { status: r.status, body: await r.text() };
};

export class KeycloakError extends Error {
  readonly status: number;
  constructor(message: string, status: number) { super(message); this.status = status; }
}

export interface ClientCredentials {
  readonly baseUrl: string;
  /** The realm the client is registered in: the restored realm for the recorder, `master` for the restore. */
  readonly tokenRealm: string;
  readonly clientId: string;
  readonly clientSecret: string;
}

export class Keycloak {
  readonly #creds: ClientCredentials;
  readonly #transport: Transport;
  #token: { value: string; expiresAtMs: number } | undefined;

  constructor(creds: ClientCredentials, transport: Transport = fetchTransport) {
    this.#creds = creds;
    this.#transport = transport;
  }

  get clientId(): string { return this.#creds.clientId; }

  /** The ways an admin event can name this client. Keycloak 26.8.1 puts the client's internal id (a
   *  UUID), not its `clientId`, in `authDetails.clientId` (G1), so a filter on the `clientId` alone never
   *  matches the restore's own changes. Needs read access to the token realm's clients. */
  async identities(): Promise<string[]> {
    const { tokenRealm, clientId } = this.#creds;
    const found = await this.json<Array<{ id: string; clientId: string }>>(tokenRealm, `/clients?clientId=${encodeURIComponent(clientId)}`);
    const own = found.find((c) => c.clientId === clientId);
    if (!own) throw new KeycloakError(`client ${clientId} not found in realm ${tokenRealm}: cannot tell the restore's own admin events apart`, 404);
    return [clientId, own.id];
  }

  /** Requests a fresh token. Every call is a `CLIENT_LOGIN` event in the token realm, which is what the
   *  recorder uses as its canary. */
  async freshToken(): Promise<string> {
    const { baseUrl, tokenRealm, clientId, clientSecret } = this.#creds;
    const r = await this.#transport("POST", `${baseUrl}/realms/${encodeURIComponent(tokenRealm)}/protocol/openid-connect/token`,
      { "content-type": "application/x-www-form-urlencoded" },
      new URLSearchParams({ grant_type: "client_credentials", client_id: clientId, client_secret: clientSecret }).toString());
    if (r.status !== 200) throw new KeycloakError(`token request for ${clientId} refused: HTTP ${r.status} ${r.body.slice(0, 200)}`, r.status);
    const j = JSON.parse(r.body) as { access_token: string; expires_in: number };
    this.#token = { value: j.access_token, expiresAtMs: Date.now() + Math.max(0, j.expires_in - 10) * 1000 };
    return j.access_token;
  }

  async #bearer(): Promise<string> {
    if (this.#token && this.#token.expiresAtMs > Date.now()) return this.#token.value;
    return this.freshToken();
  }

  async raw(method: string, realm: string, path: string, body?: unknown): Promise<Reply> {
    const headers: Record<string, string> = { authorization: `Bearer ${await this.#bearer()}` };
    if (body !== undefined) headers["content-type"] = "application/json";
    return this.#transport(method, `${this.#creds.baseUrl}/admin/realms/${encodeURIComponent(realm)}${path}`, headers,
      body === undefined ? undefined : JSON.stringify(body));
  }

  async json<T>(realm: string, path: string): Promise<T> {
    const r = await this.raw("GET", realm, path);
    if (r.status !== 200) throw new KeycloakError(`GET ${path}: HTTP ${r.status} ${r.body.slice(0, 200)}`, r.status);
    return JSON.parse(r.body) as T;
  }

  /** A write that must succeed. 404 is returned to the caller, which decides whether absence is a no-op. */
  async write(method: string, realm: string, path: string, body?: unknown): Promise<number> {
    const r = await this.raw(method, realm, path, body);
    if (r.status >= 300 && r.status !== 404) throw new KeycloakError(`${method} ${path}: HTTP ${r.status} ${r.body.slice(0, 200)}`, r.status);
    return r.status;
  }
}
