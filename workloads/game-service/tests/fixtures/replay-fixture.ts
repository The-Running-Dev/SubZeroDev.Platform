/**
 * The committed replay fixture (S5) — one step per row, in an order one campaign can actually play,
 * with every id literal rather than captured at run time. That is possible only because the replay
 * profile's `RecordIdSource` counts from zero on every run: `create-session` is always
 * `counting-session-id-0`, and `save-game` is always `counting-save-id-0` (`20-contract.md`,
 * `ReplayStep.arguments` note; invariant 12b).
 *
 * A change to this file invalidates the golden transcript, and the two are regenerated and
 * reviewed together (`npm run regenerate-golden`) or the suite goes red — the intended coupling,
 * not a hazard (`20-contract.md`'s persisted-schemas table).
 */
import { WORLD_GRAPH_MVP_CAMPAIGN_ID } from "@the-running-dev/game-engine";
import type { OperationId } from "@subzerodev/service-contract";
import type { ReplayFixture } from "../../src/types.js";

const SESSION_ID = "counting-session-id-0";
const ACTION_ID = "advance_ticks";
const ACTION_PARAMS = { ticks: 1 };
const PROFILE_ID = "s5-replay-profile";
// `RecordIdSource` counts saves independently of sessions, from zero, so the fixture's first
// `save-game` is always this id and its second is always the next one — the same reasoning this
// file's header note gives for `SESSION_ID`.
const KEPT_SAVE_ID = "counting-save-id-0";
const DELETED_SAVE_ID = "counting-save-id-1";
// The replay profile's own fixed clock reading (`REPLAY_FIXED_INSTANT`, `../../src/replay.ts`) —
// what every `save-game` in this fixture stamps a record's `savedAt` with, so it is safe to
// hardcode here as `delete-save`'s precondition rather than thread it through from a response.
const FIXED_SAVED_AT = "2026-01-01T00:00:00.000Z";

export const REPLAY_FIXTURE: ReplayFixture = {
  campaignId: WORLD_GRAPH_MVP_CAMPAIGN_ID,
  seed: "s5-byte-identity-proof",
  steps: [
    { operation: "list-campaigns" as OperationId, arguments: {} },
    { operation: "create-session" as OperationId, arguments: { campaignId: WORLD_GRAPH_MVP_CAMPAIGN_ID, profileId: PROFILE_ID } },
    // `atActionCount: 0` branches at the session's own start (`20-contract.md` §7.4, "`0` branches
    // at ... inclusive") — placed here, before `submit-action`, rather than between it and
    // `save-game`, so S5.5's transposition of those two never drags this step's precondition
    // (a fork point within `[0, actionLog.length]`) out from under it. This mints the fixture's
    // second session id; `load-game` below mints its third.
    { operation: "branch-session" as OperationId, arguments: { sessionId: SESSION_ID, atActionCount: 0 } },
    { operation: "get-scene" as OperationId, arguments: { sessionId: SESSION_ID } },
    { operation: "get-view" as OperationId, arguments: { sessionId: SESSION_ID } },
    { operation: "get-strings" as OperationId, arguments: { sessionId: SESSION_ID } },
    { operation: "preview-action" as OperationId, arguments: { sessionId: SESSION_ID, actionId: ACTION_ID, params: ACTION_PARAMS } },
    { operation: "submit-action" as OperationId, arguments: { sessionId: SESSION_ID, actionId: ACTION_ID, params: ACTION_PARAMS } },
    { operation: "resume-session" as OperationId, arguments: { sessionId: SESSION_ID } },
    // Two saves, not one: `delete-save` below removes the second, and a fixture that only ever
    // made one save would leave the final serialization with zero — exactly the empty set
    // `assertNonEmpty` (`durable-replay.test.ts`) exists to rule out comparison A vacuously
    // passing against.
    { operation: "save-game" as OperationId, arguments: { sessionId: SESSION_ID } },
    { operation: "save-game" as OperationId, arguments: { sessionId: SESSION_ID } },
    { operation: "list-saves" as OperationId, arguments: { profileId: PROFILE_ID } },
    { operation: "load-game" as OperationId, arguments: { saveId: KEPT_SAVE_ID } },
    {
      operation: "delete-save" as OperationId,
      arguments: { profileId: PROFILE_ID, saveId: DELETED_SAVE_ID, expectedSavedAt: FIXED_SAVED_AT },
    },
  ],
};
