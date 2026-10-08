import { readdirSync, readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
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

// Cases: 7 total — 2 positive (a well-formed document parses; adding or
// removing a slice heading changes the parsed count with no code edit) and
// 5 negative (no headings at all, headings that never match the slice
// pattern, a missing Status line, an unrecognised status value, a missing
// Depends-on line — each throws rather than returning an empty or partial
// result).
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

// Cases: 6 total — 3 positive (a Landed row and a Landed bullet each parse
// as shipped with no dependency, alongside heading slices; a bullet's PR is
// the one after 'via'; a Landed bullet outside '## Landed' is not counted)
// and 3 negative (the same id as a heading and a Landed row, as two Landed
// bullets, and a ledger with neither headings nor Landed entries — each
// throws rather than counting a slice twice or returning an empty roadmap).
describe("the retired forms under '## Landed'", () => {
  const ledger = `# Slices — commercial (D5)

## Outstanding

S3 is next.

## S3 — Third slice
**Status:** in progress
Status: todo

Depends on: none

## Landed

| Slice | Name | Issue | Criteria | Body complete at |
|---|---|---|---|---|
| **S2** | Second slice | [#2](https://github.com/example/repo/issues/2), closed | S2.1–S2.3 | \`abc1234\` |

- **S1 — First slice** — shipped:
  [#1](https://github.com/example/repo/issues/1) via
  [#11](https://github.com/example/repo/pull/11) and
  [#12](https://github.com/example/repo/pull/12).
`;

  it("parses a Landed row and a Landed bullet as shipped, with no dependency line and no throw", () => {
    const result = parseSlices(ledger);
    expect(result.map((s) => [s.id, s.title, s.status, s.dependsOn])).toEqual([
      ["S1", "First slice", "shipped", ""],
      ["S2", "Second slice", "shipped", ""],
      ["S3", "Third slice", "in-progress", "none"],
    ]);
    expect(() => assertConsistent(result)).not.toThrow();
  });

  it("takes a bullet's pull request from after 'via', not its issue link", () => {
    const [first, second] = parseSlices(ledger);
    expect(first.pr).toEqual({
      number: "11",
      url: "https://github.com/example/repo/pull/11",
    });
    expect(second.pr).toBeUndefined();
  });

  it("counts Landed entries only under '## Landed'", () => {
    const elsewhere = ledger.replace(
      "S3 is next.",
      "S3 is next.\n\n- **S9 — Mentioned slice** — shipped: elsewhere.",
    );
    expect(parseSlices(elsewhere).map((s) => s.id)).toEqual(["S1", "S2", "S3"]);
  });

  it("throws when an id is both a heading and a Landed row", () => {
    const twice = ledger.replace(
      "| **S2** | Second slice",
      "| **S3** | Third slice",
    );
    expect(() => parseSlices(twice)).toThrow(
      /S3 appears twice — as a heading and as a Landed row/,
    );
  });

  it("throws when an id appears as two Landed bullets", () => {
    const twice = `${ledger}- **S1 — First slice again** — shipped: again.\n`;
    expect(() => parseSlices(twice)).toThrow(/S1 appears twice/);
  });

  it("throws on a ledger with neither slice headings nor Landed entries", () => {
    const empty =
      "# Slices\n\n## Landed\n\nNothing yet.\n\n## Outstanding\n\nNone.\n";
    expect(() => parseSlices(empty)).toThrow(/no 'S<n> — ' slice headings/);
  });
});

// Cases: 5 total — 2 positive (one in-progress slice with queued slices
// after it; all-shipped with none in progress and none queued) and 3
// negative (more than one in-progress slice, zero in-progress while a
// queued slice exists, a queued slice ordered before a shipped one).
describe("assertConsistent — the invariants Test-SliceStatusMarkers.ps1 also checks", () => {
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

describe("the active design/30-slices.md — the ledger the site renders", () => {
  const activeRaw = readFileSync(
    join(
      dirname(fileURLToPath(import.meta.url)).replace(
        /[\\/]src[\\/]roadmap$/,
        "",
      ),
      "..",
      "design",
      "30-slices.md",
    ),
    "utf8",
  );

  // One test, so a merge that breaks the count or the markers fails here at
  // the causing commit. The expected numbers are read from the ledger's text
  // directly, not from the parser under test.
  it("counts every slice once, from S1 to the highest id the ledger names, and is consistent", () => {
    const highest = Math.max(
      ...[...activeRaw.matchAll(/\bS(\d+)\b/g)].map((m) => Number(m[1])),
    );
    const landed = activeRaw.slice(activeRaw.indexOf("\n## Landed"));
    const shippedInLedger =
      [...activeRaw.matchAll(/^\*\*Status:\*\*\s*shipped/gm)].length +
      [...landed.matchAll(/^\|\s*\*\*S\d+\*\*\s*\|/gm)].length +
      [...landed.matchAll(/^- \*\*S\d+ — .+?\*\* — shipped:/gm)].length;

    expect(totalCount).toBe(highest);
    expect(slices.map((s) => s.id)).toEqual(
      Array.from({ length: highest }, (_, i) => `S${i + 1}`),
    );
    expect(shippedCount).toBe(shippedInLedger);
    expect(shippedCount).toBe(shippedSlices.length);
    expect(
      shippedSlices.length + (currentSlice ? 1 : 0) + queuedSlices.length,
    ).toBe(totalCount);
    expect(parseSlices(activeRaw)).toEqual(slices);
    expect(() => assertConsistent(slices)).not.toThrow();
  });

  it("is the only ledger any module under site/src imports — none reaches into design/d3", () => {
    const srcRoot = dirname(fileURLToPath(import.meta.url)).replace(
      /[\\/]roadmap$/,
      "",
    );
    const offenders = readdirSync(srcRoot, { recursive: true })
      .map(String)
      .filter((file) => /\.(ts|tsx)$/.test(file))
      .filter((file) =>
        /^\s*import[^;]*["'][^"']*design\/d3\//m.test(
          readFileSync(join(srcRoot, file), "utf8"),
        ),
      );
    expect(offenders).toEqual([]);
  });
});
