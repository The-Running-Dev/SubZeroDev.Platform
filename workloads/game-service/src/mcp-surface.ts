/**
 * The MCP surface. `listTools()` has exactly as many entries as the table has rows (S6.1), and
 * `callTool` validates against the same request schema and calls the same `Dispatcher` the HTTP
 * surface uses — no row-specific argument type and no MCP-specific path, because that is precisely
 * what must not exist.
 *
 * **Known gap — the correlation, pending a contract amendment.** `20-contract.md` says two things
 * this surface cannot satisfy at once: "Workload — request context" has the MCP surface adopt
 * whatever `traceparent` the transport carried, and invariant 29 has every response carry the
 * correlation — while the `McpSurface` declared in "MCP surface — workload" gives `callTool` no
 * context parameter to receive one and `McpToolOutcome`'s result arm no member to return one. So
 * every call mints a fresh correlation that no inbound trace reaches, and a successful tool result
 * carries none at all. That is a contradiction inside the contract, not a shortcut taken here;
 * closing it is a contract amendment, tracked rather than silently reconciled.
 */
import type { ContractPackage, McpToolName, OperationRow } from "@subzerodev/service-contract";
import { canonicalEncode } from "./canonical.js";
import { mintCorrelation } from "./correlation.js";
import { schemaPresent, validateRequest, validateResponse, validatorsFor } from "./validate.js";
import { INTERNAL_FAILURE, resolvedCode } from "./wire-error.js";
import { err, ok } from "./types.js";
import type {
  Dispatcher,
  JsonValue,
  McpSurface,
  McpToolDescriptor,
  McpToolOutcome,
  Outcome,
  OperationId,
  SurfaceBuildError,
  WireErrorCode,
} from "./types.js";

export function buildMcpSurface(
  contract: ContractPackage,
  dispatcher: Dispatcher,
): Outcome<McpSurface, SurfaceBuildError> {
  const tools = new Map<string, OperationRow>();

  for (const row of contract.operations) {
    const name = row.mcpTool as string;
    const clash = tools.get(name);
    if (clash) {
      return err({ code: "DuplicateToolName", first: clash.operation as string, second: row.operation as string });
    }
    for (const reference of [row.requestShape as string, row.responseShape as string]) {
      if (!schemaPresent(contract, reference)) {
        return err({ code: "MissingSchema", operation: row.operation as string, reference });
      }
    }
    tools.set(name, row);
  }

  // Compiled before the bind, alongside the presence check above — a schema ajv cannot resolve is a
  // startup refusal, not a tool that fails the first time it is asked to validate anything
  // (invariant 19). `buildHttpSurface` forces the same compilation; neither surface may depend on
  // the other having been built first for the refusal to happen.
  try {
    validatorsFor(contract);
  } catch (thrown) {
    return err({ code: "SchemaCompile", detail: thrown instanceof Error ? thrown.message : String(thrown) });
  }

  const descriptors: readonly McpToolDescriptor[] = contract.operations.map((row) => ({
    name: row.mcpTool,
    inputSchema: contract.schemas.find((schema) => (schema.$id as string) === (row.requestShape as string))!,
  }));

  function errorOutcome(code: WireErrorCode): McpToolOutcome {
    return { kind: "error", error: { code, correlation: mintCorrelation() } };
  }

  const surface: McpSurface = {
    listTools(): readonly McpToolDescriptor[] {
      return descriptors;
    },

    async callTool(name: McpToolName, args: JsonValue): Promise<McpToolOutcome> {
      try {
        const row = tools.get(name as string);
        if (!row) {
          return errorOutcome("unknown_operation" as WireErrorCode);
        }

        const validated = validateRequest(contract, row, args);
        if (!validated.ok) {
          // Nothing happened: the store was never reached (S6.5).
          return errorOutcome("malformed_payload" as WireErrorCode);
        }

        const outcome = await dispatcher.invoke(row.operation as OperationId, validated.value);

        if (outcome.kind === "error") {
          // The engine's code travels verbatim — no MCP-specific error vocabulary (S6.6) — and a
          // code the mapping does not name resolves to the same `internal_failure` the JSON wire
          // answers with, so the two surfaces cannot disagree about a code the artifact left out.
          return errorOutcome(resolvedCode(contract, outcome.code as unknown as WireErrorCode));
        }

        const checked = validateResponse(contract, row, outcome.value);
        if (!checked.ok) {
          return errorOutcome(INTERNAL_FAILURE);
        }

        const encoded = canonicalEncode(outcome.value);
        if (!encoded.ok) {
          return errorOutcome(INTERNAL_FAILURE);
        }
        return { kind: "result", value: encoded.value };
      } catch {
        return errorOutcome(INTERNAL_FAILURE);
      }
    },
  };

  return ok(surface);
}
