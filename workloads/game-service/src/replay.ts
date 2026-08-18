/**
 * The proof harness — S5. `runInProcess` and `runHosted` drive the identical fixture through the
 * identical `Dispatcher` code, differing only in transport: run 1 calls it directly, run 2 crosses
 * the socket a real `startWorkload` process binds. Neither retries and neither normalizes, because
 * a byte-identity suite that can be told what to skip stops comparing anything.
 */
import { createDispatcher } from "./dispatch.js";
import { compose } from "./compose.js";
import { canonicalEncode } from "./canonical.js";
import { err, ok } from "./types.js";
import type {
  CanonicalJson,
  ComparisonResult,
  Divergence,
  HostedTarget,
  JsonValue,
  Outcome,
  ReplayError,
  ReplayFixture,
  RunResult,
  StoreSerializationSnapshot,
  Transcript,
  ValidatedArguments,
  WorkloadConfiguration,
} from "./types.js";
import type { ContractPackage } from "@subzerodev/service-contract";

/** The fixed instant the replay profile's clock reports for both runs — unchanging, and never
 *  compared (`20-contract.md`'s own reasoning for `fixedInstant`), so any ISO-8601 value works. */
export const REPLAY_FIXED_INSTANT = "2026-01-01T00:00:00.000Z";

/** The contract's own `wireVersion` — G1 pins exactly one, so this is a lookup rather than a
 *  negotiation (`20-contract.md`, *Workload — request context*). `runHosted` addresses the path
 *  this way rather than taking the contract, on the same terms its own signature does. */
const WIRE_VERSION = "v1";

function checkCoverage(fixture: ReplayFixture, contract: ContractPackage): Outcome<void, ReplayError> {
  const tableOperations = new Set(contract.operations.map((row) => row.operation as string));
  const fixtureOperations = new Set(fixture.steps.map((step) => step.operation as string));

  const onlyInFixture = [...fixtureOperations].filter((operation) => !tableOperations.has(operation));
  const onlyInTable = [...tableOperations].filter((operation) => !fixtureOperations.has(operation));

  if (onlyInFixture.length > 0 || onlyInTable.length > 0) {
    return err({ code: "CoverageIncomplete", onlyInFixture, onlyInTable });
  }
  return ok(undefined);
}

/** Composes the engine and the store directly and drives them through a `Dispatcher` — the same
 *  projection and canonical encoding `runHosted`'s surface uses, so only the transport differs
 *  (`20-contract.md`, additions requiring a decision-log entry, item 2). It does not call the
 *  store's methods itself. */
export async function runInProcess(
  fixture: ReplayFixture,
  contract: ContractPackage,
): Promise<Outcome<RunResult, ReplayError>> {
  const coverage = checkCoverage(fixture, contract);
  if (!coverage.ok) return coverage;

  const configuration: WorkloadConfiguration = {
    listen: { host: "127.0.0.1", port: 0 },
    // `dumpPath` is unused by `compose()` — only `writeDeterminismDump` reads it, and this run
    // never calls it — but the type requires one, so a placeholder that is never opened is honest.
    determinism: { kind: "replay", fixedInstant: REPLAY_FIXED_INSTANT, dumpPath: "(runInProcess: unused)" },
    otlpEndpoint: null,
    storage: { kind: "in-memory" },
  };

  const composed = await compose(configuration, contract);
  if (!composed.ok) {
    return err({ code: "Composition", cause: composed.error });
  }

  const dispatcher = createDispatcher(contract, composed.value.stores.forRequest());
  const transcript: CanonicalJson[] = [];

  for (const [index, step] of fixture.steps.entries()) {
    const outcome = await dispatcher.invoke(step.operation, step.arguments as ValidatedArguments);
    if (outcome.kind === "error") {
      return err({ code: "StepFailed", step: index, operation: step.operation as string, wireErrorCode: outcome.code as string });
    }
    const encoded = canonicalEncode(outcome.value);
    if (!encoded.ok) {
      return err({ code: "StepFailed", step: index, operation: step.operation as string, wireErrorCode: encoded.error.code });
    }
    transcript.push(encoded.value);
  }

  const serialization = await composed.value.serialization.snapshot();
  return ok({ transcript, serialization });
}

/** Sends strictly sequentially — each response is fully read before the next request is sent, and
 *  there is no concurrency option to turn that off (invariant 34, S5.7). The last thing this does
 *  is shut the target down and read its dump, which is what makes `RunResult.serialization` the
 *  hosted run's own — the dump is a file the workload writes, never a value read out of memory. */
export async function runHosted(
  fixture: ReplayFixture,
  target: HostedTarget,
): Promise<Outcome<RunResult, ReplayError>> {
  const transcript: CanonicalJson[] = [];

  // Shuts the target down on every path out of this function, including a step failure — the
  // doc comment's "the last thing this does is shut the target down" applies to a failed run as
  // much as a passing one, or the process the failure path abandons keeps its socket bound.
  async function bail(error: ReplayError): Promise<Outcome<RunResult, ReplayError>> {
    await target.shutdown();
    return err(error);
  }

  for (const [index, step] of fixture.steps.entries()) {
    let response: Response;
    try {
      response = await fetch(`${target.baseAddress}/${WIRE_VERSION}/${step.operation as string}`, {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify(step.arguments),
      });
    } catch (thrown) {
      return bail({ code: "TransportFailure", detail: thrown instanceof Error ? thrown.message : String(thrown) });
    }

    // Fully read before the next request is sent — the sequencing this criterion is about, not
    // merely the absence of a stated concurrency option.
    const body = await response.text();

    if (response.status !== 200) {
      let code = "unknown";
      try {
        code = (JSON.parse(body) as { code?: string }).code ?? code;
      } catch {
        // A non-JSON error body is itself a StepFailed below, under the unknown code.
      }
      if (code === "unknown_operation") {
        return bail({ code: "UnknownOperationInFixture", step: index, operation: step.operation as string });
      }
      return bail({ code: "StepFailed", step: index, operation: step.operation as string, wireErrorCode: code });
    }

    transcript.push(body as CanonicalJson);
  }

  const shutdown = await target.shutdown();
  if (!shutdown.ok) {
    return err({ code: "Shutdown", cause: shutdown.error });
  }

  const dump = await target.readDump();
  if (!dump.ok) {
    return err({ code: "DumpRead", cause: dump.error });
  }

  return ok({ transcript, serialization: dump.value });
}

function blobLocator(kind: "sessions" | "saves", index: number): string {
  return `${kind}[${index}]`;
}

function blobText(blob: { readonly id: string; readonly blob: string } | undefined): string {
  return blob ? `${blob.id}:${blob.blob}` : "(absent)";
}

/** A byte comparison and nothing else — no ignore-list, no normalization, no options parameter
 *  (invariant 36). Compares `blob` strings directly, ordered the way `compose()`'s snapshot and
 *  `readDeterminismDump` both already sort: ascending by id. */
export function compareSerializations(
  expected: StoreSerializationSnapshot,
  actual: StoreSerializationSnapshot,
): ComparisonResult {
  for (const kind of ["sessions", "saves"] as const) {
    const expectedBlobs = expected[kind];
    const actualBlobs = actual[kind];
    const length = Math.max(expectedBlobs.length, actualBlobs.length);

    for (let index = 0; index < length; index += 1) {
      const expectedBlob = expectedBlobs[index];
      const actualBlob = actualBlobs[index];
      if (expectedBlob?.id !== actualBlob?.id || expectedBlob?.blob !== actualBlob?.blob) {
        const divergence: Divergence = {
          locator: blobLocator(kind, index),
          expected: blobText(expectedBlob),
          actual: blobText(actualBlob),
        };
        return { matched: false, firstDivergence: divergence };
      }
    }
  }
  return { matched: true, firstDivergence: null };
}

/** Compares encoded strings, entry for entry, in order. */
export function compareTranscripts(expected: Transcript, actual: Transcript): ComparisonResult {
  const length = Math.max(expected.length, actual.length);
  for (let index = 0; index < length; index += 1) {
    const expectedEntry = expected[index];
    const actualEntry = actual[index];
    if (expectedEntry !== actualEntry) {
      const divergence: Divergence = {
        locator: `[${index}]`,
        expected: expectedEntry ?? "(absent)",
        actual: actualEntry ?? "(absent)",
      };
      return { matched: false, firstDivergence: divergence };
    }
  }
  return { matched: true, firstDivergence: null };
}

export { readDeterminismDump } from "./dump.js";
