/**
 * End-to-end over the fake realm: members act after a backup, the recorder copies the events off-host, the
 * database is rolled back to the backup, and the restore replays the record onto it.
 */
import { describe, expect, it } from "vitest";
import { restore, type RestoreInput } from "../src/apply.ts";
import { DEFAULT_RECORDER, runCycle, type RecorderOptions } from "../src/recorder.ts";
import { releaseTempLocks } from "../src/release.ts";
import type { BackupMeta } from "../src/types.ts";
import { Clock, clientUuid, FakeKeycloak, MemoryStore, OPERATOR, PASSKEY, REALM, RECORDER, RECOVERY_CODES, UNSAFE_FLOW } from "./fakes.ts";

const HOUR = 3_600_000;

function world() {
  const clock = new Clock();
  const kc = new FakeKeycloak(clock);
  const store = new MemoryStore(clock);
  const recorder = kc.client(RECORDER, REALM);
  const operator = kc.client(OPERATOR, "master");
  const o: RecorderOptions = { ...DEFAULT_RECORDER, realm: REALM, recorderName: "rec-test", now: clock.now, sleep: clock.sleep };
  const cycle = async (advanceMs = 30_000) => { clock.t += advanceMs; return runCycle(recorder, store, o); };
  return { clock, kc, store, recorder, operator, o, cycle };
}

type World = ReturnType<typeof world>;

interface Scenario extends World {
  backup: BackupMeta;
  serviceEndedAtMs: number;
  storeRetiredAtMs: number;
  ids: Record<"alice" | "bob" | "carol" | "dave" | "erin" | "frank" | "gwen", string>;
  erinUntilMs: number;
}

/**
 * Seven members. After the backup: alice removes her passkey (keeps recovery codes), bob signs in with a
 * recovery code, carol removes her only credential, dave deletes his account, erin is temporarily locked
 * out, an admin disables frank, and gwen registers.
 */
async function scenario(opts: { gapDuring?: boolean; beforeDump?: (w: World) => void; skipDrain?: boolean } = {}): Promise<Scenario> {
  const w = world();
  const { kc, clock } = w;
  const alice = kc.addUser("alice", [{ type: PASSKEY }, { type: RECOVERY_CODES }]);
  const bob = kc.addUser("bob", [{ type: PASSKEY }, { type: RECOVERY_CODES }]);
  const carol = kc.addUser("carol", [{ type: PASSKEY }]);
  const dave = kc.addUser("dave", [{ type: PASSKEY }]);
  const erin = kc.addUser("erin", [{ type: PASSKEY }]);
  const frank = kc.addUser("frank", [{ type: PASSKEY }]);
  await w.cycle(); // bootstrap
  for (let i = 0; i < 4; i++) await w.cycle();

  opts.beforeDump?.(w);
  const startedAtMs = clock.t;
  const dump = kc.snapshot();
  clock.t += 2000;
  const backup: BackupMeta = { startedAtMs, finishedAtMs: clock.t, sha256: "test", bytes: 1 };

  clock.t += 60_000;
  kc.removeCredential(alice, PASSKEY); clock.t += 1000;
  kc.useRecoveryCode(bob); clock.t += 1000;
  if (opts.gapDuring) { await w.cycle(); kc.storeEvents = false; }
  kc.removeCredential(carol, PASSKEY); clock.t += 1000;
  if (opts.gapDuring) { await w.cycle(); kc.storeEvents = true; }
  kc.deleteAccount(dave); clock.t += 1000;
  const erinUntilMs = clock.t + 2 * HOUR;
  kc.tempLock(erin, erinUntilMs); clock.t += 1000;
  kc.adminSetEnabled(frank, false); clock.t += 1000;
  const gwen = kc.addUser("gwen", [{ type: PASSKEY }]);
  kc.userEvent("REGISTER", gwen.id); clock.t += 1000;
  for (let i = 0; i < 3; i++) await w.cycle();

  // Runbook: close ingress, wait out the settle delay, drain, stop the recorder, retire the old store.
  const serviceEndedAtMs = clock.t;
  if (!opts.skipDrain) {
    clock.t = serviceEndedAtMs + w.o.settleMs + 1;
    expect(await runCycle(w.recorder, w.store, w.o)).toMatchObject({ wrote: true, window: { status: "covered" } });
  }
  clock.t += 1000;
  const storeRetiredAtMs = clock.t;
  clock.t += 1000;
  kc.restoreFrom(dump);
  clock.t += 1000;
  return { ...w, backup, serviceEndedAtMs, storeRetiredAtMs, erinUntilMs,
    ids: { alice: alice.id, bob: bob.id, carol: carol.id, dave: dave.id, erin: erin.id, frank: frank.id, gwen: gwen.id } };
}

const input = (s: Scenario, over: Partial<RestoreInput> = {}): RestoreInput => ({
  kc: s.operator, realm: REALM, store: s.store, restoreId: "restore-1", backup: s.backup, marginMs: 60_000,
  serviceEndedAtMs: s.serviceEndedAtMs, storeRetiredAtMs: s.storeRetiredAtMs, memberClients: ["members-web"],
  restoreClientIds: [OPERATOR, clientUuid(OPERATOR)], reopen: true, allowReplayLockouts: false, now: s.clock.now, ...over,
});

const creds = (s: Scenario, id: string) => s.kc.users.get(id)?.credentials.map((c) => c.type).sort();
const userWrites = (s: Scenario) => s.kc.writes.filter((x) => x.includes("/users/")).length;

describe("rollback replay", () => {
  it("re-applies every recorded removal, resets sessions, and holds on the member it locks out", async () => {
    const s = await scenario();
    // The rollback undid all of it.
    expect(creds(s, s.ids.alice)).toEqual([RECOVERY_CODES, PASSKEY].sort());
    expect(s.kc.users.has(s.ids.dave)).toBe(true);

    const r = await restore(input(s));
    expect(r.coverage?.complete).toBe(true);
    expect(r.outcome).toBe("held");
    expect(r.blocking).toEqual([expect.stringContaining("no passkey and no recovery codes")]);
    expect(r.lockouts?.replayAffected.map((x) => x.username)).toEqual(["carol"]);
    expect(r.lockouts?.preExisting).toEqual([]);

    expect(creds(s, s.ids.alice)).toEqual([RECOVERY_CODES]);
    expect(creds(s, s.ids.bob)).toEqual([PASSKEY]);
    expect(creds(s, s.ids.carol)).toEqual([]);
    expect(s.kc.users.has(s.ids.dave)).toBe(false);
    expect(s.kc.users.get(s.ids.erin)?.enabled).toBe(false);
    expect(s.kc.users.get(s.ids.frank)?.enabled).toBe(false);
    expect(r.tempLocks).toEqual([{ userId: s.ids.erin, untilMs: Math.floor(s.erinUntilMs / 1000) * 1000, releaseTo: true }]);

    // Lost, not replayed: gwen's registration. Reported, not blocking.
    expect(s.kc.users.has(s.ids.gwen)).toBe(false);
    expect(r.plan?.findings).toContainEqual(expect.objectContaining({ code: "lost-change", severity: "lost", userId: s.ids.gwen }));

    // Session rollback: one realm-wide reset, held realm.
    expect(s.kc.logoutAll).toBe(1);
    expect(s.kc.realm.notBefore).toBe(r.sessionReset!.notBefore);
    expect(s.kc.realm.enabled).toBe(false);
    expect(r.verify?.ok).toBe(true);
  });

  it("is a no-op the second time, keeps the first run's release target, and reopens once the owner allows the lockout", async () => {
    const s = await scenario();
    await restore(input(s));
    const before = userWrites(s);
    s.clock.t += 60_000;
    const r = await restore(input(s, { allowReplayLockouts: true }));
    expect(userWrites(s)).toBe(before);
    expect(new Set(r.applied!.map((x) => x.result))).toEqual(new Set(["no-op", "absent"]));
    expect(r.outcome).toBe("reopened");
    expect(r.blocking).toEqual([]);
    // erin is disabled by the first run now; her release target is still the state before the lock.
    expect(r.tempLocks).toEqual([expect.objectContaining({ userId: s.ids.erin, releaseTo: true })]);
    expect(s.kc.realm.enabled).toBe(true);
    expect(s.kc.logoutAll).toBe(2);
  });

  it("refuses to run again after it reopened, and writes nothing", async () => {
    const s = await scenario();
    await restore(input(s, { allowReplayLockouts: true }));
    const writes = s.kc.writes.length;
    const r = await restore(input(s, { allowReplayLockouts: true }));
    expect(r).toMatchObject({ outcome: "refused", refusedBecause: expect.stringContaining("already reopened") });
    expect(s.kc.writes.length).toBe(writes);
  });

  it("refuses a replay window that reaches back over an earlier restore's reopen", async () => {
    const s = await scenario();
    await s.store.appendRestoreLog({ restoreId: "restore-0", phase: "reopened", atMs: s.backup.startedAtMs - 1000, detail: {} });
    const r = await restore(input(s));
    expect(r).toMatchObject({ outcome: "refused", refusedBecause: expect.stringContaining("restore-0") });
    expect(s.kc.writes).toEqual([]);
  });

  it("does not refuse for an earlier restore that reopened before the window", async () => {
    const s = await scenario();
    await s.store.appendRestoreLog({ restoreId: "restore-0", phase: "reopened", atMs: s.backup.startedAtMs - 61_000, detail: {} });
    expect((await restore(input(s, { allowReplayLockouts: true }))).outcome).toBe("reopened");
  });
});

describe("temporary locks", () => {
  it("lifts the stand-in disable on a rerun after the lock ended between runs", async () => {
    const s = await scenario();
    await restore(input(s));
    expect(s.kc.users.get(s.ids.erin)?.enabled).toBe(false);
    s.clock.t = s.erinUntilMs + 1000;
    const r = await restore(input(s, { allowReplayLockouts: true }));
    expect(s.kc.users.get(s.ids.erin)?.enabled).toBe(true);
    expect(r.applied).toContainEqual(expect.objectContaining({ op: "temp-lock", userId: s.ids.erin, result: "applied" }));
    expect(r.tempLocks).toEqual([]);
    expect(r.outcome).toBe("reopened");
  });

  it("releases after the reopen only once the lock has ended", async () => {
    const s = await scenario();
    await restore(input(s, { allowReplayLockouts: true }));
    const ids = await s.operator.identities();
    const rel = () => releaseTempLocks({ kc: s.operator, realm: REALM, store: s.store, restoreClientIds: ids, now: s.clock.now });
    expect(await rel()).toEqual([expect.objectContaining({ userId: s.ids.erin, result: "pending" })]);
    expect(s.kc.users.get(s.ids.erin)?.enabled).toBe(false);
    s.clock.t = s.erinUntilMs + 1;
    expect(await rel()).toEqual([expect.objectContaining({ userId: s.ids.erin, result: "released" })]);
    expect(s.kc.users.get(s.ids.erin)?.enabled).toBe(true);
    expect(await rel()).toEqual([expect.objectContaining({ result: "already-enabled" })]);
    expect(s.store.log.filter((e) => e.phase === "released")).toHaveLength(1);
  });

  it("leaves disabled a member an admin disabled after the reopen", async () => {
    const s = await scenario();
    await restore(input(s, { allowReplayLockouts: true }));
    s.clock.t += 1000;
    s.kc.adminSetEnabled(s.kc.users.get(s.ids.erin)!, false);
    s.clock.t = s.erinUntilMs + 1;
    const out = await releaseTempLocks({ kc: s.operator, realm: REALM, store: s.store, restoreClientIds: await s.operator.identities(), now: s.clock.now });
    expect(out).toEqual([expect.objectContaining({ result: "skipped", detail: expect.stringContaining("admin-cli") })]);
    expect(s.kc.users.get(s.ids.erin)?.enabled).toBe(false);
  });
});

describe("release after an admin's change that leaves no enabled detail (G1)", () => {
  const released = async (change: (s: Scenario) => void) => {
    const s = await scenario();
    await restore(input(s, { allowReplayLockouts: true }));
    s.clock.t += 1000;
    change(s);
    s.clock.t = s.erinUntilMs + 1;
    const out = await releaseTempLocks({ kc: s.operator, realm: REALM, store: s.store, restoreClientIds: await s.operator.identities(), now: s.clock.now });
    return { s, out };
  };

  it("keeps disabled a member an admin disabled while the stand-in already held them disabled", async () => {
    // Keycloak writes no previous_enabled / updated_enabled for a no-op; only the representation says so.
    const { s, out } = await released((s) => s.kc.adminSetEnabled(s.kc.users.get(s.ids.erin)!, false));
    expect(s.kc.adminEvents.at(-1)?.details).toEqual({});
    expect(out).toEqual([expect.objectContaining({ userId: s.ids.erin, result: "skipped" })]);
    expect(s.kc.users.get(s.ids.erin)?.enabled).toBe(false);
  });

  it("keeps disabled a member whose account an admin updated in a way that cannot be read", async () => {
    const { s, out } = await released((s) => s.kc.adminEvent("UPDATE", `users/${s.ids.erin}`, {}));
    expect(out).toEqual([expect.objectContaining({ userId: s.ids.erin, result: "skipped" })]);
    expect(s.kc.users.get(s.ids.erin)?.enabled).toBe(false);
  });

  it("still releases a member after an admin update that did not touch enabled", async () => {
    const { s, out } = await released((s) => s.kc.adminEvent("UPDATE", `users/${s.ids.erin}`, {}, "admin-cli", s.clock.t, { firstName: "Erin" }));
    expect(out).toEqual([expect.objectContaining({ userId: s.ids.erin, result: "released" })]);
    expect(s.kc.users.get(s.ids.erin)?.enabled).toBe(true);
  });
});

describe("the restore's own identity", () => {
  it("names the restore's client both ways admin events can: clientId and internal id", async () => {
    const w = world();
    expect(await w.kc.client(OPERATOR, "master").identities()).toEqual([OPERATOR, clientUuid(OPERATOR)]);
    expect(w.kc.adminSetEnabled(w.kc.addUser("x", []), false, OPERATOR).authDetails?.clientId).toBe(clientUuid(OPERATOR));
  });
});

describe("release outcomes (negative cases)", () => {
  it("covers each marker outcome", async () => {
    const w = world();
    const mk = (name: string, enabled: boolean) => w.kc.addUser(name, [{ type: PASSKEY }], enabled);
    const [pending, kept, gone, already, byAdmin, perm, own, plain] =
      [mk("p", false), mk("k", false), mk("g", false), mk("a", true), mk("b", false), mk("m", false), mk("o", false), mk("r", false)];
    w.kc.users.delete(gone.id);
    const reopenAt = w.clock.t;
    const until = reopenAt + 1000;
    const m = (u: { id: string }, releaseTo = true, untilMs = until) => ({ userId: u.id, untilMs, releaseTo });
    await w.store.appendRestoreLog({ restoreId: "r", phase: "reopened", atMs: reopenAt, detail: {
      tempLocks: [m(pending, true, reopenAt + HOUR), m(kept, false), m(gone), m(already), m(byAdmin), m(perm), m(own), m(plain)] } });
    w.clock.t += 10;
    w.kc.adminSetEnabled(byAdmin, false);
    w.kc.userEvent("USER_DISABLED_BY_PERMANENT_LOCKOUT", perm.id);
    w.kc.adminSetEnabled(own, false, OPERATOR);
    w.clock.t = until + 1;
    const op = w.kc.client(OPERATOR, "master");
    const out = await releaseTempLocks({ kc: op, realm: REALM, store: w.store, restoreClientIds: await op.identities(), now: w.clock.now });
    const by = Object.fromEntries(out.map((x) => [x.userId, x.result]));
    expect(by).toEqual({
      [pending.id]: "pending", [kept.id]: "kept-disabled", [gone.id]: "absent", [already.id]: "already-enabled",
      [byAdmin.id]: "skipped", [perm.id]: "skipped", [own.id]: "released", [plain.id]: "released",
    });
    expect(w.kc.users.get(kept.id)?.enabled).toBe(false);
    expect(w.kc.users.get(byAdmin.id)?.enabled).toBe(false);
    expect(w.kc.users.get(perm.id)?.enabled).toBe(false);
    expect(w.kc.users.get(plain.id)?.enabled).toBe(true);
  });
});

describe("missing events fail closed (negative cases)", () => {
  it("an unrecorded removal is a gap: the realm stays held and nothing reopens", async () => {
    const s = await scenario({ gapDuring: true });
    const r = await restore(input(s, { allowReplayLockouts: true }));
    expect(r.outcome).toBe("held");
    expect(r.coverage?.complete).toBe(false);
    expect(r.blocking).toContainEqual(expect.stringMatching(/record gap \(interior\).*canary/));
    expect(s.kc.realm.enabled).toBe(false);
    // carol's removal was never recorded, so the replay cannot know to remove her passkey again.
    expect(creds(s, s.ids.carol)).toEqual([PASSKEY]);
  });

  it("blocks when the end of service is not given", async () => {
    const s = await scenario();
    const { serviceEndedAtMs: _x, ...rest } = input(s, { allowReplayLockouts: true });
    const r = await restore(rest);
    expect(r.outcome).toBe("held");
    expect(r.blocking).toContainEqual(expect.stringContaining("record gap (tail)"));
  });

  it("blocks when the store-retired time is not given", async () => {
    const s = await scenario();
    const { storeRetiredAtMs: _x, ...rest } = input(s, { allowReplayLockouts: true });
    const r = await restore(rest);
    expect(r.outcome).toBe("held");
    expect(r.blocking).toContainEqual(expect.stringContaining("record gap (read-source)"));
  });

  it("does not count a recorder left running against the restored database", async () => {
    const s = await scenario({ skipDrain: true });
    // The restored realm accepts the recorder's token, so its canary is found; the window must still not count.
    s.clock.t = s.serviceEndedAtMs + 60_000;
    expect(await runCycle(s.recorder, s.store, s.o)).toMatchObject({ wrote: true, window: { status: "covered" } });
    const r = await restore(input(s, { allowReplayLockouts: true }));
    expect(r.outcome).toBe("held");
    expect(r.blocking).toContainEqual(expect.stringContaining("record gap (tail)"));
  });

  it("blocks on a restored realm whose member client is bound to the unsafe recovery flow", async () => {
    const s = await scenario({ beforeDump: (w) => { w.kc.clients[0]!.authenticationFlowBindingOverrides.browser = "flow-browser"; } });
    const r = await restore(input(s, { allowReplayLockouts: true }));
    expect(r.fixA?.ok).toBe(false);
    expect(r.outcome).toBe("held");
    expect(r.blocking).toContainEqual(expect.stringContaining("fixA"));
    expect(UNSAFE_FLOW.length).toBeGreaterThan(0);
  });

  it("blocks when the realm's own browser flow is unsafe, even if every member client overrides it", async () => {
    const s = await scenario({ beforeDump: (w) => { w.kc.realm.browserFlow = "browser"; } });
    const r = await restore(input(s, { allowReplayLockouts: true }));
    expect(r.outcome).toBe("held");
    expect(r.fixA?.flows.find((f) => f.boundTo === "realm browser flow")?.problems.length).toBeGreaterThan(0);
  });

  it("blocks when a named member client is missing from the restored realm", async () => {
    const s = await scenario();
    const r = await restore(input(s, { allowReplayLockouts: true, memberClients: ["members-web", "members-app"] }));
    expect(r.outcome).toBe("held");
    expect(r.blocking).toContainEqual(expect.stringContaining("does not exist"));
  });
});
