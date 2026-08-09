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
import { compose } from "./compose.js";
import { loadContract } from "./contract.js";
import { err, ok } from "./types.js";
import type {
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
    return ok(loadPublishedContract());
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

async function readRequestBody(message: IncomingMessage): Promise<Uint8Array> {
  const chunks: Buffer[] = [];
  for await (const chunk of message) {
    chunks.push(chunk as Buffer);
  }
  return new Uint8Array(Buffer.concat(chunks));
}

function serve(surface: HttpSurface, probes: ProbeSurface): Server {
  return createServer((message: IncomingMessage, response: ServerResponse) => {
    void (async () => {
      const path = message.url ?? "/";

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
    })();
  });
}

export async function startWorkload(
  configuration: WorkloadConfiguration,
): Promise<Outcome<WorkloadProcess, StartupError>> {
  if (!Number.isInteger(configuration.listen.port) || configuration.listen.port < 0) {
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
      // The replay profile's dump is written here, before the listener stops accepting. S4 is
      // where that lands; the default profile writes nothing and has nowhere to write to.
      await new Promise<void>((resolve) => server.close(() => resolve()));
      return ok(undefined);
    },
  });
}
