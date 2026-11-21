/**
 * The replay planner. Folds the recorded events after the backup into one final intent per user and
 * credential, so the result does not depend on the order the events arrive in or on how many times it
 * runs. Pure: no I/O.
 *
 * What it can and cannot do, by construction:
 *  - It re-applies removals the restore undid: deleted accounts, removed credentials, used or replaced
 *    recovery-code sets, disables and locks.
 *  - It cannot bring back additions the restore lost: accounts, passkeys or code sets created after the
 *    backup. Those are listed as `lost`, for the operator to tell the members concerned.
 *  - Session events are listed, not replayed: the restore ends every session unconditionally, which covers
 *    session rollback and nothing else.
 *  - An event it cannot classify, or one that changes realm configuration, is `blocking`.
 */
import { RECOVERY_CODES, type KcAdminEvent, type KcUserEvent, type StoredEvent } from "./types.ts";

export interface EventRef { readonly id: string; readonly timeMs: number; readonly what: string }

export type Intent =
  | { readonly op: "delete-user"; readonly userId: string; readonly because: readonly EventRef[] }
  | { readonly op: "delete-credential"; readonly userId: string; readonly credentialId: string; readonly because: readonly EventRef[] }
  /** Delete every recovery-code credential created at or before `createdAtOrBeforeMs`. A set created after
   *  that time cannot be in the backup, so a rerun never deletes a set a member made after the restore. */
  | { readonly op: "drop-recovery-codes"; readonly userId: string; readonly createdAtOrBeforeMs: number; readonly because: readonly EventRef[] }
  | { readonly op: "set-enabled"; readonly userId: string; readonly enabled: boolean; readonly because: readonly EventRef[] }
  /** Keycloak has no API to impose a temporary lock, so it is replayed as a disable that
   *  `release-temp-locks` lifts at `untilMs`. */
  | { readonly op: "temp-lock"; readonly userId: string; readonly untilMs: number; readonly because: readonly EventRef[] };

export interface Finding {
  readonly severity: "blocking" | "lost" | "info";
  readonly code: string;
  readonly eventId: string;
  readonly timeMs: number;
  readonly userId?: string;
  readonly text: string;
}

export interface Plan {
  readonly intents: readonly Intent[];
  readonly findings: readonly Finding[];
  readonly counts: {
    readonly considered: number;
    readonly otherRealm: number;
    readonly restoreOwn: number;
    readonly beforeBackupStart: number;
    readonly session: number;
  };
}

export interface PlanInput {
  readonly events: readonly StoredEvent[];
  /** Keycloak's internal id of the realm being restored (`RealmRepresentation.id`). */
  readonly realmId: string;
  readonly backupStartedAtMs: number;
  readonly backupFinishedAtMs: number;
  readonly nowMs: number;
  /** The realm's `maxFailureWaitSeconds`, used when a temporary-lock event carries no end time. */
  readonly maxFailureWaitSeconds: number;
  /** Clients the restore itself authenticates as; their admin events are the restore's own and ignored. */
  readonly restoreClientIds: readonly string[];
}

/** User event types the record must hold for the plan to be complete. The recorder refuses to mark a
 *  window covered unless all of these are enabled in the realm's event configuration. */
export const REQUIRED_USER_EVENT_TYPES: readonly string[] = [
  "LOGIN",
  "CLIENT_LOGIN",
  "REMOVE_CREDENTIAL",
  "UPDATE_CREDENTIAL",
  "DELETE_ACCOUNT",
  "USER_DISABLED_BY_TEMPORARY_LOCKOUT",
  "USER_DISABLED_BY_PERMANENT_LOCKOUT",
  "UPDATE_PASSWORD",
  "RESET_PASSWORD",
  "REMOVE_FEDERATED_IDENTITY",
];

/** Ended or refreshed sessions and token traffic: covered by the unconditional session reset. */
const SESSION_TYPES = new Set([
  "LOGIN", "LOGOUT", "REVOKE_GRANT", "REFRESH_TOKEN", "CODE_TO_TOKEN", "CLIENT_LOGIN", "TOKEN_EXCHANGE",
  "INTROSPECT_TOKEN", "USER_INFO_REQUEST", "PERMISSION_TOKEN", "AUTHREQID_TO_TOKEN", "CLIENT_INITIATED_ACCOUNT_LINKING",
  "OAUTH2_DEVICE_AUTH", "OAUTH2_DEVICE_CODE_TO_TOKEN", "OAUTH2_DEVICE_VERIFY_USER_CODE", "RESTART_AUTHENTICATION",
  "INVALID_SIGNATURE", "VALIDATE_ACCESS_TOKEN", "PUSHED_AUTHORIZATION_REQUEST", "AUTHREQID_TO_TOKEN",
  "USER_SESSION_DELETED",
]);

/** Additions and profile changes the restore loses and replay cannot re-create. */
const LOST_TYPES = new Set([
  "REGISTER", "UPDATE_PROFILE", "UPDATE_EMAIL", "VERIFY_EMAIL", "VERIFY_PROFILE", "UPDATE_CREDENTIAL",
  "CUSTOM_REQUIRED_ACTION", "GRANT_CONSENT", "UPDATE_CONSENT", "REVOKE_CONSENT", "SEND_VERIFY_EMAIL",
  "SEND_RESET_PASSWORD", "SEND_IDENTITY_PROVIDER_LINK", "EXECUTE_ACTIONS", "EXECUTE_ACTION_TOKEN",
  "UPDATE_CONSENT", "FEDERATED_IDENTITY_LINK", "IDENTITY_PROVIDER_FIRST_LOGIN", "IDENTITY_PROVIDER_POST_LOGIN",
  "IDENTITY_PROVIDER_LINK_ACCOUNT", "IDENTITY_PROVIDER_LOGIN", "IDENTITY_PROVIDER_RESPONSE",
  "IDENTITY_PROVIDER_RETRIEVE_TOKEN", "INVITE_ORG",
]);

function parseLockEnd(raw: string | undefined): number | null {
  if (raw === undefined) return null;
  if (/^\d+$/.test(raw)) { const n = Number(raw); return n < 1e12 ? n * 1000 : n; }
  // Keycloak 26.8.1 writes `not_before` as a UTC local date-time with no zone, e.g. 2026-11-20T22:22:04.
  const iso = /^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(\.\d+)?$/.test(raw) ? `${raw}Z` : raw;
  const t = Date.parse(iso);
  return Number.isNaN(t) ? null : t;
}

interface UserFold {
  deletes: EventRef[];
  credentials: Map<string, EventRef[]>;
  recovery: EventRef[];
  enabled: Array<{ ref: EventRef; value: boolean }>;
  locks: Array<{ ref: EventRef; untilMs: number }>;
  clears: EventRef[];
}

const newFold = (): UserFold => ({ deletes: [], credentials: new Map(), recovery: [], enabled: [], locks: [], clears: [] });

export function plan(input: PlanInput): Plan {
  const findings: Finding[] = [];
  const users = new Map<string, UserFold>();
  const clearsAll: EventRef[] = [];
  const counts = { considered: 0, otherRealm: 0, restoreOwn: 0, beforeBackupStart: 0, session: 0 };
  const fold = (u: string) => { let f = users.get(u); if (!f) { f = newFold(); users.set(u, f); } return f; };
  const find = (severity: Finding["severity"], code: string, ref: EventRef, text: string, userId?: string) =>
    findings.push({ severity, code, eventId: ref.id, timeMs: ref.timeMs, ...(userId === undefined ? {} : { userId }), text });

  // Sorted so the fold below sees one order however the events were read; ties broken by id.
  const events = [...input.events].sort((a, b) => a.timeMs - b.timeMs || (a.id < b.id ? -1 : a.id > b.id ? 1 : 0));

  for (const e of events) {
    if (e.body.realmId !== input.realmId) { counts.otherRealm++; continue; }
    if (e.kind === "admin" && input.restoreClientIds.includes(e.body.authDetails?.clientId ?? "")) { counts.restoreOwn++; continue; }
    counts.considered++;
    if (e.timeMs < input.backupStartedAtMs) counts.beforeBackupStart++;
    if (e.kind === "user") planUser(e.body, e);
    else planAdmin(e.body, e);
  }

  function planUser(ev: KcUserEvent, e: StoredEvent) {
    const ref: EventRef = { id: e.id, timeMs: e.timeMs, what: `user ${ev.type}` };
    const d = ev.details ?? {};
    const u = ev.userId;
    if (ev.type.endsWith("_ERROR")) {
      find("info", "error-event", ref, `${ev.type}: a failed attempt changes no credential or account state (failure counts below the lockout threshold are lost)`, u);
      return;
    }
    if (d["credential_type"] === RECOVERY_CODES) {
      if (!u) return find("blocking", "missing-user", ref, `${ev.type} on a recovery-code credential names no user`);
      // The event does not say which code was used, or the set may have been replaced: drop the set.
      fold(u).recovery.push(ref);
      if (ev.type === "LOGIN") counts.session++;
      return;
    }
    switch (ev.type) {
      case "REMOVE_CREDENTIAL": {
        const c = d["credential_id"] ?? d["selected_credential_id"];
        if (!u || !c) return find("blocking", "unidentified-removal", ref, "a credential removal that does not name both the user and the credential", u);
        const list = fold(u).credentials.get(c) ?? [];
        list.push(ref);
        fold(u).credentials.set(c, list);
        return;
      }
      case "DELETE_ACCOUNT":
        if (!u) return find("blocking", "missing-user", ref, "an account deletion that names no user");
        fold(u).deletes.push(ref);
        return;
      case "USER_DISABLED_BY_PERMANENT_LOCKOUT":
        if (!u) return find("blocking", "missing-user", ref, "a permanent lockout that names no user");
        fold(u).enabled.push({ ref, value: false });
        return;
      case "USER_DISABLED_BY_TEMPORARY_LOCKOUT": {
        if (!u) return find("blocking", "missing-user", ref, "a temporary lockout that names no user");
        const end = parseLockEnd(d["not_before"]);
        if (end === null) find("info", "lock-end-assumed", ref, `no readable lock end on the event; assuming the realm maximum of ${input.maxFailureWaitSeconds}s`, u);
        fold(u).locks.push({ ref, untilMs: end ?? e.timeMs + input.maxFailureWaitSeconds * 1000 });
        return;
      }
      case "UPDATE_PASSWORD": case "RESET_PASSWORD": case "UPDATE_TOTP": case "REMOVE_TOTP": case "REMOVE_FEDERATED_IDENTITY":
        return find("blocking", "unexpected-credential-event", ref, `${ev.type}: the member design has no passwords, OTP or federated links; the restore brings back the earlier state and replay does not handle it`, u);
      default:
        if (SESSION_TYPES.has(ev.type)) { counts.session++; return; }
        if (LOST_TYPES.has(ev.type)) return find("lost", "lost-change", ref, `${ev.type} after the backup is undone by the restore and not re-created`, u);
        return find("blocking", "unclassified", ref, `user event ${ev.type} is not classified; review before reopening`, u);
    }
  }

  function planAdmin(ev: KcAdminEvent, e: StoredEvent) {
    const p = ev.resourcePath ?? "";
    const op = ev.operationType;
    const ref: EventRef = { id: e.id, timeMs: e.timeMs, what: `admin ${op} ${ev.resourceType ?? ""} ${p}`.trim() };
    let m: RegExpMatchArray | null;
    if ((m = p.match(/^users\/([^/]+)$/))) {
      const u = m[1]!;
      if (op === "DELETE") { fold(u).deletes.push(ref); return; }
      if (op === "CREATE") return find("lost", "lost-change", ref, "an account created by an admin after the backup is not re-created", u);
      if (op === "UPDATE") {
        const v = ev.details?.["updated_enabled"];
        const prev = ev.details?.["previous_enabled"];
        if (v === "true" || v === "false") fold(u).enabled.push({ ref, value: v === "true" });
        if (v === undefined || v === prev) find("lost", "lost-change", ref, "an admin update to the account other than enabling or disabling is not re-applied", u);
        return;
      }
    }
    if ((m = p.match(/^users\/([^/]+)\/credentials\/([^/]+)$/)) && (op === "DELETE" || op === "ACTION")) {
      const [u, c] = [m[1]!, m[2]!];
      const list = fold(u).credentials.get(c) ?? [];
      list.push(ref);
      fold(u).credentials.set(c, list);
      return;
    }
    if (/^users\/[^/]+\/credentials\/[^/]+(\/.*)?$/.test(p)) return find("lost", "lost-change", ref, "an admin change to a credential's label or order is not re-applied");
    if ((m = p.match(/^attack-detection\/brute-force\/users\/([^/]+)$/)) && op === "DELETE") { fold(m[1]!).clears.push(ref); return; }
    if (p === "attack-detection/brute-force/users" && op === "DELETE") { clearsAll.push(ref); return; }
    if (/^users\/[^/]+\/(logout|sessions)(\/.*)?$/.test(p) || /^sessions\/[^/]+$/.test(p) || /^(logout-all|push-revocation)$/.test(p)) { counts.session++; return; }
    if ((m = p.match(/^users\/([^/]+)\/(role-mappings|groups)(\/.*)?$/))) {
      if (op === "CREATE") return find("lost", "lost-change", ref, "access granted after the backup is lost", m[1]!);
      return find("blocking", "access-removed", ref, "access removed after the backup comes back with the restore; replay does not re-remove it", m[1]!);
    }
    if ((m = p.match(/^users\/([^/]+)\/(.+)$/))) {
      return find("blocking", "unexpected-user-action", ref, `admin ${op} on users/…/${m[2]} is not handled by replay`, m[1]!);
    }
    return find("blocking", "configuration-changed", ref, `admin ${op} ${ev.resourceType ?? "?"} ${p}: realm, client or flow configuration changed after the backup; verify or re-apply it`);
  }

  const intents: Intent[] = [];
  const lastTime = (refs: readonly EventRef[]) => refs.reduce((t, r) => Math.max(t, r.timeMs), Number.NEGATIVE_INFINITY);
  for (const [u, f] of [...users.entries()].sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0))) {
    if (f.deletes.length > 0) { intents.push({ op: "delete-user", userId: u, because: f.deletes }); continue; }
    for (const [c, because] of [...f.credentials.entries()].sort(([a], [b]) => (a < b ? -1 : 1))) intents.push({ op: "delete-credential", userId: u, credentialId: c, because });
    if (f.recovery.length > 0) intents.push({ op: "drop-recovery-codes", userId: u, createdAtOrBeforeMs: input.backupFinishedAtMs, because: f.recovery });

    let finalEnabled: boolean | undefined;
    if (f.enabled.length > 0) {
      const t = Math.max(...f.enabled.map((x) => x.ref.timeMs));
      const atLast = f.enabled.filter((x) => x.ref.timeMs === t);
      // Two changes in the same millisecond cannot be ordered: the disable wins.
      finalEnabled = atLast.every((x) => x.value);
      intents.push({ op: "set-enabled", userId: u, enabled: finalEnabled, because: f.enabled.map((x) => x.ref) });
    }
    if (f.locks.length > 0) {
      const last = f.locks.reduce((a, b) => (b.ref.timeMs >= a.ref.timeMs ? b : a));
      const clearedAt = Math.max(lastTime(f.clears), lastTime(clearsAll));
      // A clear in the same millisecond as the lock cannot be ordered after it: the lock stands.
      if (clearedAt > last.ref.timeMs) {
        find("info", "lock-cleared", last.ref, "temporary lock cleared by an admin after it was imposed", u);
      } else if (last.untilMs <= input.nowMs) {
        find("info", "lock-expired", last.ref, `temporary lock ended at ${new Date(last.untilMs).toISOString()}, before this restore`, u);
      } else if (finalEnabled === false) {
        find("info", "lock-subsumed", last.ref, "temporary lock not replayed separately: the account's final state is disabled", u);
      } else {
        intents.push({ op: "temp-lock", userId: u, untilMs: last.untilMs, because: f.locks.map((x) => x.ref) });
      }
    }
  }
  return { intents, findings, counts };
}
