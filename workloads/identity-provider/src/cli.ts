/**
 * Operator commands for the member realm.
 *
 *   node src/cli.ts configure   make Keycloak match realm/members.json, then check it
 *   node src/cli.ts check       report every difference that matters for sign-in safety (exit 1 on any)
 *   node src/cli.ts jwks        print the realm's published key set, for pinning at the edge
 *   node src/cli.ts backup --out FILE [-- COMMAND...]
 *                               dump the member database to FILE, outside the repository, with
 *                               FILE.meta.json beside it; prints only the path, size and sha256
 *
 * Environment: KC_BASE_URL (default http://localhost:8180), KC_BOOTSTRAP_ADMIN_USERNAME (default
 * bootstrap-admin), KC_BOOTSTRAP_ADMIN_PASSWORD (required), and IDP_<NAME> to override a value in
 * realm/members.json. `backup` needs no admin credentials: its default command is pg_dump in the
 * running `db` container of compose project IDP_COMPOSE_PROJECT (default identity-provider).
 */
import { pathToFileURL } from "node:url";

import { KeycloakAdmin } from "./admin.ts";
import { backup, composeDumpCommand, metaPath } from "./backup.ts";
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

async function runBackup(args: readonly string[]): Promise<number> {
  const at = args.indexOf("--out");
  const out = at >= 0 ? args[at + 1] : undefined;
  if (!out) {
    console.error("usage: node src/cli.ts backup --out FILE [-- COMMAND...]");
    return 2;
  }
  const sep = args.indexOf("--");
  const command = sep >= 0 ? args.slice(sep + 1) : composeDumpCommand();
  const meta = await backup(out, command);
  console.log(`backup ${out} (${meta.bytes} bytes, sha256 ${meta.sha256})`);
  console.log(`metadata ${metaPath(out)}`);
  return 0;
}

async function main(command: string | undefined, args: readonly string[]): Promise<number> {
  if (command === "backup") return runBackup(args);
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
      console.error("usage: node src/cli.ts configure | check | jwks | backup --out FILE");
      return 2;
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  main(process.argv[2], process.argv.slice(3)).then(
    (code) => process.exit(code),
    (e: unknown) => {
      console.error(e instanceof Error ? e.message : e);
      process.exit(1);
    },
  );
}
