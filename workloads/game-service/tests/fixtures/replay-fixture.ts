/**
 * The committed replay fixture (S5) — one step per row, in an order one campaign can actually play,
 * with every id literal rather than captured at run time. That is possible only because the replay
 * profile's `RecordIdSource` counts from zero on every run: `create-session` is always
 * `counting-session-id-0`, and `save-game` is always `counting-save-id-0` (`20-contract.md`,
 * `ReplayStep.arguments` note; invariant 12b). `branch-session` mints a session id the same way —
 * it is the second call to `RecordIdSource.newSessionId()` in step order (after `create-session`
 * and before `load-game`), so it is always `counting-session-id-1`, and `load-game`'s own mint
 * moves to `counting-session-id-2` — not referenced by any later step, so the shift is invisible
 * here, but stated so a future reordering does not silently assume otherwise.
 *
 * `PROFILE_ID` is what makes `list-saves`/`delete-save` meaningful: both are profile-scoped
 * (engine `04-core.md` §7.4), so `create-session` carries it — invariant P2 (04 §7.1) is that
 * doing so is byte-identical to the anonymous session every other step in this file already
 * exercised, so nothing upstream of it in this fixture changes shape.
 *
 * `EXPECTED_SAVED_AT` is `save-game`'s own `savedAt` stamp under this harness's fixed clock
 * (`replay.ts`'s `REPLAY_FIXED_INSTANT`) — literal for the same reason every other id here is:
 * the clock is fixed, so the stamp is as deterministic as a counted id. `delete-save` runs last,
 * after `load-game` has already used the same `saveId` — deleting a save after loading it is a
 * real, playable order; deleting it first would leave `load-game` nothing to load.
 *
 * A change to this file invalidates the golden transcript, and the two are regenerated and
 * reviewed together (`npm run regenerate-golden`) or the suite goes red — the intended coupling,
 * not a hazard (`20-contract.md`'s persisted-schemas table).
 */
import { WORLD_GRAPH_MVP_CAMPAIGN_ID } from "@the-running-dev/game-engine";
import type { OperationId } from "@subzerodev/service-contract";
import type { ReplayFixture } from "../../src/types.js";
import { REPLAY_FIXED_INSTANT } from "../../src/replay.js";

const SESSION_ID = "counting-session-id-0";
const ACTION_ID = "advance_ticks";
const ACTION_PARAMS = { ticks: 1 };
const PROFILE_ID = "s5-replay-profile";
const SAVE_ID = "counting-save-id-0";
const EXPECTED_SAVED_AT = REPLAY_FIXED_INSTANT;

export const REPLAY_FIXTURE: ReplayFixture = {
  campaignId: WORLD_GRAPH_MVP_CAMPAIGN_ID,
  seed: "s5-byte-identity-proof",
  steps: [
    { operation: "list-campaigns" as OperationId, arguments: {} },
    { operation: "create-session" as OperationId, arguments: { campaignId: WORLD_GRAPH_MVP_CAMPAIGN_ID, profileId: PROFILE_ID } },
    { operation: "get-scene" as OperationId, arguments: { sessionId: SESSION_ID } },
    { operation: "get-view" as OperationId, arguments: { sessionId: SESSION_ID } },
    { operation: "get-strings" as OperationId, arguments: { sessionId: SESSION_ID } },
    { operation: "preview-action" as OperationId, arguments: { sessionId: SESSION_ID, actionId: ACTION_ID, params: ACTION_PARAMS } },
    { operation: "submit-action" as OperationId, arguments: { sessionId: SESSION_ID, actionId: ACTION_ID, params: ACTION_PARAMS } },
    // `atActionCount: 0` rather than `1` — deliberately valid whether or not `submit-action` has
    // already run: `[0, actionLog.length]` always includes `0`, so this step stays valid even
    // under S5.5's `transposed(REPLAY_FIXTURE, "submit-action", "save-game")`, which moves
    // `submit-action` past this step without moving this step itself.
    { operation: "branch-session" as OperationId, arguments: { sessionId: SESSION_ID, atActionCount: 0 } },
    { operation: "resume-session" as OperationId, arguments: { sessionId: SESSION_ID } },
    { operation: "save-game" as OperationId, arguments: { sessionId: SESSION_ID } },
    { operation: "load-game" as OperationId, arguments: { saveId: SAVE_ID } },
    { operation: "list-saves" as OperationId, arguments: { profileId: PROFILE_ID } },
    { operation: "delete-save" as OperationId, arguments: { profileId: PROFILE_ID, saveId: SAVE_ID, expectedSavedAt: EXPECTED_SAVED_AT } },
  ],
};
