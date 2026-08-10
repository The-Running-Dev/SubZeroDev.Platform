/**
 * S7.8 — the same replay, with the client addressed at the .NET edge instead of the workload
 * directly, still passes both comparisons: the dump is still read from the workload, and the golden
 * transcript is still the one Stage 1 asserts against. S7.9 — Stage 1's single-hop replay
 * (`replay.test.ts`) is untouched by this file and still runs in the same suite.
 */
import { afterEach, describe, expect, it } from "vitest";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { loadPublishedContract } from "@subzerodev/service-contract";

import { compareSerializations, compareTranscripts, runHosted, runInProcess } from "../src/replay.js";
import type { Transcript } from "../src/types.js";
import { REPLAY_FIXTURE } from "./fixtures/replay-fixture.js";
import { spawnHostedEdge } from "./support/hosted-edge.js";
import type { SpawnedHostedEdge } from "./support/hosted-edge.js";

const GOLDEN_PATH = fileURLToPath(new URL("./fixtures/golden-transcript.json", import.meta.url));
const contract = loadPublishedContract();

function goldenTranscript(): Transcript {
  return JSON.parse(readFileSync(GOLDEN_PATH, "utf8")) as Transcript;
}

const spawned: SpawnedHostedEdge[] = [];

afterEach(() => {
  for (const edge of spawned.splice(0)) edge.forceKill();
});

describe("S7.8 — the byte-identity proof holds with the client addressed at the edge", () => {
  it(
    "comparison A: the edge-fronted run's dump equals the in-process snapshot, blob for blob",
    { timeout: 60_000 },
    async () => {
      const inProcess = await runInProcess(REPLAY_FIXTURE, contract);
      expect(inProcess.ok).toBe(true);
      if (!inProcess.ok) return;

      const edge = await spawnHostedEdge();
      spawned.push(edge);
      const hosted = await runHosted(REPLAY_FIXTURE, edge.target);
      expect(hosted.ok).toBe(true);
      if (!hosted.ok) return;

      const comparisonA = compareSerializations(inProcess.value.serialization, hosted.value.serialization);
      expect(comparisonA.firstDivergence).toBeNull();
      expect(comparisonA.matched).toBe(true);
    },
  );

  it(
    "comparison B: the edge-fronted run's transcript equals the committed golden transcript, byte for byte",
    { timeout: 60_000 },
    async () => {
      const golden = goldenTranscript();

      const edge = await spawnHostedEdge();
      spawned.push(edge);
      const hosted = await runHosted(REPLAY_FIXTURE, edge.target);
      expect(hosted.ok).toBe(true);
      if (!hosted.ok) return;

      const comparisonB = compareTranscripts(golden, hosted.value.transcript);
      expect(comparisonB.firstDivergence).toBeNull();
      expect(comparisonB.matched).toBe(true);
    },
  );
});
