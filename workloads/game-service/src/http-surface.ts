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
import { findRow, statusFor } from "./contract.js";
import { correlationFrom } from "./correlation.js";
import { schemaPresent, validateRequest, validateResponse } from "./validate.js";
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

const INTERNAL_FAILURE = "internal_failure" as WireErrorCode;
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

/** The two-member envelope, encoded by the same encoder every success uses. Never exception text
 *  and never payload content (invariant 30). */
function errorResponse(status: HttpStatus, code: WireErrorCode, correlation: CorrelationId): WireResponse {
  const encoded = canonicalEncode({ code: code as string, correlation: correlation as string });
  // Two string members cannot fail canonical encoding; the fallback exists so this path has no
  // throw of its own rather than because it is reachable.
  const body = encoded.ok ? (encoded.value as string) : `{"code":"${INTERNAL_FAILURE}","correlation":"${correlation}"}`;
  return respond(status, body, correlation);
}

function internalFailure(contract: ContractPackage, correlation: CorrelationId): WireResponse {
  const status = statusFor(contract, INTERNAL_FAILURE);
  return errorResponse(status.ok ? status.value : 500, INTERNAL_FAILURE, correlation);
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

  const surface: HttpSurface = {
    async handle(request: WireRequest): Promise<WireResponse> {
      const correlation = correlationFrom(request.headers.get("traceparent") ?? null);

      try {
        const parts = splitPath(request.path);
        if (!parts) {
          return errorResponse(await status(contract, "unknown_operation"), "unknown_operation" as WireErrorCode, correlation);
        }
        if (parts.version !== (contract.wireVersion as string)) {
          return errorResponse(
            await status(contract, "unsupported_version"),
            "unsupported_version" as WireErrorCode,
            correlation,
          );
        }

        const row = routes.get(parts.operation) ?? findRow(contract, parts.operation as OperationId);
        if (!row || !routes.has(row.httpPath as string)) {
          return errorResponse(await status(contract, "unknown_operation"), "unknown_operation" as WireErrorCode, correlation);
        }

        const body = parseBody(request.body);
        if (body === undefined) {
          return errorResponse(await status(contract, "malformed_payload"), "malformed_payload" as WireErrorCode, correlation);
        }

        const validated = validateRequest(contract, row, body);
        if (!validated.ok) {
          // Nothing happened: the store was never reached, so nothing here is idempotency-sensitive.
          return errorResponse(await status(contract, "malformed_payload"), "malformed_payload" as WireErrorCode, correlation);
        }

        const outcome = await dispatcher.invoke(row.operation as OperationId, validated.value);

        if (outcome.kind === "error") {
          const mapped = statusFor(contract, outcome.code);
          if (!mapped.ok) {
            // A code with no mapping is a defect the generation gate should have caught. It fails
            // the request as an internal failure rather than becoming an unattributed 500.
            return internalFailure(contract, correlation);
          }
          // The engine's code travels verbatim — no paraphrase, no normalization.
          return errorResponse(mapped.value, outcome.code, correlation);
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

async function status(contract: ContractPackage, code: string): Promise<HttpStatus> {
  const mapped = statusFor(contract, code as WireErrorCode);
  return mapped.ok ? mapped.value : 500;
}
