/**
 * Reading the determinism dump back — the harness's side of `writeDeterminismDump`. An absent
 * dump is never read as an empty one (`CompositionError.DumpWriteFailed`'s own rule, restated
 * here for the reader): a dump that was never written and a dump that describes an empty store
 * are different facts, and this module keeps them distinguishable.
 */
import { existsSync, readFileSync } from "node:fs";
import { err, ok } from "./types.js";
import type { DeterminismDump, DumpReadError, Outcome, StoreSerializationSnapshot } from "./types.js";

function isStringRecord(value: unknown): value is Record<string, string> {
  return (
    typeof value === "object" &&
    value !== null &&
    !Array.isArray(value) &&
    Object.values(value as Record<string, unknown>).every((entry) => typeof entry === "string")
  );
}

/** Parses already-read bytes. `contents` is bytes rather than a path because that is what
 *  `20-contract.md` declares — the file-absence question the path form of "over an absent file"
 *  asks is answered by `readDeterminismDumpFile` below, before there are any bytes to hand this. */
export function readDeterminismDump(contents: Uint8Array): Outcome<StoreSerializationSnapshot, DumpReadError> {
  let parsed: unknown;
  try {
    parsed = JSON.parse(new TextDecoder().decode(contents));
  } catch {
    return err({ code: "DumpMalformed" });
  }

  if (typeof parsed !== "object" || parsed === null || Array.isArray(parsed)) {
    return err({ code: "DumpMalformed" });
  }
  const candidate = parsed as Partial<DeterminismDump>;
  if (!isStringRecord(candidate.sessions) || !isStringRecord(candidate.saves)) {
    return err({ code: "DumpMalformed" });
  }

  return ok({
    sessions: Object.entries(candidate.sessions).map(([id, blob]) => ({ id, blob })),
    saves: Object.entries(candidate.saves).map(([id, blob]) => ({ id, blob })),
  });
}

/** The path-taking convenience S5's `HostedTarget.readDump()` calls through. Absence is checked
 *  here, before `readDeterminismDump` — its signature takes bytes, and there are none to offer
 *  for a file that does not exist. */
export function readDeterminismDumpFile(path: string): Outcome<StoreSerializationSnapshot, DumpReadError> {
  if (!existsSync(path)) {
    return err({ code: "DumpAbsent" });
  }
  return readDeterminismDump(readFileSync(path));
}
