/**
 * The engine's W99/`SaveRecordStore` and `PlayerProfile` `formatVersion: 2` additions
 * (`design/20-contract.md`, "Workload — the durable rows"; engine `04-core.md` §7.1, §7.4).
 *
 * Two additive changes, shipped together because both are the same dependency bump:
 *
 * - `save.saved_at` — the Clock-stamped column `saves.listByProfile`'s sort key and
 *   `saves.delete`'s compare-and-delete precondition depend on. Backfilled to the epoch for any
 *   row written before the column existed (`04-core.md` §7.4: "There is no correct value to
 *   invent for a save taken before the field existed; backfill them to the epoch so they sort
 *   last, which is honest about the fact that their real time was never recorded").
 * - `profile_terminal` — `PlayerProfile.terminals`'s own table, on the identical footing as
 *   `profile_achievement`: one row per `(campaignId, terminalId)` a profile has reached, upserted
 *   the same idempotent way. Split from `profile_achievement` rather than joined with it — a
 *   single query joining both would cross-product one profile's achievements with its terminals.
 */
import type { ColumnDefinitions, MigrationBuilder } from "node-pg-migrate";

export const shorthands: ColumnDefinitions | undefined = undefined;

const IMPLICIT_TENANT = "implicit-tenant";
const EPOCH_INSTANT = "1970-01-01T00:00:00.000Z";

export async function up(pgm: MigrationBuilder): Promise<void> {
  pgm.addColumn("save", {
    saved_at: { type: "text", notNull: false },
  });
  pgm.sql(`update save set saved_at = '${EPOCH_INSTANT}' where saved_at is null`);
  pgm.alterColumn("save", "saved_at", { notNull: true });

  // `listByProfile` (04 §7.2) is a profile-scoped read, same shape as `profile_achievement`'s own
  // per-profile lookups — an index rather than a sequential scan per call.
  pgm.createIndex("save", ["tenant_id", "profile_id"]);

  pgm.createTable("profile_terminal", {
    tenant_id: { type: "text", notNull: true, default: IMPLICIT_TENANT },
    profile_id: { type: "text", notNull: true },
    campaign_id: { type: "text", notNull: true },
    terminal_id: { type: "text", notNull: true },
    row_created_at: { type: "timestamptz", notNull: true, default: pgm.func("now()") },
  });
  pgm.addConstraint("profile_terminal", "profile_terminal_pkey", {
    primaryKey: ["tenant_id", "profile_id", "campaign_id", "terminal_id"],
  });
}

export async function down(pgm: MigrationBuilder): Promise<void> {
  pgm.dropTable("profile_terminal");
  pgm.dropIndex("save", ["tenant_id", "profile_id"]);
  pgm.dropColumn("save", "saved_at");
}
