/** #353 mixed-version regression: the pre-upgrade adapter advances version but does not know
 * revision. Keep its accepted updates on the same revision sequence as the new adapter, including
 * after a code-only rollback. The new adapter still checks revision in its WHERE predicate before
 * this trigger runs; this cannot turn a rejected new-code update into an accepted one. */
import type { ColumnDefinitions, MigrationBuilder } from "node-pg-migrate";

export const shorthands: ColumnDefinitions | undefined = undefined;

export async function up(pgm: MigrationBuilder): Promise<void> {
  // Repair rows written by legacy code between the preceding migration and this one. Both
  // runtimes start at version 1 / revision 0 and advance once per accepted session write.
  pgm.sql("update session set revision = version - 1 where revision <> version - 1");
  pgm.sql(`
    create function advance_legacy_session_revision() returns trigger language plpgsql as $$
    begin
      new.revision := old.revision + 1;
      return new;
    end;
    $$;
    create trigger session_legacy_revision before update on session
      for each row
      when (new.version = old.version + 1 and new.revision = old.revision)
      execute function advance_legacy_session_revision();
  `);
}

export async function down(pgm: MigrationBuilder): Promise<void> {
  pgm.sql("drop trigger session_legacy_revision on session");
  pgm.sql("drop function advance_legacy_session_revision()");
}
