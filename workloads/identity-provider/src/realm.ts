/**
 * The realms as code: load `realm/<name>.json` (`members` for players, `operators` for the people who
 * run the platform), fill its placeholders, make a Keycloak match it (`configure`), and report every
 * way a Keycloak differs from it (`check`). `configure` ends by running `check` and fails when any
 * problem remains, so a configured realm is a checked realm.
 */
import { readFileSync } from "node:fs";

import { KeycloakAdmin } from "./admin.ts";

export type Requirement = "REQUIRED" | "ALTERNATIVE" | "CONDITIONAL" | "DISABLED";
export type FlowNode =
  | { provider: string; requirement: Requirement }
  | { sub: string; requirement: Requirement; children: FlowNode[] };

interface Mapper { name: string; protocolMapper: string; config: Record<string, string>; [field: string]: unknown }
export interface ClientDefinition {
  clientId: string;
  protocolMappers?: Mapper[];
  defaultClientScopes?: string[];
  optionalClientScopes?: string[];
  /** For a service-account client: the client roles its account holds, by client, and nothing more. */
  serviceAccountRoles?: Record<string, string[]>;
  [field: string]: unknown;
}

/** What Keycloak stores about sign-ins and admin changes; the lists are complete sets, not minimums. */
export interface EventsDefinition {
  eventsEnabled: boolean;
  eventsExpiration: number;
  eventsListeners: string[];
  enabledEventTypes: string[];
  adminEventsEnabled: boolean;
  adminEventsDetailsEnabled: boolean;
  /** Seconds. Keycloak keeps it as the realm attribute of the same name, not in /events/config. */
  adminEventsExpiration: number;
}

export interface ProfileDefinition {
  /** The only user attributes the realm declares. */
  attributes: string[];
  /** Attributes a member may see and edit; the rest are admin-only. */
  memberEditable: string[];
  /** Attributes that must be present. */
  required: string[];
}

export interface RealmDefinition {
  realm: Record<string, unknown> & { realm: string };
  userProfile: ProfileDefinition;
  requiredActions: string[];
  events: EventsDefinition;
  browserFlow: { alias: string; description: string; nodes: FlowNode[] };
  clients: ClientDefinition[];
  /**
   * The game audience the edge accepts; only clients whose mappers name it may receive it. Only the
   * members realm has one: no operators client is a game client.
   */
  gameAudience?: string;
}

/** Every realm this workload owns, in the order `configure` and `check` take them. */
export const REALMS = ["members", "operators"] as const;
export type RealmName = (typeof REALMS)[number];
export const isRealmName = (name: string): name is RealmName => (REALMS as readonly string[]).includes(name);

const PLACEHOLDER = /\$\{([A-Z0-9_]+)\}/g;
const WHOLE_PLACEHOLDER = /^\$\{([A-Z0-9_]+)\}$/;

/**
 * Reads `realm/<name>.json` and fills `${NAME}` from `IDP_NAME` in `env`, else from the file's
 * `values` (all test-only defaults). A string that is exactly one numeric placeholder becomes a
 * number. The realms share their placeholder names where they share a value (the relying-party ID,
 * the mail server, the event expiries), so one setting covers both.
 */
export function loadDefinition(env: NodeJS.ProcessEnv = process.env, name: RealmName = "members"): RealmDefinition {
  const source = `realm/${name}.json`;
  const file = JSON.parse(readFileSync(new URL(`../${source}`, import.meta.url), "utf8")) as Record<string, unknown> & { values: Record<string, string> };
  const value = (key: string): string => {
    const v = env[`IDP_${key}`] ?? file.values[key];
    if (v === undefined) throw new Error(`${source} uses \${${key}} but neither IDP_${key} nor values.${key} is set`);
    return v;
  };
  const fill = (node: unknown): unknown => {
    if (typeof node === "string") {
      const whole = node.match(WHOLE_PLACEHOLDER);
      if (whole) {
        const v = value(whole[1]!);
        return /^\d+$/.test(v) ? Number(v) : v;
      }
      return node.replace(PLACEHOLDER, (_, name: string) => value(name));
    }
    if (Array.isArray(node)) return node.map(fill);
    if (node && typeof node === "object") {
      return Object.fromEntries(Object.entries(node).filter(([k]) => k !== "$comment").map(([k, v]) => [k, fill(v)]));
    }
    return node;
  };
  const { values: _values, ...rest } = file;
  const filled = fill(rest) as Omit<RealmDefinition, "gameAudience">;
  // Keycloak keeps the mail settings as strings; a filled port is a number until it is put back.
  const smtp = filled.realm["smtpServer"] as Record<string, unknown> | undefined;
  if (smtp) filled.realm["smtpServer"] = Object.fromEntries(Object.entries(smtp).map(([k, v]) => [k, String(v)]));
  if (filled.realm.realm !== name) throw new Error(`${source} defines realm ${filled.realm.realm}, not ${name}`);
  return "GAME_AUDIENCE" in file.values ? { ...filled, gameAudience: value("GAME_AUDIENCE") } : filled;
}

/** Realm keys `check` does not compare value by value: the name, the keys (checked as published) and mail (below). */
const UNCOMPARED = new Set(["realm", "components", "smtpServer"]);
/** Mail settings `check` compares. The password is never compared: Keycloak answers it masked. */
const SMTP_COMPARED = ["host", "port", "from", "fromDisplayName", "ssl", "starttls", "auth", "user"] as const;

/** Every way the realm's own settings differ from the definition: lifetimes, lockout, passkey policy, mail. */
function settingProblems(have: Record<string, unknown>, want: Record<string, unknown>): string[] {
  const problems: string[] = [];
  const same = (a: unknown, b: unknown) => JSON.stringify(a ?? null) === JSON.stringify(b ?? null);
  for (const [k, v] of Object.entries(want)) {
    if (UNCOMPARED.has(k)) continue;
    if (!same(have[k], v)) problems.push(`realm setting ${k} is ${JSON.stringify(have[k] ?? null)}, not ${JSON.stringify(v)}`);
  }
  const wantSmtp = want["smtpServer"] as Record<string, string> | undefined;
  if (wantSmtp) {
    const haveSmtp = (have["smtpServer"] ?? {}) as Record<string, string>;
    for (const k of SMTP_COMPARED) {
      // Keycloak drops an empty mail setting rather than storing "".
      if ((haveSmtp[k] ?? "") !== (wantSmtp[k] ?? "")) problems.push(`mail setting ${k} is ${JSON.stringify(haveSmtp[k] ?? "")}, not ${JSON.stringify(wantSmtp[k] ?? "")}`);
    }
  }
  return problems;
}

// ---------------------------------------------------------------- reading a realm

interface Execution {
  id: string;
  requirement: string;
  level: number;
  index: number;
  providerId?: string;
  displayName: string;
  authenticationFlow?: boolean;
  flowId?: string;
}
interface Flow { id: string; alias: string }
interface ProfileAttribute {
  name: string;
  required?: unknown;
  permissions?: { view?: string[]; edit?: string[] };
  [field: string]: unknown;
}
interface Profile { attributes: ProfileAttribute[]; unmanagedAttributePolicy?: string; [field: string]: unknown }
const profileOf = (kc: KeycloakAdmin, realm: string) => kc.get<Profile>(`/realms/${enc(realm)}/users/profile`);

/** Every way a user profile differs from the definition. */
function profileProblems(have: Profile, want: ProfileDefinition): string[] {
  const problems: string[] = [];
  const names = have.attributes.map((a) => a.name);
  for (const n of names) if (!want.attributes.includes(n)) problems.push(`user profile declares attribute ${n}`);
  for (const n of want.attributes) if (!names.includes(n)) problems.push(`user profile lacks attribute ${n}`);
  for (const a of have.attributes) {
    if (a.required && !want.required.includes(a.name)) problems.push(`user profile requires ${a.name}`);
    const member = [...(a.permissions?.view ?? []), ...(a.permissions?.edit ?? [])].includes("user");
    if (member && !want.memberEditable.includes(a.name)) problems.push(`members can see or edit ${a.name}`);
  }
  if (have.unmanagedAttributePolicy) problems.push(`user profile allows unmanaged attributes (${have.unmanagedAttributePolicy})`);
  return problems;
}
interface Client { id: string; clientId: string; authenticationFlowBindingOverrides?: Record<string, string>; protocolMappers?: Mapper[] }
export interface Jwk { kty: string; alg?: string; crv?: string; use?: string; kid: string }

const enc = encodeURIComponent;
const flowsOf = (kc: KeycloakAdmin, realm: string) => kc.get<Flow[]>(`/realms/${enc(realm)}/authentication/flows`);
const executionsOf = (kc: KeycloakAdmin, realm: string, alias: string) =>
  kc.get<Execution[]>(`/realms/${enc(realm)}/authentication/flows/${enc(alias)}/executions`);

/** The flow tree flattened in Keycloak's execution order: one line per execution. */
export function flatten(nodes: FlowNode[], level = 0): string[] {
  return nodes.flatMap((n) => "sub" in n
    ? [`${level} flow:${n.sub} ${n.requirement}`, ...flatten(n.children, level + 1)]
    : [`${level} ${n.provider} ${n.requirement}`]);
}
const describeExecutions = (executions: Execution[]) =>
  executions.map((e) => `${e.level} ${e.authenticationFlow ? `flow:${e.displayName}` : e.providerId} ${e.requirement}`);

/** The published signing keys of a realm. */
export async function publishedKeys(baseUrl: string, realm: string): Promise<Jwk[]> {
  const r = await fetch(`${baseUrl}/realms/${enc(realm)}/protocol/openid-connect/certs`);
  if (r.status !== 200) throw new Error(`JWKS answered ${r.status}`);
  return ((await r.json()) as { keys: Jwk[] }).keys;
}

// ---------------------------------------------------------------- check

/**
 * Every way the realm differs from the definition in what matters for sign-in safety: the browser
 * flow's shape and binding, any client overriding the browser flow (OD-12 F1: every client in the
 * realm, not only those defined here), who receives the game audience, the published key set, and
 * the required actions enrolment depends on, the stored-event settings the identity-restore recorder
 * relies on, what each service account may do, any brokered identity provider (there must be none),
 * and the realm's own settings (D9's session lifetimes, the lockout numbers, the passkey policy and its
 * relying-party ID, and the mail server the invite and reset links go through). An empty list is a pass.
 */
export async function check(kc: KeycloakAdmin, def: RealmDefinition): Promise<string[]> {
  const realm = def.realm.realm;
  const problems: string[] = [];
  const info = await kc.raw("GET", `/realms/${enc(realm)}`);
  if (info.status === 404) return [`realm ${realm} does not exist`];
  const rep = JSON.parse(info.body) as Record<string, unknown> & { browserFlow?: string };

  problems.push(...settingProblems(rep, def.realm));
  if (rep.browserFlow !== def.browserFlow.alias) problems.push(`realm browser flow is ${rep.browserFlow}, not ${def.browserFlow.alias}`);
  const flows = await flowsOf(kc, realm);
  const flow = flows.find((f) => f.alias === def.browserFlow.alias);
  if (!flow) problems.push(`flow ${def.browserFlow.alias} does not exist`);
  else {
    const want = flatten(def.browserFlow.nodes);
    const have = describeExecutions(await executionsOf(kc, realm, flow.alias));
    if (want.join("|") !== have.join("|")) problems.push(`flow ${flow.alias} is [${have.join(", ")}], not [${want.join(", ")}]`);
  }

  const clients = await kc.get<Client[]>(`/realms/${enc(realm)}/clients`);
  const namesGame = (mappers: Mapper[] | undefined) => def.gameAudience !== undefined &&
    (mappers ?? []).some((m) => m.protocolMapper === "oidc-audience-mapper" && m.config["included.custom.audience"] === def.gameAudience);
  const gameClients = new Set(def.clients.filter((c) => namesGame(c.protocolMappers)).map((c) => c.clientId));
  for (const c of clients) {
    const override = c.authenticationFlowBindingOverrides?.["browser"];
    if (override && override !== flow?.id) {
      const alias = flows.find((f) => f.id === override)?.alias ?? override;
      problems.push(`client ${c.clientId} overrides the browser flow with ${alias}`);
    }
    const audience = namesGame(c.protocolMappers);
    if (audience && !gameClients.has(c.clientId)) problems.push(`client ${c.clientId} receives the game audience but is not a game client in the definition`);
    if (!audience && gameClients.has(c.clientId)) problems.push(`game client ${c.clientId} has no game-audience mapper`);
  }
  for (const c of def.clients) if (!clients.some((x) => x.clientId === c.clientId)) problems.push(`client ${c.clientId} does not exist`);

  // Neither realm signs anyone in through another provider: an account is enrolled by invitation and
  // holds only a passkey and recovery codes, so a brokered provider (GitHub, say) is a second door.
  for (const p of await kc.get<{ alias: string; providerId: string }[]>(`/realms/${enc(realm)}/identity-provider/instances`)) {
    problems.push(`realm brokers identity provider ${p.alias} (${p.providerId})`);
  }

  const keys = await publishedKeys(kc.baseUrl, realm);
  if (keys.length === 0) problems.push("the realm publishes no signing keys");
  for (const k of keys) {
    if (k.kty !== "EC" || k.crv !== "P-256" || k.alg !== "ES256") problems.push(`published key ${k.kid} is ${k.kty}/${k.crv ?? "-"}/${k.alg ?? "-"}, not EC/P-256/ES256`);
  }

  problems.push(...profileProblems(await profileOf(kc, realm), def.userProfile));

  const actions = await kc.get<{ alias: string; enabled: boolean }[]>(`/realms/${enc(realm)}/authentication/required-actions`);
  for (const alias of def.requiredActions) {
    const ra = actions.find((a) => a.alias === alias);
    if (!ra) problems.push(`required action ${alias} is not registered`);
    else if (!ra.enabled) problems.push(`required action ${alias} is disabled`);
  }
  // Any enabled action is reachable by a signed-in member through kc_action (UPDATE_PASSWORD adds a
  // password credential), so the definition's list is the whole set, not a minimum.
  for (const a of actions) if (a.enabled && !def.requiredActions.includes(a.alias)) problems.push(`required action ${a.alias} is enabled but not in the definition`);

  problems.push(...eventProblems(await eventsOf(kc, realm), def.events));
  for (const c of def.clients) {
    const found = clients.find((x) => x.clientId === c.clientId);
    if (c.serviceAccountRoles && found) problems.push(...(await serviceAccountProblems(kc, realm, found, c.serviceAccountRoles)));
  }
  return problems;
}

interface EventsConfig {
  eventsEnabled?: boolean;
  eventsExpiration?: number | null;
  eventsListeners?: string[];
  enabledEventTypes?: string[];
  adminEventsEnabled?: boolean;
  adminEventsDetailsEnabled?: boolean;
}
type StoredEvents = EventsConfig & { adminEventsExpiration: string | undefined };

async function eventsOf(kc: KeycloakAdmin, realm: string): Promise<StoredEvents> {
  const config = await kc.get<EventsConfig>(`/realms/${enc(realm)}/events/config`);
  const rep = await kc.get<{ attributes?: Record<string, string> }>(`/realms/${enc(realm)}`);
  return { ...config, adminEventsExpiration: rep.attributes?.["adminEventsExpiration"] };
}
const sorted = (a: readonly string[] = []) => [...a].sort().join(", ");

/** Every way the stored-event settings differ from the definition. */
function eventProblems(have: StoredEvents, want: EventsDefinition): string[] {
  const problems: string[] = [];
  for (const k of ["eventsEnabled", "adminEventsEnabled", "adminEventsDetailsEnabled"] as const) {
    if (have[k] !== want[k]) problems.push(`events: ${k} is ${String(have[k])}, not ${String(want[k])}`);
  }
  if (have.eventsExpiration !== want.eventsExpiration) problems.push(`events: eventsExpiration is ${String(have.eventsExpiration)}, not ${want.eventsExpiration}`);
  if (have.adminEventsExpiration !== String(want.adminEventsExpiration)) problems.push(`events: adminEventsExpiration is ${String(have.adminEventsExpiration)}, not ${want.adminEventsExpiration}`);
  const types = have.enabledEventTypes ?? [];
  // An empty list makes Keycloak store every type; the definition always names its types.
  if (types.length === 0) problems.push("events: enabledEventTypes is empty");
  for (const t of want.enabledEventTypes) if (types.length && !types.includes(t)) problems.push(`events: ${t} is not stored`);
  for (const t of types) if (!want.enabledEventTypes.includes(t)) problems.push(`events: ${t} is stored but not in the definition`);
  if (sorted(have.eventsListeners) !== sorted(want.eventsListeners)) problems.push(`events: listeners are [${sorted(have.eventsListeners)}], not [${sorted(want.eventsListeners)}]`);
  return problems;
}

interface Role { id: string; name: string }
async function clientUuid(kc: KeycloakAdmin, realm: string, clientId: string): Promise<string> {
  const found = (await kc.get<Client[]>(`/realms/${enc(realm)}/clients?clientId=${enc(clientId)}`)).find((c) => c.clientId === clientId);
  if (!found) throw new Error(`client ${clientId} does not exist in realm ${realm}`);
  return found.id;
}

/** A service account's effective roles of each named client must be exactly the definition's, and its
 *  client must have exactly those in scope (the client's fullScopeAllowed is false). */
async function serviceAccountProblems(kc: KeycloakAdmin, realm: string, client: Client, want: Record<string, string[]>): Promise<string[]> {
  const account = await kc.raw("GET", `/realms/${enc(realm)}/clients/${client.id}/service-account-user`);
  if (account.status !== 200) return [`client ${client.clientId} has no service account`];
  const user = (JSON.parse(account.body) as { id: string }).id;
  const problems: string[] = [];
  for (const [owner, roles] of Object.entries(want)) {
    const ownerId = await clientUuid(kc, realm, owner);
    const held = (await kc.get<Role[]>(`/realms/${enc(realm)}/users/${user}/role-mappings/clients/${ownerId}/composite`)).map((r) => r.name);
    if (sorted(held) !== sorted(roles)) problems.push(`client ${client.clientId} service account holds ${owner} roles [${sorted(held)}], not [${sorted(roles)}]`);
    const scoped = (await kc.get<Role[]>(`/realms/${enc(realm)}/clients/${client.id}/scope-mappings/clients/${ownerId}`)).map((r) => r.name);
    if (sorted(scoped) !== sorted(roles)) problems.push(`client ${client.clientId} has ${owner} roles [${sorted(scoped)}] in scope, not [${sorted(roles)}]`);
  }
  return problems;
}

// ---------------------------------------------------------------- configure

async function addChildren(kc: KeycloakAdmin, realm: string, parent: string, nodes: FlowNode[], description: string): Promise<void> {
  const base = `/realms/${enc(realm)}/authentication/flows/${enc(parent)}/executions`;
  for (const n of nodes) {
    if ("sub" in n) await kc.call("POST", `${base}/flow`, { alias: n.sub, type: "basic-flow", provider: "registration-page-form", description });
    else await kc.call("POST", `${base}/execution`, { provider: n.provider });
    // the new execution is the parent's last direct child
    const direct = (await executionsOf(kc, realm, parent)).filter((e) => e.level === 0);
    const added = direct.reduce((a, b) => (b.index > a.index ? b : a));
    await kc.call("PUT", base, { ...added, requirement: n.requirement });
    if ("sub" in n) await addChildren(kc, realm, n.sub, n.children, description);
  }
}

async function ensureFlow(kc: KeycloakAdmin, def: RealmDefinition): Promise<string[]> {
  const realm = def.realm.realm;
  const { alias, description, nodes } = def.browserFlow;
  const flow = (await flowsOf(kc, realm)).find((f) => f.alias === alias);
  if (!flow) {
    await kc.call("POST", `/realms/${enc(realm)}/authentication/flows`, { alias, providerId: "basic-flow", topLevel: true, builtIn: false, description });
    await addChildren(kc, realm, alias, nodes, description);
    return [`built flow ${alias}`];
  }
  const have = describeExecutions(await executionsOf(kc, realm, alias));
  // A live browser flow is never rebuilt in place: members would meet a half-built flow meanwhile.
  if (have.join("|") !== flatten(nodes).join("|")) {
    throw new Error(`flow ${alias} exists with a different shape [${have.join(", ")}]; configure does not rebuild a live flow in place`);
  }
  return [];
}

async function reconcileClient(kc: KeycloakAdmin, realm: string, want: ClientDefinition, scopeIds: Map<string, string>): Promise<string> {
  const base = `/realms/${enc(realm)}/clients`;
  const { serviceAccountRoles, ...rep } = want;
  const found = (await kc.get<Client[]>(`${base}?clientId=${enc(want.clientId)}`))[0];
  if (!found) {
    await kc.call("POST", base, rep);
    const created = (await kc.get<Client[]>(`${base}?clientId=${enc(want.clientId)}`))[0]!;
    if (serviceAccountRoles) await grantServiceAccount(kc, realm, created, serviceAccountRoles);
    return `created client ${want.clientId}`;
  }
  const { protocolMappers = [], defaultClientScopes = [], optionalClientScopes = [], ...fields } = rep;
  await kc.call("PUT", `${base}/${found.id}`, { ...fields, id: found.id });
  // A client PUT does not update mappers or client scopes; reconcile them by name.
  const mappers = await kc.get<(Mapper & { id: string })[]>(`${base}/${found.id}/protocol-mappers/models`);
  for (const m of protocolMappers) {
    const have = mappers.find((x) => x.name === m.name);
    if (have) await kc.call("PUT", `${base}/${found.id}/protocol-mappers/models/${have.id}`, { ...m, id: have.id });
    else await kc.call("POST", `${base}/${found.id}/protocol-mappers/models`, m);
  }
  for (const m of mappers) {
    if (!protocolMappers.some((x) => x.name === m.name)) await kc.call("DELETE", `${base}/${found.id}/protocol-mappers/models/${m.id}`);
  }
  for (const [kind, names] of [["default-client-scopes", defaultClientScopes], ["optional-client-scopes", optionalClientScopes]] as const) {
    const have = await kc.get<{ id: string; name: string }[]>(`${base}/${found.id}/${kind}`);
    for (const name of names) {
      const id = scopeIds.get(name);
      if (!id) throw new Error(`client scope ${name} does not exist in realm ${realm}`);
      if (!have.some((s) => s.name === name)) await kc.call("PUT", `${base}/${found.id}/${kind}/${id}`);
    }
    for (const s of have) if (!names.includes(s.name)) await kc.call("DELETE", `${base}/${found.id}/${kind}/${s.id}`);
  }
  if (serviceAccountRoles) await grantServiceAccount(kc, realm, found, serviceAccountRoles);
  return `reconciled client ${want.clientId}`;
}

/** Grants a service account exactly the listed client roles and puts exactly those in its client's
 *  scope, removing any other direct grant or scope mapping of the same clients' roles. */
async function grantServiceAccount(kc: KeycloakAdmin, realm: string, client: Client, want: Record<string, string[]>): Promise<void> {
  const user = (await kc.get<{ id: string }>(`/realms/${enc(realm)}/clients/${client.id}/service-account-user`)).id;
  for (const [owner, names] of Object.entries(want)) {
    const ownerId = await clientUuid(kc, realm, owner);
    const roles = await Promise.all(names.map((n) => kc.get<Role>(`/realms/${enc(realm)}/clients/${ownerId}/roles/${enc(n)}`)));
    for (const path of [`/realms/${enc(realm)}/users/${user}/role-mappings/clients/${ownerId}`, `/realms/${enc(realm)}/clients/${client.id}/scope-mappings/clients/${ownerId}`]) {
      const have = await kc.get<Role[]>(path);
      const extra = have.filter((r) => !names.includes(r.name));
      const missing = roles.filter((r) => !have.some((h) => h.id === r.id));
      if (extra.length) await kc.call("DELETE", path, extra);
      if (missing.length) await kc.call("POST", path, missing);
    }
  }
}

/** Makes the stored-event settings match the definition. */
async function configureEvents(kc: KeycloakAdmin, realm: string, want: EventsDefinition): Promise<string[]> {
  if (eventProblems(await eventsOf(kc, realm), want).length === 0) return [];
  const { adminEventsExpiration, ...config } = want;
  await kc.call("PUT", `/realms/${enc(realm)}/events/config`, config);
  const rep = await kc.get<{ attributes?: Record<string, string> }>(`/realms/${enc(realm)}`);
  await kc.call("PUT", `/realms/${enc(realm)}`, { realm, attributes: { ...rep.attributes, adminEventsExpiration: String(adminEventsExpiration) } });
  return ["reconciled the stored-event settings"];
}

/** Makes the Keycloak match the definition, then checks it. Answers what it did; throws on any remaining problem. */
export async function configure(kc: KeycloakAdmin, def: RealmDefinition): Promise<string[]> {
  const realm = def.realm.realm;
  const smtp = def.realm["smtpServer"] as Record<string, string> | undefined;
  // Every configure run writes the mail settings, so a run without the key would erase it.
  if (smtp?.["auth"] === "true" && !smtp["password"]) throw new Error("the mail server needs a password (smtpServer.auth is true): set IDP_SMTP_PASSWORD");
  const done: string[] = [];
  const existing = await kc.raw("GET", `/realms/${enc(realm)}`);
  if (existing.status === 404) {
    await kc.call("POST", "/realms", def.realm);
    done.push(`created realm ${realm}`);
  } else {
    // Keys are created with the realm and never replaced here: replacing them invalidates every token.
    const { components: _keys, ...settings } = def.realm;
    await kc.call("PUT", `/realms/${enc(realm)}`, settings);
    done.push(`reconciled realm ${realm} settings`);
  }
  for (const ra of await kc.get<(Record<string, unknown> & { alias: string; enabled: boolean })[]>(`/realms/${enc(realm)}/authentication/required-actions`)) {
    const wanted = def.requiredActions.includes(ra.alias);
    if (ra.enabled !== wanted) {
      await kc.call("PUT", `/realms/${enc(realm)}/authentication/required-actions/${enc(ra.alias)}`, { ...ra, enabled: wanted });
      done.push(`${wanted ? "enabled" : "disabled"} required action ${ra.alias}`);
    }
  }
  const profile = await profileOf(kc, realm);
  if (profileProblems(profile, def.userProfile).length) {
    const { unmanagedAttributePolicy: _policy, ...rest } = profile;
    const attributes = profile.attributes
      .filter((a) => def.userProfile.attributes.includes(a.name))
      .map((a) => {
        const { required, ...attribute } = a;
        const member = def.userProfile.memberEditable.includes(a.name);
        return {
          ...attribute,
          ...(def.userProfile.required.includes(a.name) && required ? { required } : {}),
          permissions: member ? a.permissions : { view: ["admin"], edit: ["admin"] },
        };
      });
    await kc.call("PUT", `/realms/${enc(realm)}/users/profile`, { ...rest, attributes });
    done.push("reconciled the user profile");
  }
  done.push(...(await ensureFlow(kc, def)));
  const rep = await kc.get<{ browserFlow?: string }>(`/realms/${enc(realm)}`);
  if (rep.browserFlow !== def.browserFlow.alias) {
    await kc.call("PUT", `/realms/${enc(realm)}`, { realm, browserFlow: def.browserFlow.alias });
    done.push(`bound ${def.browserFlow.alias} as the browser flow`);
  }
  done.push(...(await configureEvents(kc, realm, def.events)));
  const scopes = await kc.get<{ id: string; name: string }[]>(`/realms/${enc(realm)}/client-scopes`);
  const scopeIds = new Map(scopes.map((s) => [s.name, s.id]));
  for (const c of def.clients) done.push(await reconcileClient(kc, realm, c, scopeIds));
  const problems = await check(kc, def);
  if (problems.length) throw new Error(`configured, but the realm still fails its check:\n- ${problems.join("\n- ")}`);
  return done;
}
