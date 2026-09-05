/**
 * `saved_at` on `save` (engine `0.10.0`, `design/20-contract.md` §7.4): `StoredSaveRecord` now
 * carries a Clock-stamped `savedAt`, the sort key `SaveRecordStore.listByProfile` returns to the
 * engine and the precondition `SaveRecordStore.delete`'s conditional remove compares against
 * (invariant D2). Additive only — `saved_at_seq` stays; nothing here touches it.
 *
 * No backfill: this schema has never had a row with a `saved_at` to backfill from, the same
 * position the initial migration's own comment states for the four tables it created.
 */
import type { ColumnDefinitions, MigrationBuilder } from "node-pg-migrate";

export const shorthands: ColumnDefinitions | undefined = undefined;

export async function up(pgm: MigrationBuilder): Promise<void> {
  pgm.addColumn("save", {
    saved_at: { type: "text", notNull: true, default: "" },
  });
  pgm.alterColumn("save", "saved_at", { default: null });
}
