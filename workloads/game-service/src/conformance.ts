/**
 * Port conformance — `runPortConformance` and the two `ConformanceTarget` builders
 * (`20-contract.md`, "Proof harness — `ConformanceTarget`, `runPortConformance`"; `30-slices.md`,
 * S9).
 *
 * One assertion set, run against whatever target it is handed (S9.1's "one assertion set, run
 * over two targets" framing) — the test file calls this once against the in-memory target and
 * once against the durable one, and both must come back `ok: true`. `sessions.get/put` and
 * `saves.get/put` are asserted identical by construction (the same checks run against both); the
 * three `profiles.load` outcomes and the additive/replace merge divergence are each targeted at
 * the specific behaviour `20-contract.md` documents for that target.
 */
import {
  buildContentRegistry,
  createEngine,
  createInMemoryProfileStore,
  createSessionLayer,
  storyGraphKind,
} from "@the-running-dev/game-engine";
import { buildCampaign, buildStoryGraphCampaign } from "@the-running-dev/game-engine/authoring";
import type {
  AchievementRecord,
  ContentRegistry,
  KindRegistry,
  PlayerProfile,
  ProfileStore,
  SessionPersistence,
  SessionStore,
  StoredSaveRecord,
  StoredSessionRecord,
} from "@the-running-dev/game-engine";
import type { Campaign, StoryGraphCampaignSource } from "@the-running-dev/game-engine/authoring";
import { Pool } from "pg";
import { quoteIdentifier } from "./migrations.js";
import { IMPLICIT_TENANT_ID, openDurableStore } from "./store.js";
import { err, ok } from "./types.js";
import type { ConformanceError, ConformanceTarget, DurableStoreConfiguration, Outcome, SemanticVersion } from "./types.js";

let counter = 0;

/** Unique enough within one conformance run — every scenario below needs its own fresh id so
 *  one check's seeding never contaminates another's. */
function freshId(prefix: string): string {
  counter += 1;
  return `${prefix}-${process.pid}-${counter}-${Math.random().toString(36).slice(2)}`;
}

const FIXTURE_CAMPAIGN_ID = "conformance-fixture-campaign";

// ---------------------------------------------------------------------------- S9.1, S9.8: sessions and saves

/** `StoredSessionRecord`'s own declared members (`@the-running-dev/game-engine`'s
 *  `core/session/types.ts`) — S9.8 asserts nothing else ever appears in a returned record, which
 *  is what "no host column leaks onto the port" means in practice. */
const STORED_SESSION_RECORD_KEYS = new Set([
  "sessionId",
  "blob",
  "audience",
  "attemptCounter",
  "replayCompatible",
  "createdAt",
  "updatedAt",
  "profileId",
]);

async function checkSessionsAndSaves(target: ConformanceTarget): Promise<Outcome<void, ConformanceError>> {
  const sessionId = freshId("conformance-session");
  // Deliberately not ASCII-only and not JSON-trivial — a byte-identity check that only ever
  // round-trips `"{}"` would pass for reasons that have nothing to do with byte identity.
  const blob = JSON.stringify({ marker: freshId("blob"), nested: [1, 2, 3], text: "byte check — π≈3.14159, 世界" });
  const sessionRecord: StoredSessionRecord = {
    sessionId,
    blob,
    audience: "player",
    attemptCounter: 1,
    replayCompatible: true,
    createdAt: "2026-01-01T00:00:00.000Z",
    updatedAt: "2026-01-01T00:00:00.000Z",
  };
  await target.persistence.sessions.put(sessionRecord);
  const gotSession = await target.persistence.sessions.get(sessionId);
  if (gotSession === undefined) {
    return err({ code: "MethodDiverged", method: "sessions.get", target: target.label, detail: "a session just put through sessions.put was not found by sessions.get" });
  }
  if (gotSession.blob !== blob) {
    return err({ code: "MethodDiverged", method: "sessions.put/get", target: target.label, detail: "the round-tripped session blob was not byte-identical to what was written" });
  }
  const extraSessionKeys = Object.keys(gotSession).filter((key) => !STORED_SESSION_RECORD_KEYS.has(key));
  if (extraSessionKeys.length > 0) {
    return err({
      code: "MethodDiverged",
      method: "sessions.get",
      target: target.label,
      detail: `host column(s) leaked onto the returned StoredSessionRecord's own key set: ${extraSessionKeys.join(", ")}`,
    });
  }

  const saveId = freshId("conformance-save");
  const saveBlob = JSON.stringify({ marker: freshId("save-blob") });
  const saveRecord: StoredSaveRecord = {
    saveId,
    campaignId: FIXTURE_CAMPAIGN_ID,
    blob: saveBlob,
    savedAt: "2026-01-01T00:00:00.000Z",
    savedAtSeq: 1,
    audience: "player",
  };
  await target.persistence.saves.put(saveRecord);
  const gotSave = await target.persistence.saves.get(saveId);
  if (gotSave === undefined || gotSave.blob !== saveBlob) {
    return err({ code: "MethodDiverged", method: "saves.put/get", target: target.label, detail: "a save just put through saves.put did not round-trip byte-identically through saves.get" });
  }

  return ok(undefined);
}

// ---------------------------------------------------------------------------- S13.6: saves.delete, the seventh method

async function checkSavesDelete(target: ConformanceTarget): Promise<Outcome<void, ConformanceError>> {
  const saveId = freshId("conformance-delete-save");
  const savedAt = "2026-01-01T00:00:00.000Z";
  const saveRecord: StoredSaveRecord = {
    saveId,
    campaignId: FIXTURE_CAMPAIGN_ID,
    blob: JSON.stringify({ marker: freshId("delete-blob") }),
    savedAt,
    savedAtSeq: 1,
    audience: "player",
  };
  await target.persistence.saves.put(saveRecord);
  await target.persistence.saves.delete(saveId, savedAt);
  const afterDelete = await target.persistence.saves.get(saveId);
  if (afterDelete !== undefined) {
    return err({
      code: "MethodDiverged",
      method: "saves.delete",
      target: target.label,
      detail: "a save put and then deleted was still returned by saves.get",
    });
  }

  // Deleting an absent id is a no-op that fails neither.
  try {
    await target.persistence.saves.delete(freshId("conformance-never-existed-save"), savedAt);
  } catch {
    return err({
      code: "MethodDiverged",
      method: "saves.delete",
      target: target.label,
      detail: "deleting an absent save id failed instead of being a no-op",
    });
  }
  return ok(undefined);
}

// ---------------------------------------------------------------------------- SaveRecordStore.delete's D2 precondition

/** `20-contract.md`, invariant D2: a `delete` whose `expectedSavedAt` no longer matches the stored
 *  value removes nothing. Proven directly against the port rather than inferred from S13.6 above,
 *  which only ever deletes with the value it just put. */
async function checkSaveDeleteConditional(target: ConformanceTarget): Promise<Outcome<void, ConformanceError>> {
  const saveId = freshId("conformance-conditional-delete-save");
  const saveRecord: StoredSaveRecord = {
    saveId,
    campaignId: FIXTURE_CAMPAIGN_ID,
    blob: JSON.stringify({ marker: freshId("conditional-delete-blob") }),
    savedAt: "2026-01-01T00:00:00.000Z",
    savedAtSeq: 1,
    audience: "player",
  };
  await target.persistence.saves.put(saveRecord);

  // A stale `expectedSavedAt` — the value this save no longer carries — removes nothing.
  await target.persistence.saves.delete(saveId, "2025-01-01T00:00:00.000Z");
  const afterStaleDelete = await target.persistence.saves.get(saveId);
  if (afterStaleDelete === undefined) {
    return err({
      code: "MethodDiverged",
      method: "saves.delete",
      target: target.label,
      detail: "a delete whose expectedSavedAt did not match the stored value removed the save anyway",
    });
  }

  // The matching value deletes it, on the same footing as S13.6.
  await target.persistence.saves.delete(saveId, saveRecord.savedAt);
  const afterMatchingDelete = await target.persistence.saves.get(saveId);
  if (afterMatchingDelete !== undefined) {
    return err({
      code: "MethodDiverged",
      method: "saves.delete",
      target: target.label,
      detail: "a delete whose expectedSavedAt matched the stored value left the save in place",
    });
  }
  return ok(undefined);
}

// ---------------------------------------------------------------------------- SaveRecordStore.listByProfile

async function checkSaveListByProfile(target: ConformanceTarget): Promise<Outcome<void, ConformanceError>> {
  const profileId = freshId("conformance-list-profile");
  const otherProfileId = freshId("conformance-list-other-profile");
  const ownSaveIds = [freshId("conformance-list-save"), freshId("conformance-list-save")];
  for (const saveId of ownSaveIds) {
    await target.persistence.saves.put({
      saveId,
      campaignId: FIXTURE_CAMPAIGN_ID,
      blob: JSON.stringify({ marker: freshId("list-blob") }),
      savedAt: "2026-01-01T00:00:00.000Z",
      savedAtSeq: 1,
      audience: "player",
      profileId,
    });
  }
  const otherSaveId = freshId("conformance-list-other-save");
  await target.persistence.saves.put({
    saveId: otherSaveId,
    campaignId: FIXTURE_CAMPAIGN_ID,
    blob: JSON.stringify({ marker: freshId("list-other-blob") }),
    savedAt: "2026-01-01T00:00:00.000Z",
    savedAtSeq: 1,
    audience: "player",
    profileId: otherProfileId,
  });

  const listed = await target.persistence.saves.listByProfile(profileId);
  const listedIds = listed.map((record) => record.saveId).sort();
  if (JSON.stringify(listedIds) !== JSON.stringify([...ownSaveIds].sort())) {
    return err({
      code: "MethodDiverged",
      method: "saves.listByProfile",
      target: target.label,
      detail: `listByProfile returned ${JSON.stringify(listedIds)}, expected exactly ${JSON.stringify([...ownSaveIds].sort())}`,
    });
  }
  return ok(undefined);
}

// ---------------------------------------------------------------------------- S9.2, S9.3: profiles.load outcomes

async function seed(
  target: ConformanceTarget,
  method: "seedCorruptProfile" | "seedProfileWriteFailure",
  profileId: string,
): Promise<Outcome<void, ConformanceError>> {
  try {
    await target[method](profileId);
    return ok(undefined);
  } catch {
    // S9.9: a target unable to honour either seed method fails the suite naming it, rather than
    // the caller silently treating the scenario it was meant to seed as untested.
    return err({ code: "SeamUnavailable", method });
  }
}

async function checkProfileCorrupt(target: ConformanceTarget): Promise<Outcome<void, ConformanceError>> {
  const profileId = freshId("conformance-corrupt-profile");
  const seeded = await seed(target, "seedCorruptProfile", profileId);
  if (!seeded.ok) return seeded;

  const loaded = await target.profiles.load(profileId);
  const corrupt = loaded.warnings.some((warning) => warning.code === "profile_corrupt");
  if (!corrupt || loaded.profile.achievements.length !== 0) {
    return err({
      code: "MethodDiverged",
      method: "profiles.load",
      target: target.label,
      detail: `a profile seeded via seedCorruptProfile did not load as profile_corrupt with an empty achievement set (warnings: ${JSON.stringify(loaded.warnings)}, achievements: ${JSON.stringify(loaded.profile.achievements)})`,
    });
  }
  return ok(undefined);
}

async function checkProfileMissing(target: ConformanceTarget): Promise<Outcome<void, ConformanceError>> {
  const profileId = freshId("conformance-missing-profile");
  const loaded = await target.profiles.load(profileId);
  const missing = loaded.warnings.some((warning) => warning.code === "profile_missing");
  if (!missing || loaded.profile.achievements.length !== 0) {
    return err({
      code: "MethodDiverged",
      method: "profiles.load",
      target: target.label,
      detail: `an unseeded profileId did not load as profile_missing with an empty achievement set (warnings: ${JSON.stringify(loaded.warnings)}, achievements: ${JSON.stringify(loaded.profile.achievements)})`,
    });
  }
  return ok(undefined);
}

// ---------------------------------------------------------------------------- S9.4: profiles.save write failure

async function checkProfileWriteFailure(target: ConformanceTarget): Promise<Outcome<void, ConformanceError>> {
  const profileId = freshId("conformance-write-failure-profile");
  const seeded = await seed(target, "seedProfileWriteFailure", profileId);
  if (!seeded.ok) return seeded;

  const sessionId = freshId("conformance-write-failure-session");
  const sessionRecord: StoredSessionRecord = {
    sessionId,
    blob: "committed-before-the-profile-write",
    audience: "player",
    attemptCounter: 1,
    replayCompatible: true,
    createdAt: "2026-01-01T00:00:00.000Z",
    updatedAt: "2026-01-01T00:00:00.000Z",
  };
  // "A session write issued in the same operation before the profile write" — the two ports
  // offer no single composite operation, so the closest faithful rendering is issuing the
  // session write first and then the profile write, exactly as the engine's own `submitAction`
  // does (session write, then `upsertAchievements`).
  await target.persistence.sessions.put(sessionRecord);

  const saveResult = await target.profiles.save({
    formatVersion: 3,
    profileId,
    achievements: [{ campaignId: FIXTURE_CAMPAIGN_ID, achievementId: "unreachable" }],
    terminals: [],
    kindData: [],
  });
  const failedAsExpected = !saveResult.ok && saveResult.warnings.some((warning) => warning.code === "profile_write_failed");
  if (!failedAsExpected) {
    return err({
      code: "MethodDiverged",
      method: "profiles.save",
      target: target.label,
      detail: `a profile seeded via seedProfileWriteFailure did not fail with ok:false/profile_write_failed (result: ${JSON.stringify(saveResult)})`,
    });
  }

  const stillCommitted = await target.persistence.sessions.get(sessionId);
  if (stillCommitted === undefined || stillCommitted.blob !== sessionRecord.blob) {
    return err({
      code: "MethodDiverged",
      method: "sessions.get",
      target: target.label,
      detail: "a session write issued before a failed profile write did not remain committed afterward",
    });
  }
  return ok(undefined);
}

// ---------------------------------------------------------------------------- S9.5: achievement union

function achievementKey(achievement: AchievementRecord): string {
  return `${achievement.campaignId}::${achievement.achievementId}`;
}

/** Mirrors the engine's own `upsertAchievements` calling convention (load, then save the loaded
 *  set plus one addition) rather than replacing the achievement set outright — S9.5 is about two
 *  such calls landing, not about `profiles.save`'s raw overwrite behaviour (that is S9.6's). */
async function upsertOneAchievement(store: ProfileStore, profileId: string, achievementId: string): Promise<boolean> {
  const { profile } = await store.load(profileId);
  const already = profile.achievements.some((achievement) => achievement.achievementId === achievementId && achievement.campaignId === FIXTURE_CAMPAIGN_ID);
  const achievements = already ? profile.achievements : [...profile.achievements, { campaignId: FIXTURE_CAMPAIGN_ID, achievementId }];
  const result = await store.save({ formatVersion: 3, profileId, achievements, terminals: [], kindData: [] });
  return result.ok;
}

async function checkAchievementUnion(target: ConformanceTarget): Promise<Outcome<void, ConformanceError>> {
  const profileId = freshId("conformance-union-profile");
  // A baseline row to upsert against — the durable store's `save` is an upsert either way, but
  // this keeps the two targets on the same footing before the racing/sequential calls below.
  const baseline = await target.profiles.save({ formatVersion: 3, profileId, achievements: [], terminals: [], kindData: [] });
  if (!baseline.ok) {
    return err({ code: "MethodDiverged", method: "profiles.save", target: target.label, detail: "seeding the achievement-union baseline failed" });
  }

  // S9.5: concurrent on the durable target (its merge is additive per-statement, so two
  // simultaneous writers each landing their own achievement is exactly the property under test);
  // sequential on the in-memory one, whose `save` replaces outright — only a sequential caller
  // that reads the prior result before writing can produce a union out of it.
  let bothSucceeded: boolean;
  if (target.label === "durable") {
    const [okA, okB] = await Promise.all([
      upsertOneAchievement(target.profiles, profileId, "achievement-a"),
      upsertOneAchievement(target.profiles, profileId, "achievement-b"),
    ]);
    bothSucceeded = okA && okB;
  } else {
    const okA = await upsertOneAchievement(target.profiles, profileId, "achievement-a");
    const okB = await upsertOneAchievement(target.profiles, profileId, "achievement-b");
    bothSucceeded = okA && okB;
  }
  if (!bothSucceeded) {
    return err({ code: "MethodDiverged", method: "profiles.save", target: target.label, detail: "one of the two achievement-adding saves reported ok:false" });
  }

  const loaded = await target.profiles.load(profileId);
  const ids = new Set(loaded.profile.achievements.map(achievementKey));
  const hasBoth = ids.has(achievementKey({ campaignId: FIXTURE_CAMPAIGN_ID, achievementId: "achievement-a" })) &&
    ids.has(achievementKey({ campaignId: FIXTURE_CAMPAIGN_ID, achievementId: "achievement-b" }));
  if (!hasBoth) {
    return err({
      code: "MethodDiverged",
      method: "profiles.save",
      target: target.label,
      detail: `two achievement-adding saves against one profile did not both land (achievements: ${JSON.stringify(loaded.profile.achievements)})`,
    });
  }
  return ok(undefined);
}

// ---------------------------------------------------------------------------- S9.6: the declared merge divergence

async function checkMergeDivergence(target: ConformanceTarget): Promise<Outcome<void, ConformanceError>> {
  const profileId = freshId("conformance-divergence-profile");
  const retainedId = "durable-keeps-me-in-memory-drops-me";

  const seeded = await target.profiles.save({
    formatVersion: 3,
    profileId,
    achievements: [{ campaignId: FIXTURE_CAMPAIGN_ID, achievementId: retainedId }],
    terminals: [],
    kindData: [],
  });
  if (!seeded.ok) {
    return err({ code: "MethodDiverged", method: "profiles.save", target: target.label, detail: "seeding the merge-divergence baseline achievement failed" });
  }

  // The raw `profiles.save` call, deliberately omitting the achievement just seeded — this is
  // the store's own merge behaviour under test, not a caller's calling convention (S9.5's
  // `upsertOneAchievement` is the latter).
  const omitting = await target.profiles.save({ formatVersion: 3, profileId, achievements: [], terminals: [], kindData: [] });
  if (!omitting.ok) {
    return err({ code: "MethodDiverged", method: "profiles.save", target: target.label, detail: "the achievement-omitting save itself failed" });
  }

  const loaded = await target.profiles.load(profileId);
  const stillHasIt = loaded.profile.achievements.some((achievement) => achievement.achievementId === retainedId);
  const durableRetains = target.label === "durable";
  if (stillHasIt !== durableRetains) {
    return err({
      code: "MethodDiverged",
      method: "profiles.save",
      target: target.label,
      detail: durableRetains
        ? "the durable target's save did not retain a previously stored achievement omitted from a later save (the declared additive-merge divergence)"
        : "the in-memory target's save unexpectedly retained an achievement omitted from a later save (the declared additive-merge divergence did not hold)",
    });
  }
  return ok(undefined);
}

// ---------------------------------------------------------------------------- S9.7: the engine caller property

interface ObservedProfileWrite {
  readonly profileId: string;
  readonly loaded: readonly AchievementRecord[];
  readonly saved: readonly AchievementRecord[];
}

/** Wraps a `ProfileStore` so every `load`/`save` pair the engine issues against it is recorded —
 *  handed to the real session layer in place of `target.profiles` so S9.7 observes exactly what
 *  the engine's own `upsertAchievements` calls, not a copy the suite fabricated itself. */
function watchProfileCalls(store: ProfileStore): { readonly store: ProfileStore; readonly calls: () => readonly ObservedProfileWrite[] } {
  const lastLoaded = new Map<string, readonly AchievementRecord[]>();
  const observed: ObservedProfileWrite[] = [];
  return {
    calls: () => observed,
    store: {
      async load(profileId) {
        const result = await store.load(profileId);
        lastLoaded.set(profileId, result.profile.achievements);
        return result;
      },
      async save(profile: PlayerProfile) {
        observed.push({ profileId: profile.profileId, loaded: lastLoaded.get(profile.profileId) ?? [], saved: profile.achievements });
        return store.save(profile);
      },
    },
  };
}

/**
 * The property itself: every `save` call carries at least the achievement set the immediately
 * preceding `load` for that profile returned (`20-contract.md`: "every save the engine issues
 * carries the loaded set plus additions"). Exported so a dedicated failure-path test can drive it
 * directly against a fabricated observation — the real engine's `upsertAchievements`
 * (`@the-running-dev/game-engine`) computes `save` as `[...loaded, ...additions]` in one
 * synchronous span with no intervening `await` between the `load` and the merge, so it cannot
 * itself produce a call this rejects; this check is the regression guard against a future engine
 * build changing that, not a scenario the real engine can be provoked into failing.
 */
export function verifyCallerProfileWrites(observed: readonly ObservedProfileWrite[]): Outcome<void, ConformanceError> {
  for (const write of observed) {
    const savedKeys = new Set(write.saved.map(achievementKey));
    const missing = write.loaded.filter((achievement) => !savedKeys.has(achievementKey(achievement)));
    if (missing.length > 0) {
      return err({
        code: "CallerPropertyViolated",
        method: "profiles.save",
        observed: `profile ${write.profileId}: save carried ${JSON.stringify(write.saved)}, dropping previously loaded ${JSON.stringify(missing)}`,
      });
    }
  }
  return ok(undefined);
}

/** A two-achievement `story-graph` campaign, small and deterministic enough to build entirely
 *  within this module rather than importing the repository's own shipped narrative content (none
 *  of `bulgaria-*`/`lucifer-chronicles`/`saki-quest` reaches two achievements in fewer than
 *  several authored routes) — scoped to this conformance run, never registered anywhere else. */
function fixtureCampaignSource(): StoryGraphCampaignSource {
  return {
    description: { key: "conformance.description", text: "Conformance fixture campaign." },
    variables: {
      flagOne: { type: "bool", initial: false },
      flagTwo: { type: "bool", initial: false },
    },
    nodes: {
      start: {
        kind: "choice",
        text: { key: "conformance.node.start", text: "Start." },
        choices: [
          {
            id: "advance-one",
            label: { key: "conformance.choice.advance-one", text: "Advance one." },
            effects: [{ op: "set", var: "flagOne", value: true }],
            goto: "middle",
          },
        ],
      },
      middle: {
        kind: "choice",
        text: { key: "conformance.node.middle", text: "Middle." },
        choices: [
          {
            id: "advance-two",
            label: { key: "conformance.choice.advance-two", text: "Advance two." },
            effects: [{ op: "set", var: "flagTwo", value: true }],
            goto: "end",
          },
        ],
      },
      end: {
        kind: "ending",
        text: { key: "conformance.node.end", text: "The end." },
        endingId: "conformance-end",
        outcome: "win",
      },
    },
    startNodeId: "start",
    achievements: [
      {
        id: "first-flag",
        name: { key: "conformance.ach.first.name", text: "First Flag" },
        description: { key: "conformance.ach.first.description", text: "Set flag one." },
        condition: { field: "var.flagOne", operator: "equals", value: true },
        hidden: false,
      },
      {
        id: "second-flag",
        name: { key: "conformance.ach.second.name", text: "Second Flag" },
        description: { key: "conformance.ach.second.description", text: "Set flag two." },
        condition: { field: "var.flagTwo", operator: "equals", value: true },
        hidden: false,
      },
    ],
  };
}

function buildFixtureRegistry(): ContentRegistry {
  const { content, authoredText } = buildStoryGraphCampaign(fixtureCampaignSource());
  const titleKey = "conformance.campaign.title";
  const campaign: Campaign = {
    id: FIXTURE_CAMPAIGN_ID,
    kindId: storyGraphKind.id,
    version: "1.0.0",
    titleKey,
    content,
  };
  const built = buildCampaign(campaign, [{ key: titleKey, text: "Conformance Fixture" }, ...authoredText]);
  if (!built.ok || !built.value) {
    throw new Error(`the conformance fixture campaign failed to build: ${JSON.stringify(built.errors)}`);
  }
  const registry = buildContentRegistry([built.value]);
  if (!registry.ok || !registry.value) {
    throw new Error(`the conformance fixture campaign failed to build a content registry: ${JSON.stringify(registry.errors)}`);
  }
  return registry.value;
}

function fixtureSessionLayer(persistence: SessionPersistence, profiles: ProfileStore): SessionStore {
  const registry = buildFixtureRegistry();
  const kinds = { [storyGraphKind.id]: storyGraphKind } as unknown as KindRegistry;
  const engine = createEngine({ kinds, registry });
  return createSessionLayer({ engine, registry, persistence, profiles });
}

async function checkEngineCallerProperty(target: ConformanceTarget): Promise<Outcome<void, ConformanceError>> {
  const watched = watchProfileCalls(target.profiles);
  const sessionStore = fixtureSessionLayer(target.persistence, watched.store);
  const profileId = freshId("conformance-caller-property-profile");
  const created = await sessionStore.createSession({ campaignId: FIXTURE_CAMPAIGN_ID, profileId });
  await sessionStore.submitAction(created.sessionId, "advance-one");
  await sessionStore.submitAction(created.sessionId, "advance-two");
  const observed = watched.calls();
  if (observed.length === 0) {
    return err({
      code: "MethodDiverged",
      method: "profiles.save",
      target: target.label,
      detail: "driving the two-achievement fixture campaign through a real session layer produced no observed profiles.save calls, so the caller property could not be checked",
    });
  }
  return verifyCallerProfileWrites(observed);
}

// ---------------------------------------------------------------------------- runPortConformance

export async function runPortConformance(target: ConformanceTarget): Promise<Outcome<void, ConformanceError>> {
  const steps: readonly (() => Promise<Outcome<void, ConformanceError>>)[] = [
    () => checkSessionsAndSaves(target),
    () => checkSavesDelete(target),
    () => checkSaveDeleteConditional(target),
    () => checkSaveListByProfile(target),
    () => checkProfileMissing(target),
    () => checkProfileCorrupt(target),
    () => checkProfileWriteFailure(target),
    () => checkAchievementUnion(target),
    () => checkMergeDivergence(target),
    () => checkEngineCallerProperty(target),
  ];
  for (const step of steps) {
    const result = await step();
    if (!result.ok) return result;
  }
  return ok(undefined);
}

// ---------------------------------------------------------------------------- the reference (in-memory) target

/** The reference target's `persistence` is the workload's own map-backed implementation, not the
 *  engine's — `compose.ts`'s private `inMemoryPersistence()`, replicated here rather than
 *  exported and imported, since `compose.ts` is outside this slice's `Touches` and the shape is
 *  three lines (`20-contract.md`: "the engine exports the `SessionPersistence` type and no
 *  implementation of it"). */
function inMemoryReferencePersistence(): SessionPersistence {
  const sessions = new Map<string, StoredSessionRecord>();
  const saves = new Map<string, StoredSaveRecord>();
  return {
    sessions: {
      get: async (sessionId) => sessions.get(sessionId),
      put: async (record) => {
        sessions.set(record.sessionId, record);
      },
    },
    saves: {
      get: async (saveId) => saves.get(saveId),
      put: async (record) => {
        saves.set(record.saveId, record);
      },
      listByProfile: async (profileId) => [...saves.values()].filter((record) => record.profileId === profileId),
      delete: async (saveId, expectedSavedAt) => {
        const current = saves.get(saveId);
        if (current !== undefined && current.savedAt === expectedSavedAt) saves.delete(saveId);
      },
    },
  };
}

/**
 * The reference target's two degraded profile outcomes are provoked through the engine's own
 * seams, never stubbed at the boundary (`20-contract.md`, "ConformanceTarget": *a malformed raw
 * entry against the engine's in-memory profile store*). That distinction is the suite: a target
 * that answered `profile_corrupt` by returning a canned value would agree with the durable store
 * by construction, and the assertion would establish nothing about either implementation.
 *
 * The two seams differ in how they are reached, and only one of them needs machinery:
 *
 * - **`onSave` is live.** The engine evaluates it on every write, so a write-failure seed is a
 *   set membership the callback closes over and reaches the engine's own `ok: false` branch
 *   verbatim.
 * - **`raw` is copied at construction** (`new Map(options?.raw)`), and the suite seeds an
 *   already-built target — so a malformed entry can only reach `isValidPlayerProfile` through a
 *   store rebuilt from the entries so far. `raw` below mirrors every accepted `save` for exactly
 *   that rebuild, so seeding corruption for one profile does not discard another written earlier
 *   in the run; the engine's store stays the thing that validates, clones and replaces.
 */
export function inMemoryConformanceTarget(): ConformanceTarget {
  const raw = new Map<string, unknown>();
  const failIds = new Set<string>();

  const onSave = (profile: PlayerProfile): boolean => !failIds.has(profile.profileId);

  let store = createInMemoryProfileStore({ raw, onSave });

  const profiles: ProfileStore = {
    // Read at call time, not captured — `seedCorruptProfile` replaces the instance.
    load: (profileId) => store.load(profileId),
    async save(profile) {
      const result = await store.save(profile);
      if (result.ok) raw.set(profile.profileId, structuredClone(profile));
      return result;
    },
  };

  return {
    label: "in-memory",
    persistence: inMemoryReferencePersistence(),
    profiles,
    async seedCorruptProfile(profileId: string): Promise<void> {
      // `formatVersion` 2 — the same malformation the durable target seeds as `format_version = 2`
      // — is itself a migratable format at `0.10.0` (04 §7.1), so it alone would no longer be
      // corrupt. What still makes this entry corrupt is the missing `terminals` array
      // `isValidPlayerProfileV2` requires alongside it.
      raw.set(profileId, { formatVersion: 2, profileId, achievements: [] });
      store = createInMemoryProfileStore({ raw, onSave });
    },
    async seedProfileWriteFailure(profileId: string): Promise<void> {
      failIds.add(profileId);
    },
  };
}

// ---------------------------------------------------------------------------- the durable target

/**
 * Seeding machinery for the durable target — schema-local, created and left behind for the
 * caller's own schema teardown to drop (the same footing as this slice's other test-only DDL:
 * `tests/support/database.ts`'s `createTestSchema`/`drop()`). A control table plus a `before
 * insert or update` trigger on `profile`, rather than per-call DDL (an ad hoc `CHECK` constraint
 * per `seedProfileWriteFailure` call): one statement at target-build time instead of one
 * `ALTER TABLE` per seed call, and no need to safely interpolate a caller-supplied `profileId`
 * into DDL text at all. Scoped to the `profile` table alone, which is what keeps
 * `seedProfileWriteFailure` from touching `session`/`save` (S9.4's "only the profile write").
 */
async function ensureWriteFailureSeam(pool: Pool): Promise<void> {
  await pool.query(
    "create table if not exists conformance_write_block (tenant_id text not null, profile_id text not null, primary key (tenant_id, profile_id))",
  );
  await pool.query(`
    create or replace function conformance_block_profile_write() returns trigger as $$
    begin
      if exists (
        select 1 from conformance_write_block
        where tenant_id = new.tenant_id and profile_id = new.profile_id
      ) then
        raise exception 'conformance: profile write blocked for %/%', new.tenant_id, new.profile_id;
      end if;
      return new;
    end;
    $$ language plpgsql
  `);
  await pool.query("drop trigger if exists conformance_block_profile_write_trigger on profile");
  await pool.query(
    "create trigger conformance_block_profile_write_trigger before insert or update on profile " +
      "for each row execute function conformance_block_profile_write()",
  );
}

export interface DurableConformanceTarget extends ConformanceTarget {
  /** Not part of `ConformanceTarget` (`20-contract.md` declares no teardown member for it) —
   *  closes both this target's own seeding pool and the underlying `DurableStore`'s pool. Callers
   *  that only need the contract's own surface can ignore it; the test file uses it for cleanup. */
  close(): Promise<void>;
}

/** Builds the durable `ConformanceTarget` against an already-migrated schema (`openDurableStore`
 *  for the real `persistence`/`profiles`, plus a lightweight `pg.Pool` of its own, scoped to the
 *  same connection/schema, for the seeding machinery above — `DurableStore`'s public surface is
 *  not widened to expose its pool, since the contract names no new member for it). */
export async function openDurableConformanceTarget(
  configuration: DurableStoreConfiguration,
  engineVersion: SemanticVersion,
): Promise<Outcome<DurableConformanceTarget, ConformanceError>> {
  const opened = await openDurableStore(configuration, engineVersion);
  if (!opened.ok) {
    return err({ code: "SeamUnavailable", method: "openDurableStore" });
  }
  const store = opened.value;

  const schema = configuration.connection.schema;
  const seedPool = new Pool({
    connectionString: configuration.connection.connectionString,
    max: 2,
    connectionTimeoutMillis: configuration.connection.connectTimeoutMs,
    ...(schema !== null ? { options: `-c search_path=${quoteIdentifier(schema as unknown as string)},public` } : {}),
  });

  try {
    await ensureWriteFailureSeam(seedPool);
  } catch {
    await seedPool.end().catch(() => {});
    await store.close();
    return err({ code: "SeamUnavailable", method: "seedProfileWriteFailure" });
  }

  const tenantId = IMPLICIT_TENANT_ID;
  const persistence = store.persistenceForRequest();

  return ok({
    label: "durable",
    persistence,
    profiles: store.profiles,
    async seedCorruptProfile(profileId: string): Promise<void> {
      await seedPool.query(
        "insert into profile (tenant_id, profile_id, format_version, row_updated_at) values ($1, $2, 2, now()) " +
          "on conflict (tenant_id, profile_id) do update set format_version = 2, row_updated_at = now()",
        [tenantId, profileId],
      );
    },
    async seedProfileWriteFailure(profileId: string): Promise<void> {
      await seedPool.query(
        "insert into conformance_write_block (tenant_id, profile_id) values ($1, $2) on conflict do nothing",
        [tenantId, profileId],
      );
    },
    async close(): Promise<void> {
      await seedPool.end();
      await store.close();
    },
  });
}
