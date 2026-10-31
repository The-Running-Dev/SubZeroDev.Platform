/** Frozen old code, current candidate code, separate pools, one PostgreSQL schema. */
import { createHash } from "node:crypto";
import { readFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import * as candidateEngine from "@the-running-dev/game-engine";
import * as candidateAuthoring from "@the-running-dev/game-engine/authoring";
import * as previousEngine from "@the-running-dev/game-engine-previous";
import * as previousAuthoring from "@the-running-dev/game-engine-previous/authoring";
import * as releaseEngine from "@the-running-dev/game-engine-previous-release";
import * as releaseAuthoring from "@the-running-dev/game-engine-previous-release/authoring";
import type { SessionPersistence } from "@the-running-dev/game-engine";
import type { ProfileStore as OldProfiles, SessionPersistence as OldPersistence } from "@the-running-dev/game-engine-previous";
import type { ProfileStore as ReleaseProfiles, SessionPersistence as ReleasePersistence } from "@the-running-dev/game-engine-previous-release";
import { migrateToHead } from "../../src/migrations.js";
import { openDurableStore } from "../../src/store.js";
import type { DurableStore, DurableStoreConfiguration, Outcome, StoreError } from "../../src/types.js";
import preUpgradeManifest from "../fixtures/pre-upgrade/manifest.json" with { type: "json" };
import releaseManifest from "../fixtures/previous-release/manifest.json" with { type: "json" };

export const PRE_UPGRADE_COMMIT = "5b587d6990552b85dbf31cb9d82c22869754ac78";
export const INSPECTED_UPGRADE_COMMIT = "d47a14ca4d366e0d5ca98adeef932ce8ab6173a6";
/** The last vendored release, re-pinned on every engine upgrade (`previous-release.test.ts`). */
export const PREVIOUS_RELEASE_COMMIT = "7c82c565a6c23743a5914fa2bdfe7fca5feb87e8";
export const PREVIOUS_RELEASE_CANDIDATE = "0.13.0";
export interface OldStore {
  persistenceForRequest(): OldPersistence;
  profiles: OldProfiles;
  close(): Promise<void>;
}
export interface ReleaseStore {
  persistenceForRequest(): ReleasePersistence;
  profiles: ReleaseProfiles;
  close(): Promise<void>;
}

const fixtures = resolve(dirname(fileURLToPath(import.meta.url)), "../fixtures");

async function loadPinned<Store>(
  name: string,
  manifest: { commit: string; engineVersion: string; files: { path: string; sha256: string }[] },
  commit: string,
  engine: typeof candidateEngine,
  authoring: typeof candidateAuthoring,
) {
  const fixture = resolve(fixtures, name);
  if (manifest.commit !== commit) throw new Error(`Unexpected ${name} adapter fixture commit`);
  for (const file of manifest.files) {
    const bytes = await readFile(resolve(fixture, file.path));
    if (createHash("sha256").update(bytes).digest("hex") !== file.sha256) {
      throw new Error(`Old fixture changed: ${name}/${file.path}; regenerate from the pinned commit`);
    }
  }
  const adapter = await import(/* @vite-ignore */ pathToFileURL(resolve(fixture, "src/store.js")).href) as {
    openDurableStore(configuration: DurableStoreConfiguration, version: string): Promise<Outcome<Store, StoreError>>;
  };
  const migrations = await import(/* @vite-ignore */ pathToFileURL(resolve(fixture, "src/migrations.js")).href) as {
    migrateToHead: typeof migrateToHead;
  };
  if (engine.ENGINE_VERSION !== manifest.engineVersion) throw new Error(`Unexpected ${name} engine version`);
  return { engine, authoring, ...adapter, ...migrations };
}

/** #353's pre-upgrade adapter. Pinned for its own regressions; it does not move on upgrades. */
export async function loadOldRuntime() {
  // The npm alias installs the actual old tarball and its own dependency graph. Cast only the
  // common composition calls used by sessionLayer; old persistence records stay separately typed.
  return loadPinned<OldStore>(
    "pre-upgrade", preUpgradeManifest, PRE_UPGRADE_COMMIT,
    previousEngine as unknown as typeof candidateEngine,
    previousAuthoring as unknown as typeof candidateAuthoring,
  );
}

/** The previous release's adapter and engine, which must be re-pinned on each engine upgrade. */
export async function loadReleaseRuntime() {
  if (candidateEngine.ENGINE_VERSION !== PREVIOUS_RELEASE_CANDIDATE) {
    throw new Error("Refresh the previous-release fixture for this engine upgrade");
  }
  return loadPinned<ReleaseStore>(
    "previous-release", releaseManifest, PREVIOUS_RELEASE_COMMIT,
    releaseEngine as unknown as typeof candidateEngine,
    releaseAuthoring as unknown as typeof candidateAuthoring,
  );
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
  persistence: OldPersistence | ReleasePersistence | SessionPersistence,
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
