/**
 * S7 — Contention, two instances. The same guarantee S6 proved within one process, proved across
 * two real HTTP servers sharing one durable store — the shape a real scale-out deployment actually
 * takes, and the shape the README's own documented command reproduces (S7.6).
 */
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { ChildProcess } from "node:child_process";
import { EventEmitter } from "node:events";
import { PassThrough, Writable } from "node:stream";

import { spawnInstances } from "../src/harness.js";
import type {
  Outcome,
  SchemaName,
  TwoInstanceOptions,
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
 * `spawnInstances` a child process that ignores its shutdown byte, which is what stubbing
 * `node:child_process`'s `spawn` does here — the code under test is still `harness.ts`'s own
 * bound, its own `SIGKILL` escalation and its own error construction, unmodified.
 *
 * **The stub is at the process boundary, not at composition**, because that is now where the
 * harness's own seam is: `spawnOne` spawns `harness-entrypoint.ts` as a genuine operating-system
 * process (`design/90-decisions.md`, 2026-08-21, "The two-instance proof spawns real processes"),
 * so a `startWorkload` stub would no longer sit anywhere in its path.
 *
 * Which instance hangs is selected by `readWritePauseMs`, because that pair is the only thing
 * `spawnInstances` uses to tell the two apart: instance 0 is sent `readWritePauseMs[0]` and
 * instance 1 `readWritePauseMs[1]`, each reaching its child as `GAME_SERVICE_READ_WRITE_PAUSE_MS`.
 * Keying the stub on that variable therefore proves the reported index is the one that actually
 * hung, not merely that *an* index was reported — a harness that swapped the two would pass an
 * assertion that only counted them.
 */
const HANGING_PAUSE_MS = 111;
const EXITING_PAUSE_MS = 222;

/** A stand-in for `ChildProcessWithoutNullStreams` carrying only what `spawnOne` touches: the
 *  status line on stdout, a stderr nothing writes to, a stdin whose write is either honoured or
 *  ignored, and an `exit` event. `kill()` always reaps — a real `SIGKILL` does, and a fake that
 *  did not would be testing a hang the operating system cannot produce. */
function fakeChild(honoursShutdown: boolean, exitCode = 0): ChildProcess {
  const stdout = new PassThrough();
  const stderr = new PassThrough();
  const emitter = new EventEmitter() as ChildProcess & { exitCode: number | null };
  emitter.exitCode = null;

  const exit = (code: number): void => {
    if (emitter.exitCode !== null) return;
    emitter.exitCode = code;
    emitter.emit("exit", code, null);
  };

  const stdin = new Writable({
    write(_chunk, _encoding, done) {
      if (honoursShutdown) setImmediate(() => exit(exitCode));
      done();
    },
  });

  Object.assign(emitter, {
    stdout,
    stderr,
    stdin,
    kill: (): boolean => {
      exit(0);
      return true;
    },
  });

  stdout.write(`${JSON.stringify({ listening: { host: "127.0.0.1", port: 1 } })}\n`);
  return emitter;
}

/** Stubs `node:child_process`'s `spawn` so every instance `spawnInstances` starts is a
 *  `fakeChild`, with `hanging` selecting which of the two ignores its shutdown byte. */
function mockSpawn(hangingPauseMs: number | null): void {
  vi.resetModules();
  vi.doMock("node:child_process", async () => {
    const actual = await vi.importActual<typeof import("node:child_process")>("node:child_process");
    return {
      ...actual,
      spawn: (_command: string, _args: readonly string[], options: { env?: NodeJS.ProcessEnv }) => {
        const pause = options.env?.["GAME_SERVICE_READ_WRITE_PAUSE_MS"];
        const hangs = hangingPauseMs !== null && pause === String(hangingPauseMs);
        return fakeChild(!hangs);
      },
    };
  });
}

/** `spawnInstances` re-imported under `mockSpawn`'s stub, with `hanging` selecting which
 *  instance's child ignores its shutdown byte. */
async function spawnWithOneHangingShutdown(
  hanging: 0 | 1,
): Promise<Outcome<readonly [WorkloadInstanceUnderTest, WorkloadInstanceUnderTest], unknown>> {
  mockSpawn(HANGING_PAUSE_MS);

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
    vi.doUnmock("node:child_process");
    vi.resetModules();
  });

  it(
    "reports InstanceShutdownFailed against the hung instance's own index, and not against its sibling",
    async () => {
      // Spawning under the stub is near-instant (no real bound is waited on until shutdown()
      // below), so the two cases are set up sequentially rather than raced.
      const zeroHangs = await spawnWithOneHangingShutdown(0);
      const oneHangs = await spawnWithOneHangingShutdown(1);
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

  it("reports InstanceShutdownFailed, named the same way, when the child exits non-zero", async () => {
    vi.resetModules();
    vi.doMock("node:child_process", async () => {
      const actual = await vi.importActual<typeof import("node:child_process")>("node:child_process");
      return {
        // Honours the shutdown byte, but reports a failed exit — `harness-entrypoint.ts`'s own
        // non-zero path, which is what a dump write that failed at shutdown produces.
        ...actual,
        spawn: () => fakeChild(true, 1),
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
  const nextHeading = section.indexOf("\n## ", README_HEADING.length);
  const sectionEnd = nextHeading === -1 ? section.length : nextHeading;
  const fence = /```bash\n([\s\S]*?)```/.exec(section.slice(0, sectionEnd));
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
      // Matched at the start of a line — either a line of a `run:` block scalar or the value of a
      // single-line `run:` — followed by whitespace or end of line, so a command that merely
      // appears as a substring of an unrelated token does not satisfy the criterion's "verbatim",
      // while a trailing flag or chained command on the same line still does.
      const literal = command.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
      const asOwnLine = new RegExp(`^[ \\t]*(?:run:[ \\t]*)?${literal}(?:[ \\t]|$)`, "m");
      const found = asOwnLine.exec(workflow.slice(searchFrom));
      expect(found, `${WORKFLOW_PATH} does not run the documented command "${command}" after the one before it`)
        .not.toBeNull();
      const matchIndex = searchFrom + (found?.index ?? 0);

      // The README says "From `workloads/game-service`", so the step running THIS command must set
      // that working directory itself — not merely have the string appear somewhere else in the file.
      const stepStart = workflow.lastIndexOf("\n      - name:", matchIndex);
      const step = workflow.slice(stepStart, matchIndex);
      expect(step, `the step running "${command}" has no working-directory: workloads/game-service`).toContain(
        "working-directory: workloads/game-service",
      );

      searchFrom += (found?.index ?? 0) + (found?.[0].length ?? 0);
    }
  });
});
