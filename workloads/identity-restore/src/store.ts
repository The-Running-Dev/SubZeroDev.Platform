/**
 * The off-host record: a PostgreSQL schema on a server that is not Keycloak's, so restoring Keycloak's
 * database cannot roll it back.
 *
 * Three roles touch it, and only the owner can change or remove a row:
 *  - the recorder's role may INSERT events and windows and read windows. It cannot read, update or delete
 *    events, or update or delete windows, so a compromised recorder host can add rows but not rewrite the
 *    history it has already shipped.
 *  - the restore's role may read everything and INSERT into the restore log.
 *  - the owner (whoever runs `installSchema`) keeps every privilege; retention is the owner's job.
 */
import pg from "pg";
import type { BackupMeta, RecordWindow, RestoreLogEntry, StoredEvent } from "./types.ts";

export const quoteIdent = (s: string): string => `"${s.replaceAll('"', '""')}"`;

export interface RoleNames { readonly recorder: string; readonly restorer: string }

export function schemaStatements(schema: string, roles: RoleNames): string[] {
  const s = quoteIdent(schema);
  const rec = quoteIdent(roles.recorder);
  const res = quoteIdent(roles.restorer);
  return [
    `create schema if not exists ${s}`,
    `create table if not exists ${s}.kc_event (
       id text primary key,
       kind text not null check (kind in ('user', 'admin')),
       realm_id text not null,
       time_ms bigint not null,
       body jsonb not null,
       captured_at timestamptz not null default now())`,
    `create index if not exists kc_event_time on ${s}.kc_event (realm_id, time_ms)`,
    `create table if not exists ${s}.kc_window (
       seq bigserial primary key,
       from_ms bigint not null,
       through_ms bigint not null,
       status text not null check (status in ('covered', 'gap')),
       reason text,
       late_count integer not null default 0,
       inserted_count integer not null default 0,
       canary_id text,
       canary_ms bigint,
       recorder text not null,
       polled_at timestamptz not null default now(),
       check (through_ms >= from_ms),
       check (status = 'gap' or reason is null))`,
    `create table if not exists ${s}.kc_restore_log (
       seq bigserial primary key,
       restore_id text not null,
       phase text not null check (phase in ('held', 'locking', 'applied', 'reopened', 'refused', 'released')),
       at_ms bigint not null,
       detail jsonb not null default '{}')`,
    `revoke all on all tables in schema ${s} from public`,
    `revoke all on schema ${s} from public`,
    `grant usage on schema ${s} to ${rec}, ${res}`,
    // Column-level INSERT: the server's clock sets `captured_at` and `polled_at`, and the sequence sets
    // `seq`, so a recorder cannot backdate a window to before the store-retired time.
    `grant insert (id, kind, realm_id, time_ms, body) on ${s}.kc_event to ${rec}`,
    `grant insert (from_ms, through_ms, status, reason, late_count, inserted_count, canary_id, canary_ms, recorder) on ${s}.kc_window to ${rec}`,
    `grant select on ${s}.kc_window to ${rec}`,
    `grant usage on sequence ${s}.kc_window_seq_seq to ${rec}`,
    `grant select on ${s}.kc_event, ${s}.kc_window, ${s}.kc_restore_log to ${res}`,
    `grant insert (restore_id, phase, at_ms, detail) on ${s}.kc_restore_log to ${res}`,
    `grant usage on sequence ${s}.kc_restore_log_seq_seq to ${res}`,
  ];
}

export async function installSchema(connectionString: string, schema: string, roles: RoleNames): Promise<void> {
  const c = new pg.Client({ connectionString });
  await c.connect();
  try {
    await c.query("begin");
    for (const q of schemaStatements(schema, roles)) await c.query(q);
    await c.query("commit");
  } catch (e) {
    await c.query("rollback");
    throw e;
  } finally {
    await c.end();
  }
}

const int = (v: unknown): number => Number(v);

export class RecordStore implements RecorderStore, RestoreStore {
  readonly #pool: pg.Pool;
  readonly #s: string;

  constructor(connectionString: string, schema: string) {
    this.#pool = new pg.Pool({ connectionString, max: 2 });
    this.#s = quoteIdent(schema);
  }

  async close(): Promise<void> { await this.#pool.end(); }

  /** Inserts events not already held. Returns how many were new. Needs only INSERT: the conflict clause
   *  names no target, because PostgreSQL requires SELECT on a named target's columns (`on conflict (id)`
   *  is refused to a role with column-level INSERT alone). The primary key is the table's only unique
   *  constraint, so the targetless clause skips exactly the repeated ids. */
  async insertEvents(events: readonly StoredEvent[]): Promise<number> {
    let inserted = 0;
    for (let i = 0; i < events.length; i += 500) {
      const chunk = events.slice(i, i + 500);
      const values: unknown[] = [];
      const rows = chunk.map((e, j) => {
        values.push(e.id, e.kind, e.body.realmId, e.timeMs, JSON.stringify(e.body));
        const b = j * 5;
        return `($${b + 1}, $${b + 2}, $${b + 3}, $${b + 4}, $${b + 5}::jsonb)`;
      });
      const r = await this.#pool.query(`insert into ${this.#s}.kc_event (id, kind, realm_id, time_ms, body) values ${rows.join(", ")} on conflict do nothing`, values);
      inserted += r.rowCount ?? 0;
    }
    return inserted;
  }

  async insertWindow(w: NewWindow): Promise<void> {
    await this.#pool.query(
      `insert into ${this.#s}.kc_window (from_ms, through_ms, status, reason, late_count, inserted_count, canary_id, canary_ms, recorder)
       values ($1, $2, $3, $4, $5, $6, $7, $8, $9)`,
      [w.fromMs, w.throughMs, w.status, w.reason, w.lateCount, w.insertedCount, w.canaryId, w.canaryMs, w.recorder]);
  }

  async lastWindow(): Promise<RecordWindow | undefined> {
    const r = await this.#pool.query(`select * from ${this.#s}.kc_window order by through_ms desc, seq desc limit 1`);
    return r.rows[0] ? toWindow(r.rows[0]) : undefined;
  }

  async windows(): Promise<RecordWindow[]> {
    const r = await this.#pool.query(`select * from ${this.#s}.kc_window order by from_ms, seq`);
    return r.rows.map(toWindow);
  }

  async windowsOverlapping(fromMs: number, throughMs: number): Promise<RecordWindow[]> {
    const r = await this.#pool.query(`select * from ${this.#s}.kc_window where through_ms >= $1 and from_ms <= $2 order by from_ms, seq`, [fromMs, throughMs]);
    return r.rows.map(toWindow);
  }

  async eventsFrom(realmId: string, fromMs: number): Promise<StoredEvent[]> {
    const r = await this.#pool.query(`select id, kind, time_ms, body from ${this.#s}.kc_event where realm_id = $1 and time_ms >= $2 order by time_ms, id`, [realmId, fromMs]);
    return r.rows.map((x) => ({ kind: x.kind, id: x.id, timeMs: int(x.time_ms), body: x.body }) as StoredEvent);
  }

  async restoreLog(): Promise<RestoreLogEntry[]> {
    const r = await this.#pool.query(`select restore_id, phase, at_ms, detail from ${this.#s}.kc_restore_log order by seq`);
    return r.rows.map((x) => ({ restoreId: x.restore_id, phase: x.phase, atMs: int(x.at_ms), detail: x.detail }));
  }

  async appendRestoreLog(e: RestoreLogEntry): Promise<void> {
    await this.#pool.query(`insert into ${this.#s}.kc_restore_log (restore_id, phase, at_ms, detail) values ($1, $2, $3, $4::jsonb)`,
      [e.restoreId, e.phase, e.atMs, JSON.stringify(e.detail)]);
  }
}

function toWindow(x: Record<string, unknown>): RecordWindow {
  return {
    seq: int(x["seq"]),
    fromMs: int(x["from_ms"]),
    throughMs: int(x["through_ms"]),
    status: x["status"] as RecordWindow["status"],
    reason: (x["reason"] as string | null) ?? null,
    lateCount: int(x["late_count"]),
    canaryId: (x["canary_id"] as string | null) ?? null,
    canaryMs: x["canary_ms"] === null ? null : int(x["canary_ms"]),
    polledAtMs: (x["polled_at"] as Date).getTime(),
  };
}

export type NewWindow = Omit<RecordWindow, "seq" | "polledAtMs"> & { readonly insertedCount: number; readonly recorder: string };

/** What the recorder needs from the store: insert, and read windows. Nothing that reads events. */
export interface RecorderStore {
  insertEvents(events: readonly StoredEvent[]): Promise<number>;
  insertWindow(w: NewWindow): Promise<void>;
  lastWindow(): Promise<RecordWindow | undefined>;
  windowsOverlapping(fromMs: number, throughMs: number): Promise<RecordWindow[]>;
}

/** What the restore needs: read everything, append to the restore log. */
export interface RestoreStore {
  windows(): Promise<RecordWindow[]>;
  eventsFrom(realmId: string, fromMs: number): Promise<StoredEvent[]>;
  restoreLog(): Promise<RestoreLogEntry[]>;
  appendRestoreLog(e: RestoreLogEntry): Promise<void>;
}

export type { BackupMeta };
