/**
 * S13.8/S13.9 — the seam `SessionPersistence`, `ProfileStore` and `LifecycleProbe` are composed
 * behind (`20-contract.md`: invariant 74, "the lifecycle probe is composed behind the same seam
 * `SessionPersistence` and `ProfileStore` are"). `compose.ts`'s `composeStorageSeam` is the
 * structural mechanism; this asserts it directly with a counting decorator standing in for G3's
 * real authorization one, on both the shape any caller of the seam sees and — separately — that
 * `compose()`'s own in-memory branch (S13.9) routes its no-op probe through the identical function.
 */
import { describe, expect, it } from "vitest";
import { ok } from "../src/types.js";
import { composeStorageSeam, IDENTITY_STORAGE_DECORATOR, compose } from "../src/compose.js";
import type { StorageDecorator, StorageSeam } from "../src/compose.js";
import type { ContractPackage } from "@subzerodev/service-contract";
import { ENGINE_VERSION } from "@the-running-dev/game-engine";
import type { WorkloadConfiguration } from "../src/types.js";

function rawSeam(): StorageSeam {
  return {
    persistence: {
      sessions: { get: async () => undefined, put: async () => undefined },
      saves: { get: async () => undefined, put: async () => undefined, delete: async () => undefined },
    },
    profiles: {
      load: async (profileId: string) => ({ profile: { formatVersion: 1, profileId, achievements: [] }, warnings: [] }),
      save: async () => ({ ok: true, warnings: [] }),
    },
    lifecycle: {
      session: async () => ok("absent"),
      save: async () => ok("absent"),
    },
  };
}

describe("S13.8 — a decorator applied at the seam reaches persistence, profiles and lifecycle alike", () => {
  it("a counting decorator intercepts calls to all three ports of the returned seam", async () => {
    const counts = { persistence: 0, profiles: 0, lifecycle: 0 };
    const countingDecorator: StorageDecorator = (seam) => ({
      persistence: {
        sessions: {
          get: async (id: string) => {
            counts.persistence += 1;
            return seam.persistence.sessions.get(id);
          },
          put: seam.persistence.sessions.put,
        },
        saves: seam.persistence.saves,
      },
      profiles: {
        load: async (profileId: string) => {
          counts.profiles += 1;
          return seam.profiles.load(profileId);
        },
        save: seam.profiles.save,
      },
      lifecycle: {
        session: async (sessionId: string) => {
          counts.lifecycle += 1;
          return seam.lifecycle.session(sessionId);
        },
        save: seam.lifecycle.save,
      },
    });

    const decorated = composeStorageSeam(rawSeam(), countingDecorator);
    await decorated.persistence.sessions.get("s");
    await decorated.profiles.load("p");
    await decorated.lifecycle.session("s");

    expect(counts).toEqual({ persistence: 1, profiles: 1, lifecycle: 1 });
  });

  it("the identity decorator (G2's only one) changes nothing observable", async () => {
    const raw = rawSeam();
    const decorated = composeStorageSeam(raw, IDENTITY_STORAGE_DECORATOR);
    expect(decorated).toBe(raw);
  });

  it("composeStorageSeam defaults to the identity decorator when none is supplied", async () => {
    const raw = rawSeam();
    expect(composeStorageSeam(raw)).toBe(raw);
  });
});

describe("S13.9 — the in-memory configuration's no-op probe is composed behind the same seam", () => {
  it("compose()'s in-memory branch still reports absent for both session and save ids, unaffected by the seam", async () => {
    const contract = { engineVersion: ENGINE_VERSION } as unknown as ContractPackage;
    const configuration: WorkloadConfiguration = {
      listen: { host: "127.0.0.1", port: 0 },
      determinism: { kind: "default" },
      otlpEndpoint: null,
      storage: { kind: "in-memory" },
    };

    const composed = await compose(configuration, contract);
    expect(composed.ok).toBe(true);
    if (!composed.ok) return;
    try {
      const sessionClassification = await composed.value.lifecycle.session("never-existed");
      const saveClassification = await composed.value.lifecycle.save("never-existed");
      expect(sessionClassification).toEqual({ ok: true, value: "absent" });
      expect(saveClassification).toEqual({ ok: true, value: "absent" });
    } finally {
      await composed.value.close();
    }
  });
});
