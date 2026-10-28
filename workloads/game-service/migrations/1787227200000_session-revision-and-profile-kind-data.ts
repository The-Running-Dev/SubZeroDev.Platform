/**
 * The engine `0.12.0` persistence breaks (engine `04-core.md` §7.1, §7.2, §7.4): a required
 * `StoredSessionRecord.revision`, an optional `initialization` on session and save records, and
 * `PlayerProfile` `formatVersion: 3` with `kindData`.
 *
 * Three additive changes, shipped together because all three are the same dependency bump:
 *
 * - `session.revision` — the engine's own compare-and-swap field, `0` when first written and `+1`
 *   on every accepted write. Backfilled as `version - 1`: this store inserts at `version = 1` and
 *   advances `version` by one per accepted write, which is exactly the engine's revision rule
 *   offset by one, so every existing row gets the value the engine would have stamped on it.
 *   `integer`, not `bigint` like `version`: the engine's field is a JS `number`, so it maps the way
 *   `attempt_counter` does rather than through the store's `bigint` parser.
 * - `session.initialization` and `save.initialization` — the record's `SessionInitialization`,
 *   as JSON text. Nullable, and null means *absent*: a row written before the field existed has
 *   an unknown initialization, which the engine distinguishes from a known-empty `{}`.
 * - `profile_kind_data` — `PlayerProfile.kindData`'s own table, on the footing `profile_achievement`
 *   and `profile_terminal` already stand on: one row per `(profile, kindId)`. `data` is the
 *   record's opaque `data` as JSON text, so a record for a kind this build does not register
 *   round-trips without the store interpreting it.
 */
import type { ColumnDefinitions, MigrationBuilder } from "node-pg-migrate";

export const shorthands: ColumnDefinitions | undefined = undefined;

const IMPLICIT_TENANT = "implicit-tenant";

export async function up(pgm: MigrationBuilder): Promise<void> {
  pgm.addColumn("session", {
    revision: { type: "integer", notNull: true, default: 0 },
    initialization: { type: "text", notNull: false },
  });
  pgm.sql("update session set revision = version - 1");

  pgm.addColumn("save", {
    initialization: { type: "text", notNull: false },
  });

  pgm.createTable("profile_kind_data", {
    tenant_id: { type: "text", notNull: true, default: IMPLICIT_TENANT },
    profile_id: { type: "text", notNull: true },
    kind_id: { type: "text", notNull: true },
    data_version: { type: "integer", notNull: true },
    data: { type: "text", notNull: true },
    row_created_at: { type: "timestamptz", notNull: true, default: pgm.func("now()") },
    row_updated_at: { type: "timestamptz", notNull: true, default: pgm.func("now()") },
  });
  pgm.addConstraint("profile_kind_data", "profile_kind_data_pkey", {
    primaryKey: ["tenant_id", "profile_id", "kind_id"],
  });
}

export async function down(pgm: MigrationBuilder): Promise<void> {
  pgm.dropTable("profile_kind_data");
  pgm.dropColumn("save", "initialization");
  pgm.dropColumn("session", ["revision", "initialization"]);
}
