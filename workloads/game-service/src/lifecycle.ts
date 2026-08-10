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

/** Liveness never consults the store; readiness reports healthy once both surface construction and
 *  the bind have completed, and not before either (S3.11). */
export function createProbeSurface(): ProbeGate {
  let surfacesBuilt = false;
  let listening = false;
  const healthy: ProbeResult = { status: "healthy" };
  const unhealthy: ProbeResult = { status: "unhealthy" };

  return {
    surface: {
      liveness: () => healthy,
      readiness: () => (surfacesBuilt && listening ? healthy : unhealthy),
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
  readonly correlation: CorrelationId;
}

interface McpResponse {
  readonly status: HttpStatus;
  readonly body: string;
  /** `null` only on a successful tool result: `McpToolOutcome`'s result arm carries no correlation
   *  and `callTool` takes no context parameter, so the transport has none to report. That is a
   *  contract gap against invariant 29, recorded in `mcp-surface.ts` — not a choice made here. */
  readonly correlation: CorrelationId | null;
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
    return { status: 200, body: encoded.value as string, correlation: null };
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

  const outcome = await mcp.callTool(parsed.name as McpToolName, (parsed.arguments ?? {}) as JsonValue);
  if (outcome.kind === "result") {
    return { status: 200, body: outcome.value as string, correlation: null };
  }
  // The surface has already resolved the code and minted the correlation; re-encoding it through
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

function serve(contract: ContractPackage, surface: HttpSurface, mcp: McpSurface, probes: ProbeSurface): Server {
  return createServer((message: IncomingMessage, response: ServerResponse) => {
    void (async () => {
      const [rawPath] = (message.url ?? "/").split("?");
      const path = rawPath ?? "/";

      if (path === "/livez" || path === "/readyz") {
        const result = path === "/livez" ? probes.liveness() : probes.readiness();
        const body = JSON.stringify({ status: result.status });
        response.writeHead(result.status === "healthy" ? 200 : 503, { "content-type": "application/json" });
        response.end(body);
        return;
      }

      if (path === MCP_LIST_TOOLS || path === MCP_CALL_TOOL) {
        const inbound = message.headers["traceparent"];
        const result = await handleMcp(contract, mcp, {
          path,
          method: message.method ?? "POST",
          body: await readRequestBody(message),
          // Adopted here, where the header is in hand, so a transport-level refusal is at least
          // reachable from the caller's trace. A refusal the *surface* raises is not — see the
          // note in `mcp-surface.ts`.
          correlation: correlationFrom(typeof inbound === "string" ? inbound : null),
        });
        const headers: Record<string, string> = { "content-type": "application/json" };
        if (result.correlation !== null) {
          headers["x-correlation-id"] = result.correlation as string;
        }
        response.writeHead(result.status, headers);
        response.end(result.body);
        return;
      }

      const headers = new Map<string, string>();
      for (const [name, value] of Object.entries(message.headers)) {
        if (typeof value === "string") headers.set(name.toLowerCase(), value);
      }

      const request: WireRequest = {
        method: message.method ?? "POST",
        path,
        headers,
        body: await readRequestBody(message),
      };

      const wire = await surface.handle(request);
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

  const probes = createProbeSurface();
  const dispatcher = createDispatcher(contract.value, composed.value.store);
  const built = buildHttpSurface(contract.value, dispatcher);
  if (!built.ok) {
    return err({ code: "SurfaceBuild", cause: built.error });
  }
  const builtMcp = buildMcpSurface(contract.value, dispatcher);
  if (!builtMcp.ok) {
    return err({ code: "SurfaceBuild", cause: builtMcp.error });
  }
  probes.markSurfacesBuilt();

  // `otlpEndpoint` null means no exporter is constructed and no outbound connection is attempted —
  // not a disabled exporter, and not a default endpoint (invariant 32).
  //
  // A *configured* endpoint has no meaning until S8 builds the exporter, and the choice between
  // the two ways of having none is deliberate: this refuses to start rather than starting and
  // silently exporting nothing. A service asked to emit telemetry that cannot is the shrug this
  // slice exists to remove, and "every startup variant aborts, and none warns" is the rule the
  // contract states for exactly this shape of gap. S8 replaces the refusal with the exporter.
  if (configuration.otlpEndpoint !== null) {
    return err({ code: "ConfigurationInvalid", setting: "otlpEndpoint" });
  }

  const host = configuration.listen.host.trim().length > 0 ? configuration.listen.host : LOOPBACK;
  const server = serve(contract.value, built.value, builtMcp.value, probes.surface);

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

      if (!written.ok) {
        return err({ code: "DumpWriteFailed", cause: written.error });
      }
      return ok(undefined);
    },
  });
}
