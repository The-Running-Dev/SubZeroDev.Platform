/**
 * The workload's own declarations. Every signature here is `design/20-contract.md`'s, transcribed
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
import type { EngineErrorCode } from "@subzerodev/service-contract";
import type { SessionStore } from "@the-running-dev/game-engine";

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

// ---------------------------------------------------------------------------- probes and envelope

export type ProbeStatus = "healthy" | "unhealthy";

export interface ProbeResult {
  readonly status: ProbeStatus;
}

export interface ProbeSurface {
  liveness(): ProbeResult;
  readiness(): ProbeResult;
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
  | { readonly kind: "result"; readonly value: CanonicalJson }
  | { readonly kind: "error"; readonly error: WireErrorBody };

export interface McpSurface {
  listTools(): readonly McpToolDescriptor[];
  callTool(name: McpToolName, args: JsonValue): Promise<McpToolOutcome>;
}

// ---------------------------------------------------------------------------- composition

export interface ComposedWorkload {
  readonly store: SessionStore;
  readonly serialization: StoreSerializationHandle;
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
  | { readonly code: "DumpWriteFailed"; readonly path: string };

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
