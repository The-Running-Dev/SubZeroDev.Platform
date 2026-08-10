/**
 * The wire's error envelope, in one place because both transports produce it.
 *
 * Status is a function of the code through the contract's mapping, with no default branch
 * (invariant 27), and the body is the two-member envelope the same encoder every success uses
 * produces — never exception text and never payload content (invariant 30).
 *
 * It lives here rather than inside `http-surface.ts` because a second copy of this rule in the MCP
 * transport is exactly what let the two surfaces answer one failure with different codes and
 * different statuses. `20-contract.md` heads that table "HTTP and MCP surfaces"; one copy is what
 * makes that heading true.
 */
import type { ContractPackage, HttpStatus, WireErrorCode } from "@subzerodev/service-contract";
import { canonicalEncode } from "./canonical.js";
import { statusFor } from "./contract.js";
import type { CorrelationId } from "./types.js";

export const INTERNAL_FAILURE = "internal_failure" as WireErrorCode;

export interface WireErrorEnvelope {
  readonly status: HttpStatus;
  readonly code: WireErrorCode;
  readonly body: string;
  readonly correlation: CorrelationId;
}

function encode(code: WireErrorCode, correlation: CorrelationId): string {
  const encoded = canonicalEncode({ code: code as string, correlation: correlation as string });
  // Two string members cannot fail canonical encoding; the fallback exists so this path has no
  // throw of its own rather than because it is reachable.
  return encoded.ok ? (encoded.value as string) : `{"code":"${INTERNAL_FAILURE}","correlation":"${correlation}"}`;
}

/** The code the wire answers with: the engine's own verbatim (invariant 26), or `internal_failure`
 *  where the mapping does not name it. Both surfaces resolve it through here, so neither can answer
 *  a failure with a code the other would not. */
export function resolvedCode(contract: ContractPackage, code: WireErrorCode): WireErrorCode {
  return statusFor(contract, code).ok ? code : INTERNAL_FAILURE;
}

/** `internal_failure` is itself the answer every other unmapped code falls back to; there is
 *  nowhere further to redirect an unmapped `internal_failure`, so this is the one true default. */
export function internalFailure(contract: ContractPackage, correlation: CorrelationId): WireErrorEnvelope {
  const status = statusFor(contract, INTERNAL_FAILURE);
  return {
    status: status.ok ? status.value : 500,
    code: INTERNAL_FAILURE,
    body: encode(INTERNAL_FAILURE, correlation),
    correlation,
  };
}

/** Every error either transport answers with goes through here: the status the mapping names, or
 *  `internal_failure` if the code has none — never the code's own status defaulted to 500, which
 *  would answer with a status the mapping never produced (invariant 27). */
export function wireError(
  contract: ContractPackage,
  code: WireErrorCode,
  correlation: CorrelationId,
): WireErrorEnvelope {
  const mapped = statusFor(contract, code);
  if (!mapped.ok) {
    return internalFailure(contract, correlation);
  }
  return { status: mapped.value, code, body: encode(code, correlation), correlation };
}
