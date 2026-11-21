import { describe, expect, it } from "vitest";
import { checkFixA, fixAProblems, type Execution } from "../src/fixa.ts";
import { Clock, FakeKeycloak, FIXA, OPERATOR, UNSAFE_FLOW } from "./fakes.ts";

const [username, sub, passkey, recovery] = FIXA as [Execution, Execution, Execution, Execution];
const req = (e: Execution, requirement: string): Execution => ({ ...e, requirement });

describe("fixAProblems", () => {
  it.each<[string, Execution[]]>([
    ["the corrected flow", FIXA],
    ["an extra disabled top-level step", [...FIXA, { level: 0, index: 2, requirement: "DISABLED", providerId: "auth-otp-form" }]],
    ["a disabled sub-flow and everything under it", [username, sub, passkey, recovery,
      { level: 0, index: 2, requirement: "DISABLED", authenticationFlow: true, displayName: "old" },
      { level: 1, index: 0, requirement: "REQUIRED", providerId: "auth-recovery-authn-code-form" }]],
  ])("accepts %s", (_n, flow) => expect(fixAProblems(flow)).toEqual([]));

  it.each<[string, Execution[]]>([
    ["the spike's unsafe flow", UNSAFE_FLOW],
    ["recovery code REQUIRED inside the sub-flow", [username, sub, passkey, req(recovery, "REQUIRED")]],
    ["the sub-flow not REQUIRED", [username, req(sub, "ALTERNATIVE"), passkey, recovery]],
    ["no username form", [{ ...sub, index: 0 }, passkey, recovery]],
    ["username form ALTERNATIVE", [req(username, "ALTERNATIVE"), sub, passkey, recovery]],
    ["an extra top-level step", [...FIXA, { level: 0, index: 2, requirement: "REQUIRED", providerId: "auth-otp-form" }]],
    ["an extra step in the sub-flow", [...FIXA, { level: 1, index: 2, requirement: "ALTERNATIVE", providerId: "auth-password-form" }]],
    ["a step nested deeper", [...FIXA, { level: 2, index: 0, requirement: "REQUIRED", providerId: "auth-password-form" }]],
    ["an empty flow", []],
  ])("rejects %s", (_n, flow) => expect(fixAProblems(flow).length).toBeGreaterThan(0));
});

describe("checkFixA", () => {
  const setup = () => { const kc = new FakeKeycloak(new Clock()); return { kc, op: kc.client(OPERATOR, "master") }; };

  it("passes when the realm flow and every member client's flow are fixA", async () => {
    const { op } = setup();
    expect((await checkFixA(op, "members", ["members-web"])).ok).toBe(true);
  });

  it("checks a client without an override against the realm flow", async () => {
    const { kc, op } = setup();
    kc.clients[0]!.authenticationFlowBindingOverrides = {};
    const r = await checkFixA(op, "members", ["members-web"]);
    expect(r.ok).toBe(true);
    expect(r.flows.map((f) => f.alias)).toEqual(["members-browser", "members-browser"]);
  });

  it("fails when the realm flow is unsafe, even though the member client overrides it", async () => {
    const { kc, op } = setup();
    kc.realm.browserFlow = "browser";
    const r = await checkFixA(op, "members", ["members-web"]);
    expect(r.ok).toBe(false);
    expect(r.flows[0]!.problems.length).toBeGreaterThan(0);
    expect(r.flows[1]!.problems).toEqual([]);
  });

  it("fails when an override names a flow that does not exist", async () => {
    const { kc, op } = setup();
    kc.clients[0]!.authenticationFlowBindingOverrides = { browser: "flow-gone" };
    const r = await checkFixA(op, "members", ["members-web"]);
    expect(r.ok).toBe(false);
    expect(r.flows[1]!.problems).toEqual([expect.stringContaining("names no flow")]);
  });
});
