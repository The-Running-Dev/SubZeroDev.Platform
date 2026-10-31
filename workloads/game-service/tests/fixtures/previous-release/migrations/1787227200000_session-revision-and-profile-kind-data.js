export const shorthands = undefined;
const IMPLICIT_TENANT = "implicit-tenant";
export async function up(pgm) {
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
export async function down(pgm) {
    pgm.dropTable("profile_kind_data");
    pgm.dropColumn("save", "initialization");
    pgm.dropColumn("session", ["revision", "initialization"]);
}
