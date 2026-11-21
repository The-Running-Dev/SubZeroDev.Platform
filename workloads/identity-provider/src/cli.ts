/**
 * Operator commands for the member realm.
 *
 *   node src/cli.ts configure   make Keycloak match realm/members.json, then check it
 *   node src/cli.ts check       report every difference that matters for sign-in safety (exit 1 on any)
 *   node src/cli.ts jwks        print the realm's published key set, for pinning at the edge
 *
 * Environment: KC_BASE_URL (default http://localhost:8180), KC_BOOTSTRAP_ADMIN_USERNAME (default
 * bootstrap-admin), KC_BOOTSTRAP_ADMIN_PASSWORD (required), and IDP_<NAME> to override a value in
 * realm/members.json.
 */
import { pathToFileURL } from "node:url";

import { KeycloakAdmin } from "./admin.ts";
import { check, configure, loadDefinition, publishedKeys } from "./realm.ts";

export function adminFromEnv(env: NodeJS.ProcessEnv = process.env): KeycloakAdmin {
  const password = env["KC_BOOTSTRAP_ADMIN_PASSWORD"];
  if (!password) throw new Error("KC_BOOTSTRAP_ADMIN_PASSWORD is not set");
  return new KeycloakAdmin({
    baseUrl: env["KC_BASE_URL"] ?? "http://localhost:8180",
    username: env["KC_BOOTSTRAP_ADMIN_USERNAME"] ?? "bootstrap-admin",
    password,
  });
}

async function main(command: string | undefined): Promise<number> {
  const def = loadDefinition();
  const kc = adminFromEnv();
  switch (command) {
    case "configure":
      for (const line of await configure(kc, def)) console.log(line);
      console.log(`realm ${def.realm.realm} passes its check`);
      return 0;
    case "check": {
      const problems = await check(kc, def);
      for (const p of problems) console.log(`problem: ${p}`);
      if (problems.length === 0) console.log(`realm ${def.realm.realm} passes its check`);
      return problems.length ? 1 : 0;
    }
    case "jwks":
      console.log(JSON.stringify({ keys: await publishedKeys(kc.baseUrl, def.realm.realm) }));
      return 0;
    default:
      console.error("usage: node src/cli.ts configure | check | jwks");
      return 2;
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  main(process.argv[2]).then(
    (code) => process.exit(code),
    (e: unknown) => {
      console.error(e instanceof Error ? e.message : e);
      process.exit(1);
    },
  );
}
