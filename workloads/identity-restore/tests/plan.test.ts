import { describe, expect, it } from "vitest";
import { plan, type PlanInput } from "../src/plan.ts";
import type { KcAdminEvent, KcUserEvent, StoredEvent } from "../src/types.ts";
import { RECOVERY_CODES } from "../src/types.ts";

const T0 = 1_000_000_000_000;
const R = "realm-1";
let n = 0;
const user = (type: string, userId: string | undefined, timeMs: number, details: Record<string, string> = {}): StoredEvent => {
  const body: KcUserEvent = { id: `u${++n}`, time: timeMs, type, realmId: R, ...(userId ? { userId } : {}), details };
  return { kind: "user", id: body.id, timeMs, body };
};
const admin = (op: string, path: string, timeMs: number, details: Record<string, string> = {}, clientId = "admin-cli", realmId = R): StoredEvent => {
  const body: KcAdminEvent = { id: `a${++n}`, time: timeMs, realmId, authDetails: { clientId }, operationType: op, resourcePath: path, details };
  return { kind: "admin", id: body.id, timeMs, body };
};
const input = (events: StoredEvent[], over: Partial<PlanInput> = {}): PlanInput => ({
  events, realmId: R, backupStartedAtMs: T0, backupFinishedAtMs: T0 + 5000, nowMs: T0 + 3_600_000,
  maxFailureWaitSeconds: 900, restoreClientIds: ["identity-restore"], ...over,
});
const enable = (u: string, t: number, v: boolean) => admin("UPDATE", `users/${u}`, t, { previous_enabled: String(!v), updated_enabled: String(v) });

function shuffle<T>(xs: T[], seed: number): T[] {
  const a = [...xs];
  let s = seed;
  for (let i = a.length - 1; i > 0; i--) { s = (s * 1103515245 + 12345) % 2 ** 31; const j = s % (i + 1); [a[i], a[j]] = [a[j]!, a[i]!]; }
  return a;
}

describe("replay ordering", () => {
  const events = [
    user("REMOVE_CREDENTIAL", "alice", T0 + 10, { credential_type: "webauthn-passwordless", credential_id: "c1" }),
    user("LOGIN", "bob", T0 + 20, { credential_type: RECOVERY_CODES }),
    enable("carol", T0 + 30, false),
    enable("carol", T0 + 40, true),
    user("DELETE_ACCOUNT", "dave", T0 + 50),
    enable("dave", T0 + 60, false),
    user("USER_DISABLED_BY_TEMPORARY_LOCKOUT", "erin", T0 + 70, { not_before: String(T0 + 7_200_000) }),
    enable("frank", T0 + 80, true),
    enable("frank", T0 + 80, false),
    user("LOGOUT", "alice", T0 + 90),
  ];

  it("gives the same plan for every arrival order", () => {
    const want = plan(input(events));
    for (let seed = 1; seed <= 25; seed++) expect(plan(input(shuffle(events, seed)))).toEqual(want);
  });

  it("folds each user to one final state", () => {
    const p = plan(input(events));
    expect(p.intents.map((i) => `${i.op}:${i.userId}${"enabled" in i ? `=${i.enabled}` : ""}`)).toEqual([
      "delete-credential:alice", "drop-recovery-codes:bob", "set-enabled:carol=true", "delete-user:dave",
      "temp-lock:erin", "set-enabled:frank=false",
    ]);
    expect(p.counts.session).toBe(2);
  });

  it("orders enable and disable by time, not by arrival, and a same-millisecond tie disables", () => {
    const later = plan(input([enable("x", T0 + 2, true), enable("x", T0 + 1, false)]));
    expect(later.intents).toMatchObject([{ op: "set-enabled", enabled: true }]);
    const tie = plan(input([enable("x", T0 + 1, true), enable("x", T0 + 1, false)]));
    expect(tie.intents).toMatchObject([{ op: "set-enabled", enabled: false }]);
  });

  it("drops a lock an admin cleared later, keeps one cleared in the same millisecond, and skips an expired one", () => {
    const lock = (t: number, until: number) => user("USER_DISABLED_BY_TEMPORARY_LOCKOUT", "x", t, { not_before: String(until) });
    const clear = (t: number) => admin("DELETE", "attack-detection/brute-force/users/x", t);
    expect(plan(input([lock(T0 + 1, T0 + 9e6), clear(T0 + 2)])).intents).toEqual([]);
    expect(plan(input([lock(T0 + 1, T0 + 9e6), admin("DELETE", "attack-detection/brute-force/users", T0 + 2)])).intents).toEqual([]);
    expect(plan(input([lock(T0 + 1, T0 + 9e6), clear(T0 + 1)])).intents).toMatchObject([{ op: "temp-lock", untilMs: T0 + 9e6 }]);
    expect(plan(input([clear(T0 + 1), lock(T0 + 2, T0 + 9e6)])).intents).toMatchObject([{ op: "temp-lock" }]);
    const expired = plan(input([lock(T0 + 1, T0 + 100)]));
    expect(expired.intents).toEqual([]);
    expect(expired.findings.map((f) => f.code)).toEqual(["lock-expired"]);
  });

  it("reads Keycloak's zone-less lock end as UTC and falls back to the realm maximum", () => {
    const iso = new Date(T0 + 7_200_000).toISOString().replace(/\.\d+Z$/, "");
    expect(plan(input([user("USER_DISABLED_BY_TEMPORARY_LOCKOUT", "x", T0, { not_before: iso })])).intents)
      .toMatchObject([{ op: "temp-lock", untilMs: T0 + 7_200_000 }]);
    const fallback = plan(input([user("USER_DISABLED_BY_TEMPORARY_LOCKOUT", "x", T0 + 1000)], { nowMs: T0 }));
    expect(fallback.intents).toMatchObject([{ op: "temp-lock", untilMs: T0 + 1000 + 900_000 }]);
    expect(fallback.findings.map((f) => f.code)).toEqual(["lock-end-assumed"]);
  });

  it("a final disable subsumes a lock", () => {
    const p = plan(input([user("USER_DISABLED_BY_TEMPORARY_LOCKOUT", "x", T0 + 1, { not_before: String(T0 + 9e6) }), enable("x", T0 + 2, false)]));
    expect(p.intents.map((i) => i.op)).toEqual(["set-enabled"]);
    expect(p.findings.map((f) => f.code)).toEqual(["lock-subsumed"]);
  });

  it("a deletion wins over every other change to the same user", () => {
    const p = plan(input([user("DELETE_ACCOUNT", "x", T0 + 5), user("REMOVE_CREDENTIAL", "x", T0 + 1, { credential_id: "c" }), enable("x", T0 + 9, true)]));
    expect(p.intents).toMatchObject([{ op: "delete-user", userId: "x" }]);
  });

  it("drops recovery codes up to the end of the dump, so a set made after it is never touched", () => {
    const p = plan(input([user("UPDATE_CREDENTIAL", "x", T0 + 1, { credential_type: RECOVERY_CODES })]));
    expect(p.intents).toEqual([expect.objectContaining({ op: "drop-recovery-codes", createdAtOrBeforeMs: T0 + 5000 })]);
  });
});

describe("what the plan refuses to guess (negative cases)", () => {
  const cases: Array<[string, StoredEvent, "blocking" | "lost", string]> = [
    ["removal with no credential id", user("REMOVE_CREDENTIAL", "x", T0, {}), "blocking", "unidentified-removal"],
    ["deletion with no user", user("DELETE_ACCOUNT", undefined, T0), "blocking", "missing-user"],
    ["recovery-code use with no user", user("LOGIN", undefined, T0, { credential_type: RECOVERY_CODES }), "blocking", "missing-user"],
    ["password change", user("UPDATE_PASSWORD", "x", T0), "blocking", "unexpected-credential-event"],
    ["unknown event type", user("SOMETHING_NEW", "x", T0), "blocking", "unclassified"],
    ["role removed", admin("DELETE", "users/x/role-mappings/realm", T0), "blocking", "access-removed"],
    ["group left", admin("DELETE", "users/x/groups/g1", T0), "blocking", "access-removed"],
    ["realm config", admin("UPDATE", "", T0), "blocking", "configuration-changed"],
    ["flow config", admin("UPDATE", "authentication/executions/e1", T0), "blocking", "configuration-changed"],
    ["unknown user sub-resource", admin("ACTION", "users/x/impersonation", T0), "blocking", "unexpected-user-action"],
    ["registration", user("REGISTER", "x", T0), "lost", "lost-change"],
    ["passkey added", user("UPDATE_CREDENTIAL", "x", T0, { credential_type: "webauthn-passwordless" }), "lost", "lost-change"],
    ["admin-created account", admin("CREATE", "users/x", T0), "lost", "lost-change"],
    ["profile edit by admin", admin("UPDATE", "users/x", T0, {}), "lost", "lost-change"],
    ["role granted", admin("CREATE", "users/x/role-mappings/realm", T0), "lost", "lost-change"],
  ];
  it.each(cases)("%s", (_name, ev, severity, code) => {
    const p = plan(input([ev]));
    expect(p.findings).toEqual([expect.objectContaining({ severity, code })]);
    if (severity === "blocking") expect(p.intents).toEqual([]);
  });

  it("ignores another realm and the restore's own writes, and counts them", () => {
    const p = plan(input([admin("DELETE", "users/x", T0, {}, "admin-cli", "other-realm"), admin("DELETE", "users/y", T0, {}, "identity-restore")]));
    expect(p.intents).toEqual([]);
    expect(p.counts).toMatchObject({ otherRealm: 1, restoreOwn: 1, considered: 0 });
  });

  it("a failed attempt is information only", () => {
    const p = plan(input([user("LOGIN_ERROR", "x", T0)]));
    expect(p.intents).toEqual([]);
    expect(p.findings).toEqual([expect.objectContaining({ severity: "info", code: "error-event" })]);
  });
});
