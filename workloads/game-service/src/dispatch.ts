/**
 * Dispatch — one `Dispatcher` over one `StoreProvider`, shared by every surface.
 *
 * It holds no game logic: it does not retry, does not reinterpret a code, does not decide which
 * actions are available, and caches nothing. It takes the provider and the lifecycle probe rather
 * than the composition, so it has no path to the serialization handle (invariant 17), and it
 * resolves `forRequest()` once per request — a store handed out at wiring time would outlive the
 * request whose read versions it holds, which is the whole point of the per-request seam.
 *
 * Three things happen here and nowhere else. **Binding** turns a validated request object into the
 * store method's own call, inverting the generator's projection rule exactly: a single
 * object-shaped parameter *is* the request body (`createSession(config)`), so the whole validated
 * object is passed; otherwise the request schema's top-level members are the parameter names in
 * declaration order and are passed positionally. **Projection** applies the row's response
 * narrowings — Dispatch's, not each surface's, which is what makes MCP inherit the wire's
 * narrowings by mechanism rather than by convention (invariants 22, 23). **Expiry classification**
 * turns the engine's `unknown_session`/`unknown_save` into `session_expired`/`save_expired` when
 * the lifecycle probe says the row is expired-and-retained, and leaves the engine's own code alone
 * in every other case.
 */
import type { ContractPackage, OperationRow } from "@subzerodev/service-contract";
import type { EngineErrorCode } from "@subzerodev/service-contract";
import { SessionStoreError } from "@the-running-dev/game-engine";
import type { SessionStore } from "@the-running-dev/game-engine";
import { findRow } from "./contract.js";
import type {
  DispatchOutcome,
  Dispatcher,
  JsonObject,
  JsonValue,
  LifecycleProbe,
  OperationId,
  StoreProvider,
  ValidatedArguments,
} from "./types.js";

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

/** The two engine codes expiry classification applies to, each with the request member naming the
 *  id, the probe method that classifies it, and the code an expired-and-retained row answers with.
 *  Every other engine code travels verbatim and never reaches the probe. */
const CLASSIFIED = [
  { engineCode: "unknown_session", member: "sessionId", expiredCode: "session_expired", probe: "session" },
  { engineCode: "unknown_save", member: "saveId", expiredCode: "save_expired", probe: "save" },
] as const;

function idFrom(args: ValidatedArguments, member: string): string | null {
  const held = (args as JsonObject)[member];
  return typeof held === "string" ? held : null;
}

/**
 * The engine's code, or the expiry code the probe's classification calls for.
 *
 * A probe that fails — an `Outcome` error or a rejection — reads as `absent`, so the engine's own
 * code passes through (invariant 96). Escalating to `storage_failure` would convert an honest `404`
 * into an outage code on the one path that reaches the probe, precisely when the store is degraded.
 */
async function classify(
  lifecycle: LifecycleProbe,
  code: string,
  args: ValidatedArguments,
): Promise<string> {
  const rule = CLASSIFIED.find((candidate) => candidate.engineCode === code);
  if (!rule) return code;

  const id = idFrom(args, rule.member);
  if (id === null) return code;

  try {
    const classification = await lifecycle[rule.probe](id);
    return classification.ok && classification.value === "expired" ? rule.expiredCode : code;
  } catch {
    return code;
  }
}

export function createDispatcher(
  contract: ContractPackage,
  stores: StoreProvider,
  lifecycle: LifecycleProbe,
): Dispatcher {
  return {
    async invoke(operation: OperationId, args: ValidatedArguments): Promise<DispatchOutcome> {
      const row = findRow(contract, operation);
      if (!row) {
        // Unreachable through either surface — both resolve the row before dispatching — and a
        // throw rather than a silent result keeps it that way.
        throw new Error(`dispatch reached with no row for ${operation as string}`);
      }

      // Once per request, and never held past it: the adapter behind this store carries the read
      // versions the guarded write compares against, and a cache that outlived one request would
      // make the compare-and-swap answer stale reads.
      const store: SessionStore = stores.forRequest();

      const names = parameterNames(contract, row);
      const invocation = (store as unknown as Record<string, (...a: unknown[]) => unknown>)[row.storeMethod as string];
      if (typeof invocation !== "function") {
        throw new Error(`the store declares no ${row.storeMethod as string}`);
      }

      // `undefined` in a non-final slot must stay in that slot — dropping it would shift every
      // later argument left. Passing it through explicitly is harmless: the store method reads it
      // as an omitted optional parameter either way.
      const positional = names === null ? [args] : names.map((name) => (args as JsonObject)[name]);

      try {
        const returned = await invocation.apply(store, positional);
        return { kind: "result", value: project(returned as JsonValue, row) };
      } catch (thrown) {
        // The engine's own `SessionStoreError` is the single exception that crosses a boundary as
        // a throw, because no `SessionStore` signature has an error channel. It is converted here
        // and never travels further; everything else is an internal failure and is not caught.
        // Nothing is retried — not a conflict, not a `storage_failure`: a retried `submitAction`
        // is a second action, and merging two is unavailable.
        if (thrown instanceof SessionStoreError) {
          const code = await classify(lifecycle, thrown.code as string, args);
          return { kind: "error", code: code as unknown as EngineErrorCode };
        }
        throw thrown;
      }
    },
  };
}
