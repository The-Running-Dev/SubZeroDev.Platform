/**
 * G3 S4.1–S4.3, S4.5 — no operation crosses principals. For every operation in the vendored
 * contract that carries a `sessionId`, `saveId` or `profileId`, principal B's request on A's id
 * answers exactly as B's request on an id nobody ever minted, over HTTP and over MCP, against the
 * in-memory and the durable store. After each of those requests A's rows are byte-identical to
 * before, read back with SQL (S4.2, durable only — the in-memory store has no SQL to read).
 *
 * The matrix is keyed by contract operation, and a test derives the id-carrying operations from the
 * contract's own request schemas, so an operation added to the contract without a matrix row fails
 * here instead of silently going unchecked (S4.1).
 */
import type { PlayerProfile } from "@the-running-dev/game-engine";
import { afterAll, beforeAll, describe, expect, it } from "vitest";

import type { OwnerKey, StorageProfile, WorkloadProcess } from "../src/types.js";
import { startWorkload } from "../src/lifecycle.js";
import { configurationFor, createTestSchema, RawSchemaClient } from "./support/database.js";
import type { TestSchema } from "./support/database.js";
import { ASSERTION_HEADER, assertionFor, TEST_HANDOFF } from "./support/handoff.js";
import { contract } from "./support/harness.js";
import { openCandidate } from "./support/mixed-version.js";
import { CAMPAIGN_ID } from "./support/real-workload.js";

const OWNER_A: OwnerKey = { issuer: "https://issuer-a.test", subject: "subject-a-5d1e" };
const OWNER_B: OwnerKey = { issuer: "https://issuer-b.test", subject: "subject-b-93c7" };

/** One fixed inbound trace context, so the error body's correlation (derived from it) is equal
 *  between the two requests a comparison makes. */
const TRACEPARENT = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";

const ID_FIELDS = ["sessionId", "saveId", "profileId"] as const;
type IdField = (typeof ID_FIELDS)[number];

/** The ids a request is built from: A's real ones, or ones no principal ever minted. */
interface Ids {
  readonly sessionId: string;
  readonly saveId: string;
  readonly profileId: string;
  readonly savedAt: string;
}

const NEVER_MINTED: Ids = {
  sessionId: "never-minted-session",
  saveId: "never-minted-save",
  profileId: "never-minted-profile",
  savedAt: "2026-01-01T00:00:00.000Z",
};

type RequestBuilder = (ids: Ids) => Record<string, unknown>;

const ADVANCE = { actionId: "advance_ticks", params: { ticks: 1 } };

/** One row per contract operation that carries an id field. */
const MATRIX: Readonly<Record<string, RequestBuilder>> = {
  "list-campaigns": (ids) => ({ profileId: ids.profileId }),
  "get-scene": (ids) => ({ sessionId: ids.sessionId }),
  "get-view": (ids) => ({ sessionId: ids.sessionId }),
  "get-strings": (ids) => ({ sessionId: ids.sessionId }),
  "list-saves": (ids) => ({ profileId: ids.profileId }),
  "preview-action": (ids) => ({ sessionId: ids.sessionId, ...ADVANCE }),
  "create-session": (ids) => ({ campaignId: CAMPAIGN_ID, profileId: ids.profileId }),
  "resume-session": (ids) => ({ sessionId: ids.sessionId }),
  "submit-action": (ids) => ({ sessionId: ids.sessionId, ...ADVANCE }),
  "save-game": (ids) => ({ sessionId: ids.sessionId }),
  "load-game": (ids) => ({ saveId: ids.saveId }),
  "delete-save": (ids) => ({ profileId: ids.profileId, saveId: ids.saveId, expectedSavedAt: ids.savedAt }),
  "branch-session": (ids) => ({ sessionId: ids.sessionId, atActionCount: 0 }),
};

interface SchemaNode {
  readonly properties?: Record<string, unknown>;
  readonly $ref?: string;
  readonly definitions?: Record<string, SchemaNode>;
}

/** Every contract operation whose request schema names `sessionId`, `saveId` or `profileId`,
 *  with the fields it names — read from the schema, resolving the one `$ref` indirection
 *  `create-session` uses. */
function idCarryingOperations(): ReadonlyMap<string, readonly IdField[]> {
  const schemas = contract.schemas as unknown as ReadonlyArray<SchemaNode & { $id: string }>;
  const carrying = new Map<string, readonly IdField[]>();
  for (const row of contract.operations) {
    const root = schemas.find((schema) => schema.$id === (row.requestShape as unknown as string));
    if (!root) throw new Error(`no request schema for ${row.operation}`);
    const target = root.$ref?.startsWith("#/definitions/") ? root.definitions?.[root.$ref.slice("#/definitions/".length)] : root;
    const properties = Object.keys(target?.properties ?? {});
    const fields = ID_FIELDS.filter((field) => properties.includes(field));
    if (fields.length > 0) carrying.set(row.operation as unknown as string, fields);
  }
  return carrying;
}

/** The id-carrying contract operations a matrix has no row for. */
function missingFromMatrix(matrix: Readonly<Record<string, RequestBuilder>>): readonly string[] {
  return [...idCarryingOperations().keys()].filter((operation) => !(operation in matrix));
}

describe("S4.1 — the matrix covers every operation that carries an id", () => {
  it("derives the id-carrying operations from the contract and finds none missing", () => {
    const carrying = idCarryingOperations();
    expect([...carrying.keys()].sort()).toEqual(Object.keys(MATRIX).sort());
    expect(missingFromMatrix(MATRIX)).toEqual([]);
  });

  it("reports an operation the matrix lacks", () => {
    for (const operation of Object.keys(MATRIX)) {
      const { [operation]: _dropped, ...rest } = MATRIX;
      expect(missingFromMatrix(rest), operation).toEqual([operation]);
    }
  });

  it("each matrix row builds a request carrying exactly the fields the contract names", () => {
    const ids: Ids = { sessionId: "s", saveId: "v", profileId: "p", savedAt: "t" };
    for (const [operation, fields] of idCarryingOperations()) {
      const request = MATRIX[operation]!(ids);
      for (const field of fields) expect(request, `${operation}.${field}`).toHaveProperty(field);
    }
  });
});

// ------------------------------------------------------------------------------------ the wire

interface Answer {
  readonly status: number;
  readonly correlation: string | null;
  readonly text: string;
}

async function post(base: string, owner: OwnerKey, path: string, body: unknown): Promise<Answer> {
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

const http = (base: string, owner: OwnerKey, operation: string, body: unknown): Promise<Answer> =>
  post(base, owner, `/v1/${operation}`, body);

const mcp = (base: string, owner: OwnerKey, tool: string, args: unknown): Promise<Answer> =>
  post(base, owner, "/mcp/call-tool", { name: tool, arguments: args });

function json(answer: Answer): Record<string, unknown> {
  return JSON.parse(answer.text) as Record<string, unknown>;
}

/** A successful answer carries ids the workload just minted (the session, and the game inside its
 *  view); two such answers are equal once those are replaced. A refusal carries none, and is
 *  returned as it came. */
function normalized(answer: Answer): Answer {
  if (answer.status !== 200) return answer;
  const text = answer.text.replace(/("(?:sessionId|gameId)"\s*:\s*")[^"]*(")/g, "$1<minted>$2");
  return { ...answer, text };
}

// ------------------------------------------------------------------------------ A's state, SQL

const OWNED_TABLES = ["session", "save", "profile", "profile_achievement"] as const;
type Rows = Readonly<Record<(typeof OWNED_TABLES)[number], readonly string[]>>;

/** Every row A owns, as the database serialises it, per table in a fixed order. */
async function rowsOf(raw: RawSchemaClient, owner: OwnerKey): Promise<Rows> {
  const rows: Record<string, readonly string[]> = {};
  for (const table of OWNED_TABLES) {
    const { rows: found } = await raw.query<{ r: string }>(
      `select row_to_json(t)::text as r from ${table} t where owner_issuer = $1 and owner_subject = $2 order by 1`,
      [owner.issuer, owner.subject],
    );
    rows[table] = found.map((row) => row.r);
  }
  return rows as Rows;
}

async function countRows(raw: RawSchemaClient, table: string): Promise<number> {
  const { rows } = await raw.query<{ n: string | number }>(`select count(*) as n from ${table}`);
  return Number(rows[0]!.n);
}

const PROFILE: PlayerProfile = {
  formatVersion: 3,
  profileId: "default",
  achievements: [{ campaignId: CAMPAIGN_ID, achievementId: "first-steps" }],
  terminals: [],
  kindData: [],
};

// ------------------------------------------------------------------------------- the two stores

interface World {
  readonly base: string;
  /** A's real ids. */
  readonly a: Ids;
  readonly raw: RawSchemaClient | undefined;
  close(): Promise<void>;
}

async function openWorld(kind: "in-memory" | "durable"): Promise<World> {
  let schema: TestSchema | undefined;
  let storage: StorageProfile = { kind: "in-memory" };
  if (kind === "durable") {
    schema = await createTestSchema();
    storage = { kind: "durable", store: configurationFor(schema.schema) };
  }
  const started = await startWorkload({
    listen: { host: "127.0.0.1", port: 0 },
    determinism: { kind: "default" },
    otlpEndpoint: null,
    handoff: TEST_HANDOFF,
    storage,
  });
  if (!started.ok) throw new Error(`startWorkload failed: ${JSON.stringify(started.error)}`);
  const workload: WorkloadProcess = started.value;
  const base = `http://127.0.0.1:${workload.listening.port}`;

  const created = await http(base, OWNER_A, "create-session", { campaignId: CAMPAIGN_ID, profileId: "default" });
  expect(created.status).toBe(200);
  const sessionId = json(created)["sessionId"] as string;
  expect((await http(base, OWNER_A, "submit-action", { sessionId, ...ADVANCE })).status).toBe(200);
  const saved = await http(base, OWNER_A, "save-game", { sessionId });
  expect(saved.status).toBe(200);
  const saveId = json(saved)["saveId"] as string;
  // `save-game` answers the id alone; the instant a `delete-save` must name is what the owner lists.
  const listed = json(await http(base, OWNER_A, "list-saves", { profileId: "default" }));
  const entries = (Array.isArray(listed) ? listed : listed["saves"]) as ReadonlyArray<{ saveId: string; savedAt: string }>;
  const savedAt = entries.find((entry) => entry.saveId === saveId)?.savedAt;
  if (!savedAt) throw new Error(`A's save ${saveId} is not in A's list: ${JSON.stringify(listed)}`);

  let raw: RawSchemaClient | undefined;
  if (schema) {
    // Gameplay this short earns no achievement, so A's profile row is written through A's own port.
    const opened = await openCandidate(configurationFor(schema.schema));
    try {
      expect(await opened.storage.profiles(OWNER_A).save(PROFILE)).toEqual({ ok: true, warnings: [] });
    } finally {
      await opened.close();
    }
    raw = await RawSchemaClient.connect(schema.schema);
  }

  return {
    base,
    a: { sessionId, saveId, profileId: "default", savedAt },
    raw,
    async close() {
      await raw?.close();
      await workload.shutdown();
      await schema?.drop();
    },
  };
}

/** The code a refusal of this request must carry — what a never-minted id of the same kind gets. */
function refusalCode(fields: readonly IdField[], operation: string): string | undefined {
  if (operation === "delete-save") return undefined; // a write the contract answers by save, not by session
  if (fields.includes("sessionId")) return "unknown_session";
  if (fields.includes("saveId")) return "unknown_save";
  return undefined; // profile-only operations answer 200 for an unminted profile
}

describe.each(["in-memory", "durable"] as const)("S4.1–S4.3, S4.5 — B on A's ids answers as on never-minted ids (%s)", (kind) => {
  let world: World;

  beforeAll(async () => {
    world = await openWorld(kind);
  }, 60_000);

  afterAll(async () => {
    await world?.close();
  });

  const operations = [...idCarryingOperations()];

  it.each(operations)("S4.1 HTTP: %s", async (operation, fields) => {
    const build = MATRIX[operation]!;
    const before = world.raw ? await rowsOf(world.raw, OWNER_A) : undefined;

    const foreign = normalized(await http(world.base, OWNER_B, operation, build(world.a)));
    const unknown = normalized(await http(world.base, OWNER_B, operation, build(NEVER_MINTED)));
    expect(foreign).toStrictEqual(unknown);

    const code = refusalCode(fields, operation);
    if (code) {
      expect(foreign.status).toBe(404);
      expect(json(foreign)["code"]).toBe(code);
    }

    // S4.2 — whatever B's request did, none of it touched A's rows.
    if (world.raw && before) expect(await rowsOf(world.raw, OWNER_A)).toStrictEqual(before);
  });

  it.each(operations)("S4.3 MCP: %s", async (operation, fields) => {
    const tool = contract.operations.find((row) => (row.operation as unknown as string) === operation)!.mcpTool as unknown as string;
    const build = MATRIX[operation]!;
    const before = world.raw ? await rowsOf(world.raw, OWNER_A) : undefined;

    const foreign = normalized(await mcp(world.base, OWNER_B, tool, build(world.a)));
    const unknown = normalized(await mcp(world.base, OWNER_B, tool, build(NEVER_MINTED)));
    expect(foreign).toStrictEqual(unknown);

    // Invariant 129: the tool refuses as the HTTP operation does.
    const overHttp = await http(world.base, OWNER_B, operation, build(NEVER_MINTED));
    if (overHttp.status !== 200) {
      expect(foreign.status).toBe(overHttp.status);
      expect(json(foreign)["code"]).toBe(json(overHttp)["code"]);
    }
    const code = refusalCode(fields, operation);
    if (code) expect(json(foreign)["code"]).toBe(code);

    if (world.raw && before) expect(await rowsOf(world.raw, OWNER_A)).toStrictEqual(before);
  });

  it("S4.2: A's rows are non-empty in every owned table, so the byte-identity checks compare something", async () => {
    if (!world.raw) return;
    const rows = await rowsOf(world.raw, OWNER_A);
    for (const table of OWNED_TABLES) expect(rows[table].length, table).toBeGreaterThan(0);
  });

  it("A still reads, loads and lists everything it owns after B's attempts", async () => {
    const { sessionId, saveId } = world.a;
    expect((await http(world.base, OWNER_A, "get-view", { sessionId })).status).toBe(200);
    expect((await http(world.base, OWNER_A, "load-game", { saveId })).status).toBe(200);
    const listed = json(await http(world.base, OWNER_A, "list-saves", { profileId: "default" }));
    const saves = (Array.isArray(listed) ? listed : listed["saves"]) as readonly unknown[];
    expect(saves.length).toBeGreaterThan(0);
  });

  it("S4.5: branch-session by B from A's session is 404 unknown_session and creates no row", async () => {
    const before = world.raw ? await countRows(world.raw, "session") : undefined;
    const refused = await http(world.base, OWNER_B, "branch-session", { sessionId: world.a.sessionId, atActionCount: 0 });
    expect(refused.status).toBe(404);
    expect(json(refused)["code"]).toBe("unknown_session");
    if (world.raw && before !== undefined) expect(await countRows(world.raw, "session")).toBe(before);

    // Control: the owner's own branch succeeds and does create a row, so the count above could move.
    const own = await http(world.base, OWNER_A, "branch-session", { sessionId: world.a.sessionId, atActionCount: 0 });
    expect(own.status).toBe(200);
    if (world.raw && before !== undefined) expect(await countRows(world.raw, "session")).toBe(before + 1);
  });

  // Last: it expires A's rows, which the earlier tests still need live.
  it("a foreign request answers the same once the row is expired (invariant 115)", async () => {
    if (!world.raw) return;
    await world.raw.query("update session set expires_at = now() - interval '1 hour' where owner_subject = $1", [OWNER_A.subject]);
    await world.raw.query("update save set expires_at = now() - interval '1 hour' where owner_subject = $1", [OWNER_A.subject]);
    for (const [operation, fields] of idCarryingOperations()) {
      if (!fields.includes("sessionId") && !fields.includes("saveId")) continue;
      const build = MATRIX[operation]!;
      const foreign = normalized(await http(world.base, OWNER_B, operation, build(world.a)));
      const unknown = normalized(await http(world.base, OWNER_B, operation, build(NEVER_MINTED)));
      expect(foreign, operation).toStrictEqual(unknown);
      expect(json(foreign)["code"], operation).not.toMatch(/expired/);
    }
    // The owner, by contrast, is told the row expired.
    const own = await http(world.base, OWNER_A, "get-view", { sessionId: world.a.sessionId });
    expect(json(own)["code"]).toBe("session_expired");
  });
});
