import { readFileSync, readdirSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";

const srcRoot = join(dirname(fileURLToPath(import.meta.url)), "..");

function readAll(
  extensions: readonly string[],
): { file: string; text: string }[] {
  const out: { file: string; text: string }[] = [];
  function walk(dir: string) {
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
      const full = join(dir, entry.name);
      if (entry.isDirectory()) {
        walk(full);
      } else if (
        // Excludes this file itself and every other test file: their own
        // literal strings (the forbidden tokens, spelled out to check for
        // them) would otherwise flag themselves.
        !/\.test\.tsx?$/.test(entry.name) &&
        extensions.some((ext) => entry.name.endsWith(ext))
      ) {
        out.push({ file: full, text: readFileSync(full, "utf8") });
      }
    }
  }
  walk(srcRoot);
  return out;
}

const cssFiles = readAll([".css"]);
const sourceFiles = readAll([".css", ".tsx", ".ts"]);
const allCss = cssFiles.map((f) => f.text).join("\n");

/**
 * SubZeroDev.GameEngine/site's own tokens and accent, transcribed here as
 * literal forbidden strings rather than read from that sibling repository at
 * test time — CI checks out this repository alone, so a test cannot depend
 * on the engine's repository being present on disk. design/40-site.md,
 * "Design distinctness" is what this file enforces.
 */
const ENGINE_ONLY_TOKENS = [
  "--landing-bg",
  "--landing-surface",
  "--landing-surface-raised",
  "--landing-text",
  "--landing-muted",
  "--landing-border-decorative",
  "--landing-border-strong",
  "--landing-accent",
  "--landing-accent-soft",
  "--landing-origin-text",
  "#82d8ff",
];

describe("design distinctness — checkable anti-criteria, not vibes", () => {
  it("reuses none of the engine's custom properties or its ice-blue accent, under any name", () => {
    for (const token of ENGINE_ONLY_TOKENS) {
      expect(allCss.includes(token)).toBe(false);
    }
  });

  it("has no reveal-on-scroll: no data-reveal, no scroll-driven motion class, no useRevealOnScroll", () => {
    for (const { file, text } of sourceFiles) {
      expect(text.includes("data-reveal"), file).toBe(false);
      expect(text.includes("useRevealOnScroll"), file).toBe(false);
      expect(text.includes("IntersectionObserver"), file).toBe(false);
    }
  });

  it("ports no motion.css and defines no hooks/ directory", () => {
    const files = readdirSync(srcRoot, { recursive: true }) as string[];
    expect(files.some((f) => f.toString().includes("motion.css"))).toBe(false);
    expect(files.some((f) => f.toString().includes("hooks"))).toBe(false);
  });

  it("is light by default: the base --bg is lighter than the dark-variant --bg", () => {
    const baseBlock = allCss.slice(
      0,
      allCss.indexOf("@media (prefers-color-scheme: dark)"),
    );
    const darkBlock = allCss.slice(
      allCss.indexOf("@media (prefers-color-scheme: dark)"),
    );

    const baseBg = /--bg:\s*(#[0-9a-fA-F]{6})/.exec(baseBlock)?.[1];
    const darkBg = /--bg:\s*(#[0-9a-fA-F]{6})/.exec(darkBlock)?.[1];
    expect(baseBg).toBeDefined();
    expect(darkBg).toBeDefined();
    expect(luminance(baseBg!)).toBeGreaterThan(luminance(darkBg!));
  });

  it("sets color-scheme to support both, never a hardcoded dark-only scheme", () => {
    // Matches the `color-scheme: dark;` property declaration only — not the
    // `@media (prefers-color-scheme: dark)` query, which is required.
    expect(/(?<!prefers-)color-scheme:\s*dark\s*;/.test(allCss)).toBe(false);
    expect(allCss.includes("color-scheme: light dark")).toBe(true);
  });

  it("defines exactly one animation, scoped to the live status dot, and disables it under prefers-reduced-motion", () => {
    const keyframeCount = (allCss.match(/@keyframes\s+[\w-]+/g) ?? []).length;
    expect(keyframeCount).toBe(1);
    expect(allCss.includes(".live-dot")).toBe(true);
    const reducedMotionBlock =
      /@media \(prefers-reduced-motion: reduce\) \{[\s\S]*?\}\s*\}/.exec(
        allCss,
      )?.[0];
    expect(reducedMotionBlock).toBeDefined();
    expect(reducedMotionBlock).toContain(".live-dot");
    expect(reducedMotionBlock).toContain("animation: none");
  });

  it("keeps every heading smaller than the engine's smallest heading (1.6rem)", () => {
    const fontSizes = [
      ...allCss.matchAll(
        /font-size:\s*(?:clamp\([^,]+,\s*[^,]+,\s*([\d.]+)rem\)|([\d.]+)rem)/g,
      ),
    ]
      .map((m) => Number(m[1] ?? m[2]))
      .filter((n) => !Number.isNaN(n));
    expect(fontSizes.length).toBeGreaterThan(0);
    for (const size of fontSizes) {
      expect(size).toBeLessThan(1.6);
    }
  });

  it("loads no webfont: no @font-face, no fonts.googleapis.com, no <link> to a font host", () => {
    for (const { file, text } of sourceFiles) {
      expect(text.includes("@font-face"), file).toBe(false);
      expect(text.includes("fonts.googleapis.com"), file).toBe(false);
      expect(text.includes("fonts.gstatic.com"), file).toBe(false);
    }
  });
});

function luminance(hex: string): number {
  const r = parseInt(hex.slice(1, 3), 16);
  const g = parseInt(hex.slice(3, 5), 16);
  const b = parseInt(hex.slice(5, 7), 16);
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}
