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

import { buildHttpSurface } from "./http-surface.js";
import { createDispatcher } from "./dispatch.js";
import { compose, writeDeterminismDump } from "./compose.js";
import { loadContract, validateContract } from "./contract.js";
import { err, ok } from "./types.js";
import type {
  CompositionError,
  HttpSurface,
  ListenEndpoint,
  Outcome,
  ProbeResult,
  ProbeSurface,
  ShutdownError,
  StartupError,
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

function serve(surface: HttpSurface, probes: ProbeSurface): Server {
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
  const server = serve(built.value, probes.surface);

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
