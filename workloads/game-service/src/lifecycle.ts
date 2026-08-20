/**
 * Process lifecycle. `startWorkload` performs the design's startup order and returns only after
 * the listener is bound: configuration, contract load, version assertion, composition, the
 * surface, then bind. Every startup variant aborts and none warns.
 *
 * `startWorkload` takes a `WorkloadConfiguration` and nothing else, so the contract reaches it one
 * of two ways: the installed package's own artifact, which is the normal path, or bytes at a path
 * named by `GAME_SERVICE_CONTRACT` and read through `loadContract`. The override exists because
 * criteria S3.9, S3.10 and S6.4 each start the service against a crafted artifact, and the
 * contract's signature leaves no parameter to pass one through.
 */
import { createServer } from "node:http";
import type { IncomingMessage, Server, ServerResponse } from "node:http";
import { readFileSync } from "node:fs";
import { loadPublishedContract } from "@subzerodev/service-contract";
import type { ContractPackage } from "@subzerodev/service-contract";

import { canonicalEncode } from "./canonical.js";
import { correlationFrom } from "./correlation.js";
import { buildHttpSurface } from "./http-surface.js";
import { buildMcpSurface } from "./mcp-surface.js";
import { createDispatcher } from "./dispatch.js";
import { compose, writeDeterminismDump } from "./compose.js";
import { loadContract, validateContract } from "./contract.js";
import { createTracing } from "./telemetry.js";
import type { Tracing } from "./telemetry.js";
import { INTERNAL_FAILURE, wireError } from "./wire-error.js";
import { err, ok } from "./types.js";
import type {
  CompositionError,
  CorrelationId,
  HttpStatus,
  HttpSurface,
  JsonValue,
  ListenEndpoint,
  McpSurface,
  McpToolName,
  Outcome,
  ProbeResult,
  ProbeSurface,
  ShutdownError,
  StartupError,
  WireErrorCode,
  WireRequest,
  WorkloadConfiguration,
  WorkloadProcess,
} from "./types.js";

export const CONTRACT_PATH_VARIABLE = "GAME_SERVICE_CONTRACT";

/** Loopback unless explicitly configured otherwise (invariant 46) — so "no public exposure" is a
 *  property of the process rather than of the network it happens to be on. */
const LOOPBACK = "127.0.0.1";

export interface ProbeGate {
  readonly surface: ProbeSurface;
  markSurfacesBuilt(): void;
  markListening(): void;
}

/** Liveness never consults the store; readiness is the conjunction of three things — surface
 *  construction, the bind, and the store answering — the third supplied as a thunk so this module
 *  never learns what a store is (`20-contract.md`, "Probes — workload"). */
export function createProbeSurface(readiness: () => Promise<ProbeResult>): ProbeGate {
  let surfacesBuilt = false;
  let listening = false;
  const healthy: ProbeResult = { status: "healthy" };
  const unhealthy: ProbeResult = { status: "unhealthy" };

  return {
    surface: {
      liveness: () => healthy,
      readiness: async () => (surfacesBuilt && listening ? readiness() : unhealthy),
    },
    markSurfacesBuilt: () => {
      surfacesBuilt = true;
    },
    markListening: () => {
      listening = true;
    },
  };
}

function readContract(): Outcome<ContractPackage, StartupError> {
  const override = process.env[CONTRACT_PATH_VARIABLE];
  if (!override) {
    // The installed package's own artifact still owes the same major-version refusal the override
    // path enforces — a resolved dependency one major ahead of what this workload understands is
    // exactly the case `ContractLoadError.UnsupportedContractVersion` exists to catch.
    const validated = validateContract(loadPublishedContract());
    if (!validated.ok) {
      return err({ code: "ContractLoad", cause: validated.error });
    }
    return ok(validated.value);
  }
  let bytes: Uint8Array;
  try {
    bytes = readFileSync(override);
  } catch {
    return err({ code: "ConfigurationInvalid", setting: CONTRACT_PATH_VARIABLE });
  }
  const loaded = loadContract(bytes);
  if (!loaded.ok) {
    return err({ code: "ContractLoad", cause: loaded.error });
  }
  return ok(loaded.value);
}

// Every request/response schema in the contract describes a payload of a few kilobytes; this is
// generous headroom over any legitimate one, not a tuned limit.
const MAX_BODY_BYTES = 1_048_576;

class BodyTooLarge extends Error {}

async function readRequestBody(message: IncomingMessage): Promise<Uint8Array> {
  const chunks: Buffer[] = [];
  let total = 0;
  for await (const chunk of message as AsyncIterable<Buffer>) {
    total += chunk.length;
    if (total > MAX_BODY_BYTES) throw new BodyTooLarge();
    chunks.push(chunk);
  }
  return Buffer.concat(chunks);
}

const MCP_LIST_TOOLS = "/mcp/list-tools";
const MCP_CALL_TOOL = "/mcp/call-tool";

interface McpRequest {
  readonly path: string;
  readonly method: string;
  readonly body: Uint8Array;
  readonly inboundTraceParent: string | null;
  /** For a refusal this transport raises itself, before `callTool` is reached — same rule
   *  (`correlationFrom`), applied here because there is no row lookup to wait on. */
  readonly correlation: CorrelationId;
}

interface McpResponse {
  readonly status: HttpStatus;
  readonly body: string;
  readonly correlation: CorrelationId;
}

/**
 * The MCP HTTP transport: `POST /mcp/list-tools` with no body, and `POST /mcp/call-tool` with
 * `{ name, arguments }`. Neither carries a `/v<n>/` segment — the MCP surface has no version path
 * (`20-contract.md`, "Workload — request context").
 *
 * Codes and statuses are the JSON wire's, resolved through the same `wire-error.ts` the HTTP
 * surface calls: `20-contract.md` heads that table "HTTP and MCP surfaces", so a tool error answers
 * with the status the mapping names rather than one this transport invented.
 */
async function handleMcp(contract: ContractPackage, mcp: McpSurface, request: McpRequest): Promise<McpResponse> {
  // The wire is uniformly POST, and a tool call is one row's invocation by another name; no row has
  // a verb variant for any other method to mean. Without this, a `GET` listed the tools and a
  // `DELETE` ran a state-changing tool.
  if (request.method.toUpperCase() !== "POST") {
    return errorResponse(contract, "unknown_operation" as WireErrorCode, request.correlation);
  }

  if (request.path === MCP_LIST_TOOLS) {
    // A descriptor set is JSON the artifact itself supplied; there is no branch here that can make
    // it non-encodable, and the fallback exists so this path has no throw of its own.
    const encoded = canonicalEncode({ tools: mcp.listTools() } as unknown as JsonValue);
    if (!encoded.ok) {
      return errorResponse(contract, INTERNAL_FAILURE, request.correlation);
    }
    return { status: 200, body: encoded.value as string, correlation: request.correlation };
  }

  if (request.path !== MCP_CALL_TOOL) {
    return errorResponse(contract, "unknown_operation" as WireErrorCode, request.correlation);
  }

  // An unparsable body and a missing or non-string `name` are both the caller's payload to fix, so
  // both are `malformed_payload`. `internal_failure` is reserved for an unhandled rejection or a
  // response that failed validation, and telling a caller to read a log line for their own bad
  // envelope is the one answer that helps nobody.
  let parsed: { name?: unknown; arguments?: unknown };
  try {
    parsed = JSON.parse(new TextDecoder().decode(request.body)) as { name?: unknown; arguments?: unknown };
  } catch {
    return errorResponse(contract, "malformed_payload" as WireErrorCode, request.correlation);
  }
  if (typeof parsed.name !== "string") {
    return errorResponse(contract, "malformed_payload" as WireErrorCode, request.correlation);
  }

  const outcome = await mcp.callTool(
    parsed.name as McpToolName,
    (parsed.arguments ?? {}) as JsonValue,
    request.inboundTraceParent,
  );
  if (outcome.kind === "result") {
    return { status: 200, body: outcome.value as string, correlation: outcome.correlation };
  }
  // The surface has already derived the code and the correlation; re-encoding it through
  // `wireError` is what attaches the status, and produces the same bytes the wire would.
  return errorResponse(contract, outcome.error.code, outcome.error.correlation);
}

function errorResponse(
  contract: ContractPackage,
  code: WireErrorCode,
  correlation: CorrelationId,
): McpResponse {
  const envelope = wireError(contract, code, correlation);
  return { status: envelope.status, body: envelope.body, correlation: envelope.correlation };
}

function serve(
  contract: ContractPackage,
  surface: HttpSurface,
  mcp: McpSurface,
  probes: ProbeSurface,
  tracing: Tracing | null,
): Server {
  return createServer((message: IncomingMessage, response: ServerResponse) => {
    void (async () => {
      const [rawPath] = (message.url ?? "/").split("?");
      const path = rawPath ?? "/";

      if (path === "/livez" || path === "/readyz") {
        const result = path === "/livez" ? probes.liveness() : await probes.readiness();
        // `detail` reaches the wire when the probe carries one, which is the only surface an
        // operator reads (`20-contract.md`, "Workload — readiness"; `10-design.md`, "The store is
        // unreachable at startup" — "the readiness body naming the store check"). Omitted, never
        // null, when there is none: the member is optional on `ProbeResult` and a body carrying
        // `"detail": null` would be a third state neither document describes. Nothing parses it —
        // the edge's own check reads the status code alone (`Readiness.cs`) — so this is a
        // diagnostic member, not a contract a caller may branch on.
        const body = JSON.stringify(
          result.detail === undefined ? { status: result.status } : { status: result.status, detail: result.detail },
        );
        response.writeHead(result.status === "healthy" ? 200 : 503, { "content-type": "application/json" });
        response.end(body);
        return;
      }

      if (path === MCP_LIST_TOOLS || path === MCP_CALL_TOOL) {
        const inbound = message.headers["traceparent"];
        const inboundTraceParent = typeof inbound === "string" ? inbound : null;
        // Captured before the body is even read, so the exported span's start time reflects when
        // the request actually began rather than when the span object happened to be constructed.
        const requestStartedAt = Date.now();
        const result = await handleMcp(contract, mcp, {
          path,
          method: message.method ?? "POST",
          body: await readRequestBody(message),
          inboundTraceParent,
          // For a refusal this transport raises itself (bad method, unknown path, unparsable
          // body), before a row lookup is reached for `callTool` to derive its own.
          correlation: correlationFrom(inboundTraceParent),
        });
        // `result.correlation` is whichever of the two actually answered — reached here rather
        // than precomputed, because `correlationFrom` mints a fresh random value on every call
        // when the header is absent or malformed, and two separate calls over the same invalid
        // header would not agree with each other the way two parses of a well-formed one do.
        tracing?.startRequestSpan("game-service.mcp", inboundTraceParent, result.correlation, requestStartedAt).end();
        const headers: Record<string, string> = {
          "content-type": "application/json",
          "x-correlation-id": result.correlation as string,
        };
        response.writeHead(result.status, headers);
        response.end(result.body);
        return;
      }

      const headers = new Map<string, string>();
      for (const [name, value] of Object.entries(message.headers)) {
        if (typeof value === "string") headers.set(name.toLowerCase(), value);
      }

      // Captured before the body is even read, so the exported span's start time reflects when
      // the request actually began rather than when the span object happened to be constructed.
      const requestStartedAt = Date.now();
      const request: WireRequest = {
        method: message.method ?? "POST",
        path,
        headers,
        body: await readRequestBody(message),
      };

      const inboundTraceParent = headers.get("traceparent") ?? null;
      const wire = await surface.handle(request);
      // `wire.headers` carries whatever `HttpSurface.handle` actually derived from the same
      // header, read here rather than precomputed — `correlationFrom` mints a fresh random value
      // on every call when the header is absent or malformed, and two separate calls over the
      // same invalid header would not agree with each other the way two parses of a well-formed
      // one do.
      const correlation = (wire.headers.get("x-correlation-id") ?? correlationFrom(inboundTraceParent)) as CorrelationId;
      tracing?.startRequestSpan("game-service.request", inboundTraceParent, correlation, requestStartedAt).end();
      response.writeHead(wire.status, Object.fromEntries(wire.headers));
      // The bytes the encoder produced reach the socket unaltered — nothing between the encoder
      // and here re-encodes (invariant 33).
      response.end(Buffer.from(wire.body));
    })().catch(() => {
      // A stream error (client abort, reset connection) or an oversized body reaches here with no
      // response started yet. There is nothing left to negotiate on a broken connection, and the
      // one thing this must not do is become an unhandled rejection that takes the process with it.
      response.destroy();
    });
  });
}

export async function startWorkload(
  configuration: WorkloadConfiguration,
): Promise<Outcome<WorkloadProcess, StartupError>> {
  if (
    !Number.isInteger(configuration.listen.port) ||
    configuration.listen.port < 0 ||
    configuration.listen.port > 65535
  ) {
    return err({ code: "ConfigurationInvalid", setting: "listen.port" });
  }

  const contract = readContract();
  if (!contract.ok) return contract;

  const composed = await compose(configuration, contract.value);
  if (!composed.ok) {
    return err({ code: "Composition", cause: composed.error });
  }

  const probes = createProbeSurface(() => composed.value.readiness());
  // The provider itself, never a store resolved here: `Dispatcher.invoke` calls `forRequest()` once
  // per request, so the store a request writes through is the one composed for it. For the
  // in-memory configuration `forRequest()` always returns the same long-lived layer (S4.2). For the
  // durable configuration it means a store composed while unreachable at startup stops being
  // permanent: once the background reconnect in `compose.ts` succeeds, the very next dispatch
  // reaches the connected store rather than the `unavailablePersistence()`/`unavailableProfiles()`
  // stubs baked in at boot.
  const dispatcher = createDispatcher(contract.value, composed.value.stores, composed.value.lifecycle);
  const built = buildHttpSurface(contract.value, dispatcher);
  if (!built.ok) {
    return err({ code: "SurfaceBuild", cause: built.error });
  }
  const builtMcp = buildMcpSurface(contract.value, dispatcher);
  if (!builtMcp.ok) {
    return err({ code: "SurfaceBuild", cause: builtMcp.error });
  }
  probes.markSurfacesBuilt();

  // `otlpEndpoint` null or blank means no exporter is constructed and no outbound connection is
  // attempted — not a disabled exporter, and not a default endpoint (invariant 32).
  let tracing: Tracing | null = null;
  if (configuration.otlpEndpoint !== null && configuration.otlpEndpoint.trim().length > 0) {
    try {
      tracing = createTracing(configuration.otlpEndpoint);
    } catch {
      // `OTLPTraceExporter`'s own constructor validates the URL and throws synchronously on
      // anything it cannot parse — this is a configuration error, not a startup crash, so it goes
      // through the same `Outcome` channel every other misconfiguration here does.
      return err({ code: "ConfigurationInvalid", setting: "otlpEndpoint" });
    }
  }

  const host = configuration.listen.host.trim().length > 0 ? configuration.listen.host : LOOPBACK;
  const server = serve(contract.value, built.value, builtMcp.value, probes.surface, tracing);

  const bound = await new Promise<ListenEndpoint | null>((resolve) => {
    server.once("error", () => resolve(null));
    server.listen(configuration.listen.port, host, () => {
      const address = server.address();
      resolve(typeof address === "object" && address !== null ? { host, port: address.port } : null);
    });
  });

  if (!bound) {
    return err({ code: "ListenerBindFailed", endpoint: configuration.listen });
  }
  probes.markListening();

  return ok({
    listening: bound,
    probes: probes.surface,
    async shutdown(): Promise<Outcome<void, ShutdownError>> {
      // The replay profile's dump is written here, before the listener stops accepting
      // (invariant 14). The default profile carries no dump path, so nothing is written and
      // there is nowhere for it to write to.
      let written: Outcome<void, CompositionError> = ok(undefined);
      if (configuration.determinism.kind === "replay") {
        written = await writeDeterminismDump(composed.value, configuration.determinism);
      }

      await new Promise<void>((resolve) => server.close(() => resolve()));

      // Stops the sweep timer (and any in-flight reconnect retry) and closes the pool — after the
      // dump above has already read whatever the store held, and after the listener has stopped
      // accepting, so no request in flight is cut off by the store disappearing under it.
      await composed.value.close();

      // Flushed after the listener stops accepting, so a request handled just before shutdown is
      // not silently dropped from the collector's view.
      await tracing?.shutdown();

      if (!written.ok) {
        return err({ code: "DumpWriteFailed", cause: written.error });
      }
      return ok(undefined);
    },
  });
}
