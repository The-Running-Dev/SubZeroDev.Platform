/**
 * Store — the pool, the SQL, the compare-and-swap, expiry evaluation, the tenant constant, and
 * blob-to-column mapping (`design/10-design.md`, "Module boundaries — the Node workload").
 *
 * Store imports the engine's *type* declarations and never its runtime (invariant 58, S3.14): every
 * import from `@the-running-dev/game-engine` below is `import type`. The conflict brand's spelling
 * is therefore this module's own literal, duplicated rather than imported as a value — the design's
 * own point in choosing a duck-typed `name` brand is that it survives exactly this kind of
 * duplicated copy (`20-contract.md`, "The brand is the `name` property and nothing else").
 */
import { Pool, types as pgTypes } from "pg";
import type { CustomTypesConfig, PoolClient, PoolConfig } from "pg";
import type {
  ProfileLoadResult,
  ProfileSaveResult,
  ProfileStore,
  SessionPersistence,
  SessionPersistenceConflict,
  StoredSaveRecord,
  StoredSessionRecord,
} from "@the-running-dev/game-engine";
import { pgErrorCode, quoteIdentifier } from "./migrations.js";
import { err, ok } from "./types.js";
import type {
  DatabaseInstant,
  DurableStore,
  DurableStoreConfiguration,
  EngineInstant,
  GuardedWriteOutcome,
  LifecycleProbe,
  LifecycleState,
  Outcome,
  ProfileAchievementRow,
  ProfileRow,
  ProfileTerminalRow,
  ProjectionAudience,
  ReadVersionMap,
  SaveRow,
  SemanticVersion,
  SessionRow,
  SessionRowVersion,
  StoreError,
  StoreSerializationHandle,
  StoreSerializationSnapshot,
  SweepResult,
  TenantId,
} from "./types.js";

// int8 (`bigint` in Postgres, OID 20) columns default to a JS `string` in `pg`, to avoid silent
// precision loss for values a JS `number` cannot represent exactly. `session.version` is exactly
// such a column, and the contract requires the actual runtime type to be `bigint` (S3.17) — a
// `string` that merely looks numeric would still compare equal on a round trip while making
// arithmetic on it a concatenation. Scoped to this store's own `Pool` via `pg`'s per-instance
// `types` option, rather than `pgTypes.setTypeParser` — that call mutates the process-wide
// `pg-types` registry, which would silently reparse OID 20 for every other `pg.Pool`/`Client` in
// the process too. Exported so test support applies the identical override to its own raw pools
// instead of depending on `store.ts` having already run a global side effect first.
export const BIGINT_VERSION_TYPES: CustomTypesConfig = {
  getTypeParser: (oid, format) => (oid === 20 ? (value: string) => BigInt(value) : pgTypes.getTypeParser(oid, format)),
};

/** `TenantId` is non-empty and, in G2, is one value — the store supplies this constant to every
 *  statement (invariant 51); nothing resolves it and no request carries one. */
export const IMPLICIT_TENANT_ID = "implicit-tenant" as TenantId;

/** The engine's own brand spelling (`SESSION_PERSISTENCE_CONFLICT` in the engine package),
 *  duplicated here rather than imported as a value — see the module comment. */
const CONFLICT_BRAND = "SessionPersistenceConflict" as const;

/** The degraded-read result `openDurableStore`'s own `profiles.load` returns for a connectivity or
 *  shape failure, and the one `compose.ts`'s `unavailableProfiles()` returns while no durable store
 *  has connected yet — exported so both sites construct it from one definition. */
export function corruptProfileResult(profileId: string): ProfileLoadResult {
  return {
    profile: { formatVersion: 2, profileId, achievements: [], terminals: [] },
    warnings: [{ code: "profile_corrupt", profileId }],
  };
}

/** Same reasoning as `corruptProfileResult` above, for a write failure. */
export function profileWriteFailedResult(profileId: string): ProfileSaveResult {
  return { ok: false, warnings: [{ code: "profile_write_failed", profileId }] };
}

function sleep(ms: number): Promise<void> {
  return ms > 0 ? new Promise((resolve) => setTimeout(resolve, ms)) : Promise.resolve();
}

/** `"insert"` context is what lets a primary-key collision (`23505`) classify as `IdCollision`
 *  rather than an ordinary `StatementFailed` — the distinction the S3.12 caller depends on.
 *
 *  `statement`, when supplied, is attached to a `StatementFailed` classification and to that one
 *  only — the other classifications name a condition rather than a statement, and a connect that
 *  was refused has no statement to name. Supplied by the sweep, whose log line is the whole of its
 *  observability (`10-design.md`, "The sweep fails"); the serving-path callers leave it off,
 *  because there the operation already identifies the statement. */
function classifyStoreError(error: unknown, context: "insert" | "other", statement?: string): StoreError {
  const code = pgErrorCode(error);
  if (context === "insert" && code === "23505") return { code: "IdCollision" };
  if (code === "ECONNREFUSED" || code === "ECONNRESET" || code === "ETIMEDOUT" || code === "EHOSTUNREACH") {
    return { code: "Unreachable" };
  }
  if (error instanceof Error && /connection.*(terminated|closed)/i.test(error.message)) {
    return { code: "Unreachable" };
  }
  // `pg-pool` throws a plain `Error` with no `.code` when a connection acquisition exceeds
  // `connectionTimeoutMillis` (`pg-pool/index.js`, `'timeout exceeded when trying to connect'`) —
  // the contract classifies this as `PoolExhausted`, retryable, distinct from `StatementFailed`.
  if (error instanceof Error && /timeout exceeded when trying to connect/i.test(error.message)) {
    return { code: "PoolExhausted" };
  }
  return statement === undefined ? { code: "StatementFailed" } : { code: "StatementFailed", statement };
}

/** The minimal shape `writeSessionRow` needs from a connection — `pg.Pool` and `pg.PoolClient`
 *  both satisfy it structurally. Exported so a test can wrap a real pool with one that injects a
 *  fault into a specific statement (S3.6's re-read failure) without reimplementing the driver. */
export interface Queryable {
  query<T extends Record<string, unknown> = Record<string, unknown>>(
    text: string,
    values?: readonly unknown[],
  ): Promise<{ rows: T[]; rowCount: number | null }>;
}

/** One per request, and it dies with the request. No database access at all — a plain `Map` —
 *  so it is directly testable in isolation (S3.17's `bigint` assertion needs no live connection). */
export function createReadVersionMap(): ReadVersionMap {
  const versions = new Map<string, SessionRowVersion>();
  return {
    observed: (sessionId) => versions.get(sessionId),
    record: (sessionId, version) => {
      versions.set(sessionId, version);
    },
    advance: (sessionId, version) => {
      versions.set(sessionId, version);
    },
  };
}

/** Carries which of the two write-side classifications produced the throw — additional to, and
 *  inert for, the engine's own `SessionPersistenceConflict` shape, which only ever inspects `name`.
 *  Exported so this slice's own tests can distinguish S3.2 (`conflict`) from S3.3 (`expired`)
 *  without a second throw shape. */
export interface DurableWriteConflict extends SessionPersistenceConflict {
  readonly outcome: "conflict" | "expired";
}

function conflictError(outcome: "conflict" | "expired"): DurableWriteConflict {
  const error = new Error(`durable session write: ${outcome}`);
  Object.defineProperty(error, "name", { value: CONFLICT_BRAND, enumerable: false });
  Object.defineProperty(error, "outcome", { value: outcome, enumerable: false });
  return error as unknown as DurableWriteConflict;
}

/** `saves.delete`'s own brand (engine `04-core.md` §7.4: "signals it the way §7.2 already
 *  established — by branding `SessionPersistenceConflict`"). No `outcome` member —
 *  `deleteSave`'s only two adapter-level outcomes are "removed" and "removed nothing", and the
 *  engine's own `isPersistenceConflict` inspects `name` alone. */
function saveDeleteConflictError(): SessionPersistenceConflict {
  const error = new Error("durable save delete: savedAt mismatch");
  Object.defineProperty(error, "name", { value: CONFLICT_BRAND, enumerable: false });
  return error as unknown as SessionPersistenceConflict;
}

// ---------------------------------------------------------------------------- statement builders
//
// Pure: text and parameters only, no I/O. Exported so `tenant_id`'s presence in every statement
// (invariant 51, S3.9) is asserted by inspecting the generated SQL directly, not inferred from
// query results.

export interface Statement {
  readonly text: string;
  readonly values: readonly unknown[];
}

export function sessionSelectStatement(tenantId: TenantId, sessionId: string): Statement {
  return {
    text:
      "select tenant_id, session_id, blob, audience, attempt_counter, replay_compatible, " +
      "engine_created_at, engine_updated_at, profile_id, version, engine_version, " +
      "row_created_at, row_updated_at, expires_at " +
      "from session where tenant_id = $1 and session_id = $2 and expires_at > now()",
    values: [tenantId, sessionId],
  };
}

export interface SessionRowInput {
  readonly sessionId: string;
  readonly blob: string;
  readonly audience: ProjectionAudience;
  readonly attemptCounter: number;
  readonly replayCompatible: boolean;
  readonly engineCreatedAt: EngineInstant;
  readonly engineUpdatedAt: EngineInstant;
  readonly profileId: string | null;
}

export function sessionInsertStatement(
  tenantId: TenantId,
  row: SessionRowInput,
  engineVersion: SemanticVersion,
  sessionIdleTtlSeconds: number,
): Statement {
  return {
    text:
      "insert into session (tenant_id, session_id, blob, audience, attempt_counter, replay_compatible, " +
      "engine_created_at, engine_updated_at, profile_id, version, engine_version, row_updated_at, expires_at) " +
      "values ($1, $2, $3, $4, $5, $6, $7, $8, $9, 1, $10, now(), now() + make_interval(secs => $11))",
    values: [
      tenantId,
      row.sessionId,
      row.blob,
      row.audience,
      row.attemptCounter,
      row.replayCompatible,
      row.engineCreatedAt,
      row.engineUpdatedAt,
      row.profileId,
      engineVersion,
      sessionIdleTtlSeconds,
    ],
  };
}

/** `engine_created_at` is absent from the `set` list deliberately — it is the engine's `Clock`
 *  output at creation and never changes after (only `sessionInsertStatement` writes it). */
export function sessionGuardedUpdateStatement(
  tenantId: TenantId,
  row: SessionRowInput,
  engineVersion: SemanticVersion,
  sessionIdleTtlSeconds: number,
  observedVersion: SessionRowVersion,
): Statement {
  return {
    text:
      "update session set blob = $3, audience = $4, attempt_counter = $5, replay_compatible = $6, " +
      "engine_updated_at = $7, profile_id = $8, engine_version = $9, version = version + 1, " +
      "row_updated_at = now(), expires_at = now() + make_interval(secs => $10) " +
      "where tenant_id = $1 and session_id = $2 and version = $11 and expires_at > now()",
    values: [
      tenantId,
      row.sessionId,
      row.blob,
      row.audience,
      row.attemptCounter,
      row.replayCompatible,
      row.engineUpdatedAt,
      row.profileId,
      engineVersion,
      sessionIdleTtlSeconds,
      observedVersion,
    ],
  };
}

export function sessionReclassifyStatement(tenantId: TenantId, sessionId: string): Statement {
  return {
    text: "select version, expires_at > now() as live from session where tenant_id = $1 and session_id = $2",
    values: [tenantId, sessionId],
  };
}

export function saveSelectStatement(tenantId: TenantId, saveId: string): Statement {
  return {
    text:
      "select tenant_id, save_id, campaign_id, blob, saved_at, saved_at_seq, audience, profile_id, " +
      "engine_version, row_created_at, expires_at " +
      "from save where tenant_id = $1 and save_id = $2 and expires_at > now()",
    values: [tenantId, saveId],
  };
}

/** §7.4. Every record `saves.listByProfile` holds for one profile — the store (never the
 *  adapter) sorts by `savedAt` descending then `saveId` ascending, so this carries no `order by`. */
export function saveListByProfileStatement(tenantId: TenantId, profileId: string): Statement {
  return {
    text:
      "select tenant_id, save_id, campaign_id, blob, saved_at, saved_at_seq, audience, profile_id, " +
      "engine_version, row_created_at, expires_at " +
      "from save where tenant_id = $1 and profile_id = $2 and expires_at > now()",
    values: [tenantId, profileId],
  };
}

export interface SaveRowInput {
  readonly saveId: string;
  readonly campaignId: string;
  readonly blob: string;
  readonly savedAt: EngineInstant;
  readonly savedAtSeq: number;
  readonly audience: ProjectionAudience;
  readonly profileId: string | null;
}

/** An upsert, not a bare insert: `saves.put` is a re-put-safe port method. Every host column
 *  (`expires_at`, `engine_version`) is recomputed from `excluded` on conflict; `row_created_at`
 *  stays out of the `set` list, so a re-put never overwrites the original creation stamp (S3.10). */
export function saveUpsertStatement(
  tenantId: TenantId,
  row: SaveRowInput,
  engineVersion: SemanticVersion,
  saveTtlSeconds: number,
): Statement {
  return {
    text:
      "insert into save (tenant_id, save_id, campaign_id, blob, saved_at, saved_at_seq, audience, profile_id, " +
      "engine_version, expires_at) " +
      "values ($1, $2, $3, $4, $5, $6, $7, $8, $9, now() + make_interval(secs => $10)) " +
      "on conflict (tenant_id, save_id) do update set " +
      "campaign_id = excluded.campaign_id, blob = excluded.blob, saved_at = excluded.saved_at, " +
      "saved_at_seq = excluded.saved_at_seq, audience = excluded.audience, profile_id = excluded.profile_id, " +
      "engine_version = excluded.engine_version, expires_at = excluded.expires_at",
    values: [
      tenantId,
      row.saveId,
      row.campaignId,
      row.blob,
      row.savedAt,
      row.savedAtSeq,
      row.audience,
      row.profileId,
      engineVersion,
      saveTtlSeconds,
    ],
  };
}

/** Conditional (engine `04-core.md` §7.4): removes `saveId` only while its stored `saved_at`
 *  still equals `expectedSavedAt`. Carries no `expires_at` predicate, the same as the
 *  unconditional delete this replaces — an expired-but-not-yet-swept row is still a real row a
 *  caller who observed its `savedAt` may delete. */
export function saveDeleteStatement(tenantId: TenantId, saveId: string, expectedSavedAt: EngineInstant): Statement {
  return {
    text: "delete from save where tenant_id = $1 and save_id = $2 and saved_at = $3",
    values: [tenantId, saveId, expectedSavedAt],
  };
}

/** Existence only, deliberately not a `saved_at` re-check — see `saves.delete`'s own comment for
 *  why zero rows affected by the conditional delete above always means "still exists but the
 *  precondition failed" whenever this reports one. */
export function saveExistsStatement(tenantId: TenantId, saveId: string): Statement {
  return { text: "select 1 from save where tenant_id = $1 and save_id = $2", values: [tenantId, saveId] };
}

export function sessionLifecycleStatement(tenantId: TenantId, sessionId: string): Statement {
  return {
    text: "select expires_at > now() as live from session where tenant_id = $1 and session_id = $2",
    values: [tenantId, sessionId],
  };
}

export function saveLifecycleStatement(tenantId: TenantId, saveId: string): Statement {
  return {
    text: "select expires_at > now() as live from save where tenant_id = $1 and save_id = $2",
    values: [tenantId, saveId],
  };
}

export function sweepStatements(
  tenantId: TenantId,
  retentionHorizonSeconds: number,
): { readonly sessions: Statement; readonly saves: Statement } {
  return {
    sessions: {
      text: "delete from session where tenant_id = $1 and expires_at < now() - make_interval(secs => $2)",
      values: [tenantId, retentionHorizonSeconds],
    },
    saves: {
      text: "delete from save where tenant_id = $1 and expires_at < now() - make_interval(secs => $2)",
      values: [tenantId, retentionHorizonSeconds],
    },
  };
}

// ---------------------------------------------------------------------------- row mapping

interface RawSessionRow {
  readonly tenant_id: string;
  readonly session_id: string;
  readonly blob: string;
  readonly audience: string;
  readonly attempt_counter: number;
  readonly replay_compatible: boolean;
  readonly engine_created_at: string;
  readonly engine_updated_at: string;
  readonly profile_id: string | null;
  readonly version: bigint;
  readonly engine_version: string;
  readonly row_created_at: Date;
  readonly row_updated_at: Date;
  readonly expires_at: Date;
}

/** S13.1: a `select` succeeding is not itself proof the row is usable — a column whose SQL type
 *  was widened after the migration ran can hold a value the declared TypeScript type cannot. This
 *  is checked once, here, rather than trusted at each of `sessions.get`'s two call sites (the
 *  direct read and the row this module hands back to the engine), and named by column rather than
 *  reported as one opaque failure, per `StoreError.RowUndeserializable`'s contract. */
function firstInvalidSessionColumn(raw: Record<string, unknown>): string | null {
  if (typeof raw["tenant_id"] !== "string") return "tenant_id";
  if (typeof raw["session_id"] !== "string") return "session_id";
  if (typeof raw["blob"] !== "string") return "blob";
  if (typeof raw["audience"] !== "string") return "audience";
  if (typeof raw["attempt_counter"] !== "number") return "attempt_counter";
  if (typeof raw["replay_compatible"] !== "boolean") return "replay_compatible";
  if (typeof raw["engine_created_at"] !== "string") return "engine_created_at";
  if (typeof raw["engine_updated_at"] !== "string") return "engine_updated_at";
  if (raw["profile_id"] !== null && typeof raw["profile_id"] !== "string") return "profile_id";
  if (typeof raw["version"] !== "bigint") return "version";
  if (typeof raw["engine_version"] !== "string") return "engine_version";
  if (!(raw["row_created_at"] instanceof Date)) return "row_created_at";
  if (!(raw["row_updated_at"] instanceof Date)) return "row_updated_at";
  if (!(raw["expires_at"] instanceof Date)) return "expires_at";
  return null;
}

/** Same reasoning as `firstInvalidSessionColumn`, and scoped the same way — to every column
 *  `saveSelectStatement` returns, not merely to the subset `toStoredSaveRecord` maps. `save`'s
 *  guarded-write-free shape (no `version`) means this is the only place a malformed `save` row can
 *  be caught (S13.2), so a selected host column widened out from under its declared type would
 *  otherwise reach no check at all; and the two checkers covering different fractions of their own
 *  selects would read as an oversight rather than a choice.
 *
 *  `tenant_id` and `expires_at` are checked here for the same reason their session counterparts
 *  are, and with the same caveat: the statement's own `tenant_id = $1` and `expires_at > now()`
 *  predicates would fail the query before a widened value could reach this function, so those two
 *  lines are a backstop against a later statement that drops a predicate, not a branch any current
 *  query can take. */
function firstInvalidSaveColumn(raw: Record<string, unknown>): string | null {
  if (typeof raw["tenant_id"] !== "string") return "tenant_id";
  if (typeof raw["save_id"] !== "string") return "save_id";
  if (typeof raw["campaign_id"] !== "string") return "campaign_id";
  if (typeof raw["blob"] !== "string") return "blob";
  if (typeof raw["saved_at"] !== "string") return "saved_at";
  if (typeof raw["saved_at_seq"] !== "number") return "saved_at_seq";
  if (typeof raw["audience"] !== "string") return "audience";
  if (raw["profile_id"] !== null && typeof raw["profile_id"] !== "string") return "profile_id";
  if (typeof raw["engine_version"] !== "string") return "engine_version";
  if (!(raw["row_created_at"] instanceof Date)) return "row_created_at";
  if (!(raw["expires_at"] instanceof Date)) return "expires_at";
  return null;
}

function toSessionRow(raw: RawSessionRow): SessionRow {
  return {
    tenantId: raw.tenant_id as TenantId,
    sessionId: raw.session_id,
    blob: raw.blob,
    audience: raw.audience as ProjectionAudience,
    attemptCounter: raw.attempt_counter,
    replayCompatible: raw.replay_compatible,
    engineCreatedAt: raw.engine_created_at as EngineInstant,
    engineUpdatedAt: raw.engine_updated_at as EngineInstant,
    profileId: raw.profile_id,
    version: raw.version as SessionRowVersion,
    engineVersion: raw.engine_version as SemanticVersion,
    rowCreatedAt: raw.row_created_at as DatabaseInstant,
    rowUpdatedAt: raw.row_updated_at as DatabaseInstant,
    expiresAt: raw.expires_at as DatabaseInstant,
  };
}

/** `profileId: null` on the row maps to an *absent* `profileId` key on the record, never a member
 *  holding `undefined` (invariant 48; `20-contract.md`, "the durable rows"). */
function toStoredSessionRecord(row: SessionRow): StoredSessionRecord {
  return {
    sessionId: row.sessionId,
    blob: row.blob,
    audience: row.audience,
    attemptCounter: row.attemptCounter,
    replayCompatible: row.replayCompatible,
    createdAt: row.engineCreatedAt,
    updatedAt: row.engineUpdatedAt,
    ...(row.profileId !== null ? { profileId: row.profileId } : {}),
  };
}

interface RawSaveRow {
  readonly tenant_id: string;
  readonly save_id: string;
  readonly campaign_id: string;
  readonly blob: string;
  readonly saved_at: string;
  readonly saved_at_seq: number;
  readonly audience: string;
  readonly profile_id: string | null;
  readonly engine_version: string;
  readonly row_created_at: Date;
  readonly expires_at: Date;
}

/** Every column `saveSelectStatement` returns, not merely the subset the engine record carries —
 *  the same raw → row → record path `sessions.get` takes, so the two reads have one shape rather
 *  than two. `firstInvalidSaveColumn` has already checked all ten, which is what makes the casts
 *  below assertions about a validated row rather than hopes about an unvalidated one. */
function toSaveRow(raw: RawSaveRow): SaveRow {
  return {
    tenantId: raw.tenant_id as TenantId,
    saveId: raw.save_id,
    campaignId: raw.campaign_id,
    blob: raw.blob,
    savedAt: raw.saved_at as EngineInstant,
    savedAtSeq: raw.saved_at_seq,
    audience: raw.audience as ProjectionAudience,
    profileId: raw.profile_id,
    engineVersion: raw.engine_version as SemanticVersion,
    rowCreatedAt: raw.row_created_at as DatabaseInstant,
    expiresAt: raw.expires_at as DatabaseInstant,
  };
}

/** `profiles.load`'s own `profile` row — the achievement and terminal halves are separate queries
 *  (see `RawProfileAchievementRow`/`RawProfileTerminalRow` below), not a joined one: a single query
 *  joining `profile_achievement` and `profile_terminal` would cross-product one profile's
 *  achievements with its terminals, which is not what either mirror means. */
interface RawProfileRow {
  readonly tenant_id: string;
  readonly profile_id: string;
  readonly format_version: number;
  readonly row_created_at: Date;
  readonly row_updated_at: Date;
}

/** Same reasoning as `firstInvalidSessionColumn`, and the same caveat: `tenant_id` and `profile_id`
 *  are the statement's own predicates, so those two lines are a backstop against a later statement
 *  that drops one, not a branch any current query can take. */
function firstInvalidProfileColumn(raw: Record<string, unknown>): string | null {
  if (typeof raw["tenant_id"] !== "string") return "tenant_id";
  if (typeof raw["profile_id"] !== "string") return "profile_id";
  if (typeof raw["format_version"] !== "number") return "format_version";
  if (!(raw["row_created_at"] instanceof Date)) return "row_created_at";
  if (!(raw["row_updated_at"] instanceof Date)) return "row_updated_at";
  return null;
}

interface RawProfileAchievementRow {
  readonly tenant_id: string;
  readonly profile_id: string;
  readonly campaign_id: string;
  readonly achievement_id: string;
  readonly row_created_at: Date;
}

/** Every column `profile_achievement`'s own select returns — a dedicated query now, with no left
 *  join to leave `null` when a profile has none, so every field here is checked unconditionally. */
function firstInvalidProfileAchievementColumn(raw: Record<string, unknown>): string | null {
  if (typeof raw["campaign_id"] !== "string") return "campaign_id";
  if (typeof raw["achievement_id"] !== "string") return "achievement_id";
  if (!(raw["row_created_at"] instanceof Date)) return "row_created_at";
  return null;
}

interface RawProfileTerminalRow {
  readonly tenant_id: string;
  readonly profile_id: string;
  readonly campaign_id: string;
  readonly terminal_id: string;
  readonly row_created_at: Date;
}

/** `profile_terminal`'s own select — same shape and same reasoning as
 *  `firstInvalidProfileAchievementColumn` above. */
function firstInvalidProfileTerminalColumn(raw: Record<string, unknown>): string | null {
  if (typeof raw["campaign_id"] !== "string") return "campaign_id";
  if (typeof raw["terminal_id"] !== "string") return "terminal_id";
  if (!(raw["row_created_at"] instanceof Date)) return "row_created_at";
  return null;
}

function toProfileRow(raw: RawProfileRow): ProfileRow {
  return {
    tenantId: raw.tenant_id as TenantId,
    profileId: raw.profile_id,
    formatVersion: raw.format_version,
    rowCreatedAt: raw.row_created_at as DatabaseInstant,
    rowUpdatedAt: raw.row_updated_at as DatabaseInstant,
  };
}

function toProfileAchievementRow(raw: RawProfileAchievementRow): ProfileAchievementRow {
  return {
    tenantId: raw.tenant_id as TenantId,
    profileId: raw.profile_id,
    campaignId: raw.campaign_id,
    achievementId: raw.achievement_id,
    rowCreatedAt: raw.row_created_at as DatabaseInstant,
  };
}

function toProfileTerminalRow(raw: RawProfileTerminalRow): ProfileTerminalRow {
  return {
    tenantId: raw.tenant_id as TenantId,
    profileId: raw.profile_id,
    campaignId: raw.campaign_id,
    terminalId: raw.terminal_id,
    rowCreatedAt: raw.row_created_at as DatabaseInstant,
  };
}

/** Host columns stop here (invariant 48): `tenantId`, `engineVersion`, `rowCreatedAt` and
 *  `expiresAt` are on `SaveRow` and on no `StoredSaveRecord`. `profileId: null` maps to an *absent*
 *  key, never a member holding `undefined` — the same rule `toStoredSessionRecord` applies. */
function toStoredSaveRecord(row: SaveRow): StoredSaveRecord {
  return {
    saveId: row.saveId,
    campaignId: row.campaignId,
    blob: row.blob,
    savedAt: row.savedAt,
    savedAtSeq: row.savedAtSeq,
    audience: row.audience,
    ...(row.profileId !== null ? { profileId: row.profileId } : {}),
  };
}

// ---------------------------------------------------------------------------- the guarded write

/** The compare-and-swap itself (`20-contract.md`, "the guarded write and the lifecycle
 *  classification"). Exported — not part of `DurableStore` — so the three-way classification is
 *  directly testable without going through the engine-facing `SessionPersistence.put`, whose own
 *  signature has no channel to return one on. */
export async function writeSessionRow(
  pool: Queryable,
  tenantId: TenantId,
  engineVersion: SemanticVersion,
  sessionIdleTtlSeconds: number,
  readWritePauseMs: number,
  versions: ReadVersionMap,
  row: SessionRowInput,
): Promise<Outcome<GuardedWriteOutcome, StoreError>> {
  const observed = versions.observed(row.sessionId);

  if (observed === undefined) {
    const statement = sessionInsertStatement(tenantId, row, engineVersion, sessionIdleTtlSeconds);
    try {
      await pool.query(statement.text, statement.values as unknown[]);
    } catch (error) {
      return err(classifyStoreError(error, "insert"));
    }
    versions.record(row.sessionId, 1n as SessionRowVersion);
    return ok("applied");
  }

  // The perturbation seam (`readWritePauseMs`, default `0`): a configured pause between this
  // write's originating read and the guarded statement below, which is what makes the contention
  // races S6/S7 assert deterministic rather than merely likely.
  await sleep(readWritePauseMs);

  const statement = sessionGuardedUpdateStatement(tenantId, row, engineVersion, sessionIdleTtlSeconds, observed);
  let affected: number;
  try {
    const result = await pool.query(statement.text, statement.values as unknown[]);
    affected = result.rowCount ?? 0;
  } catch (error) {
    return err(classifyStoreError(error, "other"));
  }

  if (affected === 1) {
    versions.advance(row.sessionId, (observed + 1n) as SessionRowVersion);
    return ok("applied");
  }

  // Zero rows affected: never assumed, classified by a re-read. A re-read that itself fails is
  // `conflict`, never surfaced as a `StoreError` (S3.6) — zero rows has already established the one
  // fact the caller acts on.
  try {
    const reclassify = sessionReclassifyStatement(tenantId, row.sessionId);
    const { rows } = await pool.query(reclassify.text, reclassify.values as unknown[]);
    if (rows.length === 0) return ok("conflict");
    const current = rows[0] as { version: bigint; live: boolean };
    if (current.version !== observed) return ok("conflict");
    return ok(current.live ? "conflict" : "expired");
  } catch {
    return ok("conflict");
  }
}

// ---------------------------------------------------------------------------- openDurableStore

export async function openDurableStore(
  configuration: DurableStoreConfiguration,
  engineVersion: SemanticVersion,
): Promise<Outcome<DurableStore, StoreError>> {
  const schema = configuration.connection.schema;
  const poolConfig: PoolConfig = {
    connectionString: configuration.connection.connectionString,
    max: configuration.connection.poolSize,
    connectionTimeoutMillis: configuration.connection.connectTimeoutMs,
    types: BIGINT_VERSION_TYPES,
    ...(schema !== null
      ? { options: `-c search_path=${quoteIdentifier(schema as unknown as string)},public` }
      : {}),
  };
  const pool = new Pool(poolConfig);
  // Background errors on an idle client (the server restarting, a dropped connection) must not
  // crash the process — a request that then reaches into a broken pool surfaces its own
  // `Unreachable` classification instead.
  pool.on("error", () => {});

  let probe: PoolClient;
  try {
    probe = await pool.connect();
  } catch {
    await pool.end().catch(() => {});
    return err({ code: "Unreachable" });
  }

  // S3.13: asserted rather than inherited. At `repeatable read` or `serializable` the guarded
  // update raises a serialization failure instead of reporting zero rows, and every conflict would
  // arrive as `storage_failure` — the one criterion this mechanism exists to serve, defeated by
  // configuration. This is the first and, on failure, only statement issued.
  let isolationLevel: string;
  try {
    const { rows } = await probe.query<{ level: string }>(
      "select current_setting('transaction_isolation') as level",
    );
    isolationLevel = String(rows[0]?.level ?? "");
  } catch {
    probe.release();
    await pool.end().catch(() => {});
    return err({ code: "Unreachable" });
  }
  probe.release();

  if (isolationLevel.toLowerCase() !== "read committed") {
    await pool.end().catch(() => {});
    return err({ code: "IsolationLevelUnsupported", isolationLevel });
  }

  const tenantId = IMPLICIT_TENANT_ID;
  const { bounds, readWritePauseMs } = configuration;

  function persistenceForRequest(): SessionPersistence {
    const versions = createReadVersionMap();
    return {
      sessions: {
        get: async (sessionId: string): Promise<StoredSessionRecord | undefined> => {
          const statement = sessionSelectStatement(tenantId, sessionId);
          let result;
          try {
            result = await pool.query(statement.text, statement.values as unknown[]);
          } catch (error) {
            throw new Error("durable session read failed", { cause: classifyStoreError(error, "other") });
          }
          if (result.rows.length === 0) return undefined;
          const rawRow = result.rows[0] as Record<string, unknown>;
          const invalidColumn = firstInvalidSessionColumn(rawRow);
          if (invalidColumn !== null) {
            throw new Error("durable session read failed", { cause: { code: "RowUndeserializable", column: invalidColumn } });
          }
          const row = toSessionRow(rawRow as unknown as RawSessionRow);
          versions.record(sessionId, row.version);
          return toStoredSessionRecord(row);
        },
        put: async (record: StoredSessionRecord): Promise<void> => {
          const input: SessionRowInput = {
            sessionId: record.sessionId,
            blob: record.blob,
            audience: record.audience,
            attemptCounter: record.attemptCounter,
            replayCompatible: record.replayCompatible,
            engineCreatedAt: record.createdAt as EngineInstant,
            engineUpdatedAt: record.updatedAt as EngineInstant,
            profileId: record.profileId ?? null,
          };
          const outcome = await writeSessionRow(
            pool,
            tenantId,
            engineVersion,
            bounds.sessionIdleTtlSeconds,
            readWritePauseMs,
            versions,
            input,
          );
          if (!outcome.ok) {
            throw new Error("durable session write failed", { cause: outcome.error });
          }
          if (outcome.value !== "applied") {
            throw conflictError(outcome.value);
          }
        },
      },
      saves: {
        get: async (saveId: string): Promise<StoredSaveRecord | undefined> => {
          const statement = saveSelectStatement(tenantId, saveId);
          let result;
          try {
            result = await pool.query(statement.text, statement.values as unknown[]);
          } catch (error) {
            throw new Error("durable save read failed", { cause: classifyStoreError(error, "other") });
          }
          if (result.rows.length === 0) return undefined;
          const rawRow = result.rows[0] as Record<string, unknown>;
          const invalidColumn = firstInvalidSaveColumn(rawRow);
          if (invalidColumn !== null) {
            throw new Error("durable save read failed", { cause: { code: "RowUndeserializable", column: invalidColumn } });
          }
          return toStoredSaveRecord(toSaveRow(rawRow as unknown as RawSaveRow));
        },
        put: async (record: StoredSaveRecord): Promise<void> => {
          const input: SaveRowInput = {
            saveId: record.saveId,
            campaignId: record.campaignId,
            blob: record.blob,
            savedAt: record.savedAt as EngineInstant,
            savedAtSeq: record.savedAtSeq,
            audience: record.audience,
            profileId: record.profileId ?? null,
          };
          const statement = saveUpsertStatement(tenantId, input, engineVersion, bounds.saveTtlSeconds);
          try {
            await pool.query(statement.text, statement.values as unknown[]);
          } catch (error) {
            throw new Error("durable save write failed", { cause: classifyStoreError(error, "other") });
          }
        },
        listByProfile: async (profileId: string): Promise<readonly StoredSaveRecord[]> => {
          const statement = saveListByProfileStatement(tenantId, profileId);
          let result;
          try {
            result = await pool.query(statement.text, statement.values as unknown[]);
          } catch (error) {
            throw new Error("durable save list failed", { cause: classifyStoreError(error, "other") });
          }
          return result.rows.map((rawRow) => {
            const invalidColumn = firstInvalidSaveColumn(rawRow as Record<string, unknown>);
            if (invalidColumn !== null) {
              throw new Error("durable save list failed", { cause: { code: "RowUndeserializable", column: invalidColumn } });
            }
            return toStoredSaveRecord(toSaveRow(rawRow as unknown as RawSaveRow));
          });
        },
        // Conditional (engine `04-core.md` §7.4): `saveDeleteStatement`'s own `saved_at = $3`
        // predicate is the compare-and-delete. Zero rows affected is never assumed to be a
        // mismatch — the id may simply never have existed, which `checkSavesDelete` (S13.6)
        // requires stay a no-op — so a zero-rows result is reclassified by existence, the same
        // shape `writeSessionRow`'s own zero-rows re-read takes: still there (so the predicate
        // failed to match) throws the conflict brand; gone (or never was) returns normally.
        delete: async (saveId: string, expectedSavedAt: string): Promise<void> => {
          const statement = saveDeleteStatement(tenantId, saveId, expectedSavedAt as EngineInstant);
          let affected: number;
          try {
            const result = await pool.query(statement.text, statement.values as unknown[]);
            affected = result.rowCount ?? 0;
          } catch (error) {
            throw new Error("durable save delete failed", { cause: classifyStoreError(error, "other") });
          }
          if (affected > 0) return;

          // A re-read that itself fails is classified the same way `writeSessionRow`'s own
          // zero-rows re-read is (S3.6): the conflict brand, never a `StoreError` — the delete's
          // zero rows has already established the one fact a caller acts on (something about the
          // precondition no longer held), and a broken re-read cannot un-establish it.
          try {
            const exists = saveExistsStatement(tenantId, saveId);
            const { rows } = await pool.query(exists.text, exists.values as unknown[]);
            if (rows.length === 0) return;
          } catch {
            throw saveDeleteConflictError();
          }
          throw saveDeleteConflictError();
        },
      },
    };
  }

  const profiles: ProfileStore = {
    async load(profileId: string) {
      // The `profile` row alone — not joined with either mirror below, so a profile with zero
      // achievements or zero terminals is zero rows from those queries, never a row of nulls to
      // filter back out. Every error on this read — including a connectivity failure, which the
      // port gives no channel to distinguish from a shape problem — folds into `profile_corrupt`,
      // per `20-contract.md`'s three-warning vocabulary for `load`.
      let profileRows;
      try {
        profileRows = await pool.query<RawProfileRow>(
          "select tenant_id, profile_id, format_version, row_created_at, row_updated_at " +
            "from profile where tenant_id = $1 and profile_id = $2",
          [tenantId, profileId],
        );
      } catch {
        return corruptProfileResult(profileId);
      }
      if (profileRows.rows.length === 0) {
        return {
          profile: { formatVersion: 2, profileId, achievements: [], terminals: [] },
          warnings: [{ code: "profile_missing", profileId }],
        };
      }
      // S13.3: `ProfileStore.load` has no error channel, so a column widened to hold a value its
      // declared type cannot is folded into `profile_corrupt` here rather than thrown as
      // `StoreError.RowUndeserializable` — that variant exists for ports (`sessions.get`,
      // `saves.get`) that do have one. Checked before the mapping below, so every cast in
      // `toProfileRow` is an assertion about a validated row.
      const firstRow = profileRows.rows[0];
      if (firstRow === undefined || firstInvalidProfileColumn(firstRow as unknown as Record<string, unknown>) !== null) {
        return corruptProfileResult(profileId);
      }

      const profileRow = toProfileRow(firstRow);
      // `formatVersion` migrates 1 → 2, and the migration is total (engine `04-core.md` §7.1: "no
      // field is renamed, removed or re-typed"): a version-1 profile never had a `profile_terminal`
      // row to begin with, so reading it back as `formatVersion: 2` with whatever `terminals` the
      // query below returns (empty, for that profile) *is* the migration — nothing to transform.
      // Any other stored value is a format this release cannot read, and stays `profile_corrupt`.
      if (profileRow.formatVersion !== 1 && profileRow.formatVersion !== 2) {
        return corruptProfileResult(profileId);
      }

      // Two more queries, not one joined with either — see `RawProfileRow`'s own comment: a query
      // joining `profile_achievement` and `profile_terminal` would cross-product one profile's
      // achievements with its terminals. Run together since neither depends on the other.
      let achievementRows, terminalRows;
      try {
        [achievementRows, terminalRows] = await Promise.all([
          pool.query<RawProfileAchievementRow>(
            "select tenant_id, profile_id, campaign_id, achievement_id, row_created_at " +
              "from profile_achievement where tenant_id = $1 and profile_id = $2",
            [tenantId, profileId],
          ),
          pool.query<RawProfileTerminalRow>(
            "select tenant_id, profile_id, campaign_id, terminal_id, row_created_at " +
              "from profile_terminal where tenant_id = $1 and profile_id = $2",
            [tenantId, profileId],
          ),
        ]);
      } catch {
        return corruptProfileResult(profileId);
      }
      if (achievementRows.rows.some((row) => firstInvalidProfileAchievementColumn(row as unknown as Record<string, unknown>) !== null)) {
        return corruptProfileResult(profileId);
      }
      if (terminalRows.rows.some((row) => firstInvalidProfileTerminalColumn(row as unknown as Record<string, unknown>) !== null)) {
        return corruptProfileResult(profileId);
      }

      const achievements = achievementRows.rows
        .map(toProfileAchievementRow)
        .map((row) => ({ campaignId: row.campaignId, achievementId: row.achievementId }));
      const terminals = terminalRows.rows
        .map(toProfileTerminalRow)
        .map((row) => ({ campaignId: row.campaignId, terminalId: row.terminalId }));
      return {
        profile: { formatVersion: 2, profileId, achievements, terminals },
        warnings: [],
      };
    },
    async save(profile) {
      try {
        await pool.query(
          "insert into profile (tenant_id, profile_id, format_version, row_updated_at) values ($1, $2, 2, now()) " +
            "on conflict (tenant_id, profile_id) do update set format_version = 2, row_updated_at = now()",
          [tenantId, profile.profileId],
        );
        // One batched insert via `unnest` per mirror, not one round trip per record.
        if (profile.achievements.length > 0) {
          await pool.query(
            "insert into profile_achievement (tenant_id, profile_id, campaign_id, achievement_id) " +
              "select $1, $2, campaign_id, achievement_id " +
              "from unnest($3::text[], $4::text[]) as achievement(campaign_id, achievement_id) " +
              "on conflict do nothing",
            [
              tenantId,
              profile.profileId,
              profile.achievements.map((achievement) => achievement.campaignId),
              profile.achievements.map((achievement) => achievement.achievementId),
            ],
          );
        }
        if (profile.terminals.length > 0) {
          await pool.query(
            "insert into profile_terminal (tenant_id, profile_id, campaign_id, terminal_id) " +
              "select $1, $2, campaign_id, terminal_id " +
              "from unnest($3::text[], $4::text[]) as terminal(campaign_id, terminal_id) " +
              "on conflict do nothing",
            [
              tenantId,
              profile.profileId,
              profile.terminals.map((terminal) => terminal.campaignId),
              profile.terminals.map((terminal) => terminal.terminalId),
            ],
          );
        }
        return { ok: true, warnings: [] };
      } catch {
        return profileWriteFailedResult(profile.profileId);
      }
    },
  };

  const lifecycle: LifecycleProbe = {
    async session(sessionId: string): Promise<Outcome<LifecycleState, StoreError>> {
      const statement = sessionLifecycleStatement(tenantId, sessionId);
      try {
        const { rows } = await pool.query<{ live: boolean }>(statement.text, statement.values as unknown[]);
        if (rows.length === 0) return ok("absent");
        return ok(rows[0]?.live ? "live" : "expired");
      } catch (error) {
        return err(classifyStoreError(error, "other"));
      }
    },
    async save(saveId: string): Promise<Outcome<LifecycleState, StoreError>> {
      const statement = saveLifecycleStatement(tenantId, saveId);
      try {
        const { rows } = await pool.query<{ live: boolean }>(statement.text, statement.values as unknown[]);
        if (rows.length === 0) return ok("absent");
        return ok(rows[0]?.live ? "live" : "expired");
      } catch (error) {
        return err(classifyStoreError(error, "other"));
      }
    },
  };

  const serialization: StoreSerializationHandle = {
    async snapshot(): Promise<StoreSerializationSnapshot> {
      // `collate "C"` (invariant 83): ordering by a locale-aware collation would make the ordered
      // blob set depend on the database image's locale, which is exactly the failure mode that
      // would present as a byte-identity failure for the wrong reason.
      const [sessions, saves] = await Promise.all([
        pool.query<{ session_id: string; blob: string }>(
          'select session_id, blob from session where tenant_id = $1 order by session_id collate "C"',
          [tenantId],
        ),
        pool.query<{ save_id: string; blob: string }>(
          'select save_id, blob from save where tenant_id = $1 order by save_id collate "C"',
          [tenantId],
        ),
      ]);
      return {
        sessions: sessions.rows.map((row) => ({ id: row.session_id, blob: row.blob })),
        saves: saves.rows.map((row) => ({ id: row.save_id, blob: row.blob })),
      };
    },
  };

  return ok({
    persistenceForRequest,
    profiles,
    lifecycle,
    serialization,
    async check(): Promise<Outcome<void, StoreError>> {
      try {
        await pool.query("select 1");
        return ok(undefined);
      } catch (error) {
        return err(classifyStoreError(error, "other"));
      }
    },
    async sweepOnce(): Promise<Outcome<SweepResult, StoreError>> {
      // S13.4/S13.5: run under `LifecycleBounds.sweepStatementTimeoutMs`, on one checked-out
      // client rather than `pool.query`'s own auto-acquire-per-call — a `set local` inside a
      // transaction is session state, so both deletes must share the connection it was set on.
      // The `finally` release is what keeps a timed-out tick from holding a connection past its
      // own failure — with the pool sized to one, a serving request right after must still succeed.
      const statements = sweepStatements(tenantId, bounds.retentionHorizonSeconds);
      // `pool.connect()` itself is inside the `try`: a connect failure (the store unreachable, the
      // exact condition this tick is most likely to hit) must classify and return, on the same
      // footing as every statement failure below — not escape uncaught, which would also silently
      // stop `compose.ts`'s recursive `scheduleSweep()` from ever being called again (S4.9,
      // invariant 63).
      let client: PoolClient | undefined;
      // Which step is in flight, so a failure names the statement rather than only its class —
      // the design gives this tick's log line the whole of the sweep's observability, and
      // "StatementFailed" alone does not say whether it was the session delete or the save one.
      let inFlight = "connect";
      try {
        client = await pool.connect();
        inFlight = "begin";
        await client.query("begin");
        inFlight = "set local statement_timeout";
        await client.query(`set local statement_timeout = ${Math.trunc(bounds.sweepStatementTimeoutMs)}`);
        inFlight = statements.sessions.text;
        const sessions = await client.query(statements.sessions.text, statements.sessions.values as unknown[]);
        inFlight = statements.saves.text;
        const saves = await client.query(statements.saves.text, statements.saves.values as unknown[]);
        inFlight = "commit";
        await client.query("commit");
        return ok({ sessionsRemoved: sessions.rowCount ?? 0, savesRemoved: saves.rowCount ?? 0 });
      } catch (error) {
        if (client) await client.query("rollback").catch(() => {});
        return err(classifyStoreError(error, "other", inFlight));
      } finally {
        client?.release();
      }
    },
    async close(): Promise<void> {
      await pool.end();
    },
  });
}
