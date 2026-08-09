/**
 * S4.7 — reading the determinism dump back. An absent dump is never read as an empty one, and a
 * truncated one is never read as an empty one either — both are failures, not a quiet default.
 */
import { describe, expect, it } from "vitest";
import { mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

import { readDeterminismDump, readDeterminismDumpFile } from "../src/dump.js";

function tempDirectory(): string {
  return mkdtempSync(join(tmpdir(), "dump-read-"));
}

describe("S4.7 — DumpAbsent and DumpMalformed", () => {
  it("returns DumpAbsent when no file exists at the path", () => {
    const missing = join(tempDirectory(), "does-not-exist.json");

    const result = readDeterminismDumpFile(missing);

    expect(result.ok).toBe(false);
    if (result.ok) return;
    expect(result.error.code).toBe("DumpAbsent");
  });

  it("returns DumpMalformed over a truncated file, never an empty snapshot", () => {
    const path = join(tempDirectory(), "truncated.json");
    writeFileSync(path, '{"sessions":{"a":"blob"');

    const result = readDeterminismDumpFile(path);

    expect(result.ok).toBe(false);
    if (result.ok) return;
    expect(result.error.code).toBe("DumpMalformed");
  });

  it("returns DumpMalformed when a required member is missing or the wrong shape", () => {
    const missingMember = readDeterminismDump(new TextEncoder().encode(JSON.stringify({ sessions: {} })));
    expect(missingMember.ok).toBe(false);

    const wrongShape = readDeterminismDump(new TextEncoder().encode(JSON.stringify({ sessions: [], saves: {} })));
    expect(wrongShape.ok).toBe(false);
  });

  it("parses a well-formed dump into a StoreSerializationSnapshot", () => {
    const encoded = new TextEncoder().encode(
      JSON.stringify({ sessions: { s1: "blob-1" }, saves: { v1: "blob-2" } }),
    );

    const result = readDeterminismDump(encoded);

    expect(result.ok).toBe(true);
    if (!result.ok) return;
    expect(result.value.sessions).toEqual([{ id: "s1", blob: "blob-1" }]);
    expect(result.value.saves).toEqual([{ id: "v1", blob: "blob-2" }]);
  });
});
