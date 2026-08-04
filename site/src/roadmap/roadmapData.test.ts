import { describe, expect, it } from "vitest";
import {
  assertConsistent,
  parseSlices,
  currentSlice,
  queuedSlices,
  shippedCount,
  shippedSlices,
  slices,
  totalCount,
  type Slice,
} from "./roadmapData";

const validFixture = `# Slices — the minimal package set (D3)

Some preamble text.

## S1 — First slice
**Status:** shipped · [#11](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/11)

Delivers: the first thing.

Depends on: none.

Acceptance:
- Something is true.

---

## S2 — Second slice
**Status:** in progress

Delivers: the second thing.

Depends on: S1.

---

## S3 — Third slice
**Status:** queued

Delivers: the third thing.

Depends on: S2.

---

## What each slice discharges

| Obligation | Slice |
|---|---|
| Something | S1 |
`;

describe("parseSlices — the fixture, so tests do not go red the day a real slice ships", () => {
  it("parses every slice heading, its status, its dependency, and stops before a non-slice heading", () => {
    const result = parseSlices(validFixture);
    expect(result).toHaveLength(3);
    expect(result.map((s) => s.id)).toEqual(["S1", "S2", "S3"]);
    expect(result[0].status).toBe("shipped");
    expect(result[0].pr).toEqual({
      number: "11",
      url: "https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/11",
    });
    expect(result[1].status).toBe("in-progress");
    expect(result[1].pr).toBeUndefined();
    expect(result[2].status).toBe("queued");
    expect(result[2].dependsOn).toBe("S2.");
  });

  it("adds a tenth slice with no site edit, and removing one removes it", () => {
    const withTenth = validFixture.replace(
      "## What each slice discharges",
      `## S10 — Tenth slice
**Status:** queued

Delivers: a tenth thing.

Depends on: S3.

---

## What each slice discharges`,
    );
    expect(parseSlices(withTenth)).toHaveLength(4);

    const withoutThird = validFixture.replace(
      /## S3 — Third slice[\s\S]*?(?=---\n\n## What each slice discharges)/,
      "",
    );
    expect(parseSlices(withoutThird)).toHaveLength(2);
  });

  it("throws on no '## ' headings at all, rather than returning an empty roadmap", () => {
    expect(() => parseSlices("just some prose, no headings")).toThrow(
      /no '## ' headings/,
    );
  });

  it("throws on '## ' headings that never match the slice pattern", () => {
    expect(() => parseSlices("## Not a slice\n\nSome text.\n")).toThrow(
      /no 'S<n> — ' slice headings/,
    );
  });

  it("throws when a slice is missing its Status line", () => {
    const broken = validFixture.replace(
      "**Status:** shipped · [#11](https://github.com/The-Running-Dev/SubZeroDev.Platform/pull/11)\n\n",
      "",
    );
    expect(() => parseSlices(broken)).toThrow(/S1 has no '\*\*Status:\*\*'/);
  });

  it("throws on an unrecognised status value, rather than silently defaulting to scheduled", () => {
    const broken = validFixture.replace(
      "**Status:** in progress",
      "**Status:** vibes",
    );
    expect(() => parseSlices(broken)).toThrow(/unrecognised status/);
  });

  it("throws when a slice is missing its Depends on: line", () => {
    const broken = validFixture.replace("Depends on: S1.\n\n", "");
    expect(() => parseSlices(broken)).toThrow(/S2 has no 'Depends on:' line/);
  });
});

describe("assertConsistent — the invariants Test-Documentation.ps1 also checks", () => {
  function slice(id: string, status: Slice["status"]): Slice {
    return {
      id,
      number: Number(id.slice(1)),
      title: id,
      status,
      dependsOn: "none",
    };
  }

  it("accepts one in-progress slice with queued slices after it", () => {
    expect(() =>
      assertConsistent([
        slice("S1", "shipped"),
        slice("S2", "in-progress"),
        slice("S3", "queued"),
      ]),
    ).not.toThrow();
  });

  it("accepts all-shipped with none in progress and none queued", () => {
    expect(() =>
      assertConsistent([slice("S1", "shipped"), slice("S2", "shipped")]),
    ).not.toThrow();
  });

  it("rejects more than one slice marked in progress", () => {
    expect(() =>
      assertConsistent([
        slice("S1", "in-progress"),
        slice("S2", "in-progress"),
      ]),
    ).toThrow(/more than one slice marked 'in progress'/);
  });

  it("rejects zero in-progress while a queued slice exists", () => {
    expect(() =>
      assertConsistent([slice("S1", "shipped"), slice("S2", "queued")]),
    ).toThrow(/no slice is marked 'in progress'/);
  });

  it("rejects a queued slice ordered before a shipped one", () => {
    expect(() =>
      assertConsistent([
        slice("S1", "queued"),
        slice("S2", "in-progress"),
        slice("S3", "shipped"),
      ]),
    ).toThrow(/ordered after a 'queued' slice/);
  });
});

describe("the real design/30-slices.md — the assertion that survives every future merge", () => {
  it("parses without throwing, and every slice carries a recognised status", () => {
    expect(slices.length).toBeGreaterThan(0);
    for (const slice of slices) {
      expect(["shipped", "in-progress", "queued"]).toContain(slice.status);
    }
  });

  it("keeps its derived counts arithmetically consistent", () => {
    expect(shippedCount).toBe(shippedSlices.length);
    expect(
      shippedSlices.length + (currentSlice ? 1 : 0) + queuedSlices.length,
    ).toBe(totalCount);
  });

  it("does not throw assertConsistent against its own current state", () => {
    expect(() => assertConsistent(slices)).not.toThrow();
  });
});
