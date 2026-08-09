/**
 * Request and response validation against the artifact's own schemas.
 *
 * Ajv on the 2020-12 dialect, which is the dialect the generated set declares and the validator
 * S2.8 already asserted loads all of them (`90-decisions.md`, 2026-08-09). `$id` and `$ref` are
 * identifiers here and are never dereferenced: `loadSchema` is left unset, so a schema ajv cannot
 * resolve locally is a compile failure rather than a fetch (invariant 9).
 */
import { Ajv2020 } from "ajv/dist/2020.js";
import type { ValidateFunction } from "ajv";
import type { ContractPackage, OperationRow } from "@subzerodev/service-contract";
import { err, ok } from "./types.js";
import type { JsonValue, Outcome, ValidatedArguments, ValidationFailure } from "./types.js";

const compiled = new WeakMap<ContractPackage, Map<string, ValidateFunction>>();

/** Compiled once per contract and cached — the compilation itself is what `buildHttpSurface` forces
 *  to happen before the bind, so a schema ajv cannot resolve is a startup refusal, not a first-use
 *  failure. */
export function validatorsFor(contract: ContractPackage): Map<string, ValidateFunction> {
  const existing = compiled.get(contract);
  if (existing) return existing;

  const ajv = new Ajv2020({ strict: false, allErrors: false, validateFormats: false });
  for (const schema of contract.schemas) {
    ajv.addSchema(schema as object, schema.$id as string);
  }

  const table = new Map<string, ValidateFunction>();
  for (const schema of contract.schemas) {
    const reference = schema.$id as string;
    table.set(reference, ajv.getSchema(reference) as ValidateFunction);
  }
  compiled.set(contract, table);
  return table;
}

/** True when the artifact carries a document for every shape the rows reference — the check
 *  `buildHttpSurface` runs before binding, kept beside the validators that need it. */
export function schemaPresent(contract: ContractPackage, reference: string): boolean {
  return contract.schemas.some((schema) => (schema.$id as string) === reference);
}

function check(
  contract: ContractPackage,
  reference: string,
  value: JsonValue,
): Outcome<void, ValidationFailure> {
  const validator = validatorsFor(contract).get(reference);
  if (!validator) {
    return err({ code: "SchemaViolation", detail: `no compiled validator for ${reference}` });
  }
  if (validator(value)) {
    return ok(undefined);
  }
  // The detail stays on this side of the wire — it reaches the log line the correlation
  // identifies and never the response body (`20-contract.md`, `ValidationFailure`).
  return err({ code: "SchemaViolation", detail: JSON.stringify(validator.errors ?? []) });
}

/** The one producer of `ValidatedArguments`, which is what makes "the engine is never reached on a
 *  malformed payload" structural rather than a sequencing convention. */
export function validateRequest(
  contract: ContractPackage,
  row: OperationRow,
  body: JsonValue,
): Outcome<ValidatedArguments, ValidationFailure> {
  const outcome = check(contract, row.requestShape as string, body);
  if (!outcome.ok) return outcome;
  return ok(body as ValidatedArguments);
}

/** Runs on every response. Generation proves the schema describes the type; it does not prove the
 *  handler returned that type unaltered, and the schema is closed, so an added member fails. */
export function validateResponse(
  contract: ContractPackage,
  row: OperationRow,
  value: JsonValue,
): Outcome<void, ValidationFailure> {
  return check(contract, row.responseShape as string, value);
}
