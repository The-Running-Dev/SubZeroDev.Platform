/**
 * S4.9 — the projection-boundary gate's structural half. `StoreSerializationHandle` is passed to
 * the shutdown writer and to the harness, and to nothing that builds a route: neither surface's
 * module graph may name it (invariant 17). Only the HTTP surface exists yet; S6 extends this same
 * check to the MCP surface.
 *
 * This walks the transitive closure of local (relative) imports reachable from the surface's
 * entry file, using the TypeScript parser rather than a hand-rolled regex — a regex misses mixed
 * default+named imports, re-exports (`export { X } from`), side-effect imports, and dynamic
 * `import()`, any of which would let a real violation through a passing test. `StoreSerializationHandle`
 * is declared in `types.ts`, which the surface's graph does reach — the check that matters is
 * narrower than file reachability: nothing in the graph may *import* the name.
 */
import { describe, expect, it } from "vitest";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import ts from "typescript";

const SRC_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), "../src");
const TARGET_NAME = "StoreSerializationHandle";

function resolveRelative(fromFile: string, specifier: string): string {
  const withoutExtension = specifier.replace(/\.js$/, "");
  return `${resolve(dirname(fromFile), withoutExtension)}.ts`;
}

function parse(file: string): ts.SourceFile {
  return ts.createSourceFile(file, readFileSync(file, "utf8"), ts.ScriptTarget.Latest, true, ts.ScriptKind.TS);
}

/** Every local (relative) module specifier this file's import/export/dynamic-import statements
 *  name, regardless of form — named, default, namespace, side-effect, or re-export all push their
 *  target onto the traversal, unlike a regex keyed to one import shape. */
function localSpecifiers(source: ts.SourceFile): string[] {
  const specifiers: string[] = [];
  const visit = (node: ts.Node): void => {
    if (
      (ts.isImportDeclaration(node) || ts.isExportDeclaration(node)) &&
      node.moduleSpecifier &&
      ts.isStringLiteral(node.moduleSpecifier)
    ) {
      specifiers.push(node.moduleSpecifier.text);
    } else if (
      ts.isCallExpression(node) &&
      node.expression.kind === ts.SyntaxKind.ImportKeyword &&
      node.arguments[0] &&
      ts.isStringLiteral(node.arguments[0])
    ) {
      specifiers.push((node.arguments[0] as ts.StringLiteral).text);
    }
    ts.forEachChild(node, visit);
  };
  ts.forEachChild(source, visit);
  return specifiers.filter((specifier) => specifier.startsWith("."));
}

/** True if this file's own text can bind `target` — a named import, a named re-export, or (since
 *  a namespace import or `export *` makes every export of its target reachable through a property
 *  access this AST walk does not resolve) a literal-identifier scan whenever either wildcard form
 *  is present. Recall over precision: this is a security-boundary gate, and a wildcard import that
 *  merely mentions the name in an unrelated comment is a false positive worth accepting. */
function namesTarget(source: ts.SourceFile, target: string): boolean {
  let sawWildcard = false;
  let namedDirectly = false;
  const visit = (node: ts.Node): void => {
    if (ts.isImportDeclaration(node) && node.importClause) {
      const bindings = node.importClause.namedBindings;
      if (bindings && ts.isNamedImports(bindings)) {
        for (const element of bindings.elements) {
          if (element.name.text === target) namedDirectly = true;
        }
      }
      if (bindings && ts.isNamespaceImport(bindings)) sawWildcard = true;
    }
    if (ts.isExportDeclaration(node)) {
      if (node.exportClause && ts.isNamedExports(node.exportClause)) {
        for (const element of node.exportClause.elements) {
          if (element.name.text === target) namedDirectly = true;
        }
      } else if (!node.exportClause) {
        sawWildcard = true; // `export * from "..."` re-exports everything
      }
    }
    ts.forEachChild(node, visit);
  };
  ts.forEachChild(source, visit);
  if (namedDirectly) return true;
  return sawWildcard && new RegExp(`\\b${target}\\b`).test(source.getFullText());
}

function reachesTarget(entryFile: string, target: string): boolean {
  const visited = new Set<string>();
  const stack = [resolve(entryFile)];

  while (stack.length > 0) {
    const file = stack.pop()!;
    if (visited.has(file)) continue;
    visited.add(file);

    const source = parse(file);
    if (namesTarget(source, target)) return true;
    for (const specifier of localSpecifiers(source)) {
      stack.push(resolveRelative(file, specifier));
    }
  }
  return false;
}

const ENGINE_MODULE = "@the-running-dev/game-engine";

/** True if this file has a *runtime* (value-carrying) dependency on the engine package — a whole
 *  `import type {...}` declaration never reaches the runtime, and neither does an individual
 *  specifier marked `type` inside an otherwise-ordinary import (`import { type X, y } from`,
 *  where only `y` would be a violation). Store may depend on the engine's types; S3.14 (invariant
 *  58) is that its module graph never depends on the engine's runtime entry point.
 *
 *  Three forms besides an ordinary runtime import reach the runtime and are checked here too: a
 *  named/wildcard *re-export* (`export {...} from`/`export * from`, mirroring `namesTarget` above),
 *  a dynamic `import()` (mirroring `localSpecifiers`' own handling of it), and a bare side-effect
 *  import (`import "..."`, whose `importClause` is `undefined` and which runs the target's
 *  top-level code by itself). */
function hasRuntimeEngineImport(source: ts.SourceFile): boolean {
  let found = false;
  const isEngineSpecifier = (specifier: ts.Expression): boolean =>
    ts.isStringLiteral(specifier) && specifier.text === ENGINE_MODULE;
  const visit = (node: ts.Node): void => {
    if (ts.isImportDeclaration(node) && isEngineSpecifier(node.moduleSpecifier)) {
      if (!node.importClause) {
        found = true; // bare `import "..."` — a side effect, not type-only by definition
      } else if (!node.importClause.isTypeOnly) {
        const bindings = node.importClause.namedBindings;
        if (bindings && ts.isNamedImports(bindings)) {
          for (const element of bindings.elements) {
            if (!element.isTypeOnly) found = true;
          }
        } else {
          // A default import or a namespace import, neither marked type-only.
          found = true;
        }
        if (node.importClause.name) found = true; // `import Foo, { ... } from "..."`
      }
    }
    if (ts.isExportDeclaration(node) && node.moduleSpecifier && isEngineSpecifier(node.moduleSpecifier)) {
      if (!node.isTypeOnly) {
        if (!node.exportClause) {
          found = true; // `export * from "..."` re-exports every runtime binding
        } else if (ts.isNamedExports(node.exportClause)) {
          for (const element of node.exportClause.elements) {
            if (!element.isTypeOnly) found = true;
          }
        }
      }
    }
    if (
      ts.isCallExpression(node) &&
      node.expression.kind === ts.SyntaxKind.ImportKeyword &&
      node.arguments[0] &&
      isEngineSpecifier(node.arguments[0])
    ) {
      found = true; // dynamic `import("...")` always evaluates the target's runtime
    }
    ts.forEachChild(node, visit);
  };
  ts.forEachChild(source, visit);
  return found;
}

function reachesRuntimeEngineImport(entryFile: string): boolean {
  const visited = new Set<string>();
  const stack = [resolve(entryFile)];

  while (stack.length > 0) {
    const file = stack.pop()!;
    if (visited.has(file)) continue;
    visited.add(file);

    const source = parse(file);
    if (hasRuntimeEngineImport(source)) return true;
    for (const specifier of localSpecifiers(source)) {
      stack.push(resolveRelative(file, specifier));
    }
  }
  return false;
}

describe("S3.14 — Store's module graph imports only the engine's type declarations", () => {
  it("names no runtime (value) import of @the-running-dev/game-engine anywhere in its transitive module graph", () => {
    expect(reachesRuntimeEngineImport(resolve(SRC_ROOT, "store.ts"))).toBe(false);
  });
});

describe("S4.9 — the HTTP surface's module graph does not reach StoreSerializationHandle", () => {
  it("names no import of StoreSerializationHandle anywhere in its transitive module graph", () => {
    expect(reachesTarget(resolve(SRC_ROOT, "http-surface.ts"), TARGET_NAME)).toBe(false);
  });
});

describe("S6.8 — the MCP surface's module graph does not reach StoreSerializationHandle", () => {
  it("names no import of StoreSerializationHandle anywhere in its transitive module graph", () => {
    expect(reachesTarget(resolve(SRC_ROOT, "mcp-surface.ts"), TARGET_NAME)).toBe(false);
  });
});
