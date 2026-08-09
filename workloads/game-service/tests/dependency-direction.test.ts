/**
 * S4.9 — the projection-boundary gate's structural half. `StoreSerializationHandle` is passed to
 * the shutdown writer and to the harness, and to nothing that builds a route: neither surface's
 * module graph may name it (invariant 17). Only the HTTP surface exists yet; S6 extends this same
 * check to the MCP surface.
 *
 * This walks the transitive closure of local (relative) imports reachable from the surface's
 * entry file and collects every named import encountered anywhere in it. `StoreSerializationHandle`
 * is declared in `types.ts`, which the surface's graph does reach — the check that matters is
 * narrower than file reachability: nothing in the graph may *import* the name.
 */
import { describe, expect, it } from "vitest";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const SRC_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), "../src");

const IMPORT_RE = /import\s+(?:type\s+)?(?:\{([^}]*)\}|[\w*]+)\s+from\s+["']([^"']+)["']/g;

function resolveRelative(fromFile: string, specifier: string): string {
  const withoutExtension = specifier.replace(/\.js$/, "");
  return `${resolve(dirname(fromFile), withoutExtension)}.ts`;
}

function collectNamedImports(entryFile: string): Set<string> {
  const visited = new Set<string>();
  const named = new Set<string>();
  const stack = [resolve(entryFile)];

  while (stack.length > 0) {
    const file = stack.pop()!;
    if (visited.has(file)) continue;
    visited.add(file);

    const text = readFileSync(file, "utf8");
    for (const match of text.matchAll(IMPORT_RE)) {
      const [, names, specifier] = match;
      if (names) {
        for (const raw of names.split(",")) {
          const name = raw.trim().split(/\s+as\s+/)[0]?.trim();
          if (name) named.add(name);
        }
      }
      if (specifier?.startsWith(".")) {
        stack.push(resolveRelative(file, specifier));
      }
    }
  }

  return named;
}

describe("S4.9 — the HTTP surface's module graph does not reach StoreSerializationHandle", () => {
  it("names no import of StoreSerializationHandle anywhere in its transitive module graph", () => {
    const named = collectNamedImports(resolve(SRC_ROOT, "http-surface.ts"));
    expect(named.has("StoreSerializationHandle")).toBe(false);
  });
});
