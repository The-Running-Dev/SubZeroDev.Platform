/**
 * S12.9/S12.10 — `DEFAULT_LIFECYCLE_BOUNDS` is asserted to hold the production values, by name,
 * naming each one, and the retention horizon is asserted to no longer equal the save TTL
 * (`design/90-decisions.md`, "The retention horizon default no longer equals the save TTL": it was
 * 365 days, the same as `saveTtlSeconds`, where the contract's production default is 30).
 */
import { describe, expect, it } from "vitest";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

import { DEFAULT_LIFECYCLE_BOUNDS } from "../src/types.js";

const SRC_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), "../src");
const THIRTY_DAYS_SECONDS = 30 * 24 * 60 * 60;
const THREE_SIXTY_FIVE_DAYS_SECONDS = 365 * 24 * 60 * 60;

describe("S12.9 — the three default lifecycle bounds are the production values", () => {
  it("names sessionIdleTtlSeconds as 30 days", () => {
    expect(DEFAULT_LIFECYCLE_BOUNDS.sessionIdleTtlSeconds).toBe(THIRTY_DAYS_SECONDS);
  });

  it("names saveTtlSeconds as 365 days", () => {
    expect(DEFAULT_LIFECYCLE_BOUNDS.saveTtlSeconds).toBe(THREE_SIXTY_FIVE_DAYS_SECONDS);
  });

  it("names retentionHorizonSeconds as 30 days, no longer equal to the save TTL", () => {
    expect(DEFAULT_LIFECYCLE_BOUNDS.retentionHorizonSeconds).toBe(THIRTY_DAYS_SECONDS);
    expect(DEFAULT_LIFECYCLE_BOUNDS.retentionHorizonSeconds).not.toBe(DEFAULT_LIFECYCLE_BOUNDS.saveTtlSeconds);
  });
});

describe("S12.10 — nothing in the tree describes DEFAULT_LIFECYCLE_BOUNDS as non-production", () => {
  it("types.ts's own docstring calls it the production defaults, not 'non-production'", () => {
    const text = readFileSync(resolve(SRC_ROOT, "types.ts"), "utf8");
    expect(text).not.toContain("non-production");
    expect(text).toMatch(/production defaults[\s\S]{0,1000}export const DEFAULT_LIFECYCLE_BOUNDS/);
  });
});
