/**
 * S5.1–S5.8 — what Dispatch answers when the durable store's two new conditions reach it.
 *
 * One file, because the criteria are stated as distinctions: a conflict is asserted against a
 * `storage_failure` in the same suite (S5.2) and against a rejected action's `200` (S5.8), and a
 * distinction asserted in two files is one an edit to either can quietly erase.
 */
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";
import { loadPublishedContract } from "@subzerodev/service-contract";

import { compose } from "../src/compose.js";
import type { LifecycleProbe, LifecycleState, Outcome, StoreError, WorkloadConfiguration } from "../src/types.js";
import { err, ok } from "../src/types.js";
import { bodyJson, post, surfaceOver, throwingStore } from "./support/harness.js";
import { CAMPAIGN_ID, realStore } from "./support/real-workload.js";
import { conflictError, controlledPersistence, sessionLayerOver } from "./support/persistence-stub.js";

/** A probe that answers `state` for every id and records what it was asked about, so "Dispatch
 *  consulted the probe for *this* id" is asserted rather than inferred from the code alone. */
function probeAnswering(state: LifecycleState): LifecycleProbe & { asked: string[] } {
  const asked: string[] = [];
  return {
    asked,
    async session(sessionId: string): Promise<Outcome<LifecycleState, StoreError>> {
      asked.push(sessionId);
      return ok(state);
    },
    async save(saveId: string): Promise<Outcome<LifecycleState, StoreError>> {
      asked.push(saveId);
      return ok(state);
    },
  };
}

const failingProbe: LifecycleProbe = {
  async session(): Promise<Outcome<LifecycleState, StoreError>> {
    return err({ code: "Unreachable" });
  },
  async save(): Promise<Outcome<LifecycleState, StoreError>> {
    return err({ code: "Unreachable" });
  },
};

const throwingProbe: LifecycleProbe = {
  session(): Promise<Outcome<LifecycleState, StoreError>> {
    throw new Error("the probe's own connection is gone");
  },
  save(): Promise<Outcome<LifecycleState, StoreError>> {
    throw new Error("the probe's own connection is gone");
  },
};

/** A session created through the layer under test, so the action submitted against it in the next
 *  step is a legal one and the only thing failing is the write. */
async function sessionOver(store: Awaited<ReturnType<typeof realStore>>): Promise<string> {
  const created = await post(surfaceOver(store), "/v1/create-session", { campaignId: CAMPAIGN_ID });
  expect(created.status).toBe(200);
  return (bodyJson(created) as unknown as { sessionId: string }).sessionId;
}

describe("S5.1, S5.2 — a conflict and an outage are told apart", () => {
  it("a `sessions.put` that throws the conflict brand becomes concurrent_modification at 409", async () => {
    const controlled = controlledPersistence();
    const store = sessionLayerOver(controlled.persistence);
    const sessionId = await sessionOver(store);

    controlled.failSessionPut(conflictError);
    const response = await post(surfaceOver(store), "/v1/submit-action", {
      sessionId,
      actionId: "advance_ticks",
      params: { ticks: 1 },
    });

    expect(response.status).toBe(409);
    expect(bodyJson(response)["code"]).toBe("concurrent_modification");
  });

  it("a `sessions.put` that throws an ordinary error becomes storage_failure at 503", async () => {
    const controlled = controlledPersistence();
    const store = sessionLayerOver(controlled.persistence);
    const sessionId = await sessionOver(store);

    controlled.failSessionPut(() => new Error("the connection dropped mid-statement"));
    const response = await post(surfaceOver(store), "/v1/submit-action", {
      sessionId,
      actionId: "advance_ticks",
      params: { ticks: 1 },
    });

    expect(response.status).toBe(503);
    expect(bodyJson(response)["code"]).toBe("storage_failure");
  });
});

describe("S5.3, S5.4 — expiry is classified only on the engine's own unknown_* codes", () => {
  const cases = [
    {
      code: "unknown_session",
      expired: "session_expired",
      method: "submitAction",
      operation: "submit-action",
      args: { sessionId: "s-1", actionId: "advance_ticks" },
      id: "s-1",
    },
    {
      code: "unknown_save",
      expired: "save_expired",
      method: "loadGame",
      operation: "load-game",
      args: { saveId: "v-1" },
      id: "v-1",
    },
  ] as const;

  for (const testCase of cases) {
    it(`${testCase.code} with the probe reporting expired becomes ${testCase.expired} at 404`, async () => {
      const { store } = throwingStore(testCase.method, testCase.code);
      const probe = probeAnswering("expired");
      const response = await post(surfaceOver(store, undefined, probe), `/v1/${testCase.operation}`, testCase.args);

      expect(response.status).toBe(404);
      expect(bodyJson(response)["code"]).toBe(testCase.expired);
      expect(probe.asked).toEqual([testCase.id]);
    });

    it(`${testCase.code} with the probe reporting absent passes through verbatim at 404`, async () => {
      const { store } = throwingStore(testCase.method, testCase.code);
      const probe = probeAnswering("absent");
      const response = await post(surfaceOver(store, undefined, probe), `/v1/${testCase.operation}`, testCase.args);

      expect(response.status).toBe(404);
      expect(bodyJson(response)["code"]).toBe(testCase.code);
      expect(probe.asked).toEqual([testCase.id]);
    });

    it(`${testCase.code} with the probe reporting live passes through verbatim at 404`, async () => {
      const { store } = throwingStore(testCase.method, testCase.code);
      const response = await post(
        surfaceOver(store, undefined, probeAnswering("live")),
        `/v1/${testCase.operation}`,
        testCase.args,
      );

      expect(response.status).toBe(404);
      expect(bodyJson(response)["code"]).toBe(testCase.code);
    });
  }
});

describe("S5.5 — a probe that fails never changes the answer", () => {
  const probes = [
    { name: "returns a StoreError", probe: failingProbe },
    { name: "throws", probe: throwingProbe },
  ] as const;

  for (const { name, probe } of probes) {
    it(`unknown_session passes through verbatim when the probe ${name}`, async () => {
      const { store } = throwingStore("submitAction", "unknown_session");
      const response = await post(surfaceOver(store, undefined, probe), "/v1/submit-action", {
        sessionId: "s-1",
        actionId: "advance_ticks",
      });

      expect(response.status).toBe(404);
      expect(bodyJson(response)["code"]).toBe("unknown_session");
    });

    it(`unknown_save passes through verbatim when the probe ${name}`, async () => {
      const { store } = throwingStore("loadGame", "unknown_save");
      const response = await post(surfaceOver(store, undefined, probe), "/v1/load-game", { saveId: "v-1" });

      expect(response.status).toBe(404);
      expect(bodyJson(response)["code"]).toBe("unknown_save");
    });
  }
});

describe("S5.6 — one Dispatch for both storage profiles", () => {
  const source = readFileSync(fileURLToPath(new URL("../src/dispatch.ts", import.meta.url)), "utf8");

  it("Dispatch's source branches on no storage profile", () => {
    // The property is "no branch references which store was composed" — the probe being a no-op
    // under the in-memory profile is Composition's doing, asserted below through `compose()`.
    expect(source).not.toContain("storage.kind");
    expect(source).not.toContain("in-memory");
    expect(source).not.toContain("durable");
  });

  it("the in-memory profile's no-op probe leaves unknown_session and unknown_save verbatim", async () => {
    const configuration: WorkloadConfiguration = {
      listen: { host: "127.0.0.1", port: 0 },
      determinism: { kind: "default" },
      otlpEndpoint: null,
      storage: { kind: "in-memory" },
    };
    const composed = await compose(configuration, loadPublishedContract());
    expect(composed.ok).toBe(true);
    if (!composed.ok) return;

    // The probe `compose()` actually built, not a stand-in for it.
    expect(await composed.value.lifecycle.session("s-1")).toEqual(ok("absent"));
    expect(await composed.value.lifecycle.save("v-1")).toEqual(ok("absent"));

    const { store } = throwingStore("submitAction", "unknown_session");
    const response = await post(surfaceOver(store, undefined, composed.value.lifecycle), "/v1/submit-action", {
      sessionId: "s-1",
      actionId: "advance_ticks",
    });

    expect(response.status).toBe(404);
    expect(bodyJson(response)["code"]).toBe("unknown_session");

    await composed.value.close();
  });
});

describe("S5.7 — nothing is retried", () => {
  const failures = [
    { name: "a conflict", make: conflictError, status: 409, code: "concurrent_modification" },
    {
      name: "a storage failure",
      make: () => new Error("the connection dropped mid-statement"),
      status: 503,
      code: "storage_failure",
    },
  ] as const;

  for (const failure of failures) {
    it(`calls the store exactly once per request on ${failure.name}, even though a second call would succeed`, async () => {
      const controlled = controlledPersistence();
      const store = sessionLayerOver(controlled.persistence);
      const sessionId = await sessionOver(store);
      const before = controlled.sessionPutCalls();

      // Fails the next `put` and no more: a retry inside Dispatch would succeed and answer `200`,
      // so the assertion below fails on the status as well as on the count.
      let remaining = 1;
      controlled.failSessionPut(() => {
        if (remaining-- <= 0) controlled.failSessionPut(null);
        return failure.make();
      });

      const response = await post(surfaceOver(store), "/v1/submit-action", {
        sessionId,
        actionId: "advance_ticks",
        params: { ticks: 1 },
      });

      expect(response.status).toBe(failure.status);
      expect(bodyJson(response)["code"]).toBe(failure.code);
      expect(controlled.sessionPutCalls() - before).toBe(1);
    });
  }
});

describe("S5.8 — a rejected action is not a conflict", () => {
  it("answers 200 for the game's own rejection, where a conflict is 409", async () => {
    const rejecting = await realStore();
    const surface = surfaceOver(rejecting);
    const created = await post(surface, "/v1/create-session", { campaignId: CAMPAIGN_ID });
    const { sessionId } = bodyJson(created) as unknown as { sessionId: string };

    const rejected = await post(surface, "/v1/submit-action", { sessionId, actionId: "no-such-action" });
    expect(rejected.status).toBe(200);
    expect(bodyJson(rejected)["ok"]).toBe(false);

    const controlled = controlledPersistence();
    const conflicting = sessionLayerOver(controlled.persistence);
    const conflictSession = await sessionOver(conflicting);
    controlled.failSessionPut(conflictError);
    const conflicted = await post(surfaceOver(conflicting), "/v1/submit-action", {
      sessionId: conflictSession,
      actionId: "advance_ticks",
      params: { ticks: 1 },
    });

    expect(conflicted.status).toBe(409);
    expect(bodyJson(conflicted)["code"]).toBe("concurrent_modification");
  });
});
