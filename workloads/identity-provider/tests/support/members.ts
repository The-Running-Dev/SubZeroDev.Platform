/**
 * Members and sign-ins against a running Keycloak configured from realm/members.json.
 *
 * First-credential enrolment is an open owner decision (D3/D5), so the realm has no self-service
 * enrolment path. Tests enrol their synthetic members through a TEST-ONLY bootstrap client whose
 * browser flow is Keycloak's built-in password flow: the member is created with a throwaway
 * password and the passkey and recovery-code required actions, completes them, and the password
 * is then deleted. `check` reports that client as a problem for as long as it exists, which the
 * suite asserts, and the suite removes it afterwards. None of this is a production enrolment path.
 */
import { createHash, randomBytes } from "node:crypto";

import { KeycloakAdmin } from "../../src/admin.ts";
import { loadDefinition } from "../../src/realm.ts";
import type { RealmDefinition } from "../../src/realm.ts";
import { Authenticator } from "./authenticator.ts";
import { UserAgent, authenticateAction, credentialOptions, feedback, formById, hiddenFields, pageId, scriptField } from "./user-agent.ts";
import type { Answer } from "./user-agent.ts";

/** Loopback redirect a native client listens on; any port matches the port-less registration. */
export const CALLBACK = "http://127.0.0.1:53682/callback";
export const BOOTSTRAP_CLIENT = "test-only-enrol";
/** The adventures site's callback on the loopback; matches its port-less registration. */
export const ADVENTURES_CALLBACK = "http://127.0.0.1:53682/adventures/callback";

export interface Environment {
  kc: KeycloakAdmin;
  def: RealmDefinition;
  base: string;
  realm: string;
  issuer: string;
}

/**
 * The Keycloak under test, or undefined when none is configured. Locally the suite skips without
 * one; under CI (CI=true) a missing Keycloak is a failure, never a skip.
 */
export function environment(env: NodeJS.ProcessEnv = process.env): Environment | undefined {
  const password = env["KC_BOOTSTRAP_ADMIN_PASSWORD"];
  if (!password) {
    if (env["CI"]) throw new Error("CI run without KC_BOOTSTRAP_ADMIN_PASSWORD: the identity-provider proof cannot be skipped in CI");
    return undefined;
  }
  const base = (env["KC_BASE_URL"] ?? "http://localhost:8180").replace(/\/+$/, "");
  const kc = new KeycloakAdmin({ baseUrl: base, username: env["KC_BOOTSTRAP_ADMIN_USERNAME"] ?? "bootstrap-admin", password });
  const def = loadDefinition(env);
  return { kc, def, base, realm: def.realm.realm, issuer: `${base}/realms/${def.realm.realm}` };
}

// ---------------------------------------------------------------- OIDC

export interface Tokens {
  access_token: string;
  id_token?: string;
  refresh_token?: string;
  token_type: string;
}

/** How a client proves itself at the token endpoint and where its code arrives. */
export interface ClientAuth {
  secret?: string;
  redirect?: string;
}

/** A confidential client's current secret, read through the admin API (it is never in the definition). */
export async function clientSecret(e: Environment, clientId: string): Promise<string> {
  const c = (await e.kc.get<{ id: string }[]>(`/realms/${e.realm}/clients?clientId=${encodeURIComponent(clientId)}`))[0];
  if (!c) throw new Error(`client ${clientId} does not exist`);
  return (await e.kc.get<{ value: string }>(`/realms/${e.realm}/clients/${c.id}/client-secret`)).value;
}

export function authorizeUrl(e: Environment, clientId: string, extra: Record<string, string> = {}, redirect = CALLBACK): { url: string; verifier: string } {
  const verifier = randomBytes(32).toString("base64url");
  const q = new URLSearchParams({
    client_id: clientId,
    response_type: "code",
    redirect_uri: redirect,
    scope: "openid",
    code_challenge: createHash("sha256").update(verifier).digest("base64url"),
    code_challenge_method: "S256",
    state: randomBytes(8).toString("hex"),
    ...extra,
  });
  return { url: `${e.issuer}/protocol/openid-connect/auth?${q}`, verifier };
}

export async function exchange(e: Environment, clientId: string, code: string, verifier: string, auth: ClientAuth = {}): Promise<{ status: number; body: string }> {
  const r = await fetch(`${e.issuer}/protocol/openid-connect/token`, {
    method: "POST",
    headers: { "content-type": "application/x-www-form-urlencoded" },
    body: new URLSearchParams({
      grant_type: "authorization_code",
      code,
      redirect_uri: auth.redirect ?? CALLBACK,
      client_id: clientId,
      code_verifier: verifier,
      ...(auth.secret ? { client_secret: auth.secret } : {}),
    }),
  });
  return { status: r.status, body: await r.text() };
}

const codeOf = (a: Answer, redirect = CALLBACK): string | null =>
  (a.status === 302 || a.status === 303) && a.location?.startsWith(redirect) ? new URL(a.location).searchParams.get("code") : null;

export const outcome = (e: Environment, a: Answer): string =>
  (a.status === 302 || a.status === 303) && a.location && !a.location.startsWith(e.base)
    ? a.location.includes("code=")
      ? "code issued"
      : `redirect ${a.location.slice(0, 120)}`
    : `${pageId(a)}${feedback(a) ? ` "${feedback(a)}"` : ""}`;

export function decode(jwt: string): { header: Record<string, unknown>; payload: Record<string, unknown> } {
  const [h, p] = jwt.split(".");
  return { header: JSON.parse(Buffer.from(h!, "base64url").toString()), payload: JSON.parse(Buffer.from(p!, "base64url").toString()) };
}

// ---------------------------------------------------------------- walking a sign-in

export interface Plan {
  /** Answers the passkey challenge with this authenticator. */
  passkey?: Authenticator;
  /** Answers "Recovery code #n". */
  code?: (n: number) => string;
  /** Leaves the passkey page through "Try another way" and picks the option whose label matches. */
  choose?: RegExp;
  /** Completes passkey registration pages with this authenticator. */
  register?: Authenticator;
  /** Extra authorization request parameters (e.g. kc_action). */
  extra?: Record<string, string>;
  /** Reuse a user agent (and its SSO session). */
  agent?: UserAgent;
  /** A confidential client's secret and the redirect its code arrives at. */
  client?: ClientAuth;
}

export interface Walk {
  trail: string[];
  status: string;
  tokens?: Tokens;
  agent: UserAgent;
  /** Recovery codes Keycloak displayed during the walk. */
  codes?: string[];
  asked?: number;
  options?: string[];
}

/** Completes the page the walk is on if it is a required-action page; undefined if it is not one. */
async function requiredAction(a: UserAgent, r: Answer, plan: Plan, w: Walk): Promise<Answer | undefined> {
  const id = pageId(r);
  if (id === "login-webauthn-register" && plan.register) {
    const action = formById(r, "register")!;
    return a.submit(action, plan.register.create(scriptField(r, "challenge")!, scriptField(r, "rpId")!, scriptField(r, "userid")!));
  }
  if (id === "login-login-recovery-authn-code-config") {
    const hidden = hiddenFields(r);
    w.codes = hidden["generatedRecoveryAuthnCodes"]!.split(",");
    return a.submit(formById(r, "kc-recovery-codes-settings-form")!, { ...hidden, kcRecoveryCodesConfirmationCheck: "on" });
  }
  return undefined;
}

export async function walk(e: Environment, clientId: string, username: string | undefined, plan: Plan = {}): Promise<Walk> {
  const a = plan.agent ?? new UserAgent(e.base);
  const redirect = plan.client?.redirect ?? CALLBACK;
  const { url, verifier } = authorizeUrl(e, clientId, plan.extra ?? {}, redirect);
  let r = await a.open(url);
  const w: Walk = { trail: [], status: "", agent: a };
  const done = new Set<string>();
  const once = (k: string): boolean => (done.has(k) ? false : (done.add(k), true));
  for (let i = 0; i < 16 && r.status === 200; i++) {
    const id = pageId(r);
    w.trail.push(`${id}${feedback(r) ? ` "${feedback(r)}"` : ""}`);
    const next = await requiredAction(a, r, plan, w);
    if (next) {
      r = next;
      continue;
    }
    if (username !== undefined && /name="username"/.test(r.body) && once("username")) {
      r = await a.submit(authenticateAction(r)!, { username });
      continue;
    }
    const another = formById(r, "kc-select-try-another-way-form");
    if (id === "login-webauthn-authenticate") {
      if (plan.passkey && once("passkey")) {
        r = await a.submit(formById(r, "webauth")!, plan.passkey.get(scriptField(r, "challenge")!, scriptField(r, "rpId")!));
        continue;
      }
      if (plan.choose && another && once("another")) {
        r = await a.submit(another, { tryAnotherWay: "on" });
        continue;
      }
      break;
    }
    if (id === "login-select-authenticator") {
      const options = credentialOptions(r);
      w.options = options.map((o) => o.label);
      const pick = plan.choose ? options.find((o) => plan.choose!.test(o.label)) : undefined;
      if (pick && once("choose")) {
        r = await a.submit(formById(r, "kc-select-credential-form") ?? authenticateAction(r)!, { authenticationExecution: pick.id });
        continue;
      }
      break;
    }
    if (id === "login-login-recovery-authn-code-input") {
      w.asked = Number(r.body.match(/Recovery code #(\d+)/)?.[1]);
      if (plan.code && once("code")) {
        r = await a.submit(authenticateAction(r)!, { recoveryCodeInput: plan.code(w.asked) });
        continue;
      }
      break;
    }
    break;
  }
  w.status = outcome(e, r);
  const code = codeOf(r, redirect);
  if (code) {
    const t = await exchange(e, clientId, code, verifier, plan.client ?? {});
    if (t.status === 200) w.tokens = JSON.parse(t.body) as Tokens;
    else w.status += `; exchange ${t.status} ${t.body.slice(0, 200)}`;
  }
  return w;
}

export const describeWalk = (w: Walk): string =>
  `pages [${w.trail.join(" > ")}] -> ${w.status}${w.options ? `; options [${w.options.join(" | ")}]` : ""}${w.codes ? `; ${w.codes.length} codes shown` : ""}`;

// ---------------------------------------------------------------- members

export interface Member {
  id: string;
  username: string;
  passkey: Authenticator;
  codes: string[];
}

const users = (e: Environment): string => `/realms/${e.realm}/users`;

export interface Credential { id: string; type: string; userLabel?: string }
export const credentials = (e: Environment, userId: string): Promise<Credential[]> => e.kc.get<Credential[]>(`${users(e)}/${userId}/credentials`);

/** Installs the test-only bootstrap client: game-cli's shape, with the built-in password flow as its browser override. */
export async function installBootstrapClient(e: Environment): Promise<void> {
  const flows = await e.kc.get<{ id: string; alias: string }[]>(`/realms/${e.realm}/authentication/flows`);
  const browser = flows.find((f) => f.alias === "browser");
  if (!browser) throw new Error("the realm has no built-in browser flow to bootstrap enrolment with");
  const existing = await e.kc.get<{ id: string }[]>(`/realms/${e.realm}/clients?clientId=${BOOTSTRAP_CLIENT}`);
  const body = {
    clientId: BOOTSTRAP_CLIENT,
    name: "TEST ONLY: enrols synthetic members; never present outside a test run",
    protocol: "openid-connect",
    publicClient: true,
    standardFlowEnabled: true,
    directAccessGrantsEnabled: false,
    implicitFlowEnabled: false,
    serviceAccountsEnabled: false,
    redirectUris: ["http://127.0.0.1/callback"],
    attributes: { "pkce.code.challenge.method": "S256" },
    authenticationFlowBindingOverrides: { browser: browser.id },
    defaultClientScopes: ["basic"],
  };
  if (existing[0]) await e.kc.call("PUT", `/realms/${e.realm}/clients/${existing[0].id}`, { ...body, id: existing[0].id });
  else await e.kc.call("POST", `/realms/${e.realm}/clients`, body);
}

export async function removeBootstrapClient(e: Environment): Promise<void> {
  for (const c of await e.kc.get<{ id: string }[]>(`/realms/${e.realm}/clients?clientId=${BOOTSTRAP_CLIENT}`))
    await e.kc.call("DELETE", `/realms/${e.realm}/clients/${c.id}`);
}

/**
 * A synthetic member holding the credentials asked for, enrolled through the bootstrap client and
 * left with no password. Needs installBootstrapClient first.
 */
export async function enrol(e: Environment, prefix: string, want: { passkey?: boolean; codes?: boolean } = { passkey: true, codes: true }): Promise<Member> {
  const username = `${prefix}-${randomBytes(4).toString("hex")}`;
  const password = randomBytes(16).toString("hex");
  const requiredActions = [...(want.passkey ? ["webauthn-register-passwordless"] : []), ...(want.codes ? ["CONFIGURE_RECOVERY_AUTHN_CODES"] : [])];
  const created = await e.kc.call("POST", users(e), {
    username,
    enabled: true,
    requiredActions,
    credentials: [{ type: "password", value: password, temporary: false }],
  });
  const id = KeycloakAdmin.createdId(created.location);
  const passkey = new Authenticator(e.base);
  let codes: string[] = [];
  if (requiredActions.length) {
    const a = new UserAgent(e.base);
    const { url, verifier } = authorizeUrl(e, BOOTSTRAP_CLIENT);
    const login = await a.open(url);
    let r = await a.submit(authenticateAction(login)!, { username, password, credentialId: "" });
    const w: Walk = { trail: [], status: "", agent: a };
    for (let i = 0; i < 4 && r.status === 200; i++) {
      const next = await requiredAction(a, r, { register: passkey }, w);
      if (!next) break;
      r = next;
    }
    const code = codeOf(r);
    if (!code) throw new Error(`enrolling ${username} stopped at ${outcome(e, r)}`);
    const t = await exchange(e, BOOTSTRAP_CLIENT, code, verifier);
    if (t.status !== 200) throw new Error(`enrolling ${username}: token ${t.status} ${t.body}`);
    codes = w.codes ?? [];
  }
  for (const c of await credentials(e, id)) if (c.type === "password") await e.kc.call("DELETE", `${users(e)}/${id}/credentials/${c.id}`);
  await e.kc.call("POST", `${users(e)}/${id}/logout`);
  return { id, username, passkey, codes };
}

export async function removeMembers(e: Environment, prefix: string): Promise<void> {
  for (const u of await e.kc.get<{ id: string; username: string }[]>(`${users(e)}?search=${encodeURIComponent(prefix)}&max=500`))
    if (u.username.startsWith(prefix)) await e.kc.call("DELETE", `${users(e)}/${u.id}`);
}
