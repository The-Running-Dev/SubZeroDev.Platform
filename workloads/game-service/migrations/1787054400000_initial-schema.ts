/**
 * G2's first schema (`design/20-contract.md`, "Persisted schemas"). Four tables, created together
 * because they ship together — nothing here backfills, because nothing existed before it.
 *
 * `'implicit-tenant'` is the same literal `IMPLICIT_TENANT_ID` in `../src/store.ts` supplies to
 * every statement (invariant 51). The column default exists as a safety net only; the store never
 * relies on it; a duplicated literal is the cost of the migration tool and the store having no
 * shared module to import a constant from.
 */
import type { ColumnDefinitions, MigrationBuilder } from "node-pg-migrate";

export const shorthands: ColumnDefinitions | undefined = undefined;

const IMPLICIT_TENANT = "implicit-tenant";
const AUDIENCE_CHECK = "audience in ('player', 'ai')";

export async function up(pgm: MigrationBuilder): Promise<void> {
  pgm.createTable("session", {
    tenant_id: { type: "text", notNull: true, default: IMPLICIT_TENANT },
    session_id: { type: "text", notNull: true },
    blob: { type: "text", notNull: true },
    audience: { type: "text", notNull: true, check: AUDIENCE_CHECK },
    attempt_counter: { type: "integer", notNull: true },
    replay_compatible: { type: "boolean", notNull: true },
    engine_created_at: { type: "text", notNull: true },
    engine_updated_at: { type: "text", notNull: true },
    profile_id: { type: "text", notNull: false },
    version: { type: "bigint", notNull: true },
    engine_version: { type: "text", notNull: true },
    row_created_at: { type: "timestamptz", notNull: true, default: pgm.func("now()") },
    row_updated_at: { type: "timestamptz", notNull: true },
    expires_at: { type: "timestamptz", notNull: true },
  });
  pgm.addConstraint("session", "session_pkey", { primaryKey: ["tenant_id", "session_id"] });
  pgm.createIndex("session", ["expires_at"]);

  pgm.createTable("save", {
    tenant_id: { type: "text", notNull: true, default: IMPLICIT_TENANT },
    save_id: { type: "text", notNull: true },
    campaign_id: { type: "text", notNull: true },
    blob: { type: "text", notNull: true },
    saved_at_seq: { type: "integer", notNull: true },
    audience: { type: "text", notNull: true, check: AUDIENCE_CHECK },
    profile_id: { type: "text", notNull: false },
    engine_version: { type: "text", notNull: true },
    row_created_at: { type: "timestamptz", notNull: true, default: pgm.func("now()") },
    expires_at: { type: "timestamptz", notNull: true },
  });
  pgm.addConstraint("save", "save_pkey", { primaryKey: ["tenant_id", "save_id"] });
  pgm.createIndex("save", ["expires_at"]);

  pgm.createTable("profile", {
    tenant_id: { type: "text", notNull: true, default: IMPLICIT_TENANT },
    profile_id: { type: "text", notNull: true },
    format_version: { type: "integer", notNull: true },
    row_created_at: { type: "timestamptz", notNull: true, default: pgm.func("now()") },
    row_updated_at: { type: "timestamptz", notNull: true },
  });
  pgm.addConstraint("profile", "profile_pkey", { primaryKey: ["tenant_id", "profile_id"] });

  pgm.createTable("profile_achievement", {
    tenant_id: { type: "text", notNull: true, default: IMPLICIT_TENANT },
    profile_id: { type: "text", notNull: true },
    campaign_id: { type: "text", notNull: true },
    achievement_id: { type: "text", notNull: true },
    row_created_at: { type: "timestamptz", notNull: true, default: pgm.func("now()") },
  });
  pgm.addConstraint("profile_achievement", "profile_achievement_pkey", {
    primaryKey: ["tenant_id", "profile_id", "campaign_id", "achievement_id"],
  });
}

export async function down(pgm: MigrationBuilder): Promise<void> {
  pgm.dropTable("profile_achievement");
  pgm.dropTable("profile");
  pgm.dropTable("save");
  pgm.dropTable("session");
}
