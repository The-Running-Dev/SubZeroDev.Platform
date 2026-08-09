/**
 * A composed workload over the real engine and a real campaign — what S3.1 and S3.6 need, since
 * "the whole table is routed" and "a rejected action is a 200" are only observable against a game
 * that actually plays.
 */
import {
  buildContentRegistry,
  buildWorldGraphMvpCampaign,
  createEngine,
  createSessionLayer,
  storyGraphKind,
  simulationKind,
  worldGraphKind,
  WORLD_GRAPH_MVP_CAMPAIGN_ID,
} from "@the-running-dev/game-engine";
import type { ContentRegistry, KindRegistry, SessionStore } from "@the-running-dev/game-engine";

export const CAMPAIGN_ID = WORLD_GRAPH_MVP_CAMPAIGN_ID;

export function kinds(): KindRegistry {
  return {
    [storyGraphKind.id]: storyGraphKind,
    [simulationKind.id]: simulationKind,
    [worldGraphKind.id]: worldGraphKind,
  } as KindRegistry;
}

export function contentRegistry(): ContentRegistry {
  const campaign = buildWorldGraphMvpCampaign();
  if (!campaign.ok || !campaign.value) {
    throw new Error("the MVP campaign did not build");
  }
  const registry = buildContentRegistry([campaign.value]);
  if (!registry.ok || !registry.value) {
    throw new Error("the content registry did not build");
  }
  return registry.value;
}

/** The engine and store the workload composes, built directly — `compose()` is asserted on its
 *  own terms in the startup tests, and these tests want a store without a listener. */
export function realStore(): SessionStore {
  const registry = contentRegistry();
  const engine = createEngine({ kinds: kinds(), registry });
  return createSessionLayer({ engine, registry });
}
