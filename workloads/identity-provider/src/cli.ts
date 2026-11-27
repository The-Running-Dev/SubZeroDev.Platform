/**
 * Operator commands for the realms (`members`, `operators`).
 *
 *   node src/cli.ts configure [--realm NAME]
 *                               make Keycloak match realm/NAME.json, then check it; every realm
 *                               when --realm is not given
 *   node src/cli.ts check [--realm NAME]
 *                               report every difference that matters for sign-in safety (exit 1 on
 *                               any); every realm when --realm is not given
 *   node src/cli.ts jwks [--realm NAME]
 *                               print a realm's published key set, for pinning at the edge
 *   node src/cli.ts invite USERNAME --email ADDRESS [--realm NAME]
 *                               create an account and email it a link that registers a passkey and
 *                               recovery codes
 *   node src/cli.ts reset USERNAME [--realm NAME]
 *                               remove every credential and session of an account and email a new
 *                               link; the account and its subject are kept
 *
 * jwks, invite and reset act on the members realm unless --realm names another.
 *   node src/cli.ts backup --out FILE [-- COMMAND...]
 *                               dump the member database to FILE, outside the repository, with
 *                               FILE.meta.json beside it; prints only the path, size and sha256
 *
 * Environment: KC_BASE_URL (default http://localhost:8180), KC_BOOTSTRAP_ADMIN_USERNAME (default
 * bootstrap-admin), KC_BOOTSTRAP_ADMIN_PASSWORD (required), and IDP_<NAME> to override a value in
 * the realm files. `backup` needs no admin credentials: its default command is pg_dump in the
 * running `db` container of compose project IDP_COMPOSE_PROJECT (default identity-provider).
 */
import { pathToFileURL } from "node:url";

import { KeycloakAdmin } from "./admin.ts";
import { backup, composeDumpCommand, metaPath } from "./backup.ts";
import { invite, reset } from "./members.ts";
import { REALMS, check, configure, isRealmName, loadDefinition, publishedKeys } from "./realm.ts";
import type { RealmName } from "./realm.ts";

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

/** Takes `--realm NAME` out of the arguments: the realms it names (every realm when absent) and the rest. */
function realmOption(args: readonly string[]): { realms: RealmName[]; named: boolean; rest: string[] } {
  const at = args.indexOf("--realm");
  if (at < 0) return { realms: [...REALMS], named: false, rest: [...args] };
  const name = args[at + 1];
  if (!name || !isRealmName(name)) throw new Error(`--realm takes one of ${REALMS.join(", ")}`);
  return { realms: [name], named: true, rest: [...args.slice(0, at), ...args.slice(at + 2)] };
}

async function main(command: string | undefined, argv: readonly string[]): Promise<number> {
  if (command === "backup") return runBackup(argv);
  const { realms, named, rest: args } = realmOption(argv);
  // Commands about one account or one key set default to the members realm.
  const one = loadDefinition(process.env, named ? realms[0] : "members");
  const kc = adminFromEnv();
  switch (command) {
    case "configure":
      for (const name of realms) {
        const def = loadDefinition(process.env, name);
        for (const line of await configure(kc, def)) console.log(line);
        console.log(`realm ${name} passes its check`);
      }
      return 0;
    case "check": {
      let failed = false;
      for (const name of realms) {
        const problems = await check(kc, loadDefinition(process.env, name));
        for (const p of problems) console.log(`problem: ${name}: ${p}`);
        if (problems.length === 0) console.log(`realm ${name} passes its check`);
        failed ||= problems.length > 0;
      }
      return failed ? 1 : 0;
    }
    case "jwks":
      console.log(JSON.stringify({ keys: await publishedKeys(kc.baseUrl, one.realm.realm) }));
      return 0;
    case "invite": {
      const at = args.indexOf("--email");
      const [username] = args;
      const email = at >= 0 ? args[at + 1] : undefined;
      if (!username || username.startsWith("--") || !email) {
        console.error("usage: node src/cli.ts invite USERNAME --email ADDRESS [--realm NAME]");
        return 2;
      }
      const id = await invite(kc, one, username, email);
      console.log(`invited ${username} to ${one.realm.realm} (${id}); the enrolment link has been emailed`);
      return 0;
    }
    case "reset": {
      const [username] = args;
      if (!username || username.startsWith("--")) {
        console.error("usage: node src/cli.ts reset USERNAME [--realm NAME]");
        return 2;
      }
      const done = await reset(kc, one, username);
      console.log(`reset ${username} in ${one.realm.realm} (${done.id}): removed ${done.credentials} credential(s), ended every session${done.clients.length ? ` and the offline tokens of ${done.clients.join(", ")}` : ""}; a new enrolment link has been emailed`);
      return 0;
    }
    default:
      console.error("usage: node src/cli.ts configure | check | jwks | invite USERNAME --email ADDRESS | reset USERNAME | backup --out FILE  (add --realm NAME for one realm)");
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
