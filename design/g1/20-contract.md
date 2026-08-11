# Contract — one session, over the wire, then the edge (G1)

**Document status:** Contract. Derived from [`10-design.md`](10-design.md). Authoritative for the
artifacts and modules it describes; [`00-brief.md`](00-brief.md) stays authoritative for scope and
non-goals, and [`platform-identity.md`](../../docs/docs/platform-identity.md) for what this repository
is.

Two languages, because G1 has two processes. **TypeScript** with `strict` for the contract package,
its generator, and the Node workload. **C#** with nullable reference types enabled for the .NET
edge, which composes on the types [`d3/20-contract.md`](../d3/20-contract.md) already declares and
declares nothing of its own that duplicates one.

Types and signatures only. No package names and no namespace declarations — the contract package's
identity is [Unresolved 4](#unresolved), and the edge's placement is
[`90-decisions.md`](90-decisions.md)'s.

> **This contract depends on one engine change, decided 2026-08-08.** The design states that session
> and save ids are minted by the engine's `IdSource` port; the engine mints them with
> `crypto.randomUUID()` behind no seam, which made the response comparison unachievable for three
> rows. G1 adds the seam — see [*The engine seam G1 adds*](#the-engine-seam-g1-adds) — and
> [Unresolved 1](#unresolved) records what was blocked and what unblocked it.

---

## Types

### Contract package — identifiers and constrained values

```ts
export type OperationId = string & { readonly __brand: "OperationId" };

export type StoreMethodName = string & { readonly __brand: "StoreMethodName" };

export type McpToolName = string & { readonly __brand: "McpToolName" };

export type HttpPathSegment = string & { readonly __brand: "HttpPathSegment" };

export type WireVersion = string & { readonly __brand: "WireVersion" };

export type SchemaRef = string & { readonly __brand: "SchemaRef" };

export type SemanticVersion = string & { readonly __brand: "SemanticVersion" };

export type CanonicalJson = string & { readonly __brand: "CanonicalJson" };

export type CorrelationId = string & { readonly __brand: "CorrelationId" };
```

**Invariants carried by these types, not by their callers.** `OperationId` is non-empty, lowercase
kebab-case, and unique within one table. `StoreMethodName` is a member name of the engine's exported
`SessionStore` interface at the contract's recorded engine version — the arity gate is what makes
that true, and it is the reason this is a branded string rather than `keyof SessionStore`: the
published artifact carries no type dependency on the engine. `McpToolName` is lowercase snake_case
and unique within one table. `HttpPathSegment` is derived, and equals its row's `OperationId`
verbatim — the operation id is already the path's spelling, so the mechanical derivation is
identity, and any other rule would be a second name for one thing. `WireVersion` matches `v` followed
by a positive decimal integer with no leading zero. `SchemaRef` is an absolute `https` URL whose path
contains the contract's major version; **it is an identifier and is never dereferenced**, at build
time or at run time. `SemanticVersion` is a complete `MAJOR.MINOR.PATCH` with optional pre-release.
`CanonicalJson` is the output of `canonicalEncode` and nothing else. `CorrelationId` is 32 lowercase
hexadecimal characters, never all-zero — the same constraint
[`d3/20-contract.md`](../d3/20-contract.md) puts on Platform's own, so the two processes name one value
the same way.

```ts
export type JsonPrimitive = string | number | boolean | null;

export type JsonValue = JsonPrimitive | readonly JsonValue[] | JsonObject;

export interface JsonObject {
  readonly [member: string]: JsonValue;
}
```

**`JsonValue` is the widest type anything on the wire may hold**, and it is closed: no `unknown`, no
`any`, no `object`. A value the engine returns that is not a `JsonValue` cannot be encoded, and
`canonicalEncode` rejects it rather than coercing it.

```ts
export type ValidatedArguments = JsonObject & { readonly __brand: "ValidatedArguments" };
```

**`ValidatedArguments` is produced by request-schema validation and by nothing else.** It is the one
type Dispatch accepts, which is what makes "the engine is never reached on a malformed payload"
structural rather than a sequencing convention.

### Contract package — error codes and status

```ts
export type EngineErrorCode = string & { readonly __brand: "EngineErrorCode" };

export type TransportErrorCode =
  | "malformed_payload"
  | "unsupported_version"
  | "unknown_operation";

export type WireErrorCode = EngineErrorCode | TransportErrorCode;

export type HttpStatus = 200 | 400 | 404 | 409 | 500 | 503;
```

**`EngineErrorCode` is branded rather than enumerated, and that is the only way to state it once.**
The closed set is the engine's `SessionStoreErrorCode`, declared in the engine and re-declared
nowhere: a union copied here would be a second home for the engine's own vocabulary, and the
error-coverage gate exists precisely because a copy cannot be trusted to stay equal. What the
contract owns is the *mapping's* completeness against that set, asserted at generation.

**`TransportErrorCode` is closed and is the contract's own**, because no engine concept corresponds
to any of the three. The fourth code the design describes — the generic code on an unhandled
failure — is unnamed by the design and is [Unresolved 2](#unresolved); so are the edge's two.
Until 2 resolves, `WireErrorCode` cannot represent the `InternalFailure` body's `code`: naming the
workload's generic code makes it this union's fourth member, and invariant 2 and S2.3's gate track
the union's membership rather than a count, so the mapping gains that code's `500` entry in the
same change. The edge's two are `EdgeError`'s own — their statuses are fixed in its table, and
they enter neither this union nor the mapping.

**`HttpStatus` is the closed set the workload can return.** The edge adds `503` and `504` on its own
account and returns nothing else the workload did not produce.

```ts
export interface StatusMappingEntry {
  readonly code: WireErrorCode;
  readonly status: HttpStatus;
}

export interface StatusMapping {
  readonly entries: readonly StatusMappingEntry[];
}
```

**There is no default branch and no fallback entry**, and the type is a list rather than a partial
record for that reason: a lookup that misses is a failed gate, not a `500`.

### Contract package — the operation table

```ts
export type NarrowingSide = "request" | "response";

export interface NarrowedField {
  readonly side: NarrowingSide;
  readonly field: string;
}

export interface AuthoredRow {
  readonly operation: OperationId;
  readonly storeMethod: StoreMethodName;
  readonly mcpTool: McpToolName;
  readonly narrowings: readonly NarrowedField[];
  readonly reachableErrors: readonly WireErrorCode[];
}

export interface OperationRow extends AuthoredRow {
  readonly httpPath: HttpPathSegment;
  readonly requestShape: SchemaRef;
  readonly responseShape: SchemaRef;
}
```

**The two interfaces are the authored/derived split made structural.** `AuthoredRow` is what a
human writes and reviews; `OperationRow` is what generation emits, and the three added members are
exactly the derived ones. Nothing can author a `requestShape`, and nothing can derive an `mcpTool`.

**`NarrowedField.field` names a top-level member** of the store method's argument object or of its
result — the design's two worked examples, `audience` dropped from the request and `savedAtSeq`
dropped from the response, are both top-level, and a nested narrowing is not implied by anything the
design says. A narrowing naming a member the engine's declaration does not have fails generation.

**A row's narrowings are the table's and both surfaces inherit them.** There is no per-surface
narrowing type, and its absence is the contract's expression of the design's second decision.

### Contract package — the generated schema set

```ts
export type SchemaDialect = string & { readonly __brand: "SchemaDialect" };

export interface JsonSchemaDocument {
  readonly $id: SchemaRef;
  readonly $schema: SchemaDialect;
  readonly [keyword: string]: JsonValue | undefined;
}
```

**Every response schema is closed** — `additionalProperties` is `false` at every object level — and
no response schema resolves to the engine's envelope type. Both are asserted at generation, and
together they are the static half of the projection-boundary gate.

**Every request schema is closed on the same terms**, asserted at generation. A request member the
row's shape does not declare is a `malformed_payload`, never a tolerated extra — which is what makes
a request narrowing irreversible from the wire (a dropped `audience` cannot be re-supplied) and the
determinism profile unreachable by any caller.

**The dialect is one value for the whole set**, and which value it is is
[Unresolved 3](#unresolved).

### Contract package — the artifact

```ts
export interface ContractPackage {
  readonly contractVersion: SemanticVersion;
  readonly engineVersion: SemanticVersion;
  readonly wireVersion: WireVersion;
  readonly operations: readonly OperationRow[];
  readonly schemas: readonly JsonSchemaDocument[];
  readonly statusMapping: StatusMapping;
}
```

**`engineVersion` is the exact version the schemas were projected from**, and it is what the
workload's startup assertion compares against the engine package it actually resolved. It is a
member of the artifact rather than a build annotation because a reader must be able to answer "which
engine does this contract describe?" from the artifact alone.

**One `wireVersion` per artifact.** Serving two at once is a binding non-goal; the member exists so
the path prefix has a single stated source, not so a set can grow.

### The engine seam G1 adds

```ts
export interface RecordIdSource {
  newSessionId(): string;
  newSaveId(): string;
}
```

**Declared in the engine and supplied by the host**, as an optional member of the session layer's
composition root alongside `clock`, `persistence` and `profiles`. Omitted, the engine's present
behaviour is unchanged: `crypto.randomUUID()` for both.

**It is permitted by the engine's own rule** — a host may supply anything that cannot change
`serialize()` output, and a session id and a save id never enter `GameState`, which is the engine's
own stated reason for minting them where it does. It is a second port beside `IdSource` rather than a
widening of it, because `IdSource` supplies `gameId` and `seed`, which *are* serialized inputs, and
one port covering both would put two categories behind one name.

**This is a G1 deliverable into the engine**, and the second one — the coverage-checklist column is
the other. Without it the Stage 1 byte-identity criterion is unachievable rather than merely hard.

### Workload — configuration and the determinism profile

```ts
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
```

**The discriminated union is what makes "with the default profile, no dump is written" a type-level
fact.** `dumpPath` exists only on the replay member, so no code path holds a default profile with a
dump path, and the assertion the design demands is over a value that cannot be constructed the wrong
way.

**`fixedInstant` is what the replay profile's `Clock.now()` returns**, unchanging, as an ISO-8601
instant. It reaches only the host-owned record fields, which the dump excludes — so it constrains
neither comparison and exists to keep the run free of a wall clock rather than to be compared.

**The replay profile has no counting-`IdSource` start value**, and the design's "from a stated start"
is not satisfiable: the engine's exported `createCountingIds()` takes no argument and counts from
zero. The replay profile supplies that source unchanged, and a counting `RecordIdSource` on the same
terms — independent counters, each from zero, no argument. Two fixtures that count from different
starts prove nothing a single start does not, and a start value is one more thing two runs can
disagree about.

**`otlpEndpoint` is nullable and null is normal.** Null means no exporter is constructed and no
outbound connection is attempted — not a disabled exporter, and not a default endpoint.

### Workload — request context

```ts
export interface RequestContext {
  readonly operation: OperationId;
  readonly wireVersion: WireVersion;
  readonly inboundTraceParent: string | null;
  readonly correlation: CorrelationId;
}
```

**`correlation` is derived and never supplied.** It is the trace-id of the adopted-or-minted trace
context; a malformed `inboundTraceParent` yields a fresh root and a fresh correlation, and never a
failed request. Nothing on this type is persisted, and nothing on it reaches a session record.

**The MCP surface builds the same context without a version path.** A tool call carries no
`/v<n>/` segment, so `wireVersion` is the artifact's own `wireVersion` — the contract carries
exactly one, which is what makes the assignment a lookup rather than a negotiation. `operation` is
the resolved row's `operation`, not the requested tool name — the two differ for the three rows
whose `mcpTool` is a deliberate rename, so only `callTool`, which has looked the row up, can set it.
`correlation` is derived by the same rule as the JSON wire, from the trace context adopted or minted
for the MCP request. **`callTool` takes the raw `inboundTraceParent` the MCP HTTP transport carried**
— the one piece of the context the transport holds and `callTool` does not — and derives
`correlation` from it the same way `HttpSurface.handle` derives its own from the `WireRequest` it
receives; the rest of the context is `callTool`'s own to build once the row is resolved.

### Workload — dispatch

```ts
export type DispatchOutcome =
  | { readonly kind: "result"; readonly value: JsonValue }
  | { readonly kind: "error"; readonly code: EngineErrorCode };

export interface Dispatcher {
  invoke(operation: OperationId, args: ValidatedArguments): Promise<DispatchOutcome>;
}
```

**`DispatchOutcome` carries no status, no headers and no encoding**, and that absence is what makes
the MCP surface a second consumer rather than a second wire.

**`value` is already projected to the row's response shape.** Projection is Dispatch's, not each
surface's — if it were each surface's, the row's narrowings would be applied twice and "MCP inherits
the wire's narrowings" would be a convention instead of a mechanism.

**The error arm carries an `EngineErrorCode` only.** A transport code cannot originate in Dispatch,
because everything the three transport codes describe is decided before Dispatch is entered.

### Workload — the store-serialization handle

```ts
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

export interface DeterminismDump {
  readonly sessions: Readonly<Record<string, string>>;
  readonly saves: Readonly<Record<string, string>>;
}
```

**`blob` is the engine's canonical serialization and nothing around it** — no `createdAt`, no
`updatedAt`, no `attemptCounter`, no `audience`, no `profileId`, no `savedAtSeq`. The host-owned
record fields are excluded because they are outside the engine's serialization boundary, not because
they are noisy.

**`DeterminismDump` is keyed by id** and is written with `canonicalEncode`, whose key ordering is
what makes "in id order" a property of the encoding rather than a step the writer must remember.

**Neither surface's module graph may name `StoreSerializationHandle`.** That is asserted as a
dependency-direction test, and it is the structural half of the projection-boundary gate.

### Workload — probes and the error envelope

```ts
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
```

**`WireErrorBody` has two members and the design determines exactly these two** — the same envelope
discipline Platform applies on its own side, so the two hops do not disagree about what an error body
may contain. **Never exception text and never payload content.** The detail goes to the log line the
correlation identifies.

**The workload's liveness does not consult the store**, and its readiness reports healthy once both
surfaces are built and the listener is bound.

### Workload — the transport envelope

```ts
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
```

**Bodies are bytes on both sides.** A response the surface produced as `CanonicalJson` is the bytes
of that string; nothing between the encoder and the socket re-encodes, because comparison B is a byte
comparison and a re-encoding would be invisible until it broke it.

### Proof harness — fixture, transcript, comparisons

```ts
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
```

**`Transcript` is a list of encoded values and carries no status.** Run 1 has no HTTP status to
carry, and both runs are asserted against one golden file — so a status member would make the two
transcripts structurally different things that happen to be compared.

**`ReplayFixture.steps` covers every row in the table**, asserted by the harness rather than by
inspection: the set of operations the steps name equals the set the table declares.

**`ReplayFixture` carries no counting-`IdSource` start value**, for the reason given with the replay
profile.

**`ReplayStep.arguments` is literal, including ids.** A step following `create-session` names the
session id that call returned, written out in the fixture — which is possible only because the
replay profile's `RecordIdSource` makes it the same string in every run. A fixture that captured ids
at run time would be a harness that reproduces itself rather than a committed input two runs share.

### Edge — options, forwarding, and readiness

```csharp
public sealed record GameEdgeOptions
{
    public required Uri WorkloadBaseAddress { get; init; }
    public required TimeSpan ForwardTimeout { get; init; }
    public required TimeSpan LivenessTimeout { get; init; }
}

public sealed record ForwardedRequest(
    HttpMethod Method,
    string PathAndQuery,
    ReadOnlyMemory<byte> Body,
    string? ContentType,
    TraceContext Trace);

public sealed record ForwardedResponse(
    int StatusCode,
    ReadOnlyMemory<byte> Body,
    string? ContentType);
```

**`ForwardedRequest` carries no operation id and no parsed body**, and that is the whole of "the edge
does not know which operation it is carrying". `PathAndQuery` is forwarded unaltered; the edge
rewrites nothing.

**`ForwardedResponse.Body` is bytes and is returned unaltered.** Stage 2 asserts against the same
golden transcript Stage 1 does, so any re-encoding at the edge fails it.

**`Trace` is the ambient scope's `TraceContext`**, read from `IOperationScopeAccessor` and written to
the outbound `traceparent` by the forwarder. The edge sets the header itself because Platform's
Observability package deliberately does not wire HttpClient instrumentation; there is consequently no
client-side span for the hop, and no member here to carry one.

---

## Persisted schemas

**There is no database, no table and no collection.** Sessions and saves live in the workload's
process memory and are lost on restart, by design. Nothing in G1 survives a process, and the absence
is the brief's non-goal rather than an omission — so there is no schema to migrate and no existing
data for a migration to act on.

Five files carry state across a process boundary. Each is listed with what happens to an existing
one when it changes.

| Artifact | Written by | Read by | Migration story |
|---|---|---|---|
| **The contract package** | The generator, in the contract repository | The workload, at startup | Published under its own semantic version and pinned by the workload. A regeneration produces a new version; an existing one is never rewritten, which is what a version-pathed `$id` exists to guarantee. Does not reach `1.0.0` before its generator has rejected something. |
| **The authored row set** | A human, in the contract repository | The generator | Reviewed as a diff. Adding a row is additive; removing one is a contract major version, because a pinned consumer's routes would disappear. An engine version bump with no matching row edit fails the arity gate, so the row set cannot silently fall behind. |
| **The replay fixture** | A human, committed | Both runs of the proof | Committed, never generated per run. A change to it invalidates the golden transcript, and the two are regenerated and reviewed in one change or the suite goes red — which is the intended coupling, not a hazard. |
| **The golden transcript** | The proof, regenerated deliberately | Both comparisons | Committed. Regenerated only as an explicit act and reviewed as a diff; **never rewritten by a passing test.** A regeneration that changes bytes is a change to the projection and is reviewed as one. |
| **The determinism dump** | The workload, at graceful shutdown, replay profile only | The harness, once | Ephemeral. Overwritten each run, never committed, never read by anything but the harness in the same run. With the default profile it is not written at all, and a test asserts that. |

**None of these is reachable by a caller.** The dump in particular is a file written by a non-default
startup profile, is not an endpoint, and no route names it.

---

## Public signatures

Internal helpers are out of scope. Everything below crosses a module boundary named in the design.

### The generator — contract repository

```ts
export interface GenerationInput {
  readonly engineVersion: SemanticVersion;
  readonly contractVersion: SemanticVersion;
  readonly wireVersion: WireVersion;
  readonly rows: readonly AuthoredRow[];
  readonly statusMapping: StatusMapping;
}

export function generate(
  input: GenerationInput,
): Promise<Outcome<ContractPackage, GenerationError>>;
```

**`generate` is the only entry point**, and every gate the design names runs inside it: arity, error
coverage, closed request and response schemas, no response schema resolving to the envelope type, and
no row carrying the determinism profile. A gate failure returns a `GenerationError` and emits no
artifact — there is no partial output for a build step to pick up.

### Contract — workload

```ts
export function loadContract(
  source: Uint8Array,
): Outcome<ContractPackage, ContractLoadError>;

export function findRow(
  contract: ContractPackage,
  operation: OperationId,
): OperationRow | null;

export function statusFor(
  contract: ContractPackage,
  code: WireErrorCode,
): Outcome<HttpStatus, ContractLoadError>;
```

**`statusFor` returns an `Outcome` rather than a status with a fallback.** A code with no mapping is
a defect the generation gate should already have caught, and the one thing it must not become is a
`500` nobody attributes.

**`findRow` returns `null` rather than failing.** An unmatched segment is `unknown_operation`, which
the caller raises with the correlation it already holds; a result type here would be two ways to say
one thing.

### Composition — workload

```ts
export interface ComposedWorkload {
  readonly store: SessionStore;
  readonly serialization: StoreSerializationHandle;
}

export function compose(
  configuration: WorkloadConfiguration,
  contract: ContractPackage,
): Promise<Outcome<ComposedWorkload, CompositionError>>;

export function writeDeterminismDump(
  composed: ComposedWorkload,
  profile: ReplayDeterminismProfile,
): Promise<Outcome<void, CompositionError>>;
```

**`compose` owns the engine-version assertion**, which is why it takes the contract at all — it uses
nothing else from it. A mismatch returns `EngineVersionMismatch` and no store is built.

**`ComposedWorkload` exposes the serialization handle and the surfaces do not receive it.** The two
statements are the same statement: the value exists on this type and is passed to the shutdown writer
and to the harness, and to nothing that builds a route.

**`writeDeterminismDump` takes a `ReplayDeterminismProfile`, not a `DeterminismProfile`.** It cannot
be called with the default profile, so "with the default profile, nothing is written" is enforced by
the signature and asserted by a test rather than left to a branch.

### Dispatch — workload

```ts
export function createDispatcher(
  contract: ContractPackage,
  store: SessionStore,
): Dispatcher;
```

**Dispatch takes the store, never the composition**, so it has no path to the serialization handle.
It holds no game logic: it does not retry, does not reinterpret a code, does not decide which actions
are available, and caches nothing.

### HTTP surface — workload

```ts
export interface HttpSurface {
  handle(request: WireRequest): Promise<WireResponse>;
}

export function buildHttpSurface(
  contract: ContractPackage,
  dispatcher: Dispatcher,
): Outcome<HttpSurface, SurfaceBuildError>;

export function canonicalEncode(value: JsonValue): Outcome<CanonicalJson, EncodingError>;

export function validateRequest(
  contract: ContractPackage,
  row: OperationRow,
  body: JsonValue,
): Outcome<ValidatedArguments, ValidationFailure>;

export function validateResponse(
  contract: ContractPackage,
  row: OperationRow,
  value: JsonValue,
): Outcome<void, ValidationFailure>;
```

**`buildHttpSurface` returns an `Outcome`, and it runs before the listener binds.** A table the
service cannot satisfy fails startup rather than producing a route that fails on first use.

**`validateResponse` runs on every response, including in the replay run.** Generation proves the
schema describes the type; it does not prove the handler returned that type unaltered. The schema is
closed, so an added field is a failure rather than a tolerated extra.

**`canonicalEncode` is the wire's only encoder.** Its rule is the engine's canonical serialization
rule: JSON, object members ascending by code unit, no insignificant whitespace, members whose value
is `undefined` omitted, and non-finite numbers rejected rather than coerced.

### MCP surface — workload

```ts
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

export function buildMcpSurface(
  contract: ContractPackage,
  dispatcher: Dispatcher,
): Outcome<McpSurface, SurfaceBuildError>;
```

**`listTools()` has exactly as many entries as the table has rows**, which is the engine's own
standard for this class of claim and is checkable by counting. There is no tool that is not a row and
no row that is not a tool.

**`callTool` validates against the same request schema and calls the same `Dispatcher`.** It takes no
row-specific argument type, because an MCP-specific path is precisely what must not exist.

**`callTool` takes the raw `inboundTraceParent`, derives one `correlation` from it, and both outcome
arms carry that correlation** — the result arm on `correlation` directly, the error arm inside
`WireErrorBody` as every error body already does. A successful tool call is a response like any
other under invariant 29. `callTool` derives, rather than receives, the rest of the `RequestContext`:
`operation` from the row `name` resolves to, `wireVersion` from the contract, `correlation` from
`inboundTraceParent` — none of it is the transport's to supply, because none of it is knowable before
the row lookup `callTool` alone performs.

### Probes and process lifecycle — workload

```ts
export interface WorkloadProcess {
  readonly listening: ListenEndpoint;
  readonly probes: ProbeSurface;
  shutdown(): Promise<Outcome<void, ShutdownError>>;
}

export function startWorkload(
  configuration: WorkloadConfiguration,
): Promise<Outcome<WorkloadProcess, StartupError>>;
```

**`startWorkload` performs the design's startup order and returns only after the listener is bound.**
Configuration, contract load, version assertion, composition, both surfaces, then bind.

**`shutdown` is where the dump is written**, under the replay profile only, before the listener stops
accepting. A failed write is a `ShutdownError` and is reported; it does not become a silent absence
the harness reads as an empty dump.

### Proof harness — test scope

```ts
export interface HostedTarget {
  readonly baseAddress: string;
  shutdown(): Promise<Outcome<void, ShutdownError>>;
  readDump(): Promise<Outcome<StoreSerializationSnapshot, DumpReadError>>;
}

export function runInProcess(
  fixture: ReplayFixture,
  contract: ContractPackage,
): Promise<Outcome<RunResult, ReplayError>>;

export function runHosted(
  fixture: ReplayFixture,
  target: HostedTarget,
): Promise<Outcome<RunResult, ReplayError>>;

export function compareSerializations(
  expected: StoreSerializationSnapshot,
  actual: StoreSerializationSnapshot,
): ComparisonResult;

export function compareTranscripts(
  expected: Transcript,
  actual: Transcript,
): ComparisonResult;

export function readDeterminismDump(
  contents: Uint8Array,
): Outcome<StoreSerializationSnapshot, DumpReadError>;
```

**Both comparisons are byte comparisons and neither normalizes.** `compareSerializations` compares
`blob` strings; `compareTranscripts` compares encoded strings. Neither takes an options parameter,
and the absence of one is deliberate — an ignore-list is how a byte-identity suite stops comparing
anything.

**`runInProcess` composes the engine and store directly and drives them through a `Dispatcher`.** It
does not call the store's methods itself: the row's projection and canonical encoding are the same
code both runs use, and only the transport differs. A run that called the store directly would
diverge from the hosted run on `save-game` before any determinism defect could.

**`HostedTarget` is what makes one harness serve both stages.** `baseAddress` is the workload in
Stage 1 and the edge in Stage 2; `shutdown` and `readDump` address the workload in both, because the
dump is a file the workload writes rather than a value read out of its memory. That separation is the
whole reason the dump was paid for.

**`runHosted` sends strictly sequentially**, each response fully read before the next request. It
exposes no concurrency option — pipelining would let two actions reach one session in an order the
fixture did not specify, and the failure would present as a byte-identity break in a harness that
caused it.

### The edge — .NET

```csharp
public interface IGameWorkloadForwarder
{
    Task<Result<ForwardedResponse, EdgeError>> ForwardAsync(
        ForwardedRequest request,
        CancellationToken cancellationToken);
}

public interface IGameWorkloadProbe
{
    Task<Result<EdgeError>> ProbeLivenessAsync(CancellationToken cancellationToken);
}

public sealed class GameWorkloadReadinessCheck : IHealthCheck
{
    public GameWorkloadReadinessCheck(IGameWorkloadProbe probe, GameEdgeOptions options);

    public HealthCheckName Name { get; }
    public HealthCheckKind Kind { get; }
    public HealthCheckCriticality Criticality { get; }
    public TimeSpan Timeout { get; }
    public bool TouchesExternalDependency { get; }

    public Task<HealthCheckResult> CheckAsync(CancellationToken cancellationToken);
}

public static class GameEdgeEndpointExtensions
{
    public static IEndpointRouteBuilder MapGameWorkloadForwarding(
        this IEndpointRouteBuilder endpoints);
}
```

**`Kind` is `Readiness`, `Criticality` is `Required`, and `TouchesExternalDependency` is `true`.**
The last is what makes Platform reject this check as liveness at registration, which is the
structural form of "liveness does not depend on the workload".

**`Required` is the decision the brief demands, made in a property.** An unhealthy required check
produces an unhealthy aggregate and therefore not-ready. `Optional` would produce `Degraded`, which
would be right if there were another backend; there is exactly one.

**`ProbeLivenessAsync` probes the workload's liveness endpoint and no game operation.** A readiness
check that played a game would create sessions nobody asked for.

**There is no `AddGameEdge` and no second registration call.** The edge is composed by
`AddPlatformWebHost()`; the forwarding route and the readiness check are ordinary application code
registered the way any application registers a route and a service. A second mandatory Platform-shaped
call would be the bespoke wiring D3's own done-criterion forbids at its first consumer.

**Neither interface exposes a retry.** The forwarder does not retry a failed forward, and nothing in
this contract gives it a place to record an attempt.

### The result type — workload and generator

```ts
export type Outcome<T, E> =
  | { readonly ok: true; readonly value: T }
  | { readonly ok: false; readonly error: E };
```

**Every error crossing a module boundary in TypeScript is an `Outcome` failure carrying a typed
error value.** The single exception is the engine's own `SessionStoreError`, which is thrown because
none of `SessionStore`'s signatures has an error channel — Dispatch catches it at the boundary and
converts it to a `DispatchOutcome` error arm, and it never travels further as an exception.

---

## Error semantics

Every variant below is a value with a stable `code`. **No bare exceptions and no string errors cross
a module boundary.** Each module's error type is a discriminated union on `code`.

### Contract — `ContractLoadError`

| Variant | Raised when | Retryable | Caller does |
|---|---|---|---|
| `MalformedArtifact` | The contract package cannot be parsed, or a required member is absent | No | Fails startup, naming the member. A retry restores the same bytes |
| `UnsupportedContractVersion` | The artifact's major version is not one this workload understands | No | Fails startup, naming both versions |
| `UnmappedErrorCode` | `statusFor` is called with a code the mapping does not carry | No | Fails the request as an internal failure and fails the build's gate assertion. A generation gate should have made this unreachable |

### Composition — `CompositionError`

| Variant | Raised when | Retryable | Caller does |
|---|---|---|---|
| `EngineVersionMismatch` | The contract's recorded engine version differs from the resolved engine package's | No | Fails startup, stating both versions. The listener never binds |
| `ContentRegistryInvalid` | The content registry does not build, or the fixture's campaign is not in it | No | Fails startup, naming the campaign |
| `DumpWriteFailed` | The determinism dump cannot be written at shutdown | No | Reports the failure and exits non-zero. The harness must not read a stale or absent dump as an empty one |

### Dispatch — `DispatchFailure`

Dispatch returns no error type of its own. Its failure channel is `DispatchOutcome`'s error arm,
carrying an `EngineErrorCode` unchanged.

| Code class | Raised when | Retryable | Caller does |
|---|---|---|---|
| Engine reason code | The store threw a `SessionStoreError` | No, uniformly in G1 | The surface maps the code to a status through the contract's mapping and returns the code **verbatim**. A paraphrase would break the client's own message lookup |

**`storage_failure` is declared and unreachable in G1.** The workload's `SessionPersistence` is
map-backed and total. It has a mapping because the gate requires one, and the mapping is `503`.

**A rejected action is not an error here.** An unknown action id or an unmet requirement is a
successful `DispatchOutcome` carrying the store's unsuccessful result, and it becomes a `200`.

### HTTP and MCP surfaces — `WireError`

| Variant | `code` | Status | Raised when | Retryable | Caller does |
|---|---|---|---|---|---|
| `UnsupportedVersion` | `unsupported_version` | `404` | The path's version prefix is not the contract's `wireVersion` | No | Address the supported version. The body's code distinguishes this from the next |
| `UnknownOperation` | `unknown_operation` | `404` | The operation segment matches no row | No | Read the tool list or the table. Same status, different code, by design |
| `MalformedPayload` | `malformed_payload` | `400` | Request-schema validation fails | No | Fix the payload. **Nothing happened** — the engine was never reached, no session was created and no action was attempted, so nothing is idempotency-sensitive |
| `EngineRejection` | The engine's code, verbatim | From the mapping | The store threw | No | Render the code through the engine's own string table |
| `InternalFailure` | [Unresolved 2](#unresolved) | `500` | An unhandled rejection reaches the surface, or response validation fails | No | Read the log line the correlation identifies. The body carries **never** exception text and **never** payload content |

**Response-validation failure is an internal failure, not a passed-through body.** An unvalidated
response is not returned; the request fails.

### Surface construction — `SurfaceBuildError`

| Variant | Raised when | Retryable | Caller does |
|---|---|---|---|
| `DuplicateRoute` | Two rows derive the same path segment | No | Fails startup before binding, naming both rows |
| `DuplicateToolName` | Two rows carry the same `mcpTool` | No | Fails startup before binding, naming both rows |
| `MissingSchema` | A row references a `SchemaRef` the artifact's schema set does not contain | No | Fails startup, naming the row and the reference |

**All three fail before the listener binds**, which is what makes the design's ordering claim
assertable rather than incidental.

### Encoding and validation — `EncodingError`, `ValidationFailure`

| Type | Variant | Raised when | Retryable | Caller does |
|---|---|---|---|---|
| `EncodingError` | `NonFiniteNumber` | A value contains `NaN` or an infinity | No | An internal failure. Coercion is not available — it would make two runs' bytes depend on a coercion rule |
| `EncodingError` | `UnsupportedValue` | A value is not a `JsonValue` — a `bigint`, a function, a symbol | No | An internal failure |
| `ValidationFailure` | `SchemaViolation` | A payload or a response does not satisfy its schema | No | On a request, `malformed_payload`; on a response, an internal failure. **The violation detail never crosses the wire** |

### Startup and shutdown — `StartupError`, `ShutdownError`

| Type | Variant | Raised when | Retryable | Caller does |
|---|---|---|---|---|
| `StartupError` | `ConfigurationInvalid` | A required setting is absent, or a value is outside its range | No | Exits non-zero, naming the setting |
| `StartupError` | `ContractLoad` | Carries a `ContractLoadError` | No | Exits non-zero |
| `StartupError` | `Composition` | Carries a `CompositionError` | No | Exits non-zero |
| `StartupError` | `SurfaceBuild` | Carries a `SurfaceBuildError` | No | Exits non-zero |
| `StartupError` | `ListenerBindFailed` | The configured endpoint cannot be bound | No | Exits non-zero, naming the endpoint |
| `ShutdownError` | `DumpWriteFailed` | Carries a `CompositionError` | No | Exits non-zero |

**Every startup variant aborts, and none warns.** A service that starts against a contract describing
a different engine serves a wire its own schemas do not describe, and every downstream assertion in
this design becomes conditional.

### The generator — `GenerationError`

| Variant | Raised when | Retryable | Caller does |
|---|---|---|---|
| `ArityMismatch` | A `SessionStore` method has no row, or a row names no method | No | Fails the contract build, naming both sides. **This is what fires on an engine version bump** |
| `ErrorCodeUncovered` | A declared `SessionStoreErrorCode` has no status mapping | No | Fails the contract build, naming the code |
| `NarrowingUnknownField` | A `NarrowedField` names a member the engine's declaration does not have | No | Fails the contract build, naming the row and the member |
| `ResponseSchemaOpen` | A response schema permits additional properties | No | Fails the contract build, naming the schema |
| `RequestSchemaOpen` | A request schema permits additional properties | No | Fails the contract build, naming the schema. A tolerated extra member would make a request narrowing reversible from the wire |
| `EnvelopeReachable` | A response schema resolves to the engine's envelope type | No | Fails the contract build. **This is the permanent non-goal's static gate** |
| `DeterminismProfileInRow` | A row carries the determinism profile as a field | No | Fails the contract build |
| `DuplicateOperationId` | Two rows share an `OperationId` or an `McpToolName` | No | Fails the contract build, naming both |
| `EngineResolutionFailed` | The engine package cannot be resolved for projection | No | Fails the contract build, naming the package and the registry. **Nothing retries automatically** — a silent retry over an authentication failure records a credential problem as flakiness |

### The harness — `DumpReadError`, `ReplayError`

| Type | Variant | Raised when | Retryable | Caller does |
|---|---|---|---|---|
| `DumpReadError` | `DumpAbsent` | No dump exists at the configured path after shutdown | No | Fails the comparison. An absent dump is never read as an empty one |
| `DumpReadError` | `DumpMalformed` | The dump cannot be parsed | No | Fails the comparison |
| `ReplayError` | `CoverageIncomplete` | The fixture's operation set is not equal to the table's row set | No | Fails the suite, naming the operations on each side. This is what makes "every store operation is exercised through the hosted surface" checkable by counting |
| `ReplayError` | `UnknownOperationInFixture` | A step names an operation with no row | No | Fails the suite, naming the step |
| `ReplayError` | `StepFailed` | A step produced an error outcome the fixture did not declare | No | Fails the suite, naming the step and the code. A replay whose steps fail is not a replay |
| `ReplayError` | `TransportFailure` | The hosted client could not complete a request | No | Fails the suite. **No retry** — a retried step is a second action |
| `ReplayError` | `Shutdown` | Carries a `ShutdownError` | No | Fails the suite |
| `ReplayError` | `DumpRead` | Carries a `DumpReadError` | No | Fails the suite |

### The edge — `EdgeError`

```csharp
public abstract record EdgeError(string Code) : PlatformError(Code);
```

| Variant | Status | Raised when | Retryable | Caller does |
|---|---|---|---|---|
| `WorkloadUnreachable` | `503` | The forward cannot connect, or the readiness probe cannot reach the workload's liveness endpoint | **No** | The caller re-reads with a query operation. **The edge does not retry** — a retry against a `submitAction` whose outcome is unknown is a second action, and merging two is explicitly not available |
| `WorkloadTimeout` | `504` | The forward exceeds `ForwardTimeout` | **No** | Same. The state is unknown to the edge and knowable only at the workload, which is exactly the partial-failure case; G1's honest answer is a re-read, not a resubmit |

**`IsRetryable` is `false` on both**, and that is a statement about this system rather than about
HTTP: there is no idempotency key, and inventing one is Platform's API-conventions work, not G1's.

**The codes on both variants are unnamed by the design** and are [Unresolved 2](#unresolved).

**The edge produces no other error.** Every other status a caller sees came from the workload and was
forwarded unaltered.

---

## Invariants

Each is written to be assertable, with the module responsible for maintaining it.

| # | Invariant | Owner |
|---|---|---|
| 1 | The row set exactly covers the exported `SessionStore` interface's methods — no method without a row, no row without a method | Generator |
| 2 | Every `SessionStoreErrorCode` the engine declares and every member of `TransportErrorCode` appears exactly once in the status mapping, and the mapping has no other entry | Generator |
| 3 | Every response schema is closed at every object level, and none resolves to the engine's envelope type | Generator |
| 3a | Every request schema is closed at every object level, so a narrowed request field cannot be re-supplied and no undeclared member reaches the store | Generator |
| 4 | `httpPath` equals its row's `operation`, for every row | Generator |
| 5 | No row carries the determinism profile in any form | Generator |
| 6 | `OperationId` and `McpToolName` are each unique across the row set | Generator |
| 7 | Every `NarrowedField` names a top-level member the engine's declaration actually has | Generator |
| 8 | Every `SchemaRef` a row references resolves to a document in the same artifact's schema set | Generator |
| 9 | No `$ref` or `$id` is dereferenced over the network, at generation or at run time | Generator, Contract |
| 10 | `ContractPackage.engineVersion` equals the version the schemas were projected from | Generator |
| 11 | The workload's resolved engine package version equals `ContractPackage.engineVersion`, or the process does not start | Composition |
| 12 | The workload computes no sequence and stamps no field on a session or save record; every value is the engine's. The one thing it supplies is `RecordIdSource`, which the engine calls — and only under the replay profile | Composition |
| 12a | With the default determinism profile, no `RecordIdSource` is supplied and the engine's own minting applies unchanged | Composition |
| 12b | Under the replay profile, session and save ids are the same strings in every run, which is what makes comparison A's id ordering reproducible and the fixture's literal ids writable | Composition |
| 13 | No correlation, trace id, or other host metadata is written into a session record or into any canonical serialization | Composition |
| 14 | With the default determinism profile, no dump is written and no dump path exists to write to | Composition |
| 15 | The determinism profile is startup configuration only — never a request field, never a header, never a route segment | Composition, HTTP surface, MCP surface |
| 16 | `StoreSerializationSnapshot` and `DeterminismDump` carry canonical serializations only, never a host-owned record field | Composition |
| 17 | Neither surface's module graph reaches `StoreSerializationHandle` | HTTP surface, MCP surface |
| 18 | No response body anywhere in either transcript contains a canonical serialization | HTTP surface, MCP surface, Proof harness |
| 19 | Both surfaces are constructed from the in-memory row set before the listener binds; a construction failure aborts startup | HTTP surface, MCP surface |
| 20 | The number of MCP tools equals the number of table rows, and the two name sets correspond one-to-one | MCP surface |
| 21 | Both surfaces reach the store only through one `Dispatcher` instance over one `SessionStore` | HTTP surface, MCP surface |
| 22 | A row's request and response shapes are the same for both surfaces; no narrowing is applied by a surface | Dispatch |
| 23 | Dispatch applies the row's projection; a surface never narrows and never widens what Dispatch returned | Dispatch |
| 24 | A request that fails request-schema validation never reaches the store | HTTP surface, MCP surface |
| 25 | Every response is validated against its row's closed response schema before it is encoded | HTTP surface, MCP surface |
| 26 | An engine reason code travels to the caller verbatim; no code is paraphrased, normalized, or translated | Dispatch, HTTP surface, MCP surface |
| 27 | Status is a function of the code through the contract's mapping, with no default branch | HTTP surface |
| 28 | A rejected action is a `200` carrying the store's unsuccessful result; no game verdict determines a status | HTTP surface, MCP surface |
| 29 | Every response, success or failure, carries the correlation | HTTP surface, MCP surface, Edge |
| 30 | No error body carries exception text or payload content | HTTP surface, MCP surface, Edge |
| 31 | A malformed inbound `traceparent` yields a fresh root and never fails the request | HTTP surface |
| 32 | With no OTLP endpoint configured, no exporter is constructed and no outbound connection is attempted | Composition, Edge |
| 33 | `canonicalEncode` is the only encoder on the response path, and its output reaches the socket unaltered | HTTP surface |
| 34 | The replay is strictly sequential — each response is fully read before the next request is sent | Proof harness |
| 35 | The fixture's operation set equals the table's row set | Proof harness |
| 36 | Both comparisons are byte comparisons with no ignore-list, no normalization and no options parameter | Proof harness |
| 37 | The golden transcript is never written by a passing test | Proof harness |
| 38 | Two perturbations are asserted red: one transposing two actions must fail comparison A, one substituting a response field must fail comparison B | Proof harness |
| 39 | Stage 1's single-hop replay remains in the suite and green after the edge lands | Proof harness |
| 40 | The edge forwards method, path and body and alters none of them; the response is returned byte-for-byte | Edge |
| 41 | The edge holds no per-session state, no connection affinity and no cache, and never reorders, batches or coalesces | Edge |
| 42 | The edge's readiness check is `Required` and declares `TouchesExternalDependency`; its liveness declares no external dependency | Edge |
| 43 | The edge retries nothing and records no attempt | Edge |
| 44 | The edge is composed by Platform's standard registration call and no second Platform-shaped call | Edge |
| 45 | No project under `src/` or `samples/` references the workload | Build |
| 46 | The listener binds loopback unless explicitly configured otherwise | Composition |
| 47 | The edge's outbound `traceparent` carries the inbound trace-id and sampling flag unaltered, with the span-id naming this hop's own span (ASP.NET Core's own request instrumentation) rather than the caller's — so the workload's span parents on the edge's, not on whatever the edge's own caller sent | Edge |

---

## Unresolved

Values and signatures the design does not determine. **Each blocks something concrete**, and none is
guessed at above. **1 is resolved and nothing above is now held back**; 2, 3 and 4 are open, and each
names a value a first implementer would otherwise settle silently.

**Resolved items keep their number and are struck through rather than removed**, because
[`30-slices.md`](30-slices.md) and [`90-decisions.md`](90-decisions.md) will cite these by number and
renumbering would silently break every reference.

### ~~1. Session and save ids are not deterministic, and the design says they are~~

**Resolved 2026-08-08, by Ben: the engine gains the seam.** G1 delivers a host-suppliable
`RecordIdSource` on the engine's session composition root, defaulting to today's
`crypto.randomUUID()`, and the brief's engine-behaviour non-goal carries a carve-out naming it. See
[*The engine seam G1 adds*](#the-engine-seam-g1-adds); the reasoning and the rejected alternatives
are in [`90-decisions.md`](90-decisions.md) and are not restated here. The signatures this held back
— `runInProcess`, `runHosted`, `HostedTarget` and literal ids in `ReplayStep.arguments` — are
declared above. **The evidence that forced it is kept below**, because it is the reason a
cross-repository change entered an effort whose virtue is being cheap.

**The design stated** (*Data model*, in-memory session and save records): *"Identity: session id and
save id, both minted by the engine's `IdSource` port."*

**The engine does not do this.** In the engine's session store, `sessionId` and `saveId` come from a
module-local `mintId()` that calls `crypto.randomUUID()`. Neither `SessionHost` nor
`InMemorySessionStoreOptions` carries an `IdSource`; `IdSource` is an `EngineHost` port and governs
`gameId` and `seed` only. The engine's own comment records the intent — *"this is the one place
unpredictability is legitimate"* — so this is a deliberate engine decision, not an oversight a host
can compose around.

**What that blocks, precisely:**

- **Comparison B cannot hold for three rows.** `createSession` and `loadGame` return
  `SessionHandle { sessionId, scene }`; `saveGame`'s narrowed response is `{ saveId }`. Each carries
  a fresh random UUID in every run, so those three encoded responses differ between run 1 and run 2
  and differ again from any committed golden transcript. The remaining rows are unaffected —
  `Scene.gameId` and `PlayerView.gameId` come from `IdSource` and are deterministic under
  `createCountingIds`.
- **Comparison A's ordering is not reproducible.** *"Ordered by id"* is a random order across runs
  once more than one session or more than one save exists. The blobs themselves can still match —
  ids are store metadata and never enter `GameState` — but the sequence they are compared in does
  not.
- **The fixture cannot name a session id.** A step after `create-session` needs the id that call
  returned, and it is not knowable when the fixture is written.

**Also false, and smaller:** the design's replay profile takes *"a counting `IdSource` from a stated
start"*, and the fixture carries *"the counting `IdSource`'s starting value"*. The engine's exported
`createCountingIds()` takes no argument and counts from zero. `ReplayDeterminismProfile` and
`ReplayFixture` above therefore carry no start value, which is the only reading consistent with the
engine as published.

**What it cost to resolve it this way**, stated rather than hidden: a cross-repository engine change
inside the effort whose virtue is being the cheapest informative failure, and an amendment to a
binding non-goal. Both were accepted because the alternatives each removed something the brief calls
load-bearing — excluding ids from the transcript comparison is a normalization the design refuses by
name; restricting the fixture to one session leaves comparison B failing on `create-session` and
stops the fixture exercising every row.

### 2. The three transport-only error codes the design describes but does not name

The workload's generic code on an unhandled failure, and the edge's codes for an unreachable and a
timed-out workload. Each is a wire-visible string a client renders and never parses around, so it is
a contract value rather than an implementation detail — and there are three of them, so a first
implementer would settle three names silently. `WireError`'s `InternalFailure` row and both
`EdgeError` variants point here.

Resolving this amends declarations that are otherwise closed, by design rather than by accident:
the workload's code becomes `TransportErrorCode`'s fourth member — before that, `WireErrorCode`
cannot represent the `InternalFailure` body's `code` at all — and invariant 2 then requires its
`500` entry in the status mapping, since the invariant tracks the union's membership. The edge's
two codes are `EdgeError`'s own: their statuses are fixed in its table, and they enter neither
`WireErrorCode` nor the mapping.

### 3. The JSON Schema dialect the generated schema set declares

`JsonSchemaDocument.$schema` has a type and no value. The dialect decides whether `additionalProperties: false` composes as the closed-schema gate assumes, and whether a
version-pathed `$id` is an identifier the validator will decline to fetch. It also fixes which
validator the workload can use, which is a dependency and therefore a decision-log entry of its own.

### 4. The contract package's published name and registry

[`10-design.md`](10-design.md)'s own open question 7. ADR-005 fixes the repository and the versioning
discipline and not the artifact's identity. It blocks nothing above — no signature names it — and it
blocks the first line of the workload's dependency declaration.

### Design questions that shape this contract without blocking a signature

[`10-design.md`](10-design.md)'s open questions 1, 5 and 6 change what this contract contains without
leaving any signature above undetermined, and are recorded here so the coupling is visible when they
are answered.

- **~~Question 1 — which engine version G1 pins.~~ Resolved 2026-08-08: the release S1 cuts from the
  engine's `main`, carrying ten operations.** It sets the row set's contents, which are data in the
  published artifact rather than a type here, so `OperationRow` is unchanged either way. The table is
  **ten** rows, and invariant 1 is asserted against a ten-method `SessionStore`.
- **Question 5 — whether the shutdown dump is inside the permanent non-goal.** This contract takes
  the design's reading. If it is reversed, `StoreSerializationHandle`, `DeterminismDump`,
  `writeDeterminismDump`, `readDeterminismDump`, `DumpReadError` and invariants 14 and 16 are removed,
  and comparison A narrows to the in-process run.
- **Question 6 — whether the edge covers the MCP surface.** This contract routes the JSON wire only.
  Covering MCP adds a second `Map…` extension and a second forwarder route; it adds no type.

---

## Additions requiring a decision-log entry

Six things above originated here rather than in the design, and none was derivable from it. Each is
small, mechanical, and named here rather than left for a reader to discover in the code. One (2) has
since been folded back into the design's own text.

1. **`Outcome<T, E>` as the TypeScript error channel.** The design specifies error *semantics* and no
   carrier for them on the Node side; "no bare exceptions, no string errors" needs one, and D3's
   `Result<T, TError>` is C# and in another package.
2. **Run 1 drives `Dispatcher`, not the store directly.** Now stated by the design itself (*Control
   flow* §3) — the design originally had run 1 play the fixture's action list *against the store*,
   and this addition was folded back in when the design was corrected. The reasoning stands in the
   decision log: both transcripts are asserted against one golden file, which is only true if run 1
   applies the row's projection and canonical encoding — otherwise `save-game` alone diverges, since
   the store returns `SaveHandle` and the wire returns `{ saveId }`. Only the transport differs
   between the runs.
3. **`canonicalEncode` is a second implementation of the engine's canonical serialization rule.** The
   engine does not export `canonicalStringify`. The rule is restated in this contract under
   `canonicalEncode` because the workload must implement it; **that is a second copy of a rule and
   therefore a drift hazard**, and the alternative — the engine exporting its encoder — is a
   cross-repository change this contract does not have the standing to make.
4. **`DeterminismDump` ordering comes from the encoding.** The design says *"keyed by id, in id
   order"*; writing the dump with `canonicalEncode`, whose members sort ascending by code unit, makes
   the ordering a property of the encoder rather than a step. It is reproducible only because
   Unresolved 1's resolution makes the ids themselves reproducible.
5. **The listener binds loopback by default** (invariant 46). Implied by trusted-local reachability
   and by the brief's non-goal on exposure, stated by neither.
6. **`RecordIdSource`'s name and shape.** A second port beside `IdSource`, with two members rather
   than one, and no counter start. The engine repository owns the final naming; this contract names
   it so the G1 slices and the engine PR are describing one thing.
