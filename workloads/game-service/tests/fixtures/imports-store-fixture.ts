/**
 * S13.7's perturbation fixture — the one thing `dependency-direction.test.ts`'s Store check must
 * catch, and never a file either real surface (`http-surface.ts`, `mcp-surface.ts`) may resemble.
 * Not imported anywhere except by that test's own module-graph walk.
 */
export { openDurableStore } from "../../src/store.js";
