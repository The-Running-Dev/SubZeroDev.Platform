/**
 * The MCP surface. `listTools()` has exactly as many entries as the table has rows (S6.1), and
 * `callTool` validates against the same request schema and calls the same `Dispatcher` the HTTP
 * surface uses — no row-specific argument type and no MCP-specific path, because that is precisely
 * what must not exist.
 *
 * `callTool`'s signature carries no header or trace-context parameter, so unlike the HTTP surface's
 * correlation (adopted from `traceparent` when present) every MCP call mints a fresh one.
 */
import type { ContractPackage, McpToolName, OperationRow } from "@subzerodev/service-contract";
import { canonicalEncode } from "./canonical.js";
import { mintCorrelation } from "./correlation.js";
import { schemaPresent, validateRequest, validateResponse } from "./validate.js";
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

const INTERNAL_FAILURE = "internal_failure" as WireErrorCode;

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
          // The engine's code travels verbatim — no MCP-specific error vocabulary (S6.6).
          return errorOutcome(outcome.code as unknown as WireErrorCode);
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
