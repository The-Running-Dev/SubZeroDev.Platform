import { existsSync, readdirSync, readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";
import { routes } from "./shared";

const siteRoot = dirname(fileURLToPath(import.meta.url)).replace(
  /[\\/]src$/,
  "",
);
const docsRoot = join(siteRoot, "..", "docs", "docs");

/** Every real file under docs/docs/, exact case, no extension — mirrors how
 * Docusaurus derives a route from a file with no `slug:` frontmatter. */
function collectDocFiles(dir: string, prefix = ""): Set<string> {
  const found = new Set<string>();
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    if (entry.name.startsWith("_")) continue; // _category_.json, etc.
    const relPath = prefix ? `${prefix}/${entry.name}` : entry.name;
    if (entry.isDirectory()) {
      for (const nested of collectDocFiles(join(dir, entry.name), relPath)) {
        found.add(nested);
      }
    } else if (entry.name.endsWith(".md")) {
      found.add(relPath.replace(/\.md$/, ""));
    }
  }
  return found;
}

describe("routes — every /docs/ destination must resolve, matching case", () => {
  const docFiles = collectDocFiles(docsRoot);

  it("finds real documentation files to check against", () => {
    expect(docFiles.size).toBeGreaterThan(0);
  });

  for (const [key, path] of Object.entries(routes)) {
    if (path === "/docs/") continue; // the docs index itself, not a file route

    it(`${key} (${path}) resolves to a real file under docs/docs/, exact case`, () => {
      const withoutPrefix = path.replace(/^\/docs\//, "").replace(/\/$/, "");
      expect(docFiles.has(withoutPrefix)).toBe(true);
    });
  }

  it("has no route pointing at a file docs/docs/ does not have under any case", () => {
    const lowercasedDocFiles = new Set(
      [...docFiles].map((f) => f.toLowerCase()),
    );
    for (const path of Object.values(routes)) {
      if (path === "/docs/") continue;
      const withoutPrefix = path.replace(/^\/docs\//, "").replace(/\/$/, "");
      if (!docFiles.has(withoutPrefix)) {
        // A case-only mismatch is a sharper failure than "missing entirely".
        expect(lowercasedDocFiles.has(withoutPrefix.toLowerCase())).toBe(false);
      }
    }
  });
});

describe("no stray /docs/ destination outside the routes constant", () => {
  const srcRoot = join(siteRoot, "src");

  function collectSourceFiles(dir: string): string[] {
    const files: string[] = [];
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
      const full = join(dir, entry.name);
      if (entry.isDirectory()) {
        files.push(...collectSourceFiles(full));
      } else if (
        /\.(tsx?|css)$/.test(entry.name) &&
        entry.name !== "shared.tsx"
      ) {
        files.push(full);
      }
    }
    return files;
  }

  it('finds no literal "/docs/ string outside shared.tsx', () => {
    const offenders: string[] = [];
    for (const file of collectSourceFiles(srcRoot)) {
      if (/\.test\.tsx?$/.test(file)) continue;
      const text = readFileSync(file, "utf8");
      if (/["'`]\/docs\//.test(text)) {
        offenders.push(file);
      }
    }
    expect(offenders).toEqual([]);
  });
});

it("the docs root this test walks actually exists", () => {
  expect(existsSync(docsRoot)).toBe(true);
});
