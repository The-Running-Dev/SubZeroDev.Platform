/**
 * The workload's own declarations. Every signature here is `design/g1/20-contract.md`'s, transcribed
 * rather than invented — the contract package owns the artifact's types and re-exports them, so
 * this file declares only what the workload adds on its own side of the boundary.
 */
import type {
  CanonicalJson,
  CorrelationId,
  HttpStatus,
  JsonObject,
  JsonSchemaDocument,
  JsonValue,
  McpToolName,
  OperationId,
  Outcome,
  SchemaRef,
  WireErrorCode,
  WireVersion,
} from "@subzerodev/service-contract";
import type { EngineErrorCode, SemanticVersion } from "@subzerodev/service-contract";
import type { ProfileStore, SessionPersistence, SessionStore, StoredSessionRecord } from "@the-running-dev/game-engine";
export type { SessionStore };

export type {
  CanonicalJson,
  CorrelationId,
  HttpStatus,
  JsonObject,
  JsonSchemaDocument,
  JsonValue,
  McpToolName,
  OperationId,
  Outcome,
  SchemaRef,
  SemanticVersion,
  WireErrorCode,
  WireVersion,
};

/**
 * Produced by request-schema validation and by nothing else — the one type Dispatch accepts, which
 * is what makes "the engine is never reached on a malformed payload" structural.
 *
 * `20-contract.md` declares it among the contract package's types, but the published package does
 * not export it (noted as a finding against S2 rather than fixed here — it is outside this slice).
 * Declaring it beside its only producer keeps the workload honest in the meantime.
 */
export type ValidatedArguments = JsonObject & { readonly __brand: "ValidatedArguments" };

export function ok<T>(value: T): { readonly ok: true; readonly value: T } {
  return { ok: true, value };
}

export function err<E>(error: E): { readonly ok: false; readonly error: E } {
  return { ok: false, error };
}

// ---------------------------------------------------------------------------- configuration

export interface ListenEndpoint {
  readonly host: string;
  readonly port: number;
}

export interface DefaultDeterminismProfile {
  readonly kind: "default";
}

export interface ReplayDeterminismProfile {
  readonly kind: "replay";
  readonly fixedInstant: string;
  readonly dumpPath: string;
}

export type DeterminismProfile = DefaultDeterminismProfile | ReplayDeterminismProfile;

export interface WorkloadConfiguration {
  readonly listen: ListenEndpoint;
  readonly determinism: DeterminismProfile;
  readonly otlpEndpoint: string | null;
  readonly storage: StorageProfile;
}

// ---------------------------------------------------------------------------- request context

export interface RequestContext {
  readonly operation: OperationId;
  readonly wireVersion: WireVersion;
  readonly inboundTraceParent: string | null;
  readonly correlation: CorrelationId;
}

// ---------------------------------------------------------------------------- dispatch

export type DispatchOutcome =
  | { readonly kind: "result"; readonly value: JsonValue }
  | { readonly kind: "error"; readonly code: EngineErrorCode };

export interface Dispatcher {
  invoke(operation: OperationId, args: ValidatedArguments): Promise<DispatchOutcome>;
}

// ---------------------------------------------------------------------------- serialization handle

export interface StoredBlob {
  readonly id: string;
  readonly blob: string;
}

export interface StoreSerializationSnapshot {
  readonly sessions: readonly StoredBlob[];
  readonly saves: readonly StoredBlob[];
}

export interface StoreSerializationHandle {
  snapshot(): Promise<StoreSerializationSnapshot>;
}

// A type alias (not an interface) so it picks up TypeScript's implicit index signature and is
// structurally assignable to `JsonValue` with no cast at the one call site that encodes it.
export type DeterminismDump = {
  readonly sessions: Readonly<Record<string, string>>;
  readonly saves: Readonly<Record<string, string>>;
};

// ---------------------------------------------------------------------------- store: identifiers and constrained values

/**
 * `20-contract.md`, "Workload — the store's own identifiers and constrained values". Not exported
 * by the contract package (same footing as `ValidatedArguments` above) — declared here beside
 * their only owner, Store.
 */
export type TenantId = string & { readonly __brand: "TenantId" };
export type EngineInstant = string & { readonly __brand: "EngineInstant" };
export type DatabaseInstant = Date & { readonly __brand: "DatabaseInstant" };
export type SessionRowVersion = bigint & { readonly __brand: "SessionRowVersion" };
export type SchemaName = string & { readonly __brand: "SchemaName" };

/**
 * The engine's own `"player" | "ai"` union (`core/projection/types.ts`), not re-exported from the
 * package's public entry point at `0.8.0` — derived by indexed access from `StoredSessionRecord`,
 * which is exported, so this stays a binding to the engine's type rather than a re-declared copy.
 */
export type ProjectionAudience = StoredSessionRecord["audience"];

// ---------------------------------------------------------------------------- store: durable rows

/**
 * `20-contract.md`, "Workload — the durable rows". Every row type here is Store's own internal
 * shape; none crosses the port — the adapter maps a row to a `StoredSessionRecord` or
 * `StoredSaveRecord`, which carry engine-owned members only (invariant 48).
 */
export interface SessionRow {
  readonly tenantId: TenantId;
  readonly sessionId: string;
  readonly blob: string;
  readonly audience: ProjectionAudience;
  readonly attemptCounter: number;
  readonly replayCompatible: boolean;
  readonly engineCreatedAt: EngineInstant;
  readonly engineUpdatedAt: EngineInstant;
  readonly profileId: string | null;
  readonly version: SessionRowVersion;
  readonly engineVersion: SemanticVersion;
  readonly rowCreatedAt: DatabaseInstant;
  readonly rowUpdatedAt: DatabaseInstant;
  readonly expiresAt: DatabaseInstant;
}

export interface SaveRow {
  readonly tenantId: TenantId;
  readonly saveId: string;
  readonly campaignId: string;
  readonly blob: string;
  readonly savedAtSeq: number;
  readonly audience: ProjectionAudience;
  readonly profileId: string | null;
  readonly engineVersion: SemanticVersion;
  readonly rowCreatedAt: DatabaseInstant;
  readonly expiresAt: DatabaseInstant;
}

export interface ProfileRow {
  readonly tenantId: TenantId;
  readonly profileId: string;
  readonly formatVersion: number;
  readonly rowCreatedAt: DatabaseInstant;
  readonly rowUpdatedAt: DatabaseInstant;
}

export interface ProfileAchievementRow {
  readonly tenantId: TenantId;
  readonly profileId: string;
  readonly campaignId: string;
  readonly achievementId: string;
  readonly rowCreatedAt: DatabaseInstant;
}

// ---------------------------------------------------------------------------- store: the guarded write and lifecycle

export type GuardedWriteOutcome = "applied" | "conflict" | "expired";

export type LifecycleState = "live" | "expired" | "absent";

export interface LifecycleProbe {
  session(sessionId: string): Promise<Outcome<LifecycleState, StoreError>>;
  save(saveId: string): Promise<Outcome<LifecycleState, StoreError>>;
}

// ---------------------------------------------------------------------------- store: the per-request read-version seam

/** One per request, and it dies with the request (`20-contract.md`, "the store provider and the
 *  per-request seam"). `StoreProvider` itself is S4's — Composition's, not Store's. */
export interface ReadVersionMap {
  observed(sessionId: string): SessionRowVersion | undefined;
  record(sessionId: string, version: SessionRowVersion): void;
  advance(sessionId: string, version: SessionRowVersion): void;
}

/**
 * `20-contract.md`, "Workload — the store provider and the per-request seam". One method, and the
 * two configurations differ only in what it returns: the durable configuration constructs a fresh
 * persistence adapter with an empty `ReadVersionMap` and composes a session layer over it on every
 * call; the in-memory configuration returns G1's single long-lived layer every time. A cache that
 * cannot outlive one request is what makes the compare-and-swap the only concurrency mechanism in
 * the system — a caller is never handed a store that lasts.
 */
export interface StoreProvider {
  forRequest(): SessionStore;
}

// ---------------------------------------------------------------------------- store: configuration

export interface StoreConnection {
  readonly connectionString: string;
  readonly poolSize: number;
  readonly connectTimeoutMs: number;
  readonly schema: SchemaName | null;
}

export interface LifecycleBounds {
  readonly sessionIdleTtlSeconds: number;
  readonly saveTtlSeconds: number;
  readonly retentionHorizonSeconds: number;
  readonly sweepIntervalSeconds: number;
  readonly sweepStatementTimeoutMs: number;
}

/** Generous enough that a proof or test run is never bound by its own TTLs, and comfortably clear
 *  of `ASSUMED_FORWARD_TIMEOUT_SECONDS` for the `retentionHorizonSeconds` check `compose()`
 *  performs (`compose.ts`). The one set of "out of the way" bounds every non-production
 *  `DurableStoreConfiguration` in this codebase is built from. */
export const DEFAULT_LIFECYCLE_BOUNDS: LifecycleBounds = {
  sessionIdleTtlSeconds: 2_592_000,
  saveTtlSeconds: 31_536_000,
  retentionHorizonSeconds: 31_536_000,
  sweepIntervalSeconds: 3600,
  sweepStatementTimeoutMs: 5000,
};

export interface DurableStoreConfiguration {
  readonly connection: StoreConnection;
  readonly bounds: LifecycleBounds;
  readonly readWritePauseMs: number;
}

/** `20-contract.md`, "Workload — configuration". The discriminated union that makes "the in-memory
 *  configuration reaches no database" a type-level fact — no code path holds an in-memory profile
 *  with a connection string. */
export type StorageProfile =
  | { readonly kind: "in-memory" }
  | { readonly kind: "durable"; readonly store: DurableStoreConfiguration };

// ---------------------------------------------------------------------------- store: the sweep

export interface SweepResult {
  readonly sessionsRemoved: number;
  readonly savesRemoved: number;
}

// ---------------------------------------------------------------------------- store & migrations: error types

/**
 * `20-contract.md`, "Store — `StoreError`". The contract states this as a table of variants, not a
 * type declaration — the shape here follows this file's existing discriminated-union idiom (see
 * `CompositionError`, `StartupError`, below), naming each variant's extra field the same way those
 * do ("naming the setting", "naming the migration" become a field carrying that name).
 */
export type StoreError =
  | { readonly code: "Unreachable" }
  | { readonly code: "PoolExhausted" }
  | { readonly code: "IsolationLevelUnsupported"; readonly isolationLevel: string }
  | { readonly code: "StatementFailed" }
  | { readonly code: "IdCollision" }
  | { readonly code: "RowUndeserializable" };

/** `20-contract.md`, "Migrations — `MigrationError`". Same footing as `StoreError` above. */
export type MigrationError =
  | { readonly code: "Unreachable" }
  | { readonly code: "LockTimeout" }
  | { readonly code: "MigrationFailed"; readonly migration: string };

// ---------------------------------------------------------------------------- store: the public surface

/**
 * `20-contract.md`, "Store — workload". `persistenceForRequest` is the only per-request member —
 * the pool, the schema, the profile store, the probe and the serialization handle are all
 * process-lived. Store imports the engine's *type* declarations and never its runtime (invariant 58).
 */
export interface DurableStore {
  persistenceForRequest(): SessionPersistence;
  readonly profiles: ProfileStore;
  readonly lifecycle: LifecycleProbe;
  readonly serialization: StoreSerializationHandle;
  check(): Promise<Outcome<void, StoreError>>;
  sweepOnce(): Promise<Outcome<SweepResult, StoreError>>;
  close(): Promise<void>;
}

// ---------------------------------------------------------------------------- probes and envelope

export type ProbeStatus = "healthy" | "unhealthy";

export interface ProbeResult {
  readonly status: ProbeStatus;
}

export interface ProbeSurface {
  liveness(): ProbeResult;
  /** Asynchronous from G2 on, and that is forced rather than chosen — it evaluates the store on
   *  each probe, so it reports whether the store is usable *now*, not whether it was usable once
   *  (`20-contract.md`, "Workload — readiness"). */
  readiness(): Promise<ProbeResult>;
}

export interface WireErrorBody {
  readonly code: WireErrorCode;
  readonly correlation: CorrelationId;
}

// ---------------------------------------------------------------------------- transport envelope

export type HttpHeaders = ReadonlyMap<string, string>;

export interface WireRequest {
  readonly method: string;
  readonly path: string;
  readonly headers: HttpHeaders;
  readonly body: Uint8Array;
}

export interface WireResponse {
  readonly status: HttpStatus;
  readonly headers: HttpHeaders;
  readonly body: Uint8Array;
}

export interface HttpSurface {
  handle(request: WireRequest): Promise<WireResponse>;
}

// ---------------------------------------------------------------------------- MCP surface

export interface McpToolDescriptor {
  readonly name: McpToolName;
  readonly inputSchema: JsonSchemaDocument;
}

export type McpToolOutcome =
  | { readonly kind: "result"; readonly value: CanonicalJson; readonly correlation: CorrelationId }
  | { readonly kind: "error"; readonly error: WireErrorBody };

export interface McpSurface {
  listTools(): readonly McpToolDescriptor[];
  callTool(name: McpToolName, args: JsonValue, inboundTraceParent: string | null): Promise<McpToolOutcome>;
}

// ---------------------------------------------------------------------------- composition

export interface ComposedWorkload {
  readonly stores: StoreProvider;
  readonly lifecycle: LifecycleProbe;
  readonly serialization: StoreSerializationHandle;
  readiness(): Promise<ProbeResult>;
  close(): Promise<void>;
}

export interface WorkloadProcess {
  readonly listening: ListenEndpoint;
  readonly probes: ProbeSurface;
  shutdown(): Promise<Outcome<void, ShutdownError>>;
}

// ---------------------------------------------------------------------------- error types

export type ContractLoadError =
  | { readonly code: "MalformedArtifact"; readonly member: string }
  | { readonly code: "UnsupportedContractVersion"; readonly found: string; readonly supported: string }
  | { readonly code: "UnmappedErrorCode"; readonly wireErrorCode: string };

export type CompositionError =
  | { readonly code: "EngineVersionMismatch"; readonly contractEngineVersion: string; readonly resolvedEngineVersion: string }
  | { readonly code: "ContentRegistryInvalid"; readonly campaignId: string }
  | { readonly code: "DumpWriteFailed"; readonly path: string }
  | { readonly code: "StorageConfigurationInvalid"; readonly setting: string };

export type SurfaceBuildError =
  | { readonly code: "DuplicateRoute"; readonly first: string; readonly second: string }
  | { readonly code: "DuplicateToolName"; readonly first: string; readonly second: string }
  | { readonly code: "MissingSchema"; readonly operation: string; readonly reference: string }
  | { readonly code: "SchemaCompile"; readonly detail: string };

export type EncodingError =
  | { readonly code: "NonFiniteNumber"; readonly locator: string }
  | { readonly code: "UnsupportedValue"; readonly locator: string };

export type ValidationFailure = { readonly code: "SchemaViolation"; readonly detail: string };

export type StartupError =
  | { readonly code: "ConfigurationInvalid"; readonly setting: string }
  | { readonly code: "ContractLoad"; readonly cause: ContractLoadError }
  | { readonly code: "Composition"; readonly cause: CompositionError }
  | { readonly code: "SurfaceBuild"; readonly cause: SurfaceBuildError }
  | { readonly code: "ListenerBindFailed"; readonly endpoint: ListenEndpoint };

export type ShutdownError = { readonly code: "DumpWriteFailed"; readonly cause: CompositionError };

export type DumpReadError =
  | { readonly code: "DumpAbsent" }
  | { readonly code: "DumpMalformed" };

// ---------------------------------------------------------------------------- proof harness

/**
 * `20-contract.md`'s "Proof harness — fixture, transcript, comparisons" and "test scope" sections.
 * Not exported by the contract package (same footing as `ValidatedArguments` above), so declared
 * here beside the harness that is their only producer and consumer.
 */
export interface ReplayStep {
  readonly operation: OperationId;
  readonly arguments: JsonObject;
}

export interface ReplayFixture {
  readonly campaignId: string;
  readonly seed: string;
  readonly steps: readonly ReplayStep[];
}

export type Transcript = readonly CanonicalJson[];

export interface Divergence {
  readonly locator: string;
  readonly expected: string;
  readonly actual: string;
}

export interface ComparisonResult {
  readonly matched: boolean;
  readonly firstDivergence: Divergence | null;
}

export interface RunResult {
  readonly transcript: Transcript;
  readonly serialization: StoreSerializationSnapshot;
}

export interface HostedTarget {
  readonly baseAddress: string;
  shutdown(): Promise<Outcome<void, ShutdownError>>;
  readDump(): Promise<Outcome<StoreSerializationSnapshot, DumpReadError>>;
}

export type ReplayError =
  | { readonly code: "CoverageIncomplete"; readonly onlyInFixture: readonly string[]; readonly onlyInTable: readonly string[] }
  | { readonly code: "UnknownOperationInFixture"; readonly step: number; readonly operation: string }
  | { readonly code: "StepFailed"; readonly step: number; readonly operation: string; readonly wireErrorCode: string }
  | { readonly code: "TransportFailure"; readonly detail: string }
  | { readonly code: "Composition"; readonly cause: CompositionError }
  | { readonly code: "Shutdown"; readonly cause: ShutdownError }
  | { readonly code: "DumpRead"; readonly cause: DumpReadError };

// ---------------------------------------------------------------------------- proof harness

export interface WorkloadInstance {
  readonly baseAddress: string;
  shutdown(): Promise<Outcome<void, HarnessError>>;
}

export interface TwoInstanceOptions {
  readonly connectionString: string;
  readonly schema: SchemaName;
  readonly readWritePauseMs: readonly [number, number];
}

export type HarnessError =
  | { readonly code: "SchemaCreateFailed"; readonly detail: string }
  | { readonly code: "SchemaDropFailed"; readonly schema: SchemaName; readonly detail: string }
  | { readonly code: "InstanceSpawnFailed"; readonly instance: 0 | 1; readonly detail: string }
  | { readonly code: "InstanceShutdownFailed"; readonly instance: 0 | 1; readonly detail: string };

/** `20-contract.md`, "Proof harness — the durable replay, the two instances, the conformance
 *  suite". A pristine schema per run, created and dropped by the harness — the counting
 *  `RecordIdSource` mints `counting-session-id-0` on every run, so a second run against a dirty
 *  schema is a primary-key violation in the middle of the replay (S8.6). */
export interface RunSchema {
  readonly name: SchemaName;
  drop(): Promise<Outcome<void, HarnessError>>;
}
