/**
 * Enrolment and the owner reset (decided 2026-11-27), for either realm: a member of `members` and an
 * operator of `operators` are enrolled and reset the same way. Both are operator commands through the
 * admin API; neither is reachable from the sign-in flow, which still refuses a username with no
 * credential.
 *
 * `invite` creates the account with the address the link goes to, and has Keycloak email that
 * address an action link. The link registers a passkey and then recovery codes; the account holds no
 * credential until the member follows it. `reset` is the way back when the recovery codes are gone
 * too: it removes every credential, ends every session and offline token, and sends the same link to
 * the same account, so the member keeps their subject (the `sub` the edge and game-service know).
 *
 * The address is admin-only (see the realm's user profile): no member can see or change it and no
 * token carries it. The link lasts Keycloak's default for admin-sent action links; that lifetime is
 * not decided.
 */
import { KeycloakAdmin } from "./admin.ts";
import type { RealmDefinition } from "./realm.ts";

/** What the link asks for, in the order the member meets it. */
export const ENROLMENT_ACTIONS = ["webauthn-register-passwordless", "CONFIGURE_RECOVERY_AUTHN_CODES"] as const;

const enc = encodeURIComponent;
const USERNAME = /^[a-z0-9][a-z0-9._-]{1,62}$/;
const EMAIL = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

interface User { id: string; username: string; email?: string }

/** The member with exactly this username, or undefined. */
export async function findMember(kc: KeycloakAdmin, realm: string, username: string): Promise<User | undefined> {
  return (await kc.get<User[]>(`/realms/${enc(realm)}/users?username=${enc(username)}&exact=true`)).find((u) => u.username === username);
}

async function sendLink(kc: KeycloakAdmin, realm: string, id: string): Promise<void> {
  await kc.call("PUT", `/realms/${enc(realm)}/users/${id}/execute-actions-email`, [...ENROLMENT_ACTIONS]);
}

/** Creates the member and emails the enrolment link. Answers the new member's id. */
export async function invite(kc: KeycloakAdmin, def: RealmDefinition, username: string, email: string): Promise<string> {
  const realm = def.realm.realm;
  if (!USERNAME.test(username)) throw new Error(`username ${JSON.stringify(username)} must be 2-63 lowercase letters, digits, '.', '_' or '-'`);
  if (!EMAIL.test(email)) throw new Error(`${JSON.stringify(email)} is not an email address`);
  if (await findMember(kc, realm, username)) throw new Error(`${username} already exists in realm ${realm}; \`reset\` sends a new link to an existing account`);
  const created = await kc.call("POST", `/realms/${enc(realm)}/users`, { username, email, enabled: true });
  const id = KeycloakAdmin.createdId(created.location);
  try {
    await sendLink(kc, realm, id);
  } catch (e) {
    // An account whose link never went out is an account nobody can enrol; remove it so the invite can be repeated.
    await kc.call("DELETE", `/realms/${enc(realm)}/users/${id}`);
    throw e;
  }
  return id;
}

/**
 * Removes every credential of the member, ends their sessions and offline tokens, and emails a new
 * enrolment link. The account, and so its subject, is kept. Answers what it removed.
 */
export async function reset(kc: KeycloakAdmin, def: RealmDefinition, username: string): Promise<{ id: string; credentials: number; clients: string[] }> {
  const realm = def.realm.realm;
  const user = await findMember(kc, realm, username);
  if (!user) throw new Error(`${username} does not exist in realm ${realm}`);
  if (!user.email) throw new Error(`${username} in realm ${realm} has no address to send the link to`);
  const base = `/realms/${enc(realm)}/users/${user.id}`;
  const credentials = await kc.get<{ id: string }[]>(`${base}/credentials`);
  for (const c of credentials) await kc.call("DELETE", `${base}/credentials/${c.id}`);
  // Logout ends the sessions and refuses refresh tokens issued before it; revoking each client's
  // grant ends the offline sessions, which a logout leaves in place.
  await kc.call("POST", `${base}/logout`);
  const grants = await kc.get<{ clientId: string }[]>(`${base}/consents`);
  for (const g of grants) await kc.call("DELETE", `${base}/consents/${enc(g.clientId)}`);
  await sendLink(kc, realm, user.id);
  return { id: user.id, credentials: credentials.length, clients: grants.map((g) => g.clientId) };
}
