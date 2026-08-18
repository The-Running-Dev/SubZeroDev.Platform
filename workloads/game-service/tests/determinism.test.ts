/**
 * S4 — the determinism dump: the replay profile's counting ids and fixed clock, and the file
 * `shutdown` writes before the listener stops accepting. The default profile is asserted
 * alongside each criterion, since "unchanged by omission" is only checkable next to the case it
 * is unchanged from.
 */
import { describe, expect, it } from "vitest";
import { existsSync, mkdtempSync, readFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

import { startWorkload } from "../src/lifecycle.js";
import { createFixedClock } from "../src/compose.js";
import { canonicalEncode } from "../src/canonical.js";
import { CAMPAIGN_ID } from "./support/real-workload.js";
import { bodyJson, post, recordingStore, surfaceOver } from "./support/harness.js";
import type { JsonValue, WorkloadConfiguration } from "../src/types.js";

function freshDumpPath(): string {
  return join(mkdtempSync(join(tmpdir(), "determinism-dump-")), "dump.json");
}

function replayConfiguration(dumpPath: string): WorkloadConfiguration {
  return {
    listen: { host: "127.0.0.1", port: 0 },
    determinism: { kind: "replay", fixedInstant: "2026-01-01T00:00:00.000Z", dumpPath },
    otlpEndpoint: null,
    storage: { kind: "in-memory" },
  };
}

function defaultConfiguration(): WorkloadConfiguration {
  return {
    listen: { host: "127.0.0.1", port: 0 },
    determinism: { kind: "default" },
    otlpEndpoint: null,
    storage: { kind: "in-memory" },
  };
}

async function postJson(base: string, path: string, body: unknown): Promise<{ status: number; json: Record<string, unknown> }> {
  const response = await fetch(`${base}${path}`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify(body),
  });
  const text = await response.text();
  return { status: response.status, json: text.length > 0 ? (JSON.parse(text) as Record<string, unknown>) : {} };
}

/** Two sessions and one save — S4.1's own fixture. The second session carries a `profileId` and
 *  the first has an action submitted against it, so `attemptCounter`, `profileId`, `audience` and
 *  the timestamps all take non-default values (S4.2 needs a run where they do). */
async function playThroughTwoSessionsAndOneSave(
  base: string,
): Promise<{ sessionIds: string[]; saveId: string }> {
  const first = await postJson(base, "/v1/create-session", { campaignId: CAMPAIGN_ID, profileId: "player-1" });
  const firstSessionId = (first.json as { sessionId: string }).sessionId;

  await postJson(base, "/v1/submit-action", {
    sessionId: firstSessionId,
    actionId: "advance_ticks",
    params: { ticks: 1 },
  });

  const second = await postJson(base, "/v1/create-session", { campaignId: CAMPAIGN_ID });
  const secondSessionId = (second.json as { sessionId: string }).sessionId;

  const saved = await postJson(base, "/v1/save-game", { sessionId: firstSessionId });
  const saveId = (saved.json as { saveId: string }).saveId;

  return { sessionIds: [firstSessionId, secondSessionId], saveId };
}

describe("S4.1 — the replay profile writes a canonical dump at shutdown", () => {
  it("writes sessions and saves keyed by id, ascending, each value the engine's serialization", async () => {
    const path = freshDumpPath();
    const started = await startWorkload(replayConfiguration(path));
    expect(started.ok).toBe(true);
    if (!started.ok) return;

    const base = `http://127.0.0.1:${started.value.listening.port}`;
    const { sessionIds, saveId } = await playThroughTwoSessionsAndOneSave(base);

    const shutdown = await started.value.shutdown();
    expect(shutdown.ok).toBe(true);

    const raw = readFileSync(path, "utf8");
    const parsed = JSON.parse(raw) as { sessions: Record<string, string>; saves: Record<string, string> };

    // Re-encoding the parsed value must reproduce the file byte for byte — the direct test of
    // "canonical JSON", rather than a whitespace regex that a blob's own string content (a colon
    // followed by a space, entirely legal inside a JSON string value) can trip on a correct encode.
    const reencoded = canonicalEncode(parsed as unknown as JsonValue);
    expect(reencoded.ok).toBe(true);
    if (reencoded.ok) expect(raw).toBe(reencoded.value);

    expect(Object.keys(parsed)).toEqual(["saves", "sessions"]);
    expect(Object.keys(parsed.sessions).sort()).toEqual([...sessionIds].sort());
    expect(Object.keys(parsed.saves)).toEqual([saveId]);

    for (const blob of [...Object.values(parsed.sessions), ...Object.values(parsed.saves)]) {
      expect(() => JSON.parse(blob)).not.toThrow();
    }
  });
});

describe("S4.2 — the dump carries no host-owned record field", () => {
  it("contains none of createdAt, updatedAt, attemptCounter, audience, profileId or savedAtSeq", async () => {
    const path = freshDumpPath();
    const started = await startWorkload(replayConfiguration(path));
    expect(started.ok).toBe(true);
    if (!started.ok) return;

    const base = `http://127.0.0.1:${started.value.listening.port}`;
    await playThroughTwoSessionsAndOneSave(base);

    const shutdown = await started.value.shutdown();
    expect(shutdown.ok).toBe(true);

    const raw = readFileSync(path, "utf8");
    // Matched as a JSON member name, not a bare substring — the engine's own game state carries
    // unrelated fields like `updatedAtTick` that must not trip this check.
    for (const field of ["createdAt", "updatedAt", "attemptCounter", "audience", "profileId", "savedAtSeq"]) {
      expect(raw).not.toContain(`"${field}":`);
    }
  });
});

describe("S4.3 — the default profile writes no dump anywhere", () => {
  it("leaves no file at the path a replay run would have used, after a graceful shutdown", async () => {
    const wouldBeDumpPath = freshDumpPath();
    const started = await startWorkload(defaultConfiguration());
    expect(started.ok).toBe(true);
    if (!started.ok) return;

    const base = `http://127.0.0.1:${started.value.listening.port}`;
    await playThroughTwoSessionsAndOneSave(base);

    const shutdown = await started.value.shutdown();
    expect(shutdown.ok).toBe(true);
    expect(existsSync(wouldBeDumpPath)).toBe(false);
  });

  it("has no dumpPath member on the default profile's configuration", () => {
    const configuration = defaultConfiguration();
    expect("dumpPath" in configuration.determinism).toBe(false);
  });
});

describe("S4.4 — ids are reproducible under the replay profile and random under the default", () => {
  it("produces identical session and save ids across two runs under the replay profile", async () => {
    async function run(): Promise<{ sessionIds: string[]; saveId: string }> {
      const started = await startWorkload(replayConfiguration(freshDumpPath()));
      if (!started.ok) throw new Error("startWorkload failed");
      const base = `http://127.0.0.1:${started.value.listening.port}`;
      const result = await playThroughTwoSessionsAndOneSave(base);
      await started.value.shutdown();
      return result;
    }

    const first = await run();
    const second = await run();
    expect(second).toEqual(first);
  });

  it("produces different session ids across two runs under the default profile", async () => {
    async function run(): Promise<string> {
      const started = await startWorkload(defaultConfiguration());
      if (!started.ok) throw new Error("startWorkload failed");
      const base = `http://127.0.0.1:${started.value.listening.port}`;
      const created = await postJson(base, "/v1/create-session", { campaignId: CAMPAIGN_ID });
      await started.value.shutdown();
      return (created.json as { sessionId: string }).sessionId;
    }

    const first = await run();
    const second = await run();
    expect(first).not.toBe(second);
  });
});

describe("S4.5 — the replay profile's clock is fixed for the whole run", () => {
  it("reports the same fixedInstant on every call, asserted at the composition seam", () => {
    const clock = createFixedClock("2026-01-01T00:00:00.000Z");
    const calls = Array.from({ length: 5 }, () => clock.now());
    expect(new Set(calls)).toEqual(new Set(["2026-01-01T00:00:00.000Z"]));
  });
});

describe("S4.6 — an unwritable dump path fails shutdown and leaves nothing behind", () => {
  it("exits with DumpWriteFailed naming the path, and no file is left at it", async () => {
    const unwritablePath = join(mkdtempSync(join(tmpdir(), "determinism-dump-")), "missing-directory", "dump.json");
    const started = await startWorkload(replayConfiguration(unwritablePath));
    expect(started.ok).toBe(true);
    if (!started.ok) return;

    const base = `http://127.0.0.1:${started.value.listening.port}`;
    await postJson(base, "/v1/create-session", { campaignId: CAMPAIGN_ID });

    const shutdown = await started.value.shutdown();
    expect(shutdown.ok).toBe(false);
    if (shutdown.ok) return;
    expect(shutdown.error.code).toBe("DumpWriteFailed");
    const cause = shutdown.error.cause as { code: string; path: string };
    expect(cause.code).toBe("DumpWriteFailed");
    expect(cause.path).toBe(unwritablePath);
    expect(existsSync(unwritablePath)).toBe(false);
  });
});

describe("S4.8 — a request naming the determinism profile changes nothing", () => {
  it("never reaches the store, and is rejected as malformed_payload", async () => {
    const { store, calls } = recordingStore();
    const surface = surfaceOver(store);

    const response = await post(surface, "/v1/create-session", {
      campaignId: CAMPAIGN_ID,
      determinism: { kind: "replay", fixedInstant: "x", dumpPath: "y" },
    });

    expect(response.status).toBe(400);
    expect(bodyJson(response)["code"]).toBe("malformed_payload");
    expect(calls).toEqual([]);
  });

  it("leaves the run's ids and dump identical to the same run without the rejected request", async () => {
    async function run(withRejectedRequest: boolean): Promise<{ ids: { sessionIds: string[]; saveId: string }; dump: string }> {
      const path = freshDumpPath();
      const started = await startWorkload(replayConfiguration(path));
      if (!started.ok) throw new Error("startWorkload failed");
      const base = `http://127.0.0.1:${started.value.listening.port}`;

      if (withRejectedRequest) {
        const rejected = await postJson(base, "/v1/create-session", {
          campaignId: CAMPAIGN_ID,
          determinism: { kind: "replay", fixedInstant: "2026-01-01T00:00:00.000Z", dumpPath: "/should-not-reach" },
        });
        expect(rejected.status).toBe(400);
        expect(rejected.json["code"]).toBe("malformed_payload");
      }

      const ids = await playThroughTwoSessionsAndOneSave(base);
      const shutdown = await started.value.shutdown();
      if (!shutdown.ok) throw new Error("shutdown failed");
      return { ids, dump: readFileSync(path, "utf8") };
    }

    const without = await run(false);
    const withRejected = await run(true);
    expect(withRejected.ids).toEqual(without.ids);
    expect(withRejected.dump).toBe(without.dump);
  });
});
