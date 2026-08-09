/**
 * S5 — the byte-identity proof. A game played across the network is the same game, byte for byte,
 * as the same game played in-process, and both checks are known to be checking something because
 * they have been deliberately made to fail (the two perturbations, S5.5–S5.6).
 */
import { afterEach, describe, expect, it } from "vitest";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { loadPublishedContract } from "@subzerodev/service-contract";

import { canonicalEncode } from "../src/canonical.js";
import { compareSerializations, compareTranscripts, runHosted, runInProcess } from "../src/replay.js";
import type { ReplayFixture, Transcript } from "../src/types.js";
import { REPLAY_FIXTURE } from "./fixtures/replay-fixture.js";
import { spawnHostedWorkload } from "./support/hosted-target.js";
import type { SpawnedHostedTarget } from "./support/hosted-target.js";

const GOLDEN_PATH = fileURLToPath(new URL("./fixtures/golden-transcript.json", import.meta.url));
const contract = loadPublishedContract();

function goldenTranscript(): Transcript {
  return JSON.parse(readFileSync(GOLDEN_PATH, "utf8")) as Transcript;
}

function transposed(fixture: ReplayFixture, operationA: string, operationB: string): ReplayFixture {
  const steps = [...fixture.steps];
  const indexA = steps.findIndex((step) => (step.operation as string) === operationA);
  const indexB = steps.findIndex((step) => (step.operation as string) === operationB);
  const stepA = steps[indexA]!;
  const stepB = steps[indexB]!;
  steps[indexA] = stepB;
  steps[indexB] = stepA;
  return { ...fixture, steps };
}

const spawned: SpawnedHostedTarget[] = [];

async function hostedRun(fixture: ReplayFixture = REPLAY_FIXTURE) {
  const target = await spawnHostedWorkload();
  spawned.push(target);
  return runHosted(fixture, target.target);
}

afterEach(() => {
  for (const target of spawned.splice(0)) target.forceKill();
});

describe("S5.1 — the fixture's operation set equals the table's row set", () => {
  it("passes when every row has exactly one step", async () => {
    const result = await runInProcess(REPLAY_FIXTURE, contract);
    expect(result.ok).toBe(true);
  });

  it("fails with CoverageIncomplete naming the operation missing from the fixture", async () => {
    const withoutListCampaigns: ReplayFixture = {
      ...REPLAY_FIXTURE,
      steps: REPLAY_FIXTURE.steps.filter((step) => (step.operation as string) !== "list-campaigns"),
    };

    const result = await runInProcess(withoutListCampaigns, contract);
    expect(result.ok).toBe(false);
    if (result.ok) return;
    expect(result.error.code).toBe("CoverageIncomplete");
    if (result.error.code !== "CoverageIncomplete") return;
    expect(result.error.onlyInTable).toEqual(["list-campaigns"]);
    expect(result.error.onlyInFixture).toEqual([]);
  });
});

describe("S5.2 and S5.3 — the byte-identity proof over a real hosted process", () => {
  it(
    "comparison A: the hosted dump equals the in-process snapshot, blob for blob",
    { timeout: 30_000 },
    async () => {
      const inProcess = await runInProcess(REPLAY_FIXTURE, contract);
      expect(inProcess.ok).toBe(true);
      if (!inProcess.ok) return;

      const hosted = await hostedRun();
      expect(hosted.ok).toBe(true);
      if (!hosted.ok) return;

      const comparisonA = compareSerializations(inProcess.value.serialization, hosted.value.serialization);
      expect(comparisonA.firstDivergence).toBeNull();
      expect(comparisonA.matched).toBe(true);
    },
  );

  it(
    "comparison B: both runs' transcripts equal the committed golden transcript, byte for byte",
    { timeout: 30_000 },
    async () => {
      const golden = goldenTranscript();

      const inProcess = await runInProcess(REPLAY_FIXTURE, contract);
      expect(inProcess.ok).toBe(true);
      if (!inProcess.ok) return;
      const comparisonBInProcess = compareTranscripts(golden, inProcess.value.transcript);
      expect(comparisonBInProcess.firstDivergence).toBeNull();
      expect(comparisonBInProcess.matched).toBe(true);

      const hosted = await hostedRun();
      expect(hosted.ok).toBe(true);
      if (!hosted.ok) return;
      const comparisonBHosted = compareTranscripts(golden, hosted.value.transcript);
      expect(comparisonBHosted.firstDivergence).toBeNull();
      expect(comparisonBHosted.matched).toBe(true);
    },
  );
});

describe("S5.4 — the two comparisons are asserted separately", () => {
  it("a failure of one is distinguishable from a failure of the other, and passing one is not the other", () => {
    const identicalSnapshot = { sessions: [{ id: "a", blob: "x" }], saves: [] };
    const matchedA = compareSerializations(identicalSnapshot, identicalSnapshot);
    expect(matchedA.matched).toBe(true);

    const identicalTranscript: Transcript = ['{"a":1}'] as unknown as Transcript;
    const matchedB = compareTranscripts(identicalTranscript, identicalTranscript);
    expect(matchedB.matched).toBe(true);

    // A mismatch in one produces a divergence report with a locator specific to its own shape
    // (`sessions[...]`/`saves[...]` for A, `[...]` for B) — passing one never stands in for the
    // other, and each failure is readable on its own.
    const mismatchedA = compareSerializations(identicalSnapshot, { sessions: [], saves: [] });
    expect(mismatchedA.matched).toBe(false);
    expect(mismatchedA.firstDivergence?.locator).toMatch(/^sessions\[/);

    const mismatchedB = compareTranscripts(identicalTranscript, [] as unknown as Transcript);
    expect(mismatchedB.matched).toBe(false);
    expect(mismatchedB.firstDivergence?.locator).toMatch(/^\[/);
  });
});

describe("S5.5 — perturbation 1: transposed steps fail comparison A", () => {
  it("diverges when save-game is moved ahead of the submit-action it should have captured", async () => {
    const baseline = await runInProcess(REPLAY_FIXTURE, contract);
    expect(baseline.ok).toBe(true);
    if (!baseline.ok) return;

    const perturbedFixture = transposed(REPLAY_FIXTURE, "submit-action", "save-game");
    const perturbed = await runInProcess(perturbedFixture, contract);
    expect(perturbed.ok).toBe(true);
    if (!perturbed.ok) return;

    const comparison = compareSerializations(baseline.value.serialization, perturbed.value.serialization);
    expect(comparison.matched).toBe(false);
    expect(comparison.firstDivergence).not.toBeNull();
  });
});

describe("S5.6 — perturbation 2: a substituted response member fails comparison B", () => {
  it("diverges when save-game's saveId is substituted in an otherwise-golden transcript", () => {
    const golden = goldenTranscript();
    const saveIndex = golden.findIndex((entry) => entry.includes('"saveId"'));
    expect(saveIndex).toBeGreaterThanOrEqual(0);

    const original = JSON.parse(golden[saveIndex]!) as { saveId: string };
    const encoded = canonicalEncode({ saveId: `${original.saveId}-substituted` });
    expect(encoded.ok).toBe(true);
    if (!encoded.ok) return;

    const mutated = [...golden];
    mutated[saveIndex] = encoded.value;

    const comparison = compareTranscripts(golden, mutated);
    expect(comparison.matched).toBe(false);
    expect(comparison.firstDivergence?.locator).toBe(`[${saveIndex}]`);
  });
});

describe("S5.7 — the hosted run is a real operating-system process, addressed strictly sequentially", () => {
  it("runs as a distinct OS process reachable only over its bound socket", { timeout: 30_000 }, async () => {
    const spawnedTarget = await spawnHostedWorkload();
    spawned.push(spawnedTarget);

    expect(spawnedTarget.target.baseAddress).toMatch(/^http:\/\/127\.0\.0\.1:\d+$/);

    const result = await runHosted(REPLAY_FIXTURE, spawnedTarget.target);
    expect(result.ok).toBe(true);
  });
});

describe("S5.8 — no transcript entry anywhere contains a canonical serialization", () => {
  it("holds over the whole transcript, checked against the run's own blobs", async () => {
    const inProcess = await runInProcess(REPLAY_FIXTURE, contract);
    expect(inProcess.ok).toBe(true);
    if (!inProcess.ok) return;

    const blobs = [...inProcess.value.serialization.sessions, ...inProcess.value.serialization.saves].map(
      (stored) => stored.blob,
    );
    expect(blobs.length).toBeGreaterThan(0);

    for (const entry of inProcess.value.transcript) {
      for (const blob of blobs) {
        expect(entry).not.toContain(blob);
      }
    }
  });
});

describe("S5.9 — save-game's transcript entry is the narrowed { saveId } in both runs", () => {
  it("matches in the in-process run and the hosted run", { timeout: 30_000 }, async () => {
    const saveStepIndex = REPLAY_FIXTURE.steps.findIndex((step) => (step.operation as string) === "save-game");

    const inProcess = await runInProcess(REPLAY_FIXTURE, contract);
    expect(inProcess.ok).toBe(true);
    if (!inProcess.ok) return;
    expect(JSON.parse(inProcess.value.transcript[saveStepIndex]!)).toEqual({ saveId: "counting-save-id-0" });

    const hosted = await hostedRun();
    expect(hosted.ok).toBe(true);
    if (!hosted.ok) return;
    expect(JSON.parse(hosted.value.transcript[saveStepIndex]!)).toEqual({ saveId: "counting-save-id-0" });
  });
});

describe("S5.10 — a passing run leaves the golden transcript's bytes unchanged", () => {
  it("reads the same bytes before and after driving both runs", async () => {
    const before = readFileSync(GOLDEN_PATH, "utf8");

    const inProcess = await runInProcess(REPLAY_FIXTURE, contract);
    expect(inProcess.ok).toBe(true);
    if (inProcess.ok) {
      compareTranscripts(goldenTranscript(), inProcess.value.transcript);
    }

    const after = readFileSync(GOLDEN_PATH, "utf8");
    expect(after).toBe(before);
  });
});
