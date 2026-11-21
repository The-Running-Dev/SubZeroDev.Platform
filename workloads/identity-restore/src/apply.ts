/**
 * The restore procedure, run after the database has been restored and before members are let back in.
 *
 *  1. Refuse if this restore already reopened the realm, or if the replay window reaches back over an
 *     earlier restore that reopened (the record before it describes a timeline that restore replaced).
 *  2. Hold: disable the realm. Members cannot sign in or refresh while it is held. Public ingress must
 *     also stay closed until step 9 (runbook).
 *  3. Check the record is complete from the replay window's start to the end of the old service.
 *  4. Plan, then apply the plan's removals. Each one reads first and does nothing if already done.
 *  5. End every session: realm `notBefore` = now, and log every session out. This covers session
 *     rollback only. It does not remove a credential, a recovery-code set or an account the restore
 *     brought back; only the recorded removals in step 4 do that.
 *  6. Check members sign in through fixA.
 *  7. List members left with no passkey and no recovery codes.
 *  8. Re-run step 4 without writing: every removal must now be a no-op.
 *  9. Reopen the realm only when asked and nothing above blocks.
 */
import { assessCoverage, type Coverage } from "./coverage.ts";
import { checkFixA, type FixACheck } from "./fixa.ts";
import type { Keycloak } from "./keycloak.ts";
import { plan as makePlan, type Finding, type Intent, type Plan } from "./plan.ts";
import type { RestoreStore } from "./store.ts";
import { PASSKEY, RECOVERY_CODES, type BackupMeta, type RestoreLogEntry } from "./types.ts";

export interface RestoreInput {
  readonly kc: Keycloak;
  readonly realm: string;
  readonly store: RestoreStore;
  readonly restoreId: string;
  readonly backup: BackupMeta;
  /** How far before the dump's start the replay window begins. */
  readonly marginMs: number;
  readonly serviceEndedAtMs?: number;
  readonly storeRetiredAtMs?: number;
  /** Member-facing clients whose browser flow must be fixA. */
  readonly memberClients: readonly string[];
  /** Clients the restore and the release authenticate as; their admin events are the restore's own. */
  readonly restoreClientIds: readonly string[];
  readonly reopen: boolean;
  /** Members newly left with no way to sign in block reopening unless this is set. Owner policy (D6). */
  readonly allowReplayLockouts: boolean;
  readonly now: () => number;
}

export type OpResult = {
  readonly op: Intent["op"] | "drop-recovery-code";
  readonly userId: string;
  readonly credentialId?: string;
  readonly result: "applied" | "would-apply" | "no-op" | "absent";
  readonly detail?: string;
};

export interface TempLockMarker { readonly userId: string; readonly untilMs: number; readonly releaseTo: boolean }

export interface Lockout { readonly userId: string; readonly username: string; readonly credentialTypes: readonly string[] }

export interface RestoreReport {
  readonly restoreId: string;
  readonly outcome: "refused" | "held" | "reopened";
  readonly refusedBecause?: string;
  readonly windowStartMs: number;
  readonly coverage?: Coverage;
  readonly plan?: { readonly intents: number; readonly findings: readonly Finding[]; readonly counts: Plan["counts"] };
  readonly applied?: readonly OpResult[];
  readonly tempLocks?: readonly TempLockMarker[];
  readonly sessionReset?: { readonly notBefore: number };
  readonly fixA?: FixACheck;
  readonly lockouts?: { readonly replayAffected: readonly Lockout[]; readonly preExisting: readonly Lockout[] };
  readonly verify?: { readonly ok: boolean; readonly notNoop: readonly OpResult[] };
  readonly blocking: readonly string[];
}

interface Realm { id: string; realm: string; enabled: boolean; notBefore?: number; maxFailureWaitSeconds?: number }
interface User { id: string; username: string; enabled: boolean; serviceAccountClientId?: string; [k: string]: unknown }
interface Credential { id: string; type: string; createdDate?: number }

const groupByUser = (intents: readonly Intent[]) => {
  const m = new Map<string, Intent[]>();
  for (const i of intents) m.set(i.userId, [...(m.get(i.userId) ?? []), i]);
  return m;
};

async function getUser(kc: Keycloak, realm: string, id: string): Promise<User | undefined> {
  const r = await kc.raw("GET", realm, `/users/${encodeURIComponent(id)}`);
  if (r.status === 404) return undefined;
  if (r.status !== 200) throw new Error(`GET user ${id}: HTTP ${r.status}`);
  return JSON.parse(r.body) as User;
}

/** Brings every user the plan names to the planned state. With `write` false, only reports what it
 *  would change. Reads before every write, so a second run is all no-ops. */
export async function ensurePlan(kc: Keycloak, realm: string, intents: readonly Intent[], nowMs: number, write: boolean,
  priorMarkers: ReadonlyMap<string, TempLockMarker>): Promise<{ results: OpResult[]; markers: TempLockMarker[] }> {
  const results: OpResult[] = [];
  const markers: TempLockMarker[] = [];
  const done = write ? "applied" : "would-apply";
  const enc = encodeURIComponent;
  const byUser = groupByUser(intents);
  // A user the first run locked is visited even if this run plans nothing for them: the lock may have
  // ended between the runs, and the first run's disable must then be undone.
  const userIds = [...new Set([...byUser.keys(), ...priorMarkers.keys()])].sort();
  for (const userId of userIds) {
    const list = byUser.get(userId) ?? [];
    const user = await getUser(kc, realm, userId);
    if (!user) { for (const i of list) results.push({ op: i.op, userId, result: "absent" }); continue; }

    const del = list.find((i) => i.op === "delete-user");
    if (del) {
      if (write) await kc.write("DELETE", realm, `/users/${enc(userId)}`);
      results.push({ op: "delete-user", userId, result: done });
      continue;
    }

    const creds = list.some((i) => i.op === "delete-credential" || i.op === "drop-recovery-codes")
      ? await kc.json<Credential[]>(realm, `/users/${enc(userId)}/credentials`) : [];
    for (const i of list) {
      if (i.op === "delete-credential") {
        if (!creds.some((c) => c.id === i.credentialId)) { results.push({ op: i.op, userId, credentialId: i.credentialId, result: "no-op" }); continue; }
        if (write) await kc.write("DELETE", realm, `/users/${enc(userId)}/credentials/${enc(i.credentialId)}`);
        results.push({ op: i.op, userId, credentialId: i.credentialId, result: done });
      } else if (i.op === "drop-recovery-codes") {
        const sets = creds.filter((c) => c.type === RECOVERY_CODES && (c.createdDate ?? 0) <= i.createdAtOrBeforeMs);
        if (sets.length === 0) results.push({ op: i.op, userId, result: "no-op" });
        for (const c of sets) {
          // Skip one a delete-credential above already removed.
          if (list.some((x) => x.op === "delete-credential" && x.credentialId === c.id)) continue;
          if (write) await kc.write("DELETE", realm, `/users/${enc(userId)}/credentials/${enc(c.id)}`);
          results.push({ op: "drop-recovery-code", userId, credentialId: c.id, result: done });
        }
      }
    }

    // One target for `enabled`: an unexpired temporary lock holds the account disabled whatever the
    // last enable or disable said; `release-temp-locks` lifts it to `releaseTo` when it ends.
    const set = list.find((i): i is Extract<Intent, { op: "set-enabled" }> => i.op === "set-enabled");
    const lock = list.find((i): i is Extract<Intent, { op: "temp-lock" }> => i.op === "temp-lock");
    let target: boolean | undefined = set?.enabled;
    const prior = priorMarkers.get(userId);
    if (lock && lock.untilMs > nowMs) {
      // On a rerun the account is already disabled by the first run, so its state now is not its state
      // before the lock; the first run's marker holds that.
      const releaseTo = set?.enabled ?? prior?.releaseTo ?? user.enabled;
      markers.push({ userId, untilMs: lock.untilMs, releaseTo });
      target = false;
    } else {
      if (lock) results.push({ op: "temp-lock", userId, result: "no-op", detail: "the lock has ended" });
      if (prior && target === undefined) target = prior.releaseTo;
    }
    if (target !== undefined) {
      const op = (lock && lock.untilMs > nowMs) || (prior && !set) ? "temp-lock" : "set-enabled";
      if (user.enabled === target) results.push({ op, userId, result: "no-op" });
      else {
        if (write) await kc.write("PUT", realm, `/users/${enc(userId)}`, { ...user, enabled: target });
        results.push({ op, userId, result: done, detail: `enabled → ${target}` });
      }
    }
  }
  return { results, markers };
}

async function* allUsers(kc: Keycloak, realm: string): AsyncGenerator<User> {
  for (let first = 0; ; first += 200) {
    const page = await kc.json<User[]>(realm, `/users?first=${first}&max=200&briefRepresentation=false`);
    yield* page;
    if (page.length < 200) return;
  }
}

/** Members with neither a passkey nor a recovery-code set: they cannot sign in through fixA. */
export async function lockoutCensus(kc: Keycloak, realm: string, affected: ReadonlySet<string>) {
  const replayAffected: Lockout[] = [];
  const preExisting: Lockout[] = [];
  for await (const u of allUsers(kc, realm)) {
    if (u.serviceAccountClientId) continue;
    const types = (await kc.json<Credential[]>(realm, `/users/${encodeURIComponent(u.id)}/credentials`)).map((c) => c.type);
    if (types.includes(PASSKEY) || types.includes(RECOVERY_CODES)) continue;
    (affected.has(u.id) ? replayAffected : preExisting).push({ userId: u.id, username: u.username, credentialTypes: types });
  }
  return { replayAffected, preExisting };
}

function markersOf(log: readonly RestoreLogEntry[], restoreId: string): Map<string, TempLockMarker> {
  const m = new Map<string, TempLockMarker>();
  for (const e of log) {
    if (e.restoreId !== restoreId || e.phase !== "applied") continue;
    for (const t of (e.detail["tempLocks"] as TempLockMarker[] | undefined) ?? []) if (!m.has(t.userId)) m.set(t.userId, t);
  }
  return m;
}

export async function restore(input: RestoreInput): Promise<RestoreReport> {
  const { kc, realm, store, restoreId } = input;
  const nowMs = input.now();
  const windowStartMs = input.backup.startedAtMs - input.marginMs;
  const log = await store.restoreLog();
  const entry = (phase: RestoreLogEntry["phase"], detail: Record<string, unknown>) =>
    store.appendRestoreLog({ restoreId, phase, atMs: input.now(), detail: { windowFromMs: windowStartMs, ...detail } });

  const refuse = async (why: string): Promise<RestoreReport> => {
    await entry("refused", { why });
    return { restoreId, outcome: "refused", refusedBecause: why, windowStartMs, blocking: [why] };
  };
  if (log.some((e) => e.restoreId === restoreId && e.phase === "reopened"))
    return refuse(`restore ${restoreId} already reopened the realm; a rerun would replay against members' changes since`);
  const earlier = log.find((e) => e.restoreId !== restoreId && e.phase === "reopened" && e.atMs >= windowStartMs);
  if (earlier) return refuse(`the replay window starts before restore ${earlier.restoreId} reopened at ${new Date(earlier.atMs).toISOString()}: the record before that describes a timeline it replaced`);

  // 2. Hold.
  const r0 = await kc.json<Realm>(realm, "");
  if (r0.enabled) await kc.write("PUT", realm, "", { realm, enabled: false });
  await entry("held", { wasEnabled: r0.enabled });

  const blocking: string[] = [];
  // 3. Coverage.
  const coverage = assessCoverage({
    windows: await store.windows(), windowStartMs,
    ...(input.serviceEndedAtMs === undefined ? {} : { serviceEndedAtMs: input.serviceEndedAtMs }),
    ...(input.storeRetiredAtMs === undefined ? {} : { storeRetiredAtMs: input.storeRetiredAtMs }),
  });
  for (const g of coverage.gaps) blocking.push(`record gap (${g.kind}) ${g.fromMs}–${g.throughMs ?? "open"}: ${g.reasons.join("; ")}`);

  // 4. Plan and apply. Events after the service ended belong to the restored timeline, not the old one.
  const end = input.serviceEndedAtMs ?? Number.POSITIVE_INFINITY;
  const events = (await store.eventsFrom(r0.id, windowStartMs)).filter((e) => e.timeMs <= end);
  const p = makePlan({ events, realmId: r0.id, backupStartedAtMs: input.backup.startedAtMs, backupFinishedAtMs: input.backup.finishedAtMs,
    nowMs, maxFailureWaitSeconds: r0.maxFailureWaitSeconds ?? 900, restoreClientIds: input.restoreClientIds });
  for (const f of p.findings) if (f.severity === "blocking") blocking.push(`event ${f.eventId} (${f.code}): ${f.text}`);
  const prior = markersOf(log, restoreId);
  const applied = await ensurePlan(kc, realm, p.intents, nowMs, true, prior);

  // 5. Session reset.
  const notBefore = Math.floor(input.now() / 1000);
  await kc.write("PUT", realm, "", { realm, notBefore });
  await kc.write("POST", realm, "/logout-all");

  // 6. fixA.
  const fixA = await checkFixA(kc, realm, input.memberClients);
  if (!fixA.ok) blocking.push(`members do not sign in through fixA: ${fixA.flows.flatMap((f) => f.problems.map((x) => `${f.boundTo}: ${x}`)).join("; ")}`);

  // 7. Lockouts.
  const credentialTouched = new Set(p.intents.filter((i) => i.op === "delete-credential" || i.op === "drop-recovery-codes").map((i) => i.userId));
  const lockouts = await lockoutCensus(kc, realm, credentialTouched);
  if (lockouts.replayAffected.length > 0 && !input.allowReplayLockouts)
    blocking.push(`${lockouts.replayAffected.length} member(s) left with no passkey and no recovery codes by the replay; recovering them is D6, unresolved`);

  // 8. Verify.
  const check = await ensurePlan(kc, realm, p.intents, nowMs, false, new Map([...prior, ...applied.markers.map((m) => [m.userId, m] as const)]));
  const notNoop = check.results.filter((x) => x.result === "would-apply");
  const r1 = await kc.json<Realm>(realm, "");
  if (notNoop.length > 0) blocking.push(`${notNoop.length} removal(s) still not in place after applying`);
  if ((r1.notBefore ?? 0) < notBefore) blocking.push("the realm's notBefore did not take");
  if (r1.enabled) blocking.push("the realm is not held");

  const tempLocks = applied.markers;
  await entry("applied", { tempLocks, intents: p.intents.length, results: applied.results.length, blocking: blocking.length, notBefore });

  let outcome: RestoreReport["outcome"] = "held";
  if (input.reopen && blocking.length === 0) {
    await kc.write("PUT", realm, "", { realm, enabled: true });
    await entry("reopened", { tempLocks });
    outcome = "reopened";
  }
  return {
    restoreId, outcome, windowStartMs, coverage,
    plan: { intents: p.intents.length, findings: p.findings, counts: p.counts },
    applied: applied.results, tempLocks, sessionReset: { notBefore }, fixA, lockouts,
    verify: { ok: notNoop.length === 0, notNoop }, blocking,
  };
}
