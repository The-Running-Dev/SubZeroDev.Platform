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

export const REPLAY_FIXTURE: ReplayFixture = {
  campaignId: WORLD_GRAPH_MVP_CAMPAIGN_ID,
  seed: "s5-byte-identity-proof",
  steps: [
    { operation: "list-campaigns" as OperationId, arguments: {} },
    { operation: "create-session" as OperationId, arguments: { campaignId: WORLD_GRAPH_MVP_CAMPAIGN_ID } },
    { operation: "get-scene" as OperationId, arguments: { sessionId: SESSION_ID } },
    { operation: "get-view" as OperationId, arguments: { sessionId: SESSION_ID } },
    { operation: "get-strings" as OperationId, arguments: { sessionId: SESSION_ID } },
    { operation: "preview-action" as OperationId, arguments: { sessionId: SESSION_ID, actionId: ACTION_ID, params: ACTION_PARAMS } },
    { operation: "submit-action" as OperationId, arguments: { sessionId: SESSION_ID, actionId: ACTION_ID, params: ACTION_PARAMS } },
    { operation: "resume-session" as OperationId, arguments: { sessionId: SESSION_ID } },
    { operation: "save-game" as OperationId, arguments: { sessionId: SESSION_ID } },
    { operation: "load-game" as OperationId, arguments: { saveId: "counting-save-id-0" } },
  ],
};
