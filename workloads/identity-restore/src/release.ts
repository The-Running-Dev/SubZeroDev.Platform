/**
 * Lifts the disables that stood in for temporary lockouts, once each lock's end has passed. Run on a
 * schedule after a restore has reopened. A member disabled for another reason since the reopen stays
 * disabled and is reported.
 */
import type { TempLockMarker } from "./apply.ts";
import type { Keycloak } from "./keycloak.ts";
import type { RestoreStore } from "./store.ts";
import type { KcAdminEvent, KcUserEvent } from "./types.ts";

export interface ReleaseResult {
  readonly restoreId: string;
  readonly userId: string;
  readonly untilMs: number;
  readonly result: "released" | "pending" | "already-enabled" | "absent" | "kept-disabled" | "skipped";
  readonly detail?: string;
}

/** Whether an admin's update of a user meant the account to be disabled. Keycloak writes
 *  `updated_enabled` only when `enabled` changed, so an admin disabling an account the stand-in already
 *  holds disabled leaves no detail, only `"enabled": false` in the representation (G1, member hana). An
 *  update whose effect cannot be read counts as a disable: releasing an account an admin meant to keep
 *  disabled is the worse mistake. */
function disables(e: KcAdminEvent): boolean {
  const updated = e.details?.["updated_enabled"];
  if (updated !== undefined) return updated === "false";
  if (e.representation === undefined) return true;
  try {
    const rep = JSON.parse(e.representation) as { enabled?: unknown };
    return rep.enabled === false;
  } catch { return true; }
}

export async function releaseTempLocks(input: {
  readonly kc: Keycloak; readonly realm: string; readonly store: RestoreStore;
  readonly restoreClientIds: readonly string[]; readonly now: () => number;
}): Promise<ReleaseResult[]> {
  const { kc, realm, store } = input;
  const nowMs = input.now();
  const log = await store.restoreLog();
  const reopenedAt = new Map(log.filter((e) => e.phase === "reopened").map((e) => [e.restoreId, e.atMs]));
  const markers = new Map<string, { restoreId: string; m: TempLockMarker }>();
  for (const e of log) {
    if (e.phase !== "reopened") continue;
    for (const m of (e.detail["tempLocks"] as TempLockMarker[] | undefined) ?? []) markers.set(`${e.restoreId}/${m.userId}`, { restoreId: e.restoreId, m });
  }
  const out: ReleaseResult[] = [];
  const enc = encodeURIComponent;
  for (const { restoreId, m } of markers.values()) {
    const base = { restoreId, userId: m.userId, untilMs: m.untilMs };
    if (m.untilMs > nowMs) { out.push({ ...base, result: "pending" }); continue; }
    if (!m.releaseTo) { out.push({ ...base, result: "kept-disabled", detail: "the account was disabled before the lock" }); continue; }
    const r = await kc.raw("GET", realm, `/users/${enc(m.userId)}`);
    if (r.status === 404) { out.push({ ...base, result: "absent" }); continue; }
    if (r.status !== 200) throw new Error(`GET user ${m.userId}: HTTP ${r.status}`);
    const user = JSON.parse(r.body) as { enabled: boolean } & Record<string, unknown>;
    if (user.enabled) { out.push({ ...base, result: "already-enabled" }); continue; }

    const since = reopenedAt.get(restoreId)!;
    const admin = await kc.json<KcAdminEvent[]>(realm, `/admin-events?resourcePath=${enc(`users/${m.userId}`)}&dateFrom=${since}&max=1000`);
    const byOthers = admin.filter((e) => e.operationType === "UPDATE" && !input.restoreClientIds.includes(e.authDetails?.clientId ?? "") && disables(e));
    const perm = await kc.json<KcUserEvent[]>(realm, `/events?user=${enc(m.userId)}&type=USER_DISABLED_BY_PERMANENT_LOCKOUT&dateFrom=${since}&max=10`);
    if (byOthers.length > 0 || perm.length > 0) {
      out.push({ ...base, result: "skipped", detail: byOthers.length > 0 ? `disabled by ${byOthers[0]!.authDetails?.clientId ?? "an admin"} after the reopen` : "permanently locked out after the reopen" });
      continue;
    }
    await kc.write("PUT", realm, `/users/${enc(m.userId)}`, { ...user, enabled: true });
    out.push({ ...base, result: "released" });
  }
  const changed = out.filter((x) => x.result === "released" || x.result === "skipped");
  if (changed.length > 0) {
    for (const restoreId of new Set(changed.map((x) => x.restoreId)))
      await store.appendRestoreLog({ restoreId, phase: "released", atMs: input.now(), detail: { results: changed.filter((x) => x.restoreId === restoreId) } });
  }
  return out;
}
