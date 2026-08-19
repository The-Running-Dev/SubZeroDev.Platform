/**
 * S8 — the byte-identity proof, durably. The same two comparisons S5 established, run a third
 * time against a freshly created, freshly dropped PostgreSQL schema instead of the in-memory
 * store — so persistence is shown to have changed nothing about what was recorded, rather than
 * assumed to.
 */
import { afterEach, describe, expect, it } from "vitest";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { Client } from "pg";
import { loadPublishedContract } from "@subzerodev/service-contract";
import type { StoredSessionRecord } from "@the-running-dev/game-engine";

import { createRunSchema } from "../src/harness.js";
import { IMPLICIT_TENANT_ID, openDurableStore } from "../src/store.js";
import {
  assertNonEmpty,
  compareSerializations,
  compareTranscripts,
  runDurableReplay,
  runInProcess,
} from "../src/replay.js";
import type { ReplayFixture, RunSchema, SemanticVersion, StoreSerializationSnapshot, Transcript } from "../src/types.js";
import { REPLAY_FIXTURE } from "./fixtures/replay-fixture.js";
import { spawnHostedWorkload } from "./support/hosted-target.js";
import type { SpawnedHostedTarget } from "./support/hosted-target.js";
import { RawSchemaClient, TEST_CONNECTION_STRING, configurationFor } from "./support/database.js";

const GOLDEN_PATH = fileURLToPath(new URL("./fixtures/golden-transcript.json", import.meta.url));
const contract = loadPublishedContract();

// REPLAY_FIXTURE mints one session (`create-session`) and one save (`save-game`); every other
// step, including `resume-session`, addresses that same session id rather than minting another —
// `resumeSession` only reads the record, it never writes one — and `load-game` reads the save back
// rather than minting a second one.
const EXPECTED_SESSIONS = 1;
const EXPECTED_SAVES = 1;

function goldenTranscript(): Transcript {
  return JSON.parse(readFileSync(GOLDEN_PATH, "utf8")) as Transcript;
}

const spawnedTargets: SpawnedHostedTarget[] = [];
const schemasToDrop: RunSchema[] = [];

afterEach(async () => {
  for (const target of spawnedTargets.splice(0)) await target.forceKill();
  for (const schema of schemasToDrop.splice(0)) await schema.drop();
});

/** Creates a fresh run schema, spawns a durable hosted target against it, and drives the fixture
 *  through `runDurableReplay` — the S8 counterpart to `replay.test.ts`'s own `hostedRun()`. */
async function durableRun(fixture: ReplayFixture = REPLAY_FIXTURE): Promise<{
  result: Awaited<ReturnType<typeof runDurableReplay>>;
  schema: RunSchema;
}> {
  const created = await createRunSchema(TEST_CONNECTION_STRING);
  if (!created.ok) throw new Error(`createRunSchema failed: ${JSON.stringify(created.error)}`);
  const schema = created.value;
  schemasToDrop.push(schema);

  const spawned = await spawnHostedWorkload(undefined, {
    connectionString: TEST_CONNECTION_STRING,
    schema: schema.name,
  });
  spawnedTargets.push(spawned);

  const result = await runDurableReplay(fixture, spawned.target, schema);
  return { result, schema };
}

describe("S8.1 — createRunSchema provisions a pristine schema; drop() removes it entirely", () => {
  it("migrates the schema to head, and drop() leaves no schema behind", async () => {
    const created = await createRunSchema(TEST_CONNECTION_STRING);
    expect(created.ok).toBe(true);
    if (!created.ok) return;
    const schema = created.value;

    const client = await RawSchemaClient.connect(schema.name);
    const sessionTable = await client.query(
      "select 1 from information_schema.tables where table_schema = $1 and table_name = 'session'",
      [String(schema.name)],
    );
    await client.close();
    expect(sessionTable.rowCount).toBe(1);

    const dropped = await schema.drop();
    expect(dropped.ok).toBe(true);

    const verify = new Client({ connectionString: TEST_CONNECTION_STRING });
    await verify.connect();
    try {
      const remaining = await verify.query("select 1 from information_schema.schemata where schema_name = $1", [
        String(schema.name),
      ]);
      expect(remaining.rowCount).toBe(0);
    } finally {
      await verify.end();
    }
  });
});

describe("S8.2, S8.4 — comparison A: the durable dump equals the in-process snapshot, under production lifecycle bounds", () => {
  it(
    "matches blob for blob, with no step observing an expiry code",
    { timeout: 30_000 },
    async () => {
      const inProcess = await runInProcess(REPLAY_FIXTURE, contract);
      expect(inProcess.ok).toBe(true);
      if (!inProcess.ok) return;

      const { result: durable } = await durableRun();
      // A `session_expired`/`save_expired` step under short TTLs would fail a step and resolve
      // `Err` here rather than embed itself silently in a passing transcript — `ok === true` is
      // S8.4's own assertion that production bounds (`DEFAULT_LIFECYCLE_BOUNDS`) let all ten steps
      // land as accepted operations.
      expect(durable.ok).toBe(true);
      if (!durable.ok) return;

      // Runs before comparison A, not instead of it (`replay.ts`'s own note on `assertNonEmpty`):
      // two empty ordered sets would compare byte-identical, so this is what stops a dump that
      // read the wrong schema from passing comparison A vacuously.
      const nonEmpty = assertNonEmpty(durable.value.serialization, EXPECTED_SESSIONS, EXPECTED_SAVES);
      expect(nonEmpty.matched).toBe(true);

      const comparisonA = compareSerializations(inProcess.value.serialization, durable.value.serialization);
      expect(comparisonA.firstDivergence).toBeNull();
      expect(comparisonA.matched).toBe(true);
    },
  );
});

describe("S8.3 — comparison B: the durable run's transcript equals the committed golden transcript", () => {
  it(
    "matches byte for byte",
    { timeout: 30_000 },
    async () => {
      const golden = goldenTranscript();
      const { result: durable } = await durableRun();
      expect(durable.ok).toBe(true);
      if (!durable.ok) return;

      const comparisonB = compareTranscripts(golden, durable.value.transcript);
      expect(comparisonB.firstDivergence).toBeNull();
      expect(comparisonB.matched).toBe(true);
    },
  );
});

describe("S8.5 — assertNonEmpty fails before comparison A, naming expected versus actual counts", () => {
  it("fails on an empty snapshot, naming the fixture's own expected counts", () => {
    const empty: StoreSerializationSnapshot = { sessions: [], saves: [] };
    const result = assertNonEmpty(empty, EXPECTED_SESSIONS, EXPECTED_SAVES);
    expect(result.matched).toBe(false);
    expect(result.firstDivergence?.expected).toBe(`sessions=${EXPECTED_SESSIONS} saves=${EXPECTED_SAVES}`);
    expect(result.firstDivergence?.actual).toBe("sessions=0 saves=0");
  });

  it("passes when the counts equal the fixture's own expectation", () => {
    const snapshot: StoreSerializationSnapshot = {
      sessions: [
        { id: "s0", blob: "{}" },
        { id: "s1", blob: "{}" },
      ],
      saves: [{ id: "sv", blob: "{}" }],
    };
    const result = assertNonEmpty(snapshot, EXPECTED_SESSIONS, EXPECTED_SAVES);
    expect(result.matched).toBe(true);
    expect(result.firstDivergence).toBeNull();
  });
});

describe("S8.6, S8.9 — two durable replays in sequence use two fresh schemas, one tenant, no collision", () => {
  it(
    "both runs succeed against their own schema, and every row in both carries the implicit tenant",
    { timeout: 45_000 },
    async () => {
      const first = await durableRun();
      expect(first.result.ok).toBe(true);

      const second = await durableRun();
      expect(second.result.ok).toBe(true);

      expect(String(first.schema.name)).not.toBe(String(second.schema.name));

      for (const schema of [first.schema, second.schema]) {
        const client = await RawSchemaClient.connect(schema.name);
        const rows = await client.query<{ tenant_id: string }>("select distinct tenant_id from session");
        await client.close();
        expect(rows.rows.length).toBeGreaterThan(0);
        for (const row of rows.rows) {
          expect(row.tenant_id).toBe(IMPLICIT_TENANT_ID);
        }
      }
    },
  );
});

function bareSessionRecord(id: string): StoredSessionRecord {
  return {
    sessionId: id,
    blob: "{}",
    audience: "player",
    attemptCounter: 0,
    replayCompatible: true,
    createdAt: "2026-01-01T00:00:00.000Z",
    updatedAt: "2026-01-01T00:00:00.000Z",
  };
}

describe('S8.8 — the serialization handle orders by collate "C", not locale order', () => {
  it("orders two ids whose byte order and locale order disagree by byte order", async () => {
    const created = await createRunSchema(TEST_CONNECTION_STRING);
    expect(created.ok).toBe(true);
    if (!created.ok) return;
    schemasToDrop.push(created.value);

    const opened = await openDurableStore(configurationFor(created.value.name), "1.0.0" as SemanticVersion);
    expect(opened.ok).toBe(true);
    if (!opened.ok) return;
    try {
      const persistence = opened.value.persistenceForRequest();
      // Under `collate "C"`, uppercase sorts before lowercase ("Bravo" before "alpha"); under a
      // locale-aware collation (e.g. `en-US-x-icu`) the two invert. Seeded in the order that would
      // expose either bug — an omitted `collate "C"` or a locale-default connection.
      await persistence.sessions.put(bareSessionRecord("Bravo"));
      await persistence.sessions.put(bareSessionRecord("alpha"));

      const snapshot = await opened.value.serialization.snapshot();
      expect(snapshot.sessions.map((row) => row.id)).toEqual(["Bravo", "alpha"]);
    } finally {
      await opened.value.close();
    }
  });
});
