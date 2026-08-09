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
import { createDispatcher } from "../../src/dispatch.js";
import type { HttpSurface, WireRequest, WireResponse } from "../../src/types.js";

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

export function surfaceOver(store: SessionStore, source: ContractPackage = contract): HttpSurface {
  const built = buildHttpSurface(source, createDispatcher(source, store));
  if (!built.ok) {
    throw new Error(`buildHttpSurface failed: ${JSON.stringify(built.error)}`);
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
