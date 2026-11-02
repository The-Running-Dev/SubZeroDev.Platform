/** G3: every owned row carries its owner (`design/20-contract.md`, "Persisted schemas").
 *
 * The owner columns default to the reserved empty owner, which no principal can hold
 * (invariant 117), so a session or save row a still-running G2 instance writes afterwards is owned
 * by nobody who can ask for it. That instance's profile writes fail instead: their
 * `on conflict (tenant_id, profile_id)` no longer names a unique key, and the G2 adapter reports
 * each as `profile_write_failed`. Every row present when this runs is deleted in the same run
 * (invariant 128): rows written before players had owners cannot be attributed to one.
 *
 * `profile_terminal` and `profile_kind_data` are profile rows exactly as `profile_achievement` is,
 * and get the same owner columns and key, so two owners' same-named profiles never share them. */
import type { ColumnDefinitions, MigrationBuilder } from "node-pg-migrate";

export const shorthands: ColumnDefinitions | undefined = undefined;

const OWNED_TABLES = ["session", "save", "profile", "profile_achievement", "profile_terminal", "profile_kind_data"] as const;

const OWNER_COLUMNS = {
  owner_issuer: { type: "text", notNull: true, default: "" },
  owner_subject: { type: "text", notNull: true, default: "" },
} as const;

const PROFILE_KEYS = [
  { table: "profile", g2: ["profile_id"] },
  { table: "profile_achievement", g2: ["profile_id", "campaign_id", "achievement_id"] },
  { table: "profile_terminal", g2: ["profile_id", "campaign_id", "terminal_id"] },
  { table: "profile_kind_data", g2: ["profile_id", "kind_id"] },
] as const;

export async function up(pgm: MigrationBuilder): Promise<void> {
  for (const table of OWNED_TABLES) {
    pgm.addColumns(table, OWNER_COLUMNS);
    pgm.sql(`delete from ${table}`);
  }

  for (const { table, g2 } of PROFILE_KEYS) {
    pgm.dropConstraint(table, `${table}_pkey`);
    pgm.addConstraint(table, `${table}_pkey`, {
      primaryKey: ["tenant_id", "owner_issuer", "owner_subject", ...g2],
    });
  }

  pgm.createIndex("session", ["tenant_id", "owner_issuer", "owner_subject"]);
  pgm.createIndex("save", ["tenant_id", "owner_issuer", "owner_subject"]);
}

export async function down(pgm: MigrationBuilder): Promise<void> {
  pgm.dropIndex("save", ["tenant_id", "owner_issuer", "owner_subject"]);
  pgm.dropIndex("session", ["tenant_id", "owner_issuer", "owner_subject"]);

  // Two owners may hold the same profile id; the G2 key cannot hold both, and neither row can be
  // given to the other. Down is not a restore.
  for (const { table, g2 } of PROFILE_KEYS) {
    pgm.sql(`delete from ${table}`);
    pgm.dropConstraint(table, `${table}_pkey`);
    pgm.addConstraint(table, `${table}_pkey`, { primaryKey: ["tenant_id", ...g2] });
  }

  for (const table of OWNED_TABLES) {
    pgm.dropColumns(table, Object.keys(OWNER_COLUMNS));
  }
}
