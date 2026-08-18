/**
 * Test support for S3. Nothing here asserts; it builds the pieces the criteria address —
 * a surface over a store the test controls, and a real composed surface over the real engine.
 */
import { loadPublishedContract } from "@subzerodev/service-contract";
import type { ContractPackage } from "@subzerodev/service-contract";
import type { SessionStore } from "@the-running-dev/game-engine";
import { SessionStoreError } from "@the-running-dev/game-engine";
import type { SessionStoreErrorCode } from "@the-running-dev/game-engine";

import { buildHttpSurface } from "../../src/http-surface.js";
import { buildMcpSurface } from "../../src/mcp-surface.js";
import { createDispatcher } from "../../src/dispatch.js";
import { ok } from "../../src/types.js";
import type {
  Dispatcher,
  HttpSurface,
  LifecycleProbe,
  McpSurface,
  StoreProvider,
  WireRequest,
  WireResponse,
} from "../../src/types.js";

export const contract: ContractPackage = loadPublishedContract();

export interface Call {
  readonly method: string;
  readonly args: readonly unknown[];
}

/** A `SessionStore` that records every invocation, so "the store is never called" is asserted
 *  against evidence rather than inferred (S3.4, S3.11). Every method fails unless the test
 *  supplies a behaviour, because a test that reaches the store by accident should be loud. */
export function recordingStore(overrides: Partial<SessionStore> = {}): {
  store: SessionStore;
  calls: Call[];
} {
  const calls: Call[] = [];
  const methodNames = contract.operations.map((row) => row.storeMethod as unknown as keyof SessionStore);
  const store = {} as Record<string, unknown>;

  for (const name of methodNames) {
    const override = (overrides as Record<string, unknown>)[name as string];
    store[name as string] = (...args: unknown[]) => {
      calls.push({ method: name as string, args });
      if (typeof override === "function") {
        return (override as (...a: unknown[]) => unknown)(...args);
      }
      throw new Error(`test store has no behaviour for ${String(name)}`);
    };
  }

  return { store: store as unknown as SessionStore, calls };
}

/** A store whose named method throws the engine's own `SessionStoreError` (S3.5). */
export function throwingStore(method: string, code: SessionStoreErrorCode): {
  store: SessionStore;
  calls: Call[];
} {
  return recordingStore({
    [method]: () => {
      throw new SessionStoreError(method, code);
    },
  } as Partial<SessionStore>);
}

/** The in-memory profile's classification — every id `absent`, so an engine code passes through
 *  verbatim. It is the default here because every test written before S5 predates the probe and
 *  asserts the engine's own code (`compose.ts`, the in-memory branch). */
export const absentProbe: LifecycleProbe = {
  session: async () => ok("absent"),
  save: async () => ok("absent"),
};

/** One `SessionStore` behind the provider seam: these tests control the store directly, and a
 *  provider that returns it every call is what `compose()`'s in-memory branch builds too. */
function providerOf(store: SessionStore): StoreProvider {
  return { forRequest: () => store };
}

/** The `Dispatcher` `surfaceOver` builds, for the tests that construct a surface over a deliberately
 *  broken operation table and never dispatch through it. */
export function dispatcherOver(
  store: SessionStore,
  source: ContractPackage = contract,
  lifecycle: LifecycleProbe = absentProbe,
): Dispatcher {
  return createDispatcher(source, providerOf(store), lifecycle);
}

export function surfaceOver(
  store: SessionStore,
  source: ContractPackage = contract,
  lifecycle: LifecycleProbe = absentProbe,
): HttpSurface {
  const built = buildHttpSurface(source, dispatcherOver(store, source, lifecycle));
  if (!built.ok) {
    throw new Error(`buildHttpSurface failed: ${JSON.stringify(built.error)}`);
  }
  return built.value;
}

/** The same `Dispatcher` construction `surfaceOver` uses, so a test that builds both surfaces over
 *  the same store is asserting "one store" rather than assuming it (S6.2). */
export function mcpSurfaceOver(
  store: SessionStore,
  source: ContractPackage = contract,
  lifecycle: LifecycleProbe = absentProbe,
): McpSurface {
  const built = buildMcpSurface(source, dispatcherOver(store, source, lifecycle));
  if (!built.ok) {
    throw new Error(`buildMcpSurface failed: ${JSON.stringify(built.error)}`);
  }
  return built.value;
}

export function wireRequest(
  path: string,
  body: unknown,
  headers: ReadonlyMap<string, string> = new Map(),
): WireRequest {
  return {
    method: "POST",
    path,
    headers,
    body: new TextEncoder().encode(body === undefined ? "" : JSON.stringify(body)),
  };
}

export function bodyText(response: WireResponse): string {
  return new TextDecoder().decode(response.body);
}

export function bodyJson(response: WireResponse): Record<string, unknown> {
  return JSON.parse(bodyText(response)) as Record<string, unknown>;
}

export async function post(
  surface: HttpSurface,
  path: string,
  body: unknown,
  headers?: ReadonlyMap<string, string>,
): Promise<WireResponse> {
  return surface.handle(wireRequest(path, body, headers));
}
