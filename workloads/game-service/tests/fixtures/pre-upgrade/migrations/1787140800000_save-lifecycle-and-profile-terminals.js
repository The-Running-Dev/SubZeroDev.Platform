export const shorthands = undefined;
const IMPLICIT_TENANT = "implicit-tenant";
const EPOCH_INSTANT = "1970-01-01T00:00:00.000Z";
export async function up(pgm) {
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
export async function down(pgm) {
    pgm.dropTable("profile_terminal");
    pgm.dropIndex("save", ["tenant_id", "profile_id"]);
    pgm.dropColumn("save", "saved_at");
}
