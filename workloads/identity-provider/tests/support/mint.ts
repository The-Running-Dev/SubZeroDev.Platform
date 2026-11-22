/**
 * TEST-ONLY: mints real member tokens for another workload's proof, across a process boundary.
 *
 *   node tests/support/mint.ts mint <prefix>     enrol one synthetic member, sign it in with its
 *                                                passkey through game-cli and through adventures,
 *                                                and print the tokens and the realm's key set as JSON
 *   node tests/support/mint.ts remove <prefix>   delete every synthetic member under <prefix>
 *
 * game-service's edge proof runs this rather than importing these helpers, so that the Keycloak
 * client code has one owner (this workload) and game-service owns only the edge composition. The
 * test-only bootstrap client is installed for the enrolment and removed before the tokens are
 * printed. Same environment as the CLI: KC_BOOTSTRAP_ADMIN_PASSWORD is required.
 */
import { pathToFileURL } from "node:url";

import { publishedKeys } from "../../src/realm.ts";
import { ADVENTURES_CALLBACK, clientSecret, describeWalk, enrol, environment, installBootstrapClient, removeBootstrapClient, removeMembers, walk } from "./members.ts";

export interface Minted {
  issuer: string;
  /** The realm's published key set, as the JSON string the edge's SigningKeys setting takes. */
  jwks: string;
  gameAudience: string;
  subject: string;
  /** game-cli access token: carries the game audience. */
  game: string;
  /** adventures access token: carries the adventures audience and not the game one. */
  adventures: string;
}

async function main(command: string | undefined, prefix: string | undefined): Promise<number> {
  const e = environment();
  if (!e) throw new Error("KC_BOOTSTRAP_ADMIN_PASSWORD is not set");
  if (!prefix || !/^[a-z0-9-]{3,40}$/.test(prefix)) throw new Error("usage: node tests/support/mint.ts mint|remove <prefix of 3-40 [a-z0-9-]>");
  if (command === "remove") {
    await removeMembers(e, prefix);
    return 0;
  }
  if (command !== "mint") throw new Error("usage: node tests/support/mint.ts mint|remove <prefix>");

  await installBootstrapClient(e);
  let member;
  try {
    member = await enrol(e, prefix);
  } finally {
    await removeBootstrapClient(e);
  }
  const game = await walk(e, "game-cli", member.username, { passkey: member.passkey });
  if (!game.tokens) throw new Error(`game-cli sign-in failed: ${describeWalk(game)}`);
  const adventures = await walk(e, "adventures", member.username, {
    passkey: member.passkey,
    client: { secret: await clientSecret(e, "adventures"), redirect: ADVENTURES_CALLBACK },
  });
  if (!adventures.tokens) throw new Error(`adventures sign-in failed: ${describeWalk(adventures)}`);

  const subject = JSON.parse(Buffer.from(game.tokens.access_token.split(".")[1]!, "base64url").toString()).sub as string;
  const minted: Minted = {
    issuer: e.issuer,
    jwks: JSON.stringify({ keys: await publishedKeys(e.base, e.realm) }),
    gameAudience: e.def.gameAudience,
    subject,
    game: game.tokens.access_token,
    adventures: adventures.tokens.access_token,
  };
  process.stdout.write(`${JSON.stringify(minted)}\n`);
  return 0;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  main(process.argv[2], process.argv[3]).then(
    (code) => process.exit(code),
    (err: unknown) => {
      console.error(err instanceof Error ? err.message : err);
      process.exit(1);
    },
  );
}
