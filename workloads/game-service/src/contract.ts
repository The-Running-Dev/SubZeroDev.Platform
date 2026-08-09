/**
 * Reading the contract artifact. `loadContract` parses bytes; `findRow` and `statusFor` are the
 * only two questions the surfaces ask of it.
 */
import type { ContractPackage, HttpStatus, OperationRow, WireErrorCode } from "@subzerodev/service-contract";
import { err, ok } from "./types.js";
import type { ContractLoadError, OperationId, Outcome } from "./types.js";

/** The contract major version this workload understands. A different major is a refusal to start,
 *  not a best effort (`ContractLoadError.UnsupportedContractVersion`). */
const SUPPORTED_CONTRACT_MAJOR = 0;

const REQUIRED_MEMBERS = [
  "contractVersion",
  "engineVersion",
  "wireVersion",
  "operations",
  "schemas",
  "statusMapping",
] as const;

export function loadContract(source: Uint8Array): Outcome<ContractPackage, ContractLoadError> {
  let parsed: unknown;
  try {
    parsed = JSON.parse(new TextDecoder().decode(source));
  } catch {
    return err({ code: "MalformedArtifact", member: "(document)" });
  }

  if (typeof parsed !== "object" || parsed === null || Array.isArray(parsed)) {
    return err({ code: "MalformedArtifact", member: "(document)" });
  }

  const candidate = parsed as Record<string, unknown>;
  for (const member of REQUIRED_MEMBERS) {
    if (!(member in candidate)) {
      return err({ code: "MalformedArtifact", member });
    }
  }
  if (!Array.isArray(candidate["operations"]) || !Array.isArray(candidate["schemas"])) {
    return err({ code: "MalformedArtifact", member: "operations" });
  }
  const mapping = candidate["statusMapping"];
  if (typeof mapping !== "object" || mapping === null || !Array.isArray((mapping as { entries?: unknown }).entries)) {
    return err({ code: "MalformedArtifact", member: "statusMapping" });
  }

  const contractVersion = String(candidate["contractVersion"]);
  const major = Number.parseInt(contractVersion.split(".")[0] ?? "", 10);
  if (!Number.isInteger(major)) {
    return err({ code: "MalformedArtifact", member: "contractVersion" });
  }
  if (major !== SUPPORTED_CONTRACT_MAJOR) {
    return err({
      code: "UnsupportedContractVersion",
      found: contractVersion,
      supported: `${SUPPORTED_CONTRACT_MAJOR}.x`,
    });
  }

  return ok(candidate as unknown as ContractPackage);
}

/** `null` rather than a failure: an unmatched segment is `unknown_operation`, which the caller
 *  raises with the correlation it already holds. */
export function findRow(contract: ContractPackage, operation: OperationId): OperationRow | null {
  return contract.operations.find((row) => (row.operation as string) === (operation as string)) ?? null;
}

/** No default branch and no fallback entry — a code with no mapping is a defect the generation
 *  gate should already have caught, and the one thing it must not become is an unattributed 500. */
export function statusFor(contract: ContractPackage, code: WireErrorCode): Outcome<HttpStatus, ContractLoadError> {
  const entry = contract.statusMapping.entries.find((candidate) => candidate.code === code);
  if (!entry) {
    return err({ code: "UnmappedErrorCode", wireErrorCode: code as string });
  }
  return ok(entry.status);
}
