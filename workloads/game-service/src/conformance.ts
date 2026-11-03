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
  SESSION_PERSISTENCE_CONFLICT,
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
import type {
  ConformanceError,
  ConformanceTarget,
  DurableStoreConfiguration,
  Outcome,
  OwnerKey,
  OwnerScopedStorage,
  SemanticVersion,
} from "./types.js";

/** The two owners `runPortConformance` runs every assertion under (`20-contract.md`, "Port
 *  conformance under two owners", invariant 122; G3 S4.4). Same issuer, distinct subjects: the
 *  owner predicate is the pair, and a single shared issuer is what a leak keyed on the issuer alone
 *  would not catch. */
export const CONFORMANCE_OWNERS: readonly [OwnerKey, OwnerKey] = [
  { issuer: "conformance", subject: "owner-a" },
  { issuer: "conformance", subject: "owner-b" },
];

/** What one check sees: a `ConformanceTarget` already bound to one owner, which is exactly the shape
 *  every G2 assertion was written against. Binding is the only place an owner enters a check, so the
 *  assertions themselves are unchanged and "every assertion under each owner" is a loop, not a
 *  rewrite. */
interface BoundTarget {
  readonly label: ConformanceTarget["label"];
  readonly persistence: SessionPersistence;
  readonly profiles: ProfileStore;
  seedCorruptProfile(profileId: string): Promise<void>;
  seedProfileWriteFailure(profileId: string): Promise<void>;
}

/** Every id one check wrote or read through its owner's ports — what the other owner is then asked
 *  about. */
interface TouchedIds {
  readonly sessions: Set<string>;
  readonly saves: Map<string, string>;
  readonly profiles: Set<string>;
}

/** Binds `target` to `owner` and records the ids the check touches. The recording wraps the ports
 *  rather than the checks, so a check added later is covered without being edited. */
function bind(target: ConformanceTarget, owner: OwnerKey): { readonly view: BoundTarget; readonly touched: TouchedIds } {
  const touched: TouchedIds = { sessions: new Set(), saves: new Map(), profiles: new Set() };
  const persistence = target.storage.persistence(owner);
  const profiles = target.storage.profiles(owner);
  const view: BoundTarget = {
    label: target.label,
    persistence: {
      sessions: {
        get: (sessionId) => {
          touched.sessions.add(sessionId);
          return persistence.sessions.get(sessionId);
        },
        put: (record) => {
          touched.sessions.add(record.sessionId);
          return persistence.sessions.put(record);
        },
      },
      saves: {
        get: (saveId) => {
          touched.saves.set(saveId, touched.saves.get(saveId) ?? "");
          return persistence.saves.get(saveId);
        },
        put: (record) => {
          touched.saves.set(record.saveId, record.savedAt);
          if (record.profileId !== undefined) touched.profiles.add(record.profileId);
          return persistence.saves.put(record);
        },
        listByProfile: (profileId) => {
          touched.profiles.add(profileId);
          return persistence.saves.listByProfile(profileId);
        },
        delete: (saveId, expectedSavedAt) => persistence.saves.delete(saveId, expectedSavedAt),
      },
    },
    profiles: {
      load: (profileId) => {
        touched.profiles.add(profileId);
        return profiles.load(profileId);
      },
      save: (profile) => {
        touched.profiles.add(profile.profileId);
        return profiles.save(profile);
      },
    },
    seedCorruptProfile: (profileId) => {
      touched.profiles.add(profileId);
      return target.seedCorruptProfile(owner, profileId);
    },
    seedProfileWriteFailure: (profileId) => {
      touched.profiles.add(profileId);
      return target.seedProfileWriteFailure(owner, profileId);
    },
  };
  return { view, touched };
}

function leaked(target: ConformanceTarget, method: string, detail: string): Outcome<void, ConformanceError> {
  return err({ code: "MethodDiverged", method, target: target.label, detail: `owner isolation: ${detail}` });
}

/** After a check under `owner`, asks `other`'s ports about everything the check touched. Reads come
 *  first, so a later probe's own writes cannot make an earlier read look like a leak; the two probes
 *  that write (`saves.delete`, `profiles.save`) are made against rows `owner` still holds and then
 *  confirmed not to have disturbed them. */
async function assertOtherOwnerSeesNothing(
  target: ConformanceTarget,
  owner: OwnerKey,
  other: OwnerKey,
  touched: TouchedIds,
): Promise<Outcome<void, ConformanceError>> {
  const own = { persistence: target.storage.persistence(owner), profiles: target.storage.profiles(owner) };
  const foreign = { persistence: target.storage.persistence(other), profiles: target.storage.profiles(other) };

  for (const sessionId of touched.sessions) {
    if ((await foreign.persistence.sessions.get(sessionId)) !== undefined) {
      return leaked(target, "sessions.get", `session ${sessionId} was visible to the other owner`);
    }
  }
  for (const saveId of touched.saves.keys()) {
    if ((await foreign.persistence.saves.get(saveId)) !== undefined) {
      return leaked(target, "saves.get", `save ${saveId} was visible to the other owner`);
    }
  }
  for (const profileId of touched.profiles) {
    const listed = await foreign.persistence.saves.listByProfile(profileId);
    if (listed.length > 0) {
      return leaked(target, "saves.listByProfile", `the other owner listed ${listed.length} save(s) under profile ${profileId}`);
    }
    const loaded = await foreign.profiles.load(profileId);
    const missing = loaded.warnings.some((warning) => warning.code === "profile_missing");
    if (!missing || loaded.profile.achievements.length !== 0) {
      return leaked(target, "profiles.load", `profile ${profileId} was not missing, and empty, for the other owner`);
    }
  }

  // A delete by the other owner is a no-op that reveals nothing: not a conflict (that would say the
  // row exists), and not a removal. Both savedAt values are tried — the matching one is the delete
  // that would succeed against the owner's own row.
  for (const [saveId, savedAt] of touched.saves) {
    const held = await own.persistence.saves.get(saveId);
    if (held === undefined) continue;
    for (const expected of [held.savedAt, "1999-01-01T00:00:00.000Z"]) {
      try {
        await foreign.persistence.saves.delete(saveId, expected);
      } catch {
        return leaked(target, "saves.delete", `the other owner's delete of save ${saveId} failed instead of being a no-op`);
      }
    }
    if ((await own.persistence.saves.get(saveId)) === undefined) {
      return leaked(target, "saves.delete", `the other owner's delete removed save ${saveId} (put at ${savedAt})`);
    }
  }

  for (const profileId of touched.profiles) {
    const before = JSON.stringify(await own.profiles.load(profileId));
    await foreign.profiles.save({
      formatVersion: 3,
      profileId,
      achievements: [{ campaignId: FIXTURE_CAMPAIGN_ID, achievementId: "cross-owner-probe" }],
      terminals: [],
      kindData: [],
    });
    if (JSON.stringify(await own.profiles.load(profileId)) !== before) {
      return leaked(target, "profiles.save", `the other owner's save changed profile ${profileId}`);
    }
  }
  return ok(undefined);
}

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
  "revision",
  "profileId",
  "initialization",
]);

async function checkSessionsAndSaves(target: BoundTarget): Promise<Outcome<void, ConformanceError>> {
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
    revision: 0,
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

/** `SaveRecordStore.delete`'s brand (engine `04-core.md` §7.4) — recognised by `name` alone, the
 *  same way the engine's own `isPersistenceConflict` reads it. */
function isConflictError(error: unknown): boolean {
  return typeof error === "object" && error !== null && "name" in error && (error as { name: unknown }).name === SESSION_PERSISTENCE_CONFLICT;
}

async function checkSavesDelete(target: BoundTarget): Promise<Outcome<void, ConformanceError>> {
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

  // A delete whose `expectedSavedAt` does not match the stored value removes nothing and raises
  // the conflict brand (04 §7.4, D2) — checked before the matching delete below, so this can
  // still find the record.
  let mismatchThrew = false;
  try {
    await target.persistence.saves.delete(saveId, "2026-01-01T00:00:00.001Z");
  } catch (error) {
    mismatchThrew = isConflictError(error);
  }
  if (!mismatchThrew) {
    return err({
      code: "MethodDiverged",
      method: "saves.delete",
      target: target.label,
      detail: "deleting with a stale expectedSavedAt did not raise the SessionPersistenceConflict brand",
    });
  }
  const stillThere = await target.persistence.saves.get(saveId);
  if (stillThere === undefined) {
    return err({
      code: "MethodDiverged",
      method: "saves.delete",
      target: target.label,
      detail: "a delete refused for a stale expectedSavedAt removed the record anyway",
    });
  }

  await target.persistence.saves.delete(saveId, savedAt);
  const afterDelete = await target.persistence.saves.get(saveId);
  if (afterDelete !== undefined) {
    return err({
      code: "MethodDiverged",
      method: "saves.delete",
      target: target.label,
      detail: "a save put and then deleted with the matching savedAt was still returned by saves.get",
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

// ---------------------------------------------------------------------------- S13.7: saves.listByProfile

async function checkSavesListByProfile(target: BoundTarget): Promise<Outcome<void, ConformanceError>> {
  const profileId = freshId("conformance-list-profile");
  const otherProfileId = freshId("conformance-list-other-profile");
  const ownedIds = [freshId("conformance-list-save-a"), freshId("conformance-list-save-b")];
  for (const saveId of ownedIds) {
    await target.persistence.saves.put({
      saveId,
      campaignId: FIXTURE_CAMPAIGN_ID,
      blob: "{}",
      savedAt: "2026-01-01T00:00:00.000Z",
      savedAtSeq: 1,
      audience: "player",
      profileId,
    });
  }
  const otherSaveId = freshId("conformance-list-save-other");
  await target.persistence.saves.put({
    saveId: otherSaveId,
    campaignId: FIXTURE_CAMPAIGN_ID,
    blob: "{}",
    savedAt: "2026-01-01T00:00:00.000Z",
    savedAtSeq: 1,
    audience: "player",
    profileId: otherProfileId,
  });
  const anonymousSaveId = freshId("conformance-list-save-anonymous");
  await target.persistence.saves.put({
    saveId: anonymousSaveId,
    campaignId: FIXTURE_CAMPAIGN_ID,
    blob: "{}",
    savedAt: "2026-01-01T00:00:00.000Z",
    savedAtSeq: 1,
    audience: "player",
  });

  const listed = await target.persistence.saves.listByProfile(profileId);
  const listedIds = new Set(listed.map((record) => record.saveId));
  const hasBothOwned = ownedIds.every((saveId) => listedIds.has(saveId));
  const leaksOther = listedIds.has(otherSaveId) || listedIds.has(anonymousSaveId);
  if (!hasBothOwned || leaksOther) {
    return err({
      code: "MethodDiverged",
      method: "saves.listByProfile",
      target: target.label,
      detail: `listByProfile did not return exactly the owning profile's own records (got: ${JSON.stringify([...listedIds])})`,
    });
  }
  return ok(undefined);
}

// ---------------------------------------------------------------------------- S9.2, S9.3: profiles.load outcomes

async function seed(
  target: BoundTarget,
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

async function checkProfileCorrupt(target: BoundTarget): Promise<Outcome<void, ConformanceError>> {
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

async function checkProfileMissing(target: BoundTarget): Promise<Outcome<void, ConformanceError>> {
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

async function checkProfileWriteFailure(target: BoundTarget): Promise<Outcome<void, ConformanceError>> {
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
    revision: 0,
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

async function checkAchievementUnion(target: BoundTarget): Promise<Outcome<void, ConformanceError>> {
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

async function checkMergeDivergence(target: BoundTarget): Promise<Outcome<void, ConformanceError>> {
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

async function checkEngineCallerProperty(target: BoundTarget): Promise<Outcome<void, ConformanceError>> {
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

const STEPS: readonly ((target: BoundTarget) => Promise<Outcome<void, ConformanceError>>)[] = [
  checkSessionsAndSaves,
  checkSavesDelete,
  checkSavesListByProfile,
  checkProfileMissing,
  checkProfileCorrupt,
  checkProfileWriteFailure,
  checkAchievementUnion,
  checkMergeDivergence,
  checkEngineCallerProperty,
];

/** Runs every assertion under each of `CONFORMANCE_OWNERS`, in order, and after each asks the other
 *  owner's ports about what it touched (`20-contract.md`, invariant 122). The first owner's rows are
 *  still present while the second runs, which is what makes "sees nothing of the other owner's rows"
 *  a statement about a store that holds both. */
export async function runPortConformance(target: ConformanceTarget): Promise<Outcome<void, ConformanceError>> {
  for (const owner of CONFORMANCE_OWNERS) {
    const other = owner === CONFORMANCE_OWNERS[0] ? CONFORMANCE_OWNERS[1] : CONFORMANCE_OWNERS[0];
    for (const step of STEPS) {
      const { view, touched } = bind(target, owner);
      const result = await step(view);
      if (!result.ok) return result;
      const isolated = await assertOtherOwnerSeesNothing(target, owner, other, touched);
      if (!isolated.ok) return isolated;
    }
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
        const record = saves.get(saveId);
        if (record === undefined) return;
        if (record.savedAt !== expectedSavedAt) {
          const error = new Error("in-memory save delete: savedAt mismatch");
          Object.defineProperty(error, "name", { value: SESSION_PERSISTENCE_CONFLICT, enumerable: false });
          throw error;
        }
        saves.delete(saveId);
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
interface InMemoryOwnerState {
  readonly persistence: SessionPersistence;
  readonly profiles: ProfileStore;
  seedCorrupt(profileId: string): void;
  seedWriteFailure(profileId: string): void;
}

/** One owner's slice of the in-memory target: its own persistence maps, its own raw profile
 *  entries and its own write-failure set. Nothing is shared between owners, so the engine's profile
 *  store (which has no notion of an owner) is simply instantiated once per owner. */
function inMemoryOwnerState(): InMemoryOwnerState {
  const raw = new Map<string, unknown>();
  const failIds = new Set<string>();

  const onSave = (profile: PlayerProfile): boolean => !failIds.has(profile.profileId);

  let store = createInMemoryProfileStore({ raw, onSave });

  return {
    persistence: inMemoryReferencePersistence(),
    profiles: {
      // Read at call time, not captured — `seedCorrupt` replaces the instance.
      load: (profileId) => store.load(profileId),
      async save(profile) {
        const result = await store.save(profile);
        if (result.ok) raw.set(profile.profileId, structuredClone(profile));
        return result;
      },
    },
    seedCorrupt(profileId: string): void {
      // `formatVersion` 99 — a format none of the engine's profile readers (3, 2, 1) accepts, the
      // same malformation the durable target seeds as `format_version = 99`. `2` stopped being a
      // malformation once `PlayerProfile` gained `terminals`, and `3` is this build's own current
      // format, so neither is a corrupt one.
      raw.set(profileId, { formatVersion: 99, profileId, achievements: [] });
      store = createInMemoryProfileStore({ raw, onSave });
    },
    seedWriteFailure(profileId: string): void {
      failIds.add(profileId);
    },
  };
}

export function inMemoryConformanceTarget(): ConformanceTarget {
  const owners = new Map<string, InMemoryOwnerState>();
  const stateFor = (owner: OwnerKey): InMemoryOwnerState => {
    // The pair, not the subject alone: two issuers may mint the same subject.
    const key = JSON.stringify([owner.issuer, owner.subject]);
    let state = owners.get(key);
    if (state === undefined) {
      state = inMemoryOwnerState();
      owners.set(key, state);
    }
    return state;
  };

  const storage: OwnerScopedStorage = {
    persistence: (owner) => stateFor(owner).persistence,
    profiles: (owner) => stateFor(owner).profiles,
    // An in-memory row never expires, so the probe answers `live` or `absent`; an owner that holds
    // no such row gets `absent` whoever else does (invariant 115).
    lifecycle: {
      session: async (owner, sessionId) =>
        ok((await stateFor(owner).persistence.sessions.get(sessionId)) === undefined ? "absent" : "live"),
      save: async (owner, saveId) =>
        ok((await stateFor(owner).persistence.saves.get(saveId)) === undefined ? "absent" : "live"),
    },
  };

  return {
    label: "in-memory",
    storage,
    async seedCorruptProfile(owner: OwnerKey, profileId: string): Promise<void> {
      stateFor(owner).seedCorrupt(profileId);
    },
    async seedProfileWriteFailure(owner: OwnerKey, profileId: string): Promise<void> {
      stateFor(owner).seedWriteFailure(profileId);
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
  // Keyed by owner as the `profile` table is: blocking one owner's profile must leave the other
  // owner's same-named profile writable (G3 S4.4 — the seam is part of what is under test).
  await pool.query(
    "create table if not exists conformance_write_block (tenant_id text not null, owner_issuer text not null, owner_subject text not null, profile_id text not null, " +
      "primary key (tenant_id, owner_issuer, owner_subject, profile_id))",
  );
  await pool.query(`
    create or replace function conformance_block_profile_write() returns trigger as $$
    begin
      if exists (
        select 1 from conformance_write_block
        where tenant_id = new.tenant_id and owner_issuer = new.owner_issuer
          and owner_subject = new.owner_subject and profile_id = new.profile_id
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

  return ok({
    label: "durable",
    storage: store.storage,
    async seedCorruptProfile(owner: OwnerKey, profileId: string): Promise<void> {
      // `format_version = 99` — same reasoning as the in-memory target's own sentinel above:
      // none of `1`, `2` or `3` is a malformation any longer.
      await seedPool.query(
        "insert into profile (tenant_id, owner_issuer, owner_subject, profile_id, format_version, row_updated_at) " +
          "values ($1, $2, $3, $4, 99, now()) " +
          "on conflict (tenant_id, owner_issuer, owner_subject, profile_id) do update set format_version = 99, row_updated_at = now()",
        [tenantId, owner.issuer, owner.subject, profileId],
      );
    },
    async seedProfileWriteFailure(owner: OwnerKey, profileId: string): Promise<void> {
      await seedPool.query(
        "insert into conformance_write_block (tenant_id, owner_issuer, owner_subject, profile_id) values ($1, $2, $3, $4) on conflict do nothing",
        [tenantId, owner.issuer, owner.subject, profileId],
      );
    },
    async close(): Promise<void> {
      await seedPool.end();
      await store.close();
    },
  });
}
