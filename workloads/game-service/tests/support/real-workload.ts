/**
 * A composed workload over the real engine and a real campaign — what S3.1 and S3.6 need, since
 * "the whole table is routed" and "a rejected action is a 200" are only observable against a game
 * that actually plays. Reached through `compose()` itself rather than a second copy of it, so these
 * tests exercise the same composition the deployed process runs.
 */
import { loadPublishedContract } from "@subzerodev/service-contract";
import { WORLD_GRAPH_MVP_CAMPAIGN_ID } from "@the-running-dev/game-engine";
import type { SessionStore } from "@the-running-dev/game-engine";

import { compose } from "../../src/compose.js";
import type { WorkloadConfiguration } from "../../src/types.js";

export const CAMPAIGN_ID = WORLD_GRAPH_MVP_CAMPAIGN_ID;

const CONFIGURATION: WorkloadConfiguration = {
  listen: { host: "127.0.0.1", port: 0 },
  determinism: { kind: "default" },
  otlpEndpoint: null,
};

export async function realStore(): Promise<SessionStore> {
  const composed = await compose(CONFIGURATION, loadPublishedContract());
  if (!composed.ok) {
    throw new Error(`compose() failed: ${JSON.stringify(composed.error)}`);
  }
  return composed.value.store;
}
