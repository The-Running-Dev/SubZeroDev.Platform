/**
 * Checks that members sign in through the corrected recovery flow (fixA): Username Form REQUIRED, then one
 * REQUIRED sub-flow holding exactly two ALTERNATIVES, passkey and recovery code. A restored database
 * holds the flows as they were at the backup, so a backup taken before fixA brings back the unsafe flow;
 * the restore must not reopen on it. Disabled executions, and everything under a disabled sub-flow, are
 * ignored: they never run.
 */
import type { Keycloak } from "./keycloak.ts";

export interface Execution {
  readonly level: number;
  readonly index: number;
  readonly requirement: string;
  readonly providerId?: string;
  readonly authenticationFlow?: boolean;
  readonly displayName?: string;
}

export const USERNAME_FORM = "auth-username-form";
export const PASSKEY_FORM = "webauthn-authenticator-passwordless";
export const RECOVERY_FORM = "auth-recovery-authn-code-form";

/** Returns what is wrong with a flow's executions (as `GET /authentication/flows/{alias}/executions`
 *  lists them, depth first). Empty means the flow is fixA. */
export function fixAProblems(executions: readonly Execution[]): string[] {
  const live: Execution[] = [];
  let skipBelow: number | null = null;
  for (const e of executions) {
    if (skipBelow !== null && e.level > skipBelow) continue;
    skipBelow = null;
    if (e.requirement === "DISABLED") { if (e.authenticationFlow) skipBelow = e.level; continue; }
    live.push(e);
  }
  const name = (e: Execution) => e.providerId ?? e.displayName ?? "?";
  const problems: string[] = [];
  const top = live.filter((e) => e.level === 0).sort((a, b) => a.index - b.index);
  if (top.length !== 2) problems.push(`expected 2 top-level steps, found ${top.length}: ${top.map((e) => `${name(e)} ${e.requirement}`).join(", ")}`);
  const [first, second] = top;
  if (!first || first.providerId !== USERNAME_FORM || first.requirement !== "REQUIRED")
    problems.push(`the first step must be ${USERNAME_FORM} REQUIRED, found ${first ? `${name(first)} ${first.requirement}` : "nothing"}`);
  if (!second || second.authenticationFlow !== true || second.requirement !== "REQUIRED")
    problems.push(`the second step must be a REQUIRED sub-flow, found ${second ? `${name(second)} ${second.requirement}` : "nothing"}`);
  const deeper = live.filter((e) => e.level >= 2);
  if (deeper.length > 0) problems.push(`unexpected nested steps: ${deeper.map(name).join(", ")}`);
  const inner = live.filter((e) => e.level === 1);
  const want = [PASSKEY_FORM, RECOVERY_FORM];
  const got = inner.map((e) => `${name(e)} ${e.requirement}`).sort();
  const expected = want.map((p) => `${p} ALTERNATIVE`).sort();
  if (got.join("|") !== expected.join("|")) problems.push(`the sub-flow must hold exactly ${expected.join(" and ")}, found ${got.join(", ") || "nothing"}`);
  return problems;
}

interface Realm { browserFlow?: string }
interface Client { id: string; clientId: string; authenticationFlowBindingOverrides?: { browser?: string } }
interface Flow { id: string; alias: string }

export interface FixACheck { readonly ok: boolean; readonly flows: ReadonlyArray<{ readonly boundTo: string; readonly alias: string | null; readonly problems: readonly string[] }> }

/** Checks the realm's browser flow and the browser flow each member client actually uses. */
export async function checkFixA(kc: Keycloak, realm: string, memberClients: readonly string[]): Promise<FixACheck> {
  const r = await kc.json<Realm>(realm, "");
  const flows = await kc.json<Flow[]>(realm, "/authentication/flows");
  const byId = new Map(flows.map((f) => [f.id, f.alias]));
  const targets: Array<{ boundTo: string; alias: string | null; problems: string[] }> = [];
  targets.push({ boundTo: "realm browser flow", alias: r.browserFlow ?? null, problems: [] });
  for (const clientId of memberClients) {
    const found = await kc.json<Client[]>(realm, `/clients?clientId=${encodeURIComponent(clientId)}`);
    const c = found.find((x) => x.clientId === clientId);
    if (!c) { targets.push({ boundTo: `client ${clientId}`, alias: null, problems: ["the member client does not exist in the restored realm"] }); continue; }
    const override = c.authenticationFlowBindingOverrides?.browser;
    if (override === undefined || override === "") { targets.push({ boundTo: `client ${clientId} (realm flow)`, alias: r.browserFlow ?? null, problems: [] }); continue; }
    const alias = byId.get(override) ?? null;
    targets.push({ boundTo: `client ${clientId}`, alias, problems: alias === null ? [`its browser override ${override} names no flow`] : [] });
  }
  for (const t of targets) {
    if (t.alias === null) { if (t.problems.length === 0) t.problems.push("no browser flow is bound"); continue; }
    t.problems.push(...fixAProblems(await kc.json<Execution[]>(realm, `/authentication/flows/${encodeURIComponent(t.alias)}/executions`)));
  }
  // The realm flow is always checked: every client without its own override uses it, Keycloak's account
  // console included, and a client added later starts on it.
  return { ok: targets.every((t) => t.problems.length === 0), flows: targets };
}
