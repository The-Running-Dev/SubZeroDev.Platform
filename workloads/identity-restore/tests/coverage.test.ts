import { describe, expect, it } from "vitest";
import { assessCoverage } from "../src/coverage.ts";
import type { RecordWindow } from "../src/types.ts";

let seq = 0;
const w = (fromMs: number, throughMs: number, over: Partial<RecordWindow> = {}): RecordWindow => ({
  seq: ++seq, fromMs, throughMs, status: "covered", reason: null, lateCount: 0, canaryId: "c", canaryMs: throughMs + 1,
  polledAtMs: throughMs + 10, ...over,
});
const gap = (fromMs: number, throughMs: number, reason: string, over: Partial<RecordWindow> = {}) =>
  w(fromMs, throughMs, { status: "gap", reason, canaryId: null, canaryMs: null, ...over });

const chain = [gap(0, 0, "bootstrap"), w(1, 100), w(101, 200), w(201, 300)];
const ok = { windowStartMs: 50, serviceEndedAtMs: 250, storeRetiredAtMs: 400 };

describe("missing-event detection", () => {
  it("an unbroken chain from the window start to the end of service is complete", () => {
    const c = assessCoverage({ windows: chain, ...ok });
    expect(c).toMatchObject({ complete: true, gaps: [], recordedThroughMs: 300 });
  });

  it("is complete at the exact edges: start on a window's first ms, end on its last", () => {
    expect(assessCoverage({ windows: chain, windowStartMs: 1, serviceEndedAtMs: 300, storeRetiredAtMs: 400 }).complete).toBe(true);
  });

  const negatives: Array<[string, RecordWindow[], Partial<typeof ok>, string, string?]> = [
    ["a missing window inside the range", [w(1, 100), w(151, 300)], {}, "interior", "no window recorded"],
    ["a gap window inside the range", [w(1, 100), gap(101, 150, "canary-missing"), w(151, 300)], {}, "interior", "canary-missing"],
    ["a gap window overlapping covered ones", [w(1, 300), gap(120, 130, "config: eventsEnabled off")], {}, "interior", "eventsEnabled off"],
    ["the record starts after the window start", [w(60, 300)], {}, "start"],
    ["the record ends before the service did", [w(1, 200)], {}, "tail"],
    ["the service end is unknown", chain, { serviceEndedAtMs: undefined as unknown as number }, "tail", "not known"],
    ["the store-retired time is missing", chain, { storeRetiredAtMs: undefined as unknown as number }, "read-source"],
    ["the store was retired before the service ended", chain, { storeRetiredAtMs: 240 }, "read-source", "inconsistent"],
    ["an event arrived late in a relevant window", [w(1, 100), w(101, 200, { lateCount: 2 }), w(201, 300)], {}, "late-arrival"],
    ["nothing recorded at all", [], {}, "start"],
    ["a window older than the expiry is a gap", [w(1, 100), gap(101, 200, "the window starts before Keycloak's 604800s event expiry"), w(201, 300)], {}, "interior", "expiry"],
  ];
  it.each(negatives)("%s", (_name, windows, over, kind, reason) => {
    const args = { ...ok, ...over };
    for (const k of Object.keys(args) as Array<keyof typeof args>) if (args[k] === undefined) delete args[k];
    const c = assessCoverage({ windows, ...args });
    expect(c.complete).toBe(false);
    const g = c.gaps.find((x) => x.kind === kind);
    expect(g, JSON.stringify(c.gaps)).toBeDefined();
    if (reason) expect(g!.reasons.join(" ")).toContain(reason);
  });

  it("ignores windows written after the old store was retired: a recorder reading the restored database looks covered", () => {
    // The drain ends at 200; the store is retired at 215. A recorder left running against the restored
    // database writes 201–300 at 310 with a canary it can find, because the restored store still accepts
    // new logins. It must not count.
    const windows = [gap(0, 0, "bootstrap"), w(1, 100), w(101, 200, { polledAtMs: 210 }), w(201, 300, { polledAtMs: 310 })];
    const c = assessCoverage({ windows, windowStartMs: 50, serviceEndedAtMs: 250, storeRetiredAtMs: 215 });
    expect(c.complete).toBe(false);
    expect(c.gaps.map((g) => g.kind)).toContain("tail");
    // The same windows with the drain running to the end of service are complete.
    const drained = [gap(0, 0, "bootstrap"), w(1, 100), w(101, 260, { polledAtMs: 265 })];
    expect(assessCoverage({ windows: drained, windowStartMs: 50, serviceEndedAtMs: 250, storeRetiredAtMs: 270 }).complete).toBe(true);
  });

  it("does not block on a late arrival outside the replay range", () => {
    const windows = [w(1, 40, { lateCount: 3 }), w(41, 300)];
    expect(assessCoverage({ windows, ...ok }).complete).toBe(true);
  });

  it("does not block on a gap after the service ended", () => {
    const windows = [w(1, 260), gap(261, 270, "canary-missing"), w(271, 300)];
    expect(assessCoverage({ windows, ...ok }).complete).toBe(true);
  });
});
