/**
 * G3 S8 — a player lists every save they hold and deletes their account, and no other player can do
 * either to them. The two routes sit beside the game surface (`GET /account/saves`, `DELETE
 * /account`): they read through the per-request owner-bound store, never through Dispatch, and they
 * are rows in neither the operation table nor the MCP tool table.
 */
import type { PlayerProfile } from "@the-running-dev/game-engine";
import { afterEach, describe, expect, it } from "vitest";

import { startWorkload } from "../src/lifecycle.js";
import type { OwnerKey, StorageProfile, WorkloadConfiguration, WorkloadProcess } from "../src/types.js";
import { configurationFor, createTestSchema, RawSchemaClient } from "./support/database.js";
import type { TestSchema } from "./support/database.js";
import { ASSERTION_HEADER, assertionFor, TEST_HANDOFF } from "./support/handoff.js";
import { contract } from "./support/harness.js";
import { openCandidate } from "./support/mixed-version.js";
import { CAMPAIGN_ID } from "./support/real-workload.js";

const OWNER_A: OwnerKey = { issuer: "https://issuer-a.test", subject: "subject-a-8c21" };
const OWNER_B: OwnerKey = { issuer: "https://issuer-b.test", subject: "subject-b-4f90" };

const ACCOUNT_SAVE_FIELDS = ["campaignId", "expiresAt", "profileId", "saveId", "savedAt", "savedAtSeq"];
const OWNED_TABLES = [
  "session",
  "save",
  "profile",
  "profile_achievement",
  "profile_terminal",
  "profile_kind_data",
] as const;

let workload: WorkloadProcess | undefined;
let schema: TestSchema | undefined;

afterEach(async () => {
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
  readonly text: string;
}

async function send(base: string, owner: OwnerKey, method: string, path: string, body?: unknown): Promise<Answer> {
  const response = await fetch(`${base}${path}`, {
    method,
    headers: { "content-type": "application/json", [ASSERTION_HEADER]: assertionFor(method, path, owner) },
    ...(body === undefined ? {} : { body: JSON.stringify(body) }),
  });
  return { status: response.status, text: await response.text() };
}

const call = (base: string, owner: OwnerKey, operation: string, body: unknown): Promise<Answer> =>
  send(base, owner, "POST", `/v1/${operation}`, body);

function json(answer: Answer): Record<string, unknown> {
  return JSON.parse(answer.text) as Record<string, unknown>;
}

async function savesOf(base: string, owner: OwnerKey): Promise<Record<string, unknown>[]> {
  const listed = await send(base, owner, "GET", "/account/saves");
  expect(listed.status).toBe(200);
  return json(listed)["saves"] as Record<string, unknown>[];
}

/** A session under `profileId`, one action played and the game saved; returns both ids. */
async function playAndSave(
  base: string,
  owner: OwnerKey,
  profileId: string,
): Promise<{ sessionId: string; saveId: string }> {
  const created = await call(base, owner, "create-session", { campaignId: CAMPAIGN_ID, profileId });
  expect(created.status).toBe(200);
  const sessionId = json(created)["sessionId"] as string;
  const acted = await call(base, owner, "submit-action", { sessionId, actionId: "advance_ticks", params: { ticks: 1 } });
  expect(acted.status).toBe(200);
  const saved = await call(base, owner, "save-game", { sessionId });
  expect(saved.status).toBe(200);
  return { sessionId, saveId: json(saved)["saveId"] as string };
}

const STORAGES: ReadonlyArray<{ name: string; storage: () => Promise<StorageProfile> }> = [
  { name: "in-memory", storage: async () => ({ kind: "in-memory" }) },
  { name: "durable", storage: durableStorage },
];

describe.each(STORAGES)("S8.1, S8.2 — the listing is the principal's own saves and nothing else ($name)", ({ storage }) => {
  it("lists every save across two profiles with exactly the AccountSaveEntry fields", async () => {
    const base = await start(await storage());
    const first = await playAndSave(base, OWNER_A, "default");
    const second = await playAndSave(base, OWNER_A, "second");

    const saves = await savesOf(base, OWNER_A);
    expect(saves.map((entry) => entry["saveId"]).sort()).toEqual([first.saveId, second.saveId].sort());
    expect(saves.map((entry) => entry["profileId"]).sort()).toEqual(["default", "second"]);
    for (const entry of saves) {
      expect(Object.keys(entry).sort()).toEqual(ACCOUNT_SAVE_FIELDS);
      expect(entry["campaignId"]).toBe(CAMPAIGN_ID);
      expect(Number.isInteger(entry["savedAtSeq"])).toBe(true);
      expect(Date.parse(entry["savedAt"] as string)).not.toBeNaN();
      expect(Date.parse(entry["expiresAt"] as string)).toBeGreaterThan(Date.parse(entry["savedAt"] as string));
    }
  });

  it("returns none of A's saves to B, and lists nothing for a principal that has saved nothing", async () => {
    const base = await start(await storage());
    await playAndSave(base, OWNER_A, "default");
    await playAndSave(base, OWNER_B, "default");

    const bSaves = await savesOf(base, OWNER_B);
    expect(bSaves).toHaveLength(1);
    const aSaves = await savesOf(base, OWNER_A);
    expect(aSaves).toHaveLength(1);
    expect(bSaves[0]!["saveId"]).not.toBe(aSaves[0]!["saveId"]);

    const stranger: OwnerKey = { issuer: OWNER_A.issuer, subject: "subject-with-no-saves" };
    expect(await savesOf(base, stranger)).toEqual([]);
  });
});

describe("S8.3 — deleting the account removes every one of the principal's rows and no one else's", () => {
  it("returns 204 and leaves no row of A's in any owned table, and B's rows exactly as they were", async () => {
    const storage = await durableStorage();
    const base = await start(storage);
    await playAndSave(base, OWNER_A, "default");
    await playAndSave(base, OWNER_A, "second");
    await playAndSave(base, OWNER_B, "default");

    // Gameplay this short earns no achievement, so each profile is written through its owner's port.
    const profile: PlayerProfile = {
      formatVersion: 3,
      profileId: "default",
      achievements: [{ campaignId: CAMPAIGN_ID, achievementId: "first-steps" }],
      terminals: [],
      kindData: [],
    };
    const opened = await openCandidate(configurationFor(schema!.schema));
    try {
      expect(await opened.storage.profiles(OWNER_A).save(profile)).toEqual({ ok: true, warnings: [] });
      expect(await opened.storage.profiles(OWNER_B).save(profile)).toEqual({ ok: true, warnings: [] });
    } finally {
      await opened.close();
    }

    const raw = await RawSchemaClient.connect(schema!.schema);
    try {
      const rowsOf = async (owner: OwnerKey): Promise<Record<string, number>> => {
        const counts: Record<string, number> = {};
        for (const table of OWNED_TABLES) {
          const { rows } = await raw.query<{ n: number }>(
            `select count(*)::int as n from ${table} where owner_issuer = $1 and owner_subject = $2`,
            [owner.issuer, owner.subject],
          );
          counts[table] = rows[0]!.n;
        }
        return counts;
      };
      const snapshotOfB = async (): Promise<unknown> => {
        const taken: Record<string, unknown> = {};
        for (const table of OWNED_TABLES) {
          const { rows } = await raw.query(`select * from ${table} where owner_issuer = $1 and owner_subject = $2 order by 1, 2, 3`, [
            OWNER_B.issuer,
            OWNER_B.subject,
          ]);
          taken[table] = rows;
        }
        return taken;
      };

      const before = await rowsOf(OWNER_A);
      for (const table of ["session", "save", "profile", "profile_achievement"] as const) {
        expect(before[table], table).toBeGreaterThan(0);
      }
      const bBefore = await snapshotOfB();

      const deleted = await send(base, OWNER_A, "DELETE", "/account");
      expect(deleted.status).toBe(204);
      expect(deleted.text).toBe("");

      expect(await rowsOf(OWNER_A)).toEqual(Object.fromEntries(OWNED_TABLES.map((table) => [table, 0])));
      expect(await snapshotOfB()).toEqual(bBefore);
    } finally {
      await raw.close();
    }
  });
});

describe("S8.4 — a failure in the middle of the deletion leaves every row in place", () => {
  it("rolls the whole deletion back when its last statement fails, and completes once the fault is gone", async () => {
    const storage = await durableStorage();
    const base = await start(storage);
    await playAndSave(base, OWNER_A, "default");
    await playAndSave(base, OWNER_A, "second");

    const raw = await RawSchemaClient.connect(schema!.schema);
    try {
      // `session` is the last table the deletion touches, so every earlier delete has already run
      // inside the transaction when this one raises.
      await raw.query(
        "create function s8_fail_session_delete() returns trigger language plpgsql as $$ begin raise exception 'injected'; end $$",
      );
      await raw.query(
        "create trigger s8_fail_session_delete before delete on session for each row execute function s8_fail_session_delete()",
      );

      const count = async (table: string): Promise<number> => {
        const { rows } = await raw.query<{ n: number }>(
          `select count(*)::int as n from ${table} where owner_issuer = $1 and owner_subject = $2`,
          [OWNER_A.issuer, OWNER_A.subject],
        );
        return rows[0]!.n;
      };
      const before = { session: await count("session"), save: await count("save") };
      expect(before.session).toBe(2);
      expect(before.save).toBe(2);

      const failed = await send(base, OWNER_A, "DELETE", "/account");
      expect(failed.status).toBe(500);
      expect(json(failed)["code"]).toBe("internal_failure");
      expect({ session: await count("session"), save: await count("save") }).toEqual(before);
      expect(await savesOf(base, OWNER_A)).toHaveLength(2);

      await raw.query("drop trigger s8_fail_session_delete on session");
      expect((await send(base, OWNER_A, "DELETE", "/account")).status).toBe(204);
      expect(await count("session")).toBe(0);
      expect(await count("save")).toBe(0);
    } finally {
      await raw.close();
    }
  });
});

describe("S8.5 — the account routes are not game operations", () => {
  it("appears in neither table, and the routes answer only their own method", async () => {
    const base = await start({ kind: "in-memory" });

    for (const row of contract.operations) {
      for (const name of [row.operation, row.httpPath, row.mcpTool, row.storeMethod]) {
        expect(String(name).toLowerCase(), String(row.operation)).not.toContain("account");
      }
    }

    const listed = await send(base, OWNER_A, "POST", "/mcp/list-tools");
    expect(listed.status).toBe(200);
    const tools = (json(listed)["tools"] as { name: string }[]).map((tool) => tool.name).sort();
    expect(tools).toEqual(contract.operations.map((row) => row.mcpTool as string).sort());

    // Neither route is a game operation, so the uniform POST wire has nothing at either path…
    for (const path of ["/v1/account/saves", "/v1/account"]) {
      const answer = await send(base, OWNER_A, "POST", path, {});
      expect(answer.status, path).toBe(404);
      expect(json(answer)["code"], path).toBe("unknown_operation");
    }
    // …and each account path means nothing under any verb but its own.
    for (const [method, path] of [
      ["POST", "/account/saves"],
      ["DELETE", "/account/saves"],
      ["GET", "/account"],
      ["POST", "/account"],
    ] as const) {
      const answer = await send(base, OWNER_A, method, path);
      expect(answer.status, `${method} ${path}`).toBe(404);
      expect(json(answer)["code"], `${method} ${path}`).toBe("unknown_operation");
    }
  });

  it("refuses an unsigned or wrongly bound request before it reaches the store", async () => {
    const base = await start({ kind: "in-memory" });
    const { sessionId } = await playAndSave(base, OWNER_A, "default");

    const unsigned = await fetch(`${base}/account`, { method: "DELETE" });
    expect(unsigned.status).toBe(401);
    expect(((await unsigned.json()) as { code: string }).code).toBe("assertion_refused");

    // An assertion minted for the listing is bound to GET and cannot be replayed to delete.
    const replayed = await fetch(`${base}/account`, {
      method: "DELETE",
      headers: { [ASSERTION_HEADER]: assertionFor("GET", "/account/saves", OWNER_A) },
    });
    expect(replayed.status).toBe(401);

    const stillThere = await call(base, OWNER_A, "get-view", { sessionId });
    expect(stillThere.status).toBe(200);
  });
});

describe.each(STORAGES)("S8.6, S8.7 — what committed before the deletion goes with it, and the account starts over ($name)", ({ storage }) => {
  it("answers unknown_session for A's earlier session, and leaves B's session alone", async () => {
    const base = await start(await storage());
    const mine = await playAndSave(base, OWNER_A, "default");
    const theirs = await playAndSave(base, OWNER_B, "default");

    expect((await send(base, OWNER_A, "DELETE", "/account")).status).toBe(204);

    const acted = await call(base, OWNER_A, "submit-action", {
      sessionId: mine.sessionId,
      actionId: "advance_ticks",
      params: { ticks: 1 },
    });
    expect(acted.status).toBe(404);
    expect(json(acted)["code"]).toBe("unknown_session");
    const loaded = await call(base, OWNER_A, "load-game", { saveId: mine.saveId });
    expect(loaded.status).toBe(404);
    expect(json(loaded)["code"]).toBe("unknown_save");

    expect((await call(base, OWNER_B, "get-view", { sessionId: theirs.sessionId })).status).toBe(200);
    expect(await savesOf(base, OWNER_B)).toHaveLength(1);
  });

  it("still authenticates the same principal, who can create a session and sees no earlier save", async () => {
    const base = await start(await storage());
    await playAndSave(base, OWNER_A, "default");
    expect(await savesOf(base, OWNER_A)).toHaveLength(1);

    expect((await send(base, OWNER_A, "DELETE", "/account")).status).toBe(204);

    // The same signed identity, with nothing remembered about it.
    expect(await savesOf(base, OWNER_A)).toEqual([]);
    const created = await call(base, OWNER_A, "create-session", { campaignId: CAMPAIGN_ID, profileId: "default" });
    expect(created.status).toBe(200);
    expect(await savesOf(base, OWNER_A)).toEqual([]);

    // Deleting twice is not an error: there is nothing left to delete.
    expect((await send(base, OWNER_A, "DELETE", "/account")).status).toBe(204);
  });
});
