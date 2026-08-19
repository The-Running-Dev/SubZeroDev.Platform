/**
 * S7 — Contention, two instances. The same guarantee S6 proved within one process, proved across
 * two real HTTP servers sharing one durable store — the shape a real scale-out deployment actually
 * takes, and the shape the README's own documented command reproduces (S7.6).
 */
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { afterEach, describe, expect, it, vi } from "vitest";

import { spawnInstances } from "../src/harness.js";
import type {
  Outcome,
  SchemaName,
  StartupError,
  TwoInstanceOptions,
  WorkloadConfiguration,
  WorkloadProcess,
} from "../src/types.js";
import { CAMPAIGN_ID } from "./support/real-workload.js";
import { RawSchemaClient, TEST_CONNECTION_STRING, createTestSchema } from "./support/database.js";
import { postJson } from "./support/harness.js";

function optionsFor(schema: SchemaName, readWritePauseMs: readonly [number, number]): TwoInstanceOptions {
  return { connectionString: TEST_CONNECTION_STRING, schema, readWritePauseMs };
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
      const { sessionId } = created.json as unknown as { sessionId: string };

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
      const { sessionId } = created.json as unknown as { sessionId: string };

      const [responseA, responseB] = await Promise.all([
        postJson(first.baseAddress, "/v1/submit-action", { sessionId, actionId: "advance_ticks", params: { ticks: 1 } }),
        postJson(second.baseAddress, "/v1/submit-action", { sessionId, actionId: "advance_ticks", params: { ticks: 5 } }),
      ]);

      const statuses = [responseA.status, responseB.status].sort();
      expect(statuses).toEqual([200, 409]);

      const loser = responseA.status === 409 ? responseA : responseB;
      expect(loser.json["code"]).toBe("concurrent_modification");

      // Neither response body nor the request that produced it names which instance served it —
      // there is no instance identifier field anywhere in the wire shape to inspect.
      const winner = responseA.status === 200 ? responseA : responseB;
      expect(Object.keys(winner.json)).not.toContain("instance");
      expect(Object.keys(loser.json)).not.toContain("instance");

      // ...and neither does anything the run left behind. The instances are anonymous and
      // interchangeable (`20-contract.md`, "Proof harness"), which is only true if the store
      // cannot be read afterwards to learn which of the two committed the surviving row. Both
      // halves are checked: no column is *named* for an instance, and no stored value *carries*
      // one — an instance is identified by its base address, since nothing else distinguishes
      // the two, so the address is what must be absent.
      const raw = await RawSchemaClient.connect(schema.schema);
      try {
        const columns = await raw.query<{ table_name: string; column_name: string }>(
          "select table_name, column_name from information_schema.columns where table_schema = $1",
          [schema.schema as unknown as string],
        );
        expect(columns.rows.length).toBeGreaterThan(0);
        for (const { column_name } of columns.rows) {
          expect(column_name).not.toMatch(/instance|node|replica|origin|served_by/i);
        }

        const stored = await raw.query("select * from session");
        expect(stored.rows.length).toBe(1);
        // `version` arrives as a `BigInt` (`BIGINT_VERSION_TYPES`), which `JSON.stringify` refuses
        // outright — coerced here so the scan covers every column rather than throwing on one.
        const serialised = JSON.stringify(stored.rows, (_key, value) =>
          typeof value === "bigint" ? value.toString() : value,
        );
        for (const address of [first.baseAddress, second.baseAddress]) {
          expect(serialised).not.toContain(address);
          expect(serialised).not.toContain(address.replace(/^http:\/\//, ""));
        }
      } finally {
        await raw.close();
      }
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

/**
 * S7.7's second half — the failure the harness must *report* rather than swallow. A real workload
 * cannot be asked to refuse to exit, and the bound is a module constant that `TwoInstanceOptions`
 * deliberately does not carry (`20-contract.md` declares three fields and no fourth, so widening
 * it to make this reachable would be signature drift). The only honest way in is to hand
 * `spawnInstances` a process whose `shutdown()` never settles, which is what stubbing
 * `startWorkload` does here — the code under test is still `harness.ts`'s own bound and its own
 * error construction, unmodified.
 *
 * Which instance hangs is selected by `readWritePauseMs`, because that pair is the only thing
 * `spawnInstances` uses to tell the two apart: instance 0 is sent `readWritePauseMs[0]` and
 * instance 1 `readWritePauseMs[1]`. Keying the stub on the pause therefore proves the reported
 * index is the one that actually hung, not merely that *an* index was reported — a harness that
 * swapped the two would pass an assertion that only counted them.
 */
const HANGING_PAUSE_MS = 111;
const EXITING_PAUSE_MS = 222;

/** `spawnInstances` re-imported under a stubbed `startWorkload`. `vi.doMock` is deliberate rather
 *  than a hoisted `vi.mock`: the criteria above this one need the real lifecycle, and they share
 *  this file so that the README's single documented command (S7.6) runs the whole of S7. */
async function spawnWithOneHangingShutdown(
  hanging: 0 | 1,
): Promise<Outcome<readonly [WorkloadInstanceUnderTest, WorkloadInstanceUnderTest], unknown>> {
  vi.resetModules();
  vi.doMock("../src/lifecycle.js", async () => {
    const actual = await vi.importActual<typeof import("../src/lifecycle.js")>("../src/lifecycle.js");
    return {
      ...actual,
      startWorkload: async (configuration: WorkloadConfiguration): Promise<Outcome<WorkloadProcess, StartupError>> => {
        const hangs =
          configuration.storage.kind === "durable" && configuration.storage.store.readWritePauseMs === HANGING_PAUSE_MS;
        return {
          ok: true,
          value: {
            listening: { host: "127.0.0.1", port: hangs ? 1 : 2 },
            probes: {
              liveness: () => ({ status: "healthy" }),
              readiness: async () => ({ status: "healthy" }),
            },
            shutdown: () =>
              hangs
                ? new Promise<never>(() => {})
                : Promise.resolve({ ok: true as const, value: undefined as void }),
          },
        };
      },
    };
  });

  const { spawnInstances: underTest } = await import("../src/harness.js");
  const pauses: readonly [number, number] =
    hanging === 0 ? [HANGING_PAUSE_MS, EXITING_PAUSE_MS] : [EXITING_PAUSE_MS, HANGING_PAUSE_MS];
  return underTest(optionsFor("unused_schema" as unknown as SchemaName, pauses)) as never;
}

type WorkloadInstanceUnderTest = {
  readonly baseAddress: string;
  shutdown(): Promise<{ ok: boolean; error?: { code: string; instance?: 0 | 1; detail?: string } }>;
};

describe("S7.7 — an instance that does not exit within its bound fails the harness, naming which one", () => {
  afterEach(() => {
    vi.doUnmock("../src/lifecycle.js");
    vi.resetModules();
  });

  it(
    "reports InstanceShutdownFailed against the hung instance's own index, and not against its sibling",
    async () => {
      // Both cases are driven concurrently: each waits out the same real shutdown bound, so
      // overlapping them costs one bound rather than two.
      const [zeroHangs, oneHangs] = await Promise.all([
        spawnWithOneHangingShutdown(0),
        spawnWithOneHangingShutdown(1),
      ]);
      if (!zeroHangs.ok || !oneHangs.ok) throw new Error("the stubbed spawn should have succeeded");

      const [hungZero, healthyOne] = zeroHangs.value;
      const [healthyZero, hungOne] = oneHangs.value;

      const [zeroResult, oneResult, siblingOfZero, siblingOfOne] = await Promise.all([
        hungZero.shutdown(),
        hungOne.shutdown(),
        healthyOne.shutdown(),
        healthyZero.shutdown(),
      ]);

      expect(zeroResult.ok).toBe(false);
      expect(zeroResult.error?.code).toBe("InstanceShutdownFailed");
      expect(zeroResult.error?.instance).toBe(0);

      expect(oneResult.ok).toBe(false);
      expect(oneResult.error?.code).toBe("InstanceShutdownFailed");
      expect(oneResult.error?.instance).toBe(1);

      // The bound is per-instance, not per-pair: the one that did exit is reported as having done
      // so, so a hung sibling never fails a healthy instance by association.
      expect(siblingOfZero.ok).toBe(true);
      expect(siblingOfOne.ok).toBe(true);

      // The detail names the bound it exceeded, which is what makes the failure actionable rather
      // than merely a code.
      expect(zeroResult.error?.detail).toMatch(/did not exit within \d+ms/);
      expect(oneResult.error?.detail).toMatch(/did not exit within \d+ms/);
    },
    30_000,
  );

  it("reports InstanceShutdownFailed, named the same way, when the underlying shutdown resolves an error", async () => {
    vi.resetModules();
    vi.doMock("../src/lifecycle.js", async () => {
      const actual = await vi.importActual<typeof import("../src/lifecycle.js")>("../src/lifecycle.js");
      return {
        ...actual,
        startWorkload: async (): Promise<Outcome<WorkloadProcess, StartupError>> => ({
          ok: true,
          value: {
            listening: { host: "127.0.0.1", port: 1 },
            probes: {
              liveness: () => ({ status: "healthy" }),
              readiness: async () => ({ status: "healthy" }),
            },
            shutdown: async () => ({ ok: false as const, error: { code: "DumpWriteFailed" as const } }),
          } as unknown as WorkloadProcess,
        }),
      };
    });

    const { spawnInstances: underTest } = await import("../src/harness.js");
    const spawned = (await underTest(
      optionsFor("unused_schema" as unknown as SchemaName, [0, 0]),
    )) as unknown as Outcome<readonly [WorkloadInstanceUnderTest, WorkloadInstanceUnderTest], unknown>;
    if (!spawned.ok) throw new Error("the stubbed spawn should have succeeded");

    const [first, second] = spawned.value;
    const stoppedFirst = await first.shutdown();
    const stoppedSecond = await second.shutdown();

    expect(stoppedFirst.error?.code).toBe("InstanceShutdownFailed");
    expect(stoppedFirst.error?.instance).toBe(0);
    expect(stoppedSecond.error?.code).toBe("InstanceShutdownFailed");
    expect(stoppedSecond.error?.instance).toBe(1);
  });
});

/**
 * S7.5 and S7.6 — the documented command and the job that runs it, pinned to each other. Running
 * the proof in CI is not the same claim as the README naming the command CI runs: a README edited
 * on its own would leave a green job documenting something nobody executes, which is the failure
 * the fresh-clone criterion exists to prevent. A structural check on the two committed texts, in
 * the same spirit as `compose-file.test.ts`'s.
 */
const REPOSITORY_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), "../../..");
const README_PATH = resolve(REPOSITORY_ROOT, "workloads/game-service/README.md");
const WORKFLOW_PATH = resolve(REPOSITORY_ROOT, ".github/workflows/build.yml");
const README_HEADING = "## Run the two-instance contention proof";

/** The commands in the first fenced `bash` block under the two-instance heading, in order. */
function documentedCommands(readme: string): readonly string[] {
  const section = readme.slice(readme.indexOf(README_HEADING));
  if (!section.startsWith(README_HEADING)) throw new Error(`${README_PATH} has no "${README_HEADING}" section`);
  const fence = /```bash\n([\s\S]*?)```/.exec(section.slice(0, section.indexOf("\n## ", README_HEADING.length)));
  if (!fence?.[1]) throw new Error(`"${README_HEADING}" documents no bash block`);
  return fence[1]
    .split("\n")
    .map((line) => line.replace(/\s+#.*$/, "").trim())
    .filter((line) => line.length > 0);
}

describe("S7.5, S7.6 — the CI job runs the README's own documented commands, in the documented order", () => {
  it("finds each documented command verbatim in the workflow, in the same sequence", () => {
    const commands = documentedCommands(readFileSync(README_PATH, "utf8"));
    const workflow = readFileSync(WORKFLOW_PATH, "utf8");

    // Two, not one: the store is brought up and then the proof is run against it. A README that
    // stopped naming both, or a job that stopped running both, is what this pins.
    expect(commands).toEqual(["docker compose up -d", "npx vitest run tests/contention-two-instances.test.ts"]);

    let searchFrom = 0;
    for (const command of commands) {
      // Matched as the whole of a line — either a line of a `run:` block scalar or the whole value
      // of a single-line `run:` — so a command that merely appears as a prefix of a longer one does
      // not satisfy the criterion's "verbatim".
      const literal = command.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
      const asOwnLine = new RegExp(`^[ \\t]*(?:run:[ \\t]*)?${literal}[ \\t]*$`, "m");
      const found = asOwnLine.exec(workflow.slice(searchFrom));
      expect(found, `${WORKFLOW_PATH} does not run the documented command "${command}" after the one before it`)
        .not.toBeNull();
      searchFrom += (found?.index ?? 0) + (found?.[0].length ?? 0);
    }

    // The README says "From `workloads/game-service`", so the job must run them from there too —
    // the same command from the repository root is a different command.
    expect(workflow).toContain("working-directory: workloads/game-service");
  });
});
