/** Frozen old code, current candidate code, separate pools, one PostgreSQL schema. */
import { createHash } from "node:crypto";
import { readFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import * as candidateEngine from "@the-running-dev/game-engine";
import * as candidateAuthoring from "@the-running-dev/game-engine/authoring";
import * as previousEngine from "@the-running-dev/game-engine-previous";
import * as previousAuthoring from "@the-running-dev/game-engine-previous/authoring";
import type { SessionPersistence } from "@the-running-dev/game-engine";
import type { ProfileStore as OldProfiles, SessionPersistence as OldPersistence } from "@the-running-dev/game-engine-previous";
import { migrateToHead } from "../../src/migrations.js";
import { openDurableStore } from "../../src/store.js";
import type { DurableStore, DurableStoreConfiguration, Outcome, StoreError } from "../../src/types.js";
import manifest from "../fixtures/pre-upgrade/manifest.json" with { type: "json" };

export const PRE_UPGRADE_COMMIT = "5b587d6990552b85dbf31cb9d82c22869754ac78";
export const INSPECTED_UPGRADE_COMMIT = "d47a14ca4d366e0d5ca98adeef932ce8ab6173a6";
export interface OldStore {
  persistenceForRequest(): OldPersistence;
  profiles: OldProfiles;
  close(): Promise<void>;
}

const fixture = resolve(dirname(fileURLToPath(import.meta.url)), "../fixtures/pre-upgrade");

export async function loadOldRuntime() {
  if (manifest.commit !== PRE_UPGRADE_COMMIT) throw new Error("Unexpected old adapter fixture commit");
  for (const file of manifest.files) {
    const bytes = await readFile(resolve(fixture, file.path));
    if (createHash("sha256").update(bytes).digest("hex") !== file.sha256) {
      throw new Error(`Old fixture changed: ${file.path}; regenerate from the pinned commit`);
    }
  }
  // The npm alias installs the actual old tarball and its own dependency graph. Cast only the
  // common composition calls used by sessionLayer; old persistence records stay separately typed.
  const engine = previousEngine as unknown as typeof candidateEngine;
  const authoring = previousAuthoring as unknown as typeof candidateAuthoring;
  const adapter = await import(/* @vite-ignore */ pathToFileURL(resolve(fixture, "src/store.js")).href) as {
    openDurableStore(configuration: DurableStoreConfiguration, version: string): Promise<Outcome<OldStore, StoreError>>;
  };
  const migrations = await import(/* @vite-ignore */ pathToFileURL(resolve(fixture, "src/migrations.js")).href) as {
    migrateToHead: typeof migrateToHead;
  };
  if (engine.ENGINE_VERSION !== manifest.engineVersion) throw new Error("Unexpected old engine version");
  if (candidateEngine.ENGINE_VERSION !== "0.12.0") throw new Error("Refresh mixed-version fixtures for this engine upgrade");
  return { engine, authoring, ...adapter, ...migrations };
}

export async function openCandidate(configuration: DurableStoreConfiguration): Promise<DurableStore> {
  const opened = await openDurableStore(configuration, candidateEngine.ENGINE_VERSION as Parameters<typeof openDurableStore>[1]);
  if (!opened.ok) throw new Error(JSON.stringify(opened.error));
  return opened.value;
}

/** Both versions build this same simple story-graph; no world-graph state-version changes or
 * profile-dependent initialization. Each call creates a fresh engine session cache/request. The
 * old engine is typed only at the shared entry points used here, never as a new persistence port. */
export function sessionLayer(
  runtime: { engine: typeof candidateEngine; authoring: typeof candidateAuthoring },
  persistence: OldPersistence | SessionPersistence,
) {
  const { engine: api, authoring } = runtime;
  const { content, authoredText } = authoring.buildStoryGraphCampaign({
    description: { key: "mixed.description", text: "Mixed-version fixture." },
    variables: {},
    achievements: [],
    startNodeId: "start",
    nodes: {
      start: {
        kind: "choice", text: { key: "mixed.start", text: "Start." },
        choices: [{ id: "stay", label: { key: "mixed.stay", text: "Stay." }, goto: "start" }],
      },
    },
  });
  const campaign = authoring.buildCampaign({
    id: "mixed-version", kindId: "story-graph", version: "1.0.0", titleKey: "mixed.title", content,
  }, [{ key: "mixed.title", text: "Mixed version" }, ...authoredText]);
  if (!campaign.ok || !campaign.value) throw new Error(JSON.stringify(campaign.errors));
  const registry = api.buildContentRegistry([campaign.value]);
  if (!registry.ok || !registry.value) throw new Error(JSON.stringify(registry.errors));
  const engine = api.createEngine({ kinds: { "story-graph": api.storyGraphKind } as candidateEngine.KindRegistry, registry: registry.value });
  return api.createSessionLayer({ engine, registry: registry.value, persistence: persistence as SessionPersistence });
}

export const candidateRuntime = { engine: candidateEngine, authoring: candidateAuthoring };
