/**
 * Dispatch — one `Dispatcher` over one `SessionStore`, shared by every surface.
 *
 * It holds no game logic: it does not retry, does not reinterpret a code, does not decide which
 * actions are available, and caches nothing. It takes the store rather than the composition, so it
 * has no path to the serialization handle (invariant 17).
 *
 * Two things happen here and nowhere else. **Binding** turns a validated request object into the
 * store method's own call, inverting the generator's projection rule exactly: a single
 * object-shaped parameter *is* the request body (`createSession(config)`), so the whole validated
 * object is passed; otherwise the request schema's top-level members are the parameter names in
 * declaration order and are passed positionally. **Projection** applies the row's response
 * narrowings — Dispatch's, not each surface's, which is what makes MCP inherit the wire's
 * narrowings by mechanism rather than by convention (invariants 22, 23).
 */
import type { ContractPackage, OperationRow } from "@subzerodev/service-contract";
import type { EngineErrorCode } from "@subzerodev/service-contract";
import { SessionStoreError } from "@the-running-dev/game-engine";
import type { SessionStore } from "@the-running-dev/game-engine";
import { findRow } from "./contract.js";
import type { DispatchOutcome, Dispatcher, JsonObject, JsonValue, OperationId, ValidatedArguments } from "./types.js";

/** The request schema's top-level member names, in the order the generator emitted them — which is
 *  the order the store method declares its parameters. `null` when the schema is a `$ref` to an
 *  object type, meaning the body is that object and the method takes it whole. */
function parameterNames(contract: ContractPackage, row: OperationRow): readonly string[] | null {
  const schema = contract.schemas.find((candidate) => (candidate.$id as string) === (row.requestShape as string));
  if (!schema) return null;
  if (typeof schema["$ref"] === "string") return null;
  const properties = schema["properties"];
  if (typeof properties !== "object" || properties === null || Array.isArray(properties)) {
    return [];
  }
  return Object.keys(properties);
}

function project(value: JsonValue, row: OperationRow): JsonValue {
  const dropped = row.narrowings.filter((narrowing) => narrowing.side === "response").map((narrowing) => narrowing.field);
  if (dropped.length === 0) return value;
  if (typeof value !== "object" || value === null || Array.isArray(value)) return value;

  const projected: Record<string, JsonValue> = {};
  for (const [member, held] of Object.entries(value as JsonObject)) {
    if (dropped.includes(member)) continue;
    projected[member] = held;
  }
  return projected as JsonObject;
}

export function createDispatcher(contract: ContractPackage, store: SessionStore): Dispatcher {
  return {
    async invoke(operation: OperationId, args: ValidatedArguments): Promise<DispatchOutcome> {
      const row = findRow(contract, operation);
      if (!row) {
        // Unreachable through either surface — both resolve the row before dispatching — and a
        // throw rather than a silent result keeps it that way.
        throw new Error(`dispatch reached with no row for ${operation as string}`);
      }

      const names = parameterNames(contract, row);
      const invocation = (store as unknown as Record<string, (...a: unknown[]) => unknown>)[row.storeMethod as string];
      if (typeof invocation !== "function") {
        throw new Error(`the store declares no ${row.storeMethod as string}`);
      }

      const positional =
        names === null ? [args] : names.map((name) => (args as JsonObject)[name]).filter((held) => held !== undefined);

      try {
        const returned = await invocation.apply(store, positional);
        return { kind: "result", value: project(returned as JsonValue, row) };
      } catch (thrown) {
        // The engine's own `SessionStoreError` is the single exception that crosses a boundary as
        // a throw, because no `SessionStore` signature has an error channel. It is converted here
        // and never travels further; everything else is an internal failure and is not caught.
        if (thrown instanceof SessionStoreError) {
          return { kind: "error", code: thrown.code as unknown as EngineErrorCode };
        }
        throw thrown;
      }
    },
  };
}
