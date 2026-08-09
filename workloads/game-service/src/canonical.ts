/**
 * `canonicalEncode` — the wire's only encoder.
 *
 * The rule is the engine's canonical serialization rule, restated here because the engine does not
 * export its `canonicalStringify` (`90-decisions.md`, 2026-08-09). That is a second copy of one
 * rule and a known drift hazard; comparison B is what fails loudly if the two ever disagree.
 *
 * JSON, object members ascending by code unit, no insignificant whitespace, members whose value is
 * `undefined` omitted, non-finite numbers rejected rather than coerced.
 */
import type { CanonicalJson } from "@subzerodev/service-contract";
import { err, ok } from "./types.js";
import type { EncodingError, JsonValue, Outcome } from "./types.js";

class EncodingRejection extends Error {
  constructor(readonly failure: EncodingError) {
    super(failure.code);
  }
}

function encode(value: unknown, locator: string): string {
  if (value === null) return "null";

  switch (typeof value) {
    case "boolean":
      return value ? "true" : "false";
    case "number":
      if (!Number.isFinite(value)) {
        throw new EncodingRejection({ code: "NonFiniteNumber", locator });
      }
      return JSON.stringify(value);
    case "string":
      return JSON.stringify(value);
    case "object":
      break;
    default:
      // bigint, function, symbol, undefined — none is a `JsonValue`.
      throw new EncodingRejection({ code: "UnsupportedValue", locator });
  }

  if (Array.isArray(value)) {
    const items = value.map((item, index) => encode(item, `${locator}[${index}]`));
    return `[${items.join(",")}]`;
  }

  const source = value as Record<string, unknown>;
  const members: string[] = [];
  // Ascending by code unit — `Array.prototype.sort`'s default comparison is exactly that, and it
  // is what makes "keyed by id, in id order" a property of the encoding rather than of a caller.
  for (const member of Object.keys(source).sort()) {
    const held = source[member];
    if (held === undefined) continue;
    members.push(`${JSON.stringify(member)}:${encode(held, `${locator}.${member}`)}`);
  }
  return `{${members.join(",")}}`;
}

export function canonicalEncode(value: JsonValue): Outcome<CanonicalJson, EncodingError> {
  try {
    return ok(encode(value, "$") as CanonicalJson);
  } catch (thrown) {
    if (thrown instanceof EncodingRejection) {
      return err(thrown.failure);
    }
    throw thrown;
  }
}
