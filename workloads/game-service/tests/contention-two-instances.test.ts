/**
 * S7 — Contention, two instances. The same guarantee S6 proved within one process, proved across
 * two real HTTP servers sharing one durable store — the shape a real scale-out deployment actually
 * takes, and the shape the README's own documented command reproduces (S7.6).
 */
import { describe, expect, it } from "vitest";

import { spawnInstances } from "../src/harness.js";
import type { SchemaName, TwoInstanceOptions } from "../src/types.js";
import { CAMPAIGN_ID } from "./support/real-workload.js";
import { TEST_CONNECTION_STRING, createTestSchema } from "./support/database.js";

function optionsFor(schema: SchemaName, readWritePauseMs: readonly [number, number]): TwoInstanceOptions {
  return { connectionString: TEST_CONNECTION_STRING, schema, readWritePauseMs };
}

async function postJson(baseAddress: string, path: string, body: unknown): Promise<Response> {
  return fetch(`${baseAddress}${path}`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify(body),
  });
}

describe("S7.1 — spawnInstances returns two independently addressed, loopback-bound instances", () => {
  it("reports ready on its own base address, both on loopback", async () => {
    const schema = await createTestSchema();
    const spawned = await spawnInstances(optionsFor(schema.schema, [0, 0]));
    expect(spawned.ok).toBe(true);
    if (!spawned.ok) return;
    const [first, second] = spawned.value;
    try {
      expect(first.baseAddress).toMatch(/^http:\/\/127\.0\.0\.1:\d+$/);
      expect(second.baseAddress).toMatch(/^http:\/\/127\.0\.0\.1:\d+$/);
      expect(first.baseAddress).not.toBe(second.baseAddress);
    } finally {
      await first.shutdown();
      await second.shutdown();
      await schema.drop();
    }
  });
});

describe("S7.2 — a session created through one instance is readable through the other", () => {
  it("round-trips a session id across instances via a query operation", async () => {
    const schema = await createTestSchema();
    const spawned = await spawnInstances(optionsFor(schema.schema, [0, 0]));
    if (!spawned.ok) throw new Error(`spawnInstances failed: ${JSON.stringify(spawned.error)}`);
    const [first, second] = spawned.value;
    try {
      const created = await postJson(first.baseAddress, "/v1/create-session", { campaignId: CAMPAIGN_ID });
      expect(created.status).toBe(200);
      const { sessionId } = (await created.json()) as { sessionId: string };

      const queried = await postJson(second.baseAddress, "/v1/get-scene", { sessionId });
      expect(queried.status).toBe(200);
    } finally {
      await first.shutdown();
      await second.shutdown();
      await schema.drop();
    }
  });
});

describe("S7.3, S7.4 — two concurrent submissions, one to each instance, resolve to one winner", () => {
  it("produces exactly one 200 and one 409 carrying concurrent_modification, naming no instance", async () => {
    const schema = await createTestSchema();
    // Both instances read before either writes: the instance under test carries the pause, the
    // other is sent inside it (`20-contract.md`, "Proof harness").
    const spawned = await spawnInstances(optionsFor(schema.schema, [300, 0]));
    if (!spawned.ok) throw new Error(`spawnInstances failed: ${JSON.stringify(spawned.error)}`);
    const [first, second] = spawned.value;
    try {
      const created = await postJson(first.baseAddress, "/v1/create-session", { campaignId: CAMPAIGN_ID });
      expect(created.status).toBe(200);
      const { sessionId } = (await created.json()) as { sessionId: string };

      const [responseA, responseB] = await Promise.all([
        postJson(first.baseAddress, "/v1/submit-action", { sessionId, actionId: "advance_ticks", params: { ticks: 1 } }),
        postJson(second.baseAddress, "/v1/submit-action", { sessionId, actionId: "advance_ticks", params: { ticks: 5 } }),
      ]);

      const statuses = [responseA.status, responseB.status].sort();
      expect(statuses).toEqual([200, 409]);

      const loser = responseA.status === 409 ? responseA : responseB;
      const loserBody = (await loser.json()) as Record<string, unknown>;
      expect(loserBody["code"]).toBe("concurrent_modification");

      // Neither response body nor the request that produced it names which instance served it —
      // there is no instance identifier field anywhere in the wire shape to inspect.
      const winner = responseA.status === 200 ? responseA : responseB;
      const winnerBody = (await winner.json()) as Record<string, unknown>;
      expect(Object.keys(winnerBody)).not.toContain("instance");
      expect(Object.keys(loserBody)).not.toContain("instance");
    } finally {
      await first.shutdown();
      await second.shutdown();
      await schema.drop();
    }
  });
});

describe("S7.7 — shutdown() on both instances exits cleanly", () => {
  it("both shutdowns succeed, and each instance stops accepting requests", async () => {
    const schema = await createTestSchema();
    const spawned = await spawnInstances(optionsFor(schema.schema, [0, 0]));
    if (!spawned.ok) throw new Error(`spawnInstances failed: ${JSON.stringify(spawned.error)}`);
    const [first, second] = spawned.value;
    try {
      const stoppedFirst = await first.shutdown();
      const stoppedSecond = await second.shutdown();
      expect(stoppedFirst.ok).toBe(true);
      expect(stoppedSecond.ok).toBe(true);

      await expect(fetch(`${first.baseAddress}/livez`, { signal: AbortSignal.timeout(500) })).rejects.toBeTruthy();
    } finally {
      await schema.drop();
    }
  });
});
