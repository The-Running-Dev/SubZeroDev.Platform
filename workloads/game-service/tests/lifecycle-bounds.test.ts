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

/**
 * The other two members of `LifecycleBounds`. They bound the sweep's own work rather than a row's
 * life, and they arrived with S13.4/S13.5 pinned by nothing at all — so an edit to either passed
 * every gate, which is how they came to be the two values no document named
 * (`design/90-decisions.md`, "The sweep's two bounds are the sweep's, not a row's").
 */
describe("the two sweep bounds are the production values, and are pinned by name", () => {
  it("names sweepIntervalSeconds as one hour", () => {
    expect(DEFAULT_LIFECYCLE_BOUNDS.sweepIntervalSeconds).toBe(60 * 60);
  });

  it("names sweepStatementTimeoutMs as 5 seconds", () => {
    expect(DEFAULT_LIFECYCLE_BOUNDS.sweepStatementTimeoutMs).toBe(5_000);
  });

  it("keeps the statement timeout well inside the interval, so a tick cannot outlive its own period", () => {
    // Not a tuned relationship — the point is only that the ordering holds. A timeout longer than
    // the interval would let the next tick's schedule elapse while its predecessor was still
    // running, which invariant 63 forbids and which `scheduleSweep`'s recursion currently prevents
    // by construction rather than by these two numbers.
    expect(DEFAULT_LIFECYCLE_BOUNDS.sweepStatementTimeoutMs).toBeLessThan(
      DEFAULT_LIFECYCLE_BOUNDS.sweepIntervalSeconds * 1000,
    );
  });
});

describe("S12.10 — nothing in the tree describes DEFAULT_LIFECYCLE_BOUNDS as non-production", () => {
  it("types.ts's own docstring calls it the production defaults, not 'non-production'", () => {
    const text = readFileSync(resolve(SRC_ROOT, "types.ts"), "utf8");
    expect(text).not.toContain("non-production");
    expect(text).toMatch(/production defaults[\s\S]{0,1000}export const DEFAULT_LIFECYCLE_BOUNDS/);
  });
});
