import { describe, expect, it } from "vitest";
import { configViolations, DEFAULT_RECORDER, fetchRange, runCycle, type RecorderOptions } from "../src/recorder.ts";
import { Clock, FakeKeycloak, MemoryStore, PASSKEY, REALM, RECORDER } from "./fakes.ts";

function world(over: Partial<RecorderOptions> = {}) {
  const clock = new Clock();
  const kc = new FakeKeycloak(clock);
  const store = new MemoryStore(clock);
  const recorder = kc.client(RECORDER, REALM);
  const o: RecorderOptions = { ...DEFAULT_RECORDER, realm: REALM, recorderName: "rec-test", now: clock.now, sleep: clock.sleep, ...over };
  const cycle = async (advanceMs = 30_000) => { clock.t += advanceMs; return runCycle(recorder, store, o); };
  return { clock, kc, store, recorder, o, cycle };
}

describe("the recorder", () => {
  it("starts with a gap window at its first instant, then tiles time with covered windows", async () => {
    const w = world();
    const first = await w.cycle();
    expect(first).toMatchObject({ wrote: true, window: { status: "gap", reason: expect.stringContaining("bootstrap") } });
    const u = w.kc.addUser("alice", [{ type: PASSKEY }]);
    w.kc.removeCredential(u, PASSKEY);
    await w.cycle();
    await w.cycle();
    const ws = await w.store.windows();
    expect(ws.map((x) => x.status)).toEqual(["gap", "covered", "covered"]);
    for (let i = 1; i < ws.length; i++) expect(ws[i]!.fromMs).toBe(ws[i - 1]!.throughMs + 1);
    for (const x of ws.slice(1)) expect(x.canaryMs!).toBeGreaterThan(x.throughMs);
    expect([...w.store.events.values()].some((e) => e.body.realmId && e.kind === "user" && (e.body as { type: string }).type === "REMOVE_CREDENTIAL")).toBe(true);
  });

  it("writes nothing when its token is refused, and the next cycle starts where the last window ended", async () => {
    const w = world();
    await w.cycle();
    await w.cycle();
    const before = (await w.store.windows()).length;
    w.kc.refuseTokens = true;
    expect(await w.cycle()).toMatchObject({ wrote: false, why: expect.stringContaining("token refused") });
    expect(await w.cycle()).toMatchObject({ wrote: false });
    expect((await w.store.windows()).length).toBe(before);
    w.kc.refuseTokens = false;
    const prev = (await w.store.lastWindow())!;
    const next = await w.cycle();
    expect(next).toMatchObject({ wrote: true, window: { fromMs: prev.throughMs + 1, status: "covered" } });
  });

  it("writes nothing while the settle delay has not passed", async () => {
    const w = world();
    await w.cycle();
    expect(await w.cycle(0)).toMatchObject({ wrote: false, why: expect.stringContaining("settle") });
  });

  it("marks a window a gap when its canary is not in Keycloak's store", async () => {
    const w = world();
    await w.cycle();
    w.kc.storeEvents = false;
    const r = await w.cycle();
    expect(r).toMatchObject({ wrote: true, window: { status: "gap", canaryId: null, reason: expect.stringContaining("canary") } });
  });

  it("marks a window a gap when it starts before Keycloak's event expiry", async () => {
    const w = world();
    await w.cycle();
    const r = await w.cycle(31 * 86_400_000);
    expect(r).toMatchObject({ wrote: true, window: { status: "gap", reason: expect.stringContaining("expiry") } });
  });

  it("marks a window a gap when the event configuration is unsafe", async () => {
    const w = world();
    await w.cycle();
    w.kc.eventsConfig["adminEventsDetailsEnabled"] = false;
    expect(await w.cycle()).toMatchObject({ wrote: true, window: { status: "gap", reason: expect.stringContaining("adminEventsDetailsEnabled") } });
  });

  it("counts an event that arrives late in a covered window, but not one from before the record began", async () => {
    const w = world();
    const u = w.kc.addUser("alice", [{ type: PASSKEY }]);
    w.kc.userEvent("LOGIN", u.id, {}, w.clock.t - 1000); // before the bootstrap instant once it lands
    await w.cycle();
    expect(await w.cycle()).toMatchObject({ wrote: true, window: { lateCount: 0 } });
    const covered = (await w.store.lastWindow())!;
    expect(covered.status).toBe("covered");
    // Committed after the recorder read its window, with a time inside it.
    w.kc.userEvent("REMOVE_CREDENTIAL", u.id, { credential_id: "c", credential_type: PASSKEY }, covered.throughMs - 5);
    const r = await w.cycle();
    expect(r).toMatchObject({ wrote: true, window: { lateCount: 1 } });
    const again = await w.cycle();
    expect(again).toMatchObject({ wrote: true, window: { lateCount: 0 } });
  });
});

describe("configuration the record refuses (negative cases)", () => {
  const good = {
    eventsEnabled: true, adminEventsEnabled: true, adminEventsDetailsEnabled: true, eventsExpiration: null as number | null, adminEventsExpiration: 30 * 86400,
    enabledEventTypes: new FakeKeycloak(new Clock()).eventsConfig["enabledEventTypes"] as string[],
  };
  it("accepts a safe configuration, including no expiry and an unreported admin expiry", () => {
    expect(configViolations(good, 7 * 86400)).toEqual([]);
    const { adminEventsExpiration: _a, ...noAdminExpiry } = good;
    expect(configViolations(noAdminExpiry, 7 * 86400)).toEqual([]);
  });
  const bad: Array<[string, Partial<typeof good> & Record<string, unknown>, string]> = [
    ["user events off", { eventsEnabled: false }, "eventsEnabled"],
    ["admin events off", { adminEventsEnabled: false }, "adminEventsEnabled"],
    ["admin details off", { adminEventsDetailsEnabled: false }, "adminEventsDetailsEnabled"],
    ["all types (empty list)", { enabledEventTypes: [] }, "listed explicitly"],
    ["a required type missing", { enabledEventTypes: good.enabledEventTypes.filter((t) => t !== "REMOVE_CREDENTIAL") }, "REMOVE_CREDENTIAL"],
    ["the canary type missing", { enabledEventTypes: good.enabledEventTypes.filter((t) => t !== "CLIENT_LOGIN") }, "CLIENT_LOGIN"],
    ["short user expiry", { eventsExpiration: 3600 }, "eventsExpiration"],
    ["short admin expiry", { adminEventsExpiration: 3600 }, "adminEventsExpiration"],
  ];
  it.each(bad)("%s", (_n, over, text) => {
    const v = configViolations({ ...good, ...over }, 7 * 86400);
    expect(v.length).toBe(1);
    expect(v[0]).toContain(text);
  });
});

describe("reading a range", () => {
  it("splits full pages and widens a page when one millisecond holds more than a page", async () => {
    const w = world();
    await w.cycle(); // obtains nothing; makes a token for the reads below
    const t = w.clock.t - 100_000;
    for (let i = 0; i < 12; i++) w.kc.userEvent("LOGIN", "u", {}, t + 7);
    for (let i = 0; i < 11; i++) w.kc.userEvent("LOGIN", "u", {}, t + i * 3);
    const all = await fetchRange(w.recorder, REALM, "user", t, t + 40, { pageMax: 5, pageCap: 50 });
    expect(new Set(all.map((e) => e.id)).size).toBe(23);
    expect(all.length).toBe(23);
    await expect(fetchRange(w.recorder, REALM, "user", t, t + 40, { pageMax: 5, pageCap: 10 })).rejects.toThrow(/raise the page cap/);
  });
});
