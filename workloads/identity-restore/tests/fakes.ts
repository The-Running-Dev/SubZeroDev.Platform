/**
 * An in-memory Keycloak realm behind the admin-API paths this workload calls, and an in-memory record
 * store. The fake answers the way Keycloak 26.8.1 did in the spike where the workload depends on it:
 * `dateFrom` and `dateTo` inclusive in milliseconds, partial realm PUTs, 404 for a missing user. What G1
 * established against the real server is listed in the README (*What G1 established about Keycloak*).
 */
import { Keycloak, type Reply, type Transport } from "../src/keycloak.ts";
import type { Execution } from "../src/fixa.ts";
import type { NewWindow, RecorderStore, RestoreStore } from "../src/store.ts";
import type { KcAdminEvent, KcUserEvent, RecordWindow, RestoreLogEntry, StoredEvent } from "../src/types.ts";
import { RECOVERY_CODES, PASSKEY } from "../src/types.ts";

export const REALM = "members";
export const REALM_ID = "realm-uuid-1";
export const RECORDER = "event-recorder";
export const OPERATOR = "identity-restore";
/** Keycloak's internal id for a client. Admin events name the acting client by this, not by its
 *  `clientId` (G1 against Keycloak 26.8.1). */
export const clientUuid = (clientId: string) => `uuid-of-${clientId}`;

export class Clock {
  t: number;
  constructor(t = Date.UTC(2026, 10, 21, 12, 0, 0)) { this.t = t; }
  now = () => this.t;
  sleep = async (ms: number) => { this.t += ms; };
}

export interface FakeCredential { id: string; type: string; createdDate: number }
export interface FakeUser { id: string; username: string; enabled: boolean; serviceAccountClientId?: string; credentials: FakeCredential[] }

export const FIXA: Execution[] = [
  { level: 0, index: 0, requirement: "REQUIRED", providerId: "auth-username-form" },
  { level: 0, index: 1, requirement: "REQUIRED", authenticationFlow: true, displayName: "passkey or recovery code" },
  { level: 1, index: 0, requirement: "ALTERNATIVE", providerId: "webauthn-authenticator-passwordless" },
  { level: 1, index: 1, requirement: "ALTERNATIVE", providerId: "auth-recovery-authn-code-form" },
];
/** The unsafe flow the spike found: recovery code as a bare top-level alternative to the whole sign-in. */
export const UNSAFE_FLOW: Execution[] = [
  { level: 0, index: 0, requirement: "ALTERNATIVE", providerId: "auth-cookie" },
  { level: 0, index: 1, requirement: "ALTERNATIVE", providerId: "webauthn-authenticator-passwordless" },
  { level: 0, index: 2, requirement: "ALTERNATIVE", providerId: "auth-recovery-authn-code-form" },
];

export class FakeKeycloak {
  readonly clock: Clock;
  realm = { id: REALM_ID, realm: REALM, enabled: true, notBefore: 0, maxFailureWaitSeconds: 900, browserFlow: "members-browser" };
  eventsConfig: Record<string, unknown> = {
    eventsEnabled: true, eventsExpiration: 30 * 86400, adminEventsEnabled: true, adminEventsDetailsEnabled: true,
    enabledEventTypes: ["LOGIN", "LOGIN_ERROR", "CLIENT_LOGIN", "REMOVE_CREDENTIAL", "UPDATE_CREDENTIAL", "DELETE_ACCOUNT",
      "USER_DISABLED_BY_TEMPORARY_LOCKOUT", "USER_DISABLED_BY_PERMANENT_LOCKOUT", "UPDATE_PASSWORD", "RESET_PASSWORD",
      "REMOVE_FEDERATED_IDENTITY", "REGISTER", "LOGOUT"],
  };
  users = new Map<string, FakeUser>();
  events: KcUserEvent[] = [];
  adminEvents: KcAdminEvent[] = [];
  flows = new Map<string, { id: string; executions: Execution[] }>([
    ["browser", { id: "flow-browser", executions: UNSAFE_FLOW }],
    ["members-browser", { id: "flow-members", executions: FIXA }],
  ]);
  clients = [{ id: "client-uuid-web", clientId: "members-web", authenticationFlowBindingOverrides: { browser: "flow-members" } as { browser?: string } }];
  refuseTokens = false;
  /** When false, token requests succeed but leave no `CLIENT_LOGIN`: event storage silently off. */
  storeEvents = true;
  logoutAll = 0;
  /** Writes by method and path, to count what the restore changed. */
  writes: string[] = [];
  /** Requests that fail with 500 once each, keyed `METHOD path`. */
  failOnce = new Set<string>();
  #tokens = new Map<string, string>();
  #n = 0;

  constructor(clock: Clock) { this.clock = clock; }

  id(prefix: string) { return `${prefix}-${String(++this.#n).padStart(4, "0")}`; }

  addUser(username: string, creds: Array<{ type: string; createdDate?: number }>, enabled = true): FakeUser {
    const u: FakeUser = { id: this.id("user"), username, enabled,
      credentials: creds.map((c) => ({ id: this.id("cred"), type: c.type, createdDate: c.createdDate ?? this.clock.t })) };
    this.users.set(u.id, u);
    return u;
  }

  userEvent(type: string, userId: string | undefined, details: Record<string, string> = {}, time = this.clock.t, clientId = "members-web"): KcUserEvent {
    const e: KcUserEvent = { id: this.id("ev"), time, type, realmId: REALM_ID, clientId, ...(userId ? { userId } : {}), details };
    if (this.storeEvents) this.events.push(e);
    return e;
  }

  adminEvent(operationType: string, resourcePath: string, details: Record<string, string> = {}, clientId = "admin-cli", time = this.clock.t,
    representation?: unknown): KcAdminEvent {
    const e: KcAdminEvent = { id: this.id("adm"), time, realmId: REALM_ID, authDetails: { realmId: "master-id", clientId: clientUuid(clientId) },
      operationType, resourceType: resourcePath.startsWith("users") ? "USER" : "REALM", resourcePath, details,
      ...(representation === undefined ? {} : { representation: JSON.stringify(representation) }) };
    if (this.storeEvents) this.adminEvents.push(e);
    return e;
  }

  // Member actions as they happen in Keycloak: the state change and its event.
  removeCredential(u: FakeUser, type: string) {
    const c = u.credentials.find((x) => x.type === type)!;
    u.credentials = u.credentials.filter((x) => x !== c);
    return this.userEvent("REMOVE_CREDENTIAL", u.id, { credential_type: type, credential_id: c.id });
  }
  useRecoveryCode(u: FakeUser) { return this.userEvent("LOGIN", u.id, { credential_type: RECOVERY_CODES }); }
  deleteAccount(u: FakeUser) { this.users.delete(u.id); return this.userEvent("DELETE_ACCOUNT", u.id); }
  adminSetEnabled(u: FakeUser, enabled: boolean, clientId = "admin-cli") {
    const prev = u.enabled;
    u.enabled = enabled;
    return this.adminEvent("UPDATE", `users/${u.id}`, enabledDetails(prev, enabled), clientId, this.clock.t, { enabled });
  }
  tempLock(u: FakeUser, untilMs: number) {
    return this.userEvent("USER_DISABLED_BY_TEMPORARY_LOCKOUT", u.id, { not_before: new Date(untilMs).toISOString().replace(/\.\d+Z$/, "") });
  }

  /** A deep copy of the persistent state, as a database dump would hold it (events included). */
  snapshot() {
    return structuredClone({ realm: this.realm, eventsConfig: this.eventsConfig, users: [...this.users.values()], events: this.events,
      adminEvents: this.adminEvents, flows: [...this.flows.entries()], clients: this.clients });
  }
  restoreFrom(s: ReturnType<FakeKeycloak["snapshot"]>) {
    const c = structuredClone(s);
    this.realm = c.realm; this.eventsConfig = c.eventsConfig; this.users = new Map(c.users.map((u) => [u.id, u]));
    this.events = c.events; this.adminEvents = c.adminEvents; this.flows = new Map(c.flows); this.clients = c.clients;
  }

  client(clientId: string, tokenRealm: string): Keycloak {
    return new Keycloak({ baseUrl: "https://kc.test", tokenRealm, clientId, clientSecret: "test-secret" }, this.transport);
  }

  transport: Transport = async (method, url, headers, body) => {
    const u = new URL(url);
    const q = u.searchParams;
    const path = decodeURIComponent(u.pathname);
    const key = `${method} ${path}`;
    if (this.failOnce.delete(key)) return { status: 500, body: "injected failure" };
    const ok = (x: unknown, status = 200): Reply => ({ status, body: x === undefined ? "" : JSON.stringify(x) });
    const none: Reply = { status: 404, body: '{"error":"not found"}' };

    let m: RegExpMatchArray | null;
    if ((m = path.match(/^\/realms\/([^/]+)\/protocol\/openid-connect\/token$/))) {
      const form = new URLSearchParams(body);
      const clientId = form.get("client_id")!;
      const tokenRealm = m[1]!;
      if (this.refuseTokens || (tokenRealm === REALM && !this.realm.enabled)) return { status: 401, body: '{"error":"unauthorized_client"}' };
      const token = `tok-${this.id("t")}`;
      this.#tokens.set(token, clientId);
      if (tokenRealm === REALM) this.userEvent("CLIENT_LOGIN", `sa-${clientId}`, { grant_type: "client_credentials" }, this.clock.t, clientId);
      return ok({ access_token: token, expires_in: 300 });
    }
    const caller = this.#tokens.get((headers["authorization"] ?? "").replace(/^Bearer /, ""));
    if (!caller) return { status: 401, body: "" };
    const admin = path.match(/^\/admin\/realms\/([^/]+)(.*)$/);
    if (admin?.[1] === "master" && admin[2] === "/clients" && method === "GET") {
      const id = q.get("clientId");
      return ok(id ? [{ id: clientUuid(id), clientId: id }] : []);
    }
    if (!admin || admin[1] !== REALM) return none;
    const p = admin[2]!;
    const json = body === undefined ? undefined : JSON.parse(body) as Record<string, unknown>;
    const audit = (op: string, resourcePath: string, details: Record<string, string> = {}) => this.adminEvent(op, resourcePath, details, caller);
    if (method !== "GET") this.writes.push(key);

    if (p === "") {
      if (method === "GET") return ok(this.realm);
      if (method === "PUT") {
        // Partial update: only the fields sent change.
        Object.assign(this.realm, json);
        audit("UPDATE", "", {});
        return ok(undefined, 204);
      }
    }
    if (p === "/events/config" && method === "GET") return ok(this.eventsConfig);
    if (p === "/logout-all" && method === "POST") { this.logoutAll++; audit("ACTION", "logout-all"); return ok({}, 200); }
    if (p === "/events" && method === "GET") {
      const r = this.events.filter((e) =>
        (!q.get("dateFrom") || e.time >= Number(q.get("dateFrom"))) && (!q.get("dateTo") || e.time <= Number(q.get("dateTo"))) &&
        (!q.get("type") || e.type === q.get("type")) && (!q.get("user") || e.userId === q.get("user")) &&
        (!q.get("client") || e.clientId === q.get("client")));
      return ok(page(r, q));
    }
    if (p === "/admin-events" && method === "GET") {
      const r = this.adminEvents.filter((e) =>
        (!q.get("dateFrom") || e.time >= Number(q.get("dateFrom"))) && (!q.get("dateTo") || e.time <= Number(q.get("dateTo"))) &&
        (!q.get("resourcePath") || e.resourcePath === q.get("resourcePath")));
      return ok(page(r, q));
    }
    if (p === "/users" && method === "GET") {
      const all = [...this.users.values()].sort((a, b) => (a.username < b.username ? -1 : 1)).map(({ credentials: _c, ...rest }) => rest);
      const first = Number(q.get("first") ?? 0), max = Number(q.get("max") ?? 100);
      return ok(all.slice(first, first + max));
    }
    if ((m = p.match(/^\/users\/([^/]+)$/))) {
      const user = this.users.get(m[1]!);
      if (!user) return none;
      if (method === "GET") { const { credentials: _c, ...rest } = user; return ok(rest); }
      if (method === "PUT") {
        const prev = user.enabled;
        if (typeof json?.["enabled"] === "boolean") user.enabled = json["enabled"];
        this.adminEvent("UPDATE", `users/${user.id}`, enabledDetails(prev, user.enabled), caller, this.clock.t, json);
        return ok(undefined, 204);
      }
      if (method === "DELETE") { this.users.delete(user.id); audit("DELETE", `users/${user.id}`); return ok(undefined, 204); }
    }
    if ((m = p.match(/^\/users\/([^/]+)\/credentials$/)) && method === "GET") {
      const user = this.users.get(m[1]!);
      return user ? ok(user.credentials) : none;
    }
    if ((m = p.match(/^\/users\/([^/]+)\/credentials\/([^/]+)$/)) && method === "DELETE") {
      const user = this.users.get(m[1]!);
      if (!user || !user.credentials.some((c) => c.id === m![2])) return none;
      user.credentials = user.credentials.filter((c) => c.id !== m![2]);
      audit("DELETE", `users/${user.id}/credentials/${m[2]}`);
      return ok(undefined, 204);
    }
    if (p === "/authentication/flows" && method === "GET") return ok([...this.flows.entries()].map(([alias, f]) => ({ id: f.id, alias })));
    if ((m = p.match(/^\/authentication\/flows\/([^/]+)\/executions$/)) && method === "GET") {
      const f = this.flows.get(m[1]!);
      return f ? ok(f.executions) : none;
    }
    if (p === "/clients" && method === "GET") return ok(this.clients.filter((c) => !q.get("clientId") || c.clientId === q.get("clientId")));
    return { status: 400, body: `fake: unhandled ${method} ${p}` };
  };
}

function page<T extends { time: number }>(r: T[], q: URLSearchParams): T[] {
  const sorted = [...r].sort((a, b) => (q.get("direction") === "asc" ? a.time - b.time : b.time - a.time));
  const first = Number(q.get("first") ?? 0), max = Number(q.get("max") ?? 100);
  return sorted.slice(first, first + max);
}

export class MemoryStore implements RecorderStore, RestoreStore {
  readonly events = new Map<string, StoredEvent>();
  readonly windowList: RecordWindow[] = [];
  readonly log: RestoreLogEntry[] = [];
  readonly clock: Clock;
  constructor(clock: Clock) { this.clock = clock; }
  async insertEvents(events: readonly StoredEvent[]) {
    let n = 0;
    for (const e of events) if (!this.events.has(e.id)) { this.events.set(e.id, structuredClone(e)); n++; }
    return n;
  }
  async insertWindow(w: NewWindow) {
    const { insertedCount: _i, recorder: _r, ...rest } = w;
    this.windowList.push({ ...rest, seq: this.windowList.length + 1, polledAtMs: this.clock.t });
  }
  async lastWindow() { return [...this.windowList].sort((a, b) => b.throughMs - a.throughMs || b.seq - a.seq)[0]; }
  async windows() { return [...this.windowList].sort((a, b) => a.fromMs - b.fromMs || a.seq - b.seq); }
  async windowsOverlapping(from: number, through: number) { return (await this.windows()).filter((w) => w.throughMs >= from && w.fromMs <= through); }
  async eventsFrom(realmId: string, fromMs: number) {
    return [...this.events.values()].filter((e) => e.body.realmId === realmId && e.timeMs >= fromMs).sort((a, b) => a.timeMs - b.timeMs || (a.id < b.id ? -1 : 1));
  }
  async restoreLog() { return [...this.log]; }
  async appendRestoreLog(e: RestoreLogEntry) { this.log.push(structuredClone(e)); }
}

export { RECOVERY_CODES, PASSKEY };

/** Keycloak writes the enabled details only when `enabled` changed; a no-op update has none (G1). */
const enabledDetails = (prev: boolean, now: boolean): Record<string, string> =>
  prev === now ? {} : { previous_enabled: String(prev), updated_enabled: String(now) };
