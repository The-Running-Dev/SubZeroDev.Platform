/**
 * G3 S2.2–S2.11 — the store answers only to its owner. Two principals share one workload: each sees
 * its own sessions, saves and profiles, and the other's ids answer exactly as ids nobody ever minted.
 * The owner reaches the rows (S2.5) and never the records or blobs (S2.6), the decorated store
 * serializes as the undecorated one (S2.7), the lifecycle probe stays one object (S2.8), the G3
 * migration leaves every owned table keyed by owner (S2.10), and an empty owner never reaches
 * composition (S2.11).
 */
import {
  buildContentRegistry,
  buildWorldGraphMvpCampaign,
  createCountingIds,
  createEngine,
  createInMemoryProfileStore,
  createSessionLayer,
  simulationKind,
  storyGraphKind,
  worldGraphKind,
} from "@the-running-dev/game-engine";
import type {
  KindRegistry,
  PlayerProfile,
  SessionPersistence,
  StoredSaveRecord,
  StoredSessionRecord,
} from "@the-running-dev/game-engine";
import pg from "pg";
import { afterEach, describe, expect, it, vi } from "vitest";

import { canonicalEncode } from "../src/canonical.js";
import { compose, createFixedClock } from "../src/compose.js";
import { createDispatcher } from "../src/dispatch.js";
import { startWorkload } from "../src/lifecycle.js";
import { migrateToHead } from "../src/migrations.js";
import { REPLAY_FIXED_INSTANT, runInProcess } from "../src/replay.js";
import type {
  OwnerKey,
  StorageProfile,
  StoreSerializationSnapshot,
  ValidatedArguments,
  WorkloadConfiguration,
  WorkloadProcess,
} from "../src/types.js";
import { configurationFor, createTestSchema, RawSchemaClient } from "./support/database.js";
import type { TestSchema } from "./support/database.js";
import { REPLAY_FIXTURE } from "./fixtures/replay-fixture.js";
import { ASSERTION_HEADER, assertionFor, mintTestAssertion, TEST_HANDOFF, TEST_KEY } from "./support/handoff.js";
import { contract } from "./support/harness.js";
import { loadReleaseRuntime, openCandidate } from "./support/mixed-version.js";
import { CAMPAIGN_ID } from "./support/real-workload.js";

const OWNER_A: OwnerKey = { issuer: "https://issuer-a.test", subject: "subject-a-5d1e" };
const OWNER_B: OwnerKey = { issuer: "https://issuer-b.test", subject: "subject-b-93c7" };

/** One fixed inbound trace context for both halves of a byte-identity comparison — the error body's
 *  correlation is derived from it, so two requests that differ only in the id compare whole. */
const TRACEPARENT = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";
const NEVER_MINTED_SESSION = "never-minted-session";
const NEVER_MINTED_SAVE = "never-minted-save";

let workload: WorkloadProcess | undefined;
let schema: TestSchema | undefined;

afterEach(async () => {
  vi.restoreAllMocks();
  await workload?.shutdown();
  await schema?.drop();
  workload = undefined;
  schema = undefined;
});

function configuration(storage: StorageProfile): WorkloadConfiguration {
  return {
    listen: { host: "127.0.0.1", port: 0 },
    determinism: { kind: "default" },
    otlpEndpoint: null,
    handoff: TEST_HANDOFF,
    storage,
  };
}

async function durableStorage(): Promise<StorageProfile> {
  schema = await createTestSchema();
  return { kind: "durable", store: configurationFor(schema.schema) };
}

async function start(storage: StorageProfile): Promise<string> {
  const started = await startWorkload(configuration(storage));
  if (!started.ok) throw new Error(`startWorkload failed: ${JSON.stringify(started.error)}`);
  workload = started.value;
  return `http://127.0.0.1:${started.value.listening.port}`;
}

interface Answer {
  readonly status: number;
  readonly correlation: string | null;
  readonly text: string;
}

async function call(base: string, owner: OwnerKey, operation: string, body: unknown): Promise<Answer> {
  const path = `/v1/${operation}`;
  const response = await fetch(`${base}${path}`, {
    method: "POST",
    headers: {
      "content-type": "application/json",
      traceparent: TRACEPARENT,
      [ASSERTION_HEADER]: assertionFor("POST", path, owner),
    },
    body: JSON.stringify(body),
  });
  return { status: response.status, correlation: response.headers.get("x-correlation-id"), text: await response.text() };
}

function json(answer: Answer): Record<string, unknown> {
  return JSON.parse(answer.text) as Record<string, unknown>;
}

async function createSession(base: string, owner: OwnerKey): Promise<string> {
  const created = await call(base, owner, "create-session", { campaignId: CAMPAIGN_ID, profileId: "default" });
  expect(created.status).toBe(200);
  return json(created)["sessionId"] as string;
}

function savesIn(answer: Answer): readonly unknown[] {
  const body = JSON.parse(answer.text) as unknown;
  if (Array.isArray(body)) return body;
  return (body as { saves: readonly unknown[] }).saves;
}

const STORAGES: ReadonlyArray<{ name: string; storage: () => Promise<StorageProfile> }> = [
  { name: "in-memory", storage: async () => ({ kind: "in-memory" }) },
  { name: "durable", storage: durableStorage },
];

/** What S2.2–S2.4 leave behind, for S2.5 and S2.6 to inspect. */
async function playBothPrincipals(base: string): Promise<{ sessionId: string; saveId: string }> {
  const sessionId = await createSession(base, OWNER_A);
  await createSession(base, OWNER_B);
  const acted = await call(base, OWNER_A, "submit-action", { sessionId, actionId: "advance_ticks", params: { ticks: 1 } });
  expect(acted.status).toBe(200);
  const saved = await call(base, OWNER_A, "save-game", { sessionId });
  expect(saved.status).toBe(200);
  return { sessionId, saveId: json(saved)["saveId"] as string };
}

describe.each(STORAGES)("S2.2 — another principal's session answers as one never minted ($name)", ({ storage }) => {
  it("get-view and submit-action on A's id are byte-identical to a never-minted id", async () => {
    const base = await start(await storage());
    const sessionId = await createSession(base, OWNER_A);

    const cases = [
      { operation: "get-view", body: (id: string) => ({ sessionId: id }) },
      { operation: "submit-action", body: (id: string) => ({ sessionId: id, actionId: "advance_ticks", params: { ticks: 1 } }) },
    ];
    for (const { operation, body } of cases) {
      const foreign = await call(base, OWNER_B, operation, body(sessionId));
      const unknown = await call(base, OWNER_B, operation, body(NEVER_MINTED_SESSION));
      expect(foreign.status).toBe(404);
      expect(json(foreign)["code"]).toBe("unknown_session");
      expect(foreign).toStrictEqual(unknown);
    }

    // The session is still A's, untouched by B's attempt.
    const own = await call(base, OWNER_A, "get-view", { sessionId });
    expect(own.status).toBe(200);
  });
});

describe("S2.3 — a foreign expired session is unknown, never expired", () => {
  it("B's get-view answers unknown_session and A's answers session_expired", async () => {
    const base = await start(await durableStorage());
    const sessionId = await createSession(base, OWNER_A);

    const raw = await RawSchemaClient.connect(schema!.schema);
    try {
      await raw.query("update session set expires_at = now() - interval '1 hour' where session_id = $1", [sessionId]);
    } finally {
      await raw.close();
    }

    const foreign = await call(base, OWNER_B, "get-view", { sessionId });
    const unknown = await call(base, OWNER_B, "get-view", { sessionId: NEVER_MINTED_SESSION });
    expect(foreign.status).toBe(404);
    expect(json(foreign)["code"]).toBe("unknown_session");
    expect(foreign).toStrictEqual(unknown);

    const own = await call(base, OWNER_A, "get-view", { sessionId });
    expect(own.status).toBe(404);
    expect(json(own)["code"]).toBe("session_expired");
  });
});

describe.each(STORAGES)("S2.4 — saves are listed and loaded only by their owner ($name)", ({ storage }) => {
  it("A lists one save and B none, and B's load-game on A's save answers unknown_save", async () => {
    const base = await start(await storage());
    const { saveId } = await playBothPrincipals(base);

    const listedByA = await call(base, OWNER_A, "list-saves", { profileId: "default" });
    const listedByB = await call(base, OWNER_B, "list-saves", { profileId: "default" });
    expect(listedByA.status).toBe(200);
    expect(listedByB.status).toBe(200);
    expect(savesIn(listedByA)).toHaveLength(1);
    expect(savesIn(listedByB)).toHaveLength(0);

    const foreign = await call(base, OWNER_B, "load-game", { saveId });
    const unknown = await call(base, OWNER_B, "load-game", { saveId: NEVER_MINTED_SAVE });
    expect(foreign.status).toBe(404);
    expect(json(foreign)["code"]).toBe("unknown_save");
    expect(foreign).toStrictEqual(unknown);

    const own = await call(base, OWNER_A, "load-game", { saveId });
    expect(own.status).toBe(200);
  });
});

const PROFILE: PlayerProfile = {
  formatVersion: 3,
  profileId: "default",
  achievements: [{ campaignId: CAMPAIGN_ID, achievementId: "first-steps" }],
  terminals: [],
  kindData: [],
};

function containsNoOwner(value: unknown, owners: readonly OwnerKey[]): void {
  const text = JSON.stringify(value);
  for (const owner of owners) {
    expect(text).not.toContain(owner.issuer);
    expect(text).not.toContain(owner.subject);
  }
}

describe("S2.5, S2.6 — the owner is on every row and in no record or blob", () => {
  it("stamps the inserting principal on session, save, profile and profile_achievement, and nowhere else", async () => {
    const storage = await durableStorage();
    const base = await start(storage);
    const { sessionId, saveId } = await playBothPrincipals(base);

    // Gameplay this short earns no achievement, so A's profile row is written through A's own port.
    const opened = { value: await openCandidate(configurationFor(schema!.schema)) };
    try {
      expect(await opened.value.storage.profiles(OWNER_A).save(PROFILE)).toEqual({ ok: true, warnings: [] });

      const raw = await RawSchemaClient.connect(schema!.schema);
      try {
        for (const table of ["session", "save", "profile", "profile_achievement"]) {
          const { rows } = await raw.query<{ owner_issuer: string; owner_subject: string }>(
            `select owner_issuer, owner_subject from ${table}`,
          );
          expect(rows.length, table).toBeGreaterThan(0);
          for (const row of rows) {
            expect([OWNER_A, OWNER_B], table).toContainEqual({ issuer: row.owner_issuer, subject: row.owner_subject });
          }
        }
        const { rows: sessions } = await raw.query<{ owner_issuer: string; owner_subject: string }>(
          "select owner_issuer, owner_subject from session where session_id = $1",
          [sessionId],
        );
        expect(sessions).toStrictEqual([{ owner_issuer: OWNER_A.issuer, owner_subject: OWNER_A.subject }]);
        const { rows: saves } = await raw.query<{ owner_issuer: string; owner_subject: string }>(
          "select owner_issuer, owner_subject from save where save_id = $1",
          [saveId],
        );
        expect(saves).toStrictEqual([{ owner_issuer: OWNER_A.issuer, owner_subject: OWNER_A.subject }]);
        const { rows: achievements } = await raw.query<{ owner_issuer: string; owner_subject: string }>(
          "select owner_issuer, owner_subject from profile_achievement",
        );
        expect(achievements).toStrictEqual([{ owner_issuer: OWNER_A.issuer, owner_subject: OWNER_A.subject }]);

        // S2.6 — every record, profile and blob, read back through A's ports and straight from the
        // blob columns.
        const persistence = opened.value.storage.persistence(OWNER_A);
        const session = await persistence.sessions.get(sessionId);
        const save = await persistence.saves.get(saveId);
        const listed = await persistence.saves.listByProfile("default");
        const profile = await opened.value.storage.profiles(OWNER_A).load("default");
        expect(session).toBeDefined();
        expect(save).toBeDefined();
        expect(profile.profile.achievements).toStrictEqual(PROFILE.achievements);
        containsNoOwner([session, save, listed, profile], [OWNER_A, OWNER_B]);

        const { rows: blobs } = await raw.query("select blob from session union all select blob from save");
        expect(blobs.length).toBeGreaterThan(0);
        containsNoOwner(blobs, [OWNER_A, OWNER_B]);
      } finally {
        await raw.close();
      }
    } finally {
      await opened.value.close();
    }
  });
});

describe("S2.7 — the decorated store serializes as the undecorated one", () => {
  it("is byte-identical for G2's replay fixture", async () => {
    const decorated = await runInProcess(REPLAY_FIXTURE, contract);
    if (!decorated.ok) throw new Error(`runInProcess failed: ${JSON.stringify(decorated.error)}`);

    // The same engine, sources and fixture, composed straight over plain maps with no decorator
    // and no owner anywhere — what G2's in-memory store was.
    const campaign = buildWorldGraphMvpCampaign();
    if (!campaign.ok || !campaign.value) throw new Error("campaign failed to build");
    const registry = buildContentRegistry([campaign.value]);
    if (!registry.ok || !registry.value) throw new Error("registry failed to build");
    const engine = createEngine({
      kinds: { [storyGraphKind.id]: storyGraphKind, [simulationKind.id]: simulationKind, [worldGraphKind.id]: worldGraphKind } as KindRegistry,
      registry: registry.value,
      ids: createCountingIds(),
    });
    const sessions = new Map<string, StoredSessionRecord>();
    const saves = new Map<string, StoredSaveRecord>();
    const persistence: SessionPersistence = {
      sessions: { get: async (id) => sessions.get(id), put: async (record) => void sessions.set(record.sessionId, record) },
      saves: {
        get: async (id) => saves.get(id),
        put: async (record) => void saves.set(record.saveId, record),
        listByProfile: async (profileId) => [...saves.values()].filter((record) => record.profileId === profileId),
        delete: async (id) => void saves.delete(id),
      },
    };
    let sessionCounter = 0;
    let saveCounter = 0;
    const store = createSessionLayer({
      engine,
      registry: registry.value,
      persistence,
      profiles: createInMemoryProfileStore(),
      clock: createFixedClock(REPLAY_FIXED_INSTANT),
      recordIds: {
        newSessionId: () => `counting-session-id-${sessionCounter++}`,
        newSaveId: () => `counting-save-id-${saveCounter++}`,
      },
    });
    const dispatcher = createDispatcher(contract, store, {
      session: async () => ({ ok: true, value: "absent" }),
      save: async () => ({ ok: true, value: "absent" }),
    });
    for (const step of REPLAY_FIXTURE.steps) {
      const outcome = await dispatcher.invoke(step.operation, step.arguments as ValidatedArguments);
      expect(outcome.kind).not.toBe("error");
    }
    const byId = (a: { id: string }, b: { id: string }): number => (a.id < b.id ? -1 : a.id > b.id ? 1 : 0);
    const undecorated: StoreSerializationSnapshot = {
      sessions: [...sessions.values()].map((record) => ({ id: record.sessionId, blob: record.blob })).sort(byId),
      saves: [...saves.values()].map((record) => ({ id: record.saveId, blob: record.blob })).sort(byId),
    };

    const decoratedBytes = canonicalEncode(decorated.value.serialization as never);
    const undecoratedBytes = canonicalEncode(undecorated as never);
    expect(decoratedBytes.ok && undecoratedBytes.ok).toBe(true);
    expect(decorated.value.serialization.sessions.length).toBeGreaterThan(0);
    expect(decoratedBytes).toStrictEqual(undecoratedBytes);
  });
});

describe.each(STORAGES)("S2.8 — one lifecycle probe, decorated per request ($name)", ({ storage }) => {
  it("is the same object across 100 requests, and each request's Dispatch asks it with that request's owner", async () => {
    const composed = await compose(configuration(await storage()), contract);
    if (!composed.ok) throw new Error(`compose failed: ${JSON.stringify(composed.error)}`);
    try {
      const probe = composed.value.lifecycle;
      const asked = vi.spyOn(probe, "session");
      for (let index = 0; index < 100; index += 1) {
        const owner: OwnerKey = { issuer: "https://issuer.test", subject: `request-${index}` };
        const ports = composed.value.stores.forRequest(owner);
        const dispatcher = createDispatcher(contract, ports.store, ports.lifecycle);
        const outcome = await dispatcher.invoke(
          "submit-action" as never,
          { sessionId: `missing-${index}`, actionId: "advance_ticks" } as unknown as ValidatedArguments,
        );
        expect(outcome).toMatchObject({ kind: "error", code: "unknown_session" });
        expect(composed.value.lifecycle).toBe(probe);
        expect(asked).toHaveBeenLastCalledWith(owner, `missing-${index}`);
      }
      expect(asked).toHaveBeenCalledTimes(100);
    } finally {
      await composed.value.close();
    }
  });
});

describe("S2.10 — the G3 migration keys every owned table by owner", () => {
  it("leaves both owner columns not null on all four tables and profile's key owner-first", async () => {
    const release = await loadReleaseRuntime();
    schema = await createTestSchema(release.migrateToHead);
    const migrated = await migrateToHead(schema.connection);
    expect(migrated.ok).toBe(true);

    const raw = await RawSchemaClient.connect(schema.schema);
    try {
      const { rows: columns } = await raw.query<{ table_name: string; column_name: string; is_nullable: string }>(
        "select table_name, column_name, is_nullable from information_schema.columns " +
          "where table_schema = current_schema() and column_name in ('owner_issuer', 'owner_subject')",
      );
      for (const table of ["session", "save", "profile", "profile_achievement"]) {
        const owned = columns.filter((column) => column.table_name === table);
        expect(owned.map((column) => column.column_name).sort(), table).toStrictEqual(["owner_issuer", "owner_subject"]);
        expect(owned.every((column) => column.is_nullable === "NO"), table).toBe(true);
      }

      const { rows: key } = await raw.query<{ column_name: string }>(
        "select a.attname as column_name from pg_index i " +
          "join pg_class c on c.oid = i.indrelid join pg_namespace n on n.oid = c.relnamespace " +
          "join lateral unnest(i.indkey) with ordinality as k(attnum, position) on true " +
          "join pg_attribute a on a.attrelid = c.oid and a.attnum = k.attnum " +
          "where i.indisprimary and c.relname = 'profile' and n.nspname = current_schema() order by k.position",
      );
      expect(key.map((row) => row.column_name)).toStrictEqual(["tenant_id", "owner_issuer", "owner_subject", "profile_id"]);
    } finally {
      await raw.close();
    }
  });
});

describe("S2.11 — an empty owner never reaches composition", () => {
  it("refuses an assertion whose pis and psub are both empty with 401, running no statement", async () => {
    const base = await start(await durableStorage());
    const path = "/v1/create-session";
    const empty = mintTestAssertion(TEST_KEY, { issuer: "", subject: "" }, { method: "POST", pathAndQuery: path, assertion: undefined }, new Date());

    const statements = vi.spyOn(pg.Client.prototype, "query");
    const response = await fetch(`${base}${path}`, {
      method: "POST",
      headers: { "content-type": "application/json", [ASSERTION_HEADER]: empty },
      body: JSON.stringify({ campaignId: CAMPAIGN_ID }),
    });
    expect(response.status).toBe(401);
    expect(((await response.json()) as { code: string }).code).toBe("assertion_refused");
    expect(statements).not.toHaveBeenCalled();
  });
});
