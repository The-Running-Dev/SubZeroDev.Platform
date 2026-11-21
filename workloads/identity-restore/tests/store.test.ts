/**
 * The store's grants, against a real PostgreSQL. Runs when RECORD_TEST_DATABASE_URL names a superuser
 * connection (CI sets it, and under CI a missing URL fails rather than skips; locally `docker compose up -d --wait` and
 * RECORD_TEST_DATABASE_URL=postgres://identity_record:identity_record@127.0.0.1:5433/identity_record).
 * Each run makes its own schema and roles, and drops them afterwards.
 */
import { randomBytes } from "node:crypto";
import pg from "pg";
import { afterAll, beforeAll, describe, expect, it } from "vitest";
import { installSchema, quoteIdent, RecordStore } from "../src/store.ts";
import type { StoredEvent } from "../src/types.ts";

const ADMIN_URL = process.env["RECORD_TEST_DATABASE_URL"];
const tag = randomBytes(4).toString("hex");
const schema = `rec_test_${tag}`;
const roles = { recorder: `rec_w_${tag}`, restorer: `rec_r_${tag}` };
const password = `test-${tag}`;
const as = (role: string) => { const u = new URL(ADMIN_URL!); u.username = role; u.password = password; return u.toString(); };

const event = (id: string, timeMs: number): StoredEvent =>
  ({ kind: "user", id, timeMs, body: { id, time: timeMs, type: "LOGIN", realmId: "realm-1", userId: "u" } }) as unknown as StoredEvent;

// Under CI the suite always runs, so a job that forgot the URL fails instead of passing with no store test.
describe.skipIf(!ADMIN_URL && !process.env["CI"])("record store grants (PostgreSQL)", () => {
  let admin: pg.Client;
  let recSql: pg.Client;
  let resSql: pg.Client;
  let rec: RecordStore;
  let res: RecordStore;
  const s = quoteIdent(schema);

  beforeAll(async () => {
    admin = new pg.Client({ connectionString: ADMIN_URL });
    await admin.connect();
    for (const r of Object.values(roles)) await admin.query(`create role ${quoteIdent(r)} login password '${password}'`);
    await installSchema(ADMIN_URL!, schema, roles);
    rec = new RecordStore(as(roles.recorder), schema);
    res = new RecordStore(as(roles.restorer), schema);
    recSql = new pg.Client({ connectionString: as(roles.recorder) });
    resSql = new pg.Client({ connectionString: as(roles.restorer) });
    await recSql.connect();
    await resSql.connect();
  });

  afterAll(async () => {
    await Promise.allSettled([rec?.close(), res?.close(), recSql?.end(), resSql?.end()]);
    if (!admin) return;
    await admin.query(`drop schema if exists ${s} cascade`);
    for (const r of Object.values(roles)) await admin.query(`drop role if exists ${quoteIdent(r)}`);
    await admin.end();
  });

  const denied = async (c: pg.Client, q: string) =>
    expect(c.query(q)).rejects.toMatchObject({ code: "42501" });

  it("lets the recorder insert events, and counts a repeat as nothing new", async () => {
    expect(await rec.insertEvents([event("e1", 1000), event("e2", 2000)])).toBe(2);
    expect(await rec.insertEvents([event("e1", 1000), event("e3", 3000)])).toBe(1);
  });

  it("lets the recorder insert and read windows; the server's clock sets polled_at", async () => {
    const before = Date.now();
    await rec.insertWindow({ fromMs: 0, throughMs: 999, status: "gap", reason: "bootstrap", lateCount: 0, insertedCount: 0, canaryId: null, canaryMs: null, recorder: "t" });
    await rec.insertWindow({ fromMs: 1000, throughMs: 3999, status: "covered", reason: null, lateCount: 0, insertedCount: 3, canaryId: "c1", canaryMs: 4100, recorder: "t" });
    const last = (await rec.lastWindow())!;
    expect(last).toMatchObject({ fromMs: 1000, throughMs: 3999, status: "covered", canaryMs: 4100 });
    expect(Math.abs(last.polledAtMs - before)).toBeLessThan(60_000);
    expect((await rec.windowsOverlapping(500, 1500)).map((w) => w.fromMs)).toEqual([0, 1000]);
  });

  it.each([
    ["read events", `select * from ${"S"}.kc_event`],
    ["update an event", `update ${"S"}.kc_event set time_ms = 0`],
    ["delete an event", `delete from ${"S"}.kc_event`],
    ["truncate events", `truncate ${"S"}.kc_event`],
    ["update a window", `update ${"S"}.kc_window set status = 'covered'`],
    ["delete a window", `delete from ${"S"}.kc_window`],
    ["read the restore log", `select * from ${"S"}.kc_restore_log`],
    ["write the restore log", `insert into ${"S"}.kc_restore_log (restore_id, phase, at_ms) values ('x', 'reopened', 0)`],
    ["backdate a window's polled_at", `insert into ${"S"}.kc_window (from_ms, through_ms, status, recorder, polled_at) values (5000, 6000, 'covered', 't', '2000-01-01')`],
    ["backdate an event's captured_at", `insert into ${"S"}.kc_event (id, kind, realm_id, time_ms, body, captured_at) values ('z', 'user', 'r', 0, '{}', '2000-01-01')`],
  ])("denies the recorder: %s", async (_n, q) => denied(recSql, q.replaceAll("S", s)));

  it("lets the restorer read everything and append to the log", async () => {
    expect((await res.eventsFrom("realm-1", 0)).map((e) => e.id)).toEqual(["e1", "e2", "e3"]);
    expect((await res.windows()).length).toBe(2);
    await res.appendRestoreLog({ restoreId: "r1", phase: "held", atMs: 1, detail: { a: 1 } });
    expect(await res.restoreLog()).toEqual([{ restoreId: "r1", phase: "held", atMs: 1, detail: { a: 1 } }]);
  });

  it.each([
    ["insert an event", `insert into ${"S"}.kc_event (id, kind, realm_id, time_ms, body) values ('y', 'user', 'r', 0, '{}')`],
    ["insert a window", `insert into ${"S"}.kc_window (from_ms, through_ms, status, recorder) values (9000, 9999, 'covered', 't')`],
    ["update the log", `update ${"S"}.kc_restore_log set phase = 'reopened'`],
    ["delete the log", `delete from ${"S"}.kc_restore_log`],
    ["delete an event", `delete from ${"S"}.kc_event`],
  ])("denies the restorer: %s", async (_n, q) => denied(resSql, q.replaceAll("S", s)));
});
