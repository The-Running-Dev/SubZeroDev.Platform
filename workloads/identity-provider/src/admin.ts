/**
 * A minimal Keycloak admin REST client. It signs in to the master realm's `admin-cli` with the
 * operator credentials it is given and re-authenticates before the 60 s admin token expires.
 * Nothing here is realm-specific; `realm.ts` owns what the realm should contain.
 */

export interface AdminConnection {
  /** Keycloak's public base URL, e.g. `http://localhost:8180` (the configured hostname). */
  baseUrl: string;
  username: string;
  password: string;
  /** How long a 503 from a Keycloak still bootstrapping is retried; default 120 s. */
  bootstrapWaitMs?: number;
}

export class AdminError extends Error {
  readonly status: number;
  readonly body: string;
  constructor(status: number, method: string, path: string, body: string) {
    super(`Keycloak admin ${method} ${path} answered ${status}: ${body.slice(0, 400)}`);
    this.status = status;
    this.body = body;
  }
}

export class KeycloakAdmin {
  private token: { value: string; at: number } | undefined;
  private readonly connection: AdminConnection;

  constructor(connection: AdminConnection) {
    this.connection = connection;
  }

  get baseUrl(): string {
    return this.connection.baseUrl.replace(/\/+$/, "");
  }

  /**
   * The admin token. Keycloak's readiness check can report UP while it is still creating the
   * bootstrap admin, and then answers 503 ("Bootstrap in progress"), so a 503 is retried, after its
   * Retry-After or 1 s, until `bootstrapWaitMs` has passed. Any other status fails at once.
   */
  private async accessToken(): Promise<string> {
    if (this.token && Date.now() - this.token.at < 30_000) return this.token.value;
    const deadline = Date.now() + (this.connection.bootstrapWaitMs ?? 120_000);
    for (;;) {
      const response = await fetch(`${this.baseUrl}/realms/master/protocol/openid-connect/token`, {
        method: "POST",
        headers: { "content-type": "application/x-www-form-urlencoded" },
        body: new URLSearchParams({
          grant_type: "password",
          client_id: "admin-cli",
          username: this.connection.username,
          password: this.connection.password,
        }),
      });
      const text = await response.text();
      if (response.status === 503 && Date.now() < deadline) {
        const after = Number(response.headers.get("retry-after"));
        await new Promise((r) => setTimeout(r, Math.min(Number.isFinite(after) && after > 0 ? after * 1000 : 1000, 5000)));
        continue;
      }
      if (response.status !== 200) throw new AdminError(response.status, "POST", "master token", text);
      this.token = { value: (JSON.parse(text) as { access_token: string }).access_token, at: Date.now() };
      return this.token.value;
    }
  }

  /** Any admin call; answers the raw status, body and Location header without throwing. */
  async raw(method: string, path: string, json?: unknown): Promise<{ status: number; body: string; location: string | null }> {
    const response = await fetch(`${this.baseUrl}/admin${path}`, {
      method,
      headers: {
        authorization: `Bearer ${await this.accessToken()}`,
        ...(json === undefined ? {} : { "content-type": "application/json" }),
      },
      ...(json === undefined ? {} : { body: JSON.stringify(json) }),
    });
    return { status: response.status, body: await response.text(), location: response.headers.get("location") };
  }

  /** An admin call that must succeed (2xx). */
  async call(method: string, path: string, json?: unknown): Promise<{ body: string; location: string | null }> {
    const r = await this.raw(method, path, json);
    if (r.status < 200 || r.status >= 300) throw new AdminError(r.status, method, path, r.body);
    return r;
  }

  async get<T>(path: string): Promise<T> {
    return JSON.parse((await this.call("GET", path)).body) as T;
  }

  /** The id at the end of a 201's Location header. */
  static createdId(location: string | null): string {
    const id = location?.split("/").pop();
    if (!id) throw new Error("Keycloak created a resource without a Location header");
    return id;
  }
}
