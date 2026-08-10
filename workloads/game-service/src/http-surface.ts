/**
 * The JSON wire. One route per row, uniformly `POST /v<n>/<operation>`, built from the in-memory
 * row set before the listener binds — a table the service cannot satisfy fails startup rather than
 * producing a route that fails on first use.
 *
 * Status is a function of the code through the contract's mapping, with no default branch
 * (invariant 27). A game verdict never determines a status: a rejected action is a `200` carrying
 * the store's unsuccessful result (invariant 28).
 */
import type { ContractPackage, OperationRow, WireErrorCode } from "@subzerodev/service-contract";
import { canonicalEncode } from "./canonical.js";
import { correlationFrom } from "./correlation.js";
import { schemaPresent, validateRequest, validateResponse, validatorsFor } from "./validate.js";
import { internalFailure as internalFailureEnvelope, wireError } from "./wire-error.js";
import { err, ok } from "./types.js";
import type {
  CorrelationId,
  Dispatcher,
  HttpStatus,
  HttpSurface,
  JsonValue,
  OperationId,
  Outcome,
  SurfaceBuildError,
  WireRequest,
  WireResponse,
} from "./types.js";

const JSON_CONTENT_TYPE = "application/json";

/** `POST /v1/create-session` → `{ version: "v1", operation: "create-session" }`. Anything that is
 *  not exactly two segments is an unknown operation, not a parse failure. */
function splitPath(path: string): { version: string; operation: string } | null {
  const [withoutQuery] = path.split("?");
  const segments = (withoutQuery ?? "").split("/").filter((segment) => segment.length > 0);
  if (segments.length !== 2) return null;
  return { version: segments[0]!, operation: segments[1]! };
}

function respond(status: HttpStatus, body: string, correlation: CorrelationId): WireResponse {
  return {
    status,
    headers: new Map([
      ["content-type", JSON_CONTENT_TYPE],
      ["x-correlation-id", correlation as string],
    ]),
    body: new TextEncoder().encode(body),
  };
}

function internalFailure(contract: ContractPackage, correlation: CorrelationId): WireResponse {
  const envelope = internalFailureEnvelope(contract, correlation);
  return respond(envelope.status, envelope.body, envelope.correlation);
}

/** Every error response goes through `wire-error.ts`, which the MCP transport also calls — the
 *  status the mapping names, or `internal_failure` if the code has none (invariant 27). */
function respondError(contract: ContractPackage, code: WireErrorCode, correlation: CorrelationId): WireResponse {
  const envelope = wireError(contract, code, correlation);
  return respond(envelope.status, envelope.body, envelope.correlation);
}

function parseBody(body: Uint8Array): JsonValue | undefined {
  const text = new TextDecoder().decode(body).trim();
  if (text.length === 0) return {};
  try {
    return JSON.parse(text) as JsonValue;
  } catch {
    return undefined;
  }
}

export function buildHttpSurface(
  contract: ContractPackage,
  dispatcher: Dispatcher,
): Outcome<HttpSurface, SurfaceBuildError> {
  const routes = new Map<string, OperationRow>();

  for (const row of contract.operations) {
    const segment = row.httpPath as string;
    const clash = routes.get(segment);
    if (clash) {
      return err({ code: "DuplicateRoute", first: clash.operation as string, second: row.operation as string });
    }
    for (const reference of [row.requestShape as string, row.responseShape as string]) {
      if (!schemaPresent(contract, reference)) {
        return err({ code: "MissingSchema", operation: row.operation as string, reference });
      }
    }
    routes.set(segment, row);
  }

  // Compiled before the bind, alongside the presence check above — a schema ajv cannot resolve is
  // a startup refusal, not a route that fails the first time it is asked to validate anything.
  try {
    validatorsFor(contract);
  } catch (thrown) {
    return err({ code: "SchemaCompile", detail: thrown instanceof Error ? thrown.message : String(thrown) });
  }

  const surface: HttpSurface = {
    async handle(request: WireRequest): Promise<WireResponse> {
      const correlation = correlationFrom(request.headers.get("traceparent") ?? null);

      try {
        // The wire is uniformly POST; every route is one row, and a row has no verb variants for
        // any other method to mean.
        if (request.method.toUpperCase() !== "POST") {
          return respondError(contract, "unknown_operation" as WireErrorCode, correlation);
        }

        const parts = splitPath(request.path);
        if (!parts) {
          return respondError(contract, "unknown_operation" as WireErrorCode, correlation);
        }
        if (parts.version !== (contract.wireVersion as string)) {
          return respondError(contract, "unsupported_version" as WireErrorCode, correlation);
        }

        const row = routes.get(parts.operation);
        if (!row) {
          return respondError(contract, "unknown_operation" as WireErrorCode, correlation);
        }

        const body = parseBody(request.body);
        if (body === undefined) {
          return respondError(contract, "malformed_payload" as WireErrorCode, correlation);
        }

        const validated = validateRequest(contract, row, body);
        if (!validated.ok) {
          // Nothing happened: the store was never reached, so nothing here is idempotency-sensitive.
          return respondError(contract, "malformed_payload" as WireErrorCode, correlation);
        }

        const outcome = await dispatcher.invoke(row.operation as OperationId, validated.value);

        if (outcome.kind === "error") {
          // The engine's code travels verbatim — no paraphrase, no normalization. A code with no
          // mapping is a defect the generation gate should have caught; `respondError` fails the
          // request as an internal failure rather than letting it become an unattributed 500.
          return respondError(contract, outcome.code, correlation);
        }

        const checked = validateResponse(contract, row, outcome.value);
        if (!checked.ok) {
          // An unvalidated response is not returned; the request fails.
          return internalFailure(contract, correlation);
        }

        const encoded = canonicalEncode(outcome.value);
        if (!encoded.ok) {
          return internalFailure(contract, correlation);
        }
        return respond(200, encoded.value as string, correlation);
      } catch {
        // An unhandled rejection reaching the surface is an internal failure. The detail goes to
        // the log line the correlation identifies, never to the body.
        return internalFailure(contract, correlation);
      }
    },
  };

  return ok(surface);
}
