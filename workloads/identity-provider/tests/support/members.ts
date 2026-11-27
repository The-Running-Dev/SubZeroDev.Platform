/**
 * Members and sign-ins against a running Keycloak configured from realm/members.json.
 *
 * Tests enrol their synthetic members the way the owner does (decided 2026-11-27): `invite` creates
 * the account and Keycloak emails it an action link, which the test reads from the compose mail
 * catcher and follows to register a passkey and recovery codes. No member ever holds a password.
 */
import { createHash, randomBytes } from "node:crypto";

import { KeycloakAdmin } from "../../src/admin.ts";
import { invite } from "../../src/members.ts";
import { loadDefinition } from "../../src/realm.ts";
import type { RealmDefinition } from "../../src/realm.ts";
import { Authenticator } from "./authenticator.ts";
import { UserAgent, authenticateAction, credentialOptions, feedback, formById, hiddenFields, pageId, scriptField } from "./user-agent.ts";
import type { Answer } from "./user-agent.ts";

/** Loopback redirect a native client listens on; any port matches the port-less registration. */
export const CALLBACK = "http://127.0.0.1:53682/callback";
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
  /** Answers Keycloak's "Delete <credential>?" page (kc_action=delete_credential:<id>). */
  deletion?: "accept" | "decline";
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
  /** The answer the walk stopped on. */
  last?: Answer;
  /** An enrolment link ended on Keycloak's "Your account has been updated." */
  enrolled?: boolean;
}

/** Completes the page the walk is on if it is a required-action page; undefined if it is not one. */
async function requiredAction(a: UserAgent, r: Answer, plan: Plan, w: Walk): Promise<Answer | undefined> {
  const id = pageId(r);
  if (id === "login-webauthn-register" && plan.register) {
    const action = formById(r, "register")!;
    return a.submit(action, plan.register.create(scriptField(r, "challenge")!, scriptField(r, "rpId")!, scriptField(r, "userid")!));
  }
  if (id === "login-delete-credential" && plan.deletion) {
    const action = r.body.match(/action="([^"]*login-actions\/required-action[^"]*execution=delete_credential[^"]*)"/)?.[1];
    if (!action) return undefined;
    return a.submit(action, plan.deletion === "accept" ? { accept: "" } : { "cancel-aia": "" });
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
      const wanted = plan.choose ?? (plan.passkey ? /passkey/i : undefined);
      const pick = wanted ? options.find((o) => wanted.test(o.label)) : undefined;
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
      // Keycloak opens on the member's first-priority credential; a passkey plan steps past codes.
      if (plan.passkey && !plan.code && another && once("another")) {
        r = await a.submit(another, { tryAnotherWay: "on" });
        continue;
      }
      break;
    }
    break;
  }
  w.status = outcome(e, r);
  w.last = r;
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

// ---------------------------------------------------------------- enrolment links

/** The compose mail catcher's API (mailpit). */
const mailApi = (env: NodeJS.ProcessEnv = process.env): string => (env["IDP_MAIL_API"] ?? "http://localhost:8025").replace(/\/+$/, "");

/** The address a synthetic member's links go to: the catcher accepts any, and nothing leaves it. */
export const addressOf = (username: string): string => `${username}@identity-provider.test`;

/**
 * The action link in the newest message to this address. Every message to the address is then
 * deleted, so the next call answers only a link sent after this one.
 */
export async function takeLink(address: string): Promise<string> {
  const api = mailApi();
  for (let i = 0; i < 50; i++) {
    const found = (await (await fetch(`${api}/api/v1/search?query=${encodeURIComponent(`to:"${address}"`)}`)).json()) as { messages: { ID: string }[] };
    if (found.messages.length) {
      const text = ((await (await fetch(`${api}/api/v1/message/${found.messages[0]!.ID}`)).json()) as { Text: string }).Text;
      await fetch(`${api}/api/v1/messages`, { method: "DELETE", headers: { "content-type": "application/json" }, body: JSON.stringify({ IDs: found.messages.map((m) => m.ID) }) });
      const link = text.match(/https?:\/\/\S*\/login-actions\/action-token\?\S+/)?.[0];
      if (!link) throw new Error(`the message to ${address} holds no action link`);
      return link;
    }
    await new Promise((r) => setTimeout(r, 200));
  }
  throw new Error(`no message reached ${address}`);
}

/** Follows an enrolment link: proceeds, registers the passkey with this authenticator, and records the codes shown. */
export async function followLink(e: Environment, link: string, register?: Authenticator): Promise<Walk> {
  const a = new UserAgent(e.base);
  let r = await a.open(link);
  const w: Walk = { trail: [], status: "", agent: a };
  let proceeded = false;
  for (let i = 0; i < 8 && r.status === 200; i++) {
    w.trail.push(`${pageId(r)}${feedback(r) ? ` "${feedback(r)}"` : ""}`);
    const next = await requiredAction(a, r, register ? { register } : {}, w);
    if (next) {
      r = next;
      continue;
    }
    // The link opens on "Perform the following action(s)", whose only way on is a link to the same token.
    const proceed = r.body.match(/href="([^"]*\/login-actions\/action-token\?[^"]*)"/)?.[1];
    if (pageId(r) === "login-info" && proceed && !proceeded) {
      proceeded = true;
      r = await a.open(new URL(proceed.replaceAll("&amp;", "&"), e.base).toString());
      continue;
    }
    break;
  }
  w.status = outcome(e, r);
  w.last = r;
  w.enrolled = r.status === 200 && /Your account has been updated/.test(r.body);
  return w;
}

/**
 * A synthetic member holding the credentials asked for. With both, the member is made by `invite`
 * exactly as the owner makes one. A single-credential member, which `invite` never makes, gets the
 * same link asking for only that action. With neither, the account is created and nothing is sent.
 */
export async function enrol(e: Environment, prefix: string, want: { passkey?: boolean; codes?: boolean } = { passkey: true, codes: true }): Promise<Member> {
  const username = `${prefix}-${randomBytes(4).toString("hex")}`;
  const email = addressOf(username);
  const passkey = new Authenticator(e.base);
  const actions = [...(want.passkey ? ["webauthn-register-passwordless"] : []), ...(want.codes ? ["CONFIGURE_RECOVERY_AUTHN_CODES"] : [])];
  let id: string;
  if (actions.length === 2) id = await invite(e.kc, e.def, username, email);
  else {
    id = KeycloakAdmin.createdId((await e.kc.call("POST", users(e), { username, email, enabled: true })).location);
    if (!actions.length) return { id, username, passkey, codes: [] };
    await e.kc.call("PUT", `${users(e)}/${id}/execute-actions-email`, actions);
  }
  const w = await followLink(e, await takeLink(email), passkey);
  if (!w.enrolled) throw new Error(`enrolling ${username} stopped at ${describeWalk(w)}`);
  return { id, username, passkey, codes: w.codes ?? [] };
}

/** Installs a client that overrides the browser flow with Keycloak's built-in password flow; `check` must refuse it. */
export async function installOverrideClient(e: Environment, clientId: string): Promise<void> {
  const flows = await e.kc.get<{ id: string; alias: string }[]>(`/realms/${e.realm}/authentication/flows`);
  const browser = flows.find((f) => f.alias === "browser");
  if (!browser) throw new Error("the realm has no built-in browser flow");
  await e.kc.call("POST", `/realms/${e.realm}/clients`, {
    clientId,
    name: "TEST ONLY: a browser-flow override check must refuse",
    protocol: "openid-connect",
    publicClient: true,
    standardFlowEnabled: true,
    redirectUris: ["http://127.0.0.1/callback"],
    attributes: { "pkce.code.challenge.method": "S256" },
    authenticationFlowBindingOverrides: { browser: browser.id },
  });
}

export async function removeClient(e: Environment, clientId: string): Promise<void> {
  for (const c of await e.kc.get<{ id: string }[]>(`/realms/${e.realm}/clients?clientId=${encodeURIComponent(clientId)}`))
    await e.kc.call("DELETE", `/realms/${e.realm}/clients/${c.id}`);
}

export async function removeMembers(e: Environment, prefix: string): Promise<void> {
  for (const u of await e.kc.get<{ id: string; username: string }[]>(`${users(e)}?search=${encodeURIComponent(prefix)}&max=500`))
    if (u.username.startsWith(prefix)) await e.kc.call("DELETE", `${users(e)}/${u.id}`);
}
