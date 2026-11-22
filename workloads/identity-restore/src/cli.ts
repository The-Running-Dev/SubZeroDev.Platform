/**
 * identity-restore: record Keycloak's events off-host, and replay them after a database restore.
 *
 *   install-schema                     create the record schema and grant the two roles (run as the owner)
 *   record [--once] [--interval-ms N]  copy events into the record, one window per cycle
 *   backup --out FILE -- CMD...        run the dump command, stdout to FILE, and write FILE.meta.json
 *   coverage --dump FILE [...]         is the record complete for a restore of FILE? (read-only)
 *   plan --dump FILE [...]             what would the replay do? (read-only)
 *   restore --dump FILE --restore-id ID [--reopen] [...]
 *   release-temp-locks                 lift the disables standing in for temporary lockouts that have ended
 *
 * Shared restore options: --margin-ms N (default 60000), --service-ended-at T, --store-retired-at T
 * (T is epoch ms or an ISO time), --member-clients a,b, --allow-replay-lockouts.
 *
 * Environment: KC_BASE_URL, KC_REALM, KC_RECORDER_CLIENT_ID, KC_RECORDER_CLIENT_SECRET (record),
 * KC_ADMIN_REALM (default master), KC_OPERATOR_CLIENT_ID, KC_OPERATOR_CLIENT_SECRET (coverage, plan,
 * restore, release), RECORD_DATABASE_URL, RECORD_SCHEMA (default kc_record), and for install-schema
 * RECORD_RECORDER_ROLE, RECORD_RESTORER_ROLE. NODE_EXTRA_CA_CERTS for a private CA.
 *
 * Exit codes: 0 success; 2 the record or the replay has blocking findings (the realm stays held);
 * 3 the restore was refused; 1 an error.
 */
import { parseArgs } from "node:util";
import { restore, type RestoreReport } from "./apply.ts";
import { backup, verifiedMeta } from "./backup.ts";
import { assessCoverage } from "./coverage.ts";
import { Keycloak } from "./keycloak.ts";
import { plan } from "./plan.ts";
import { DEFAULT_RECORDER, runCycle } from "./recorder.ts";
import { releaseTempLocks } from "./release.ts";
import { installSchema, RecordStore } from "./store.ts";

const env = (k: string, fallback?: string): string => {
  const v = process.env[k] ?? fallback;
  if (v === undefined || v === "") throw new Error(`${k} is not set`);
  return v;
};
const time = (s: string | undefined): number | undefined => {
  if (s === undefined) return undefined;
  const t = /^\d+$/.test(s) ? Number(s) : Date.parse(s);
  if (!Number.isFinite(t)) throw new Error(`not a time: ${s}`);
  return t;
};
const print = (x: unknown) => process.stdout.write(JSON.stringify(x, null, 2) + "\n");
const store = () => new RecordStore(env("RECORD_DATABASE_URL"), env("RECORD_SCHEMA", "kc_record"));
const operator = () => new Keycloak({ baseUrl: env("KC_BASE_URL"), tokenRealm: env("KC_ADMIN_REALM", "master"),
  clientId: env("KC_OPERATOR_CLIENT_ID"), clientSecret: env("KC_OPERATOR_CLIENT_SECRET") });

const options = {
  once: { type: "boolean" }, "interval-ms": { type: "string" }, out: { type: "string" }, dump: { type: "string" },
  "margin-ms": { type: "string" }, "service-ended-at": { type: "string" }, "store-retired-at": { type: "string" },
  "member-clients": { type: "string" }, "restore-id": { type: "string" }, reopen: { type: "boolean" },
  "allow-replay-lockouts": { type: "boolean" }, "settle-ms": { type: "string" }, "lookback-ms": { type: "string" },
} as const;

async function main(argv: string[]): Promise<number> {
  const [command, ...rest] = argv;
  const dashdash = rest.indexOf("--");
  const { values: v } = parseArgs({ args: dashdash < 0 ? rest : rest.slice(0, dashdash), options, allowPositionals: false });
  const margin = Number(v["margin-ms"] ?? 60_000);
  const serviceEnd = time(v["service-ended-at"]);
  const retired = time(v["store-retired-at"]);
  const realm = () => env("KC_REALM");

  switch (command) {
    case "install-schema":
      await installSchema(env("RECORD_DATABASE_URL"), env("RECORD_SCHEMA", "kc_record"),
        { recorder: env("RECORD_RECORDER_ROLE"), restorer: env("RECORD_RESTORER_ROLE") });
      print({ installed: env("RECORD_SCHEMA", "kc_record") });
      return 0;

    case "record": {
      const kc = new Keycloak({ baseUrl: env("KC_BASE_URL"), tokenRealm: realm(), clientId: env("KC_RECORDER_CLIENT_ID"), clientSecret: env("KC_RECORDER_CLIENT_SECRET") });
      const s = store();
      const o = { ...DEFAULT_RECORDER, realm: realm(), recorderName: env("RECORDER_NAME", "recorder"), now: Date.now,
        sleep: (ms: number) => new Promise<void>((r) => setTimeout(r, ms)),
        ...(v["settle-ms"] ? { settleMs: Number(v["settle-ms"]) } : {}), ...(v["lookback-ms"] ? { lookbackMs: Number(v["lookback-ms"]) } : {}) };
      const interval = Number(v["interval-ms"] ?? 60_000);
      try {
        for (;;) {
          try { print({ at: new Date().toISOString(), ...(await runCycle(kc, s, o)) }); }
          catch (e) { if (v.once) throw e; process.stderr.write(`cycle failed, nothing recorded: ${(e as Error).message}\n`); }
          if (v.once) return 0;
          await o.sleep(interval);
        }
      } finally { await s.close(); }
    }

    case "backup": {
      if (!v.out) throw new Error("backup needs --out FILE");
      print(await backup(v.out, dashdash < 0 ? [] : rest.slice(dashdash + 1)));
      return 0;
    }

    case "coverage": case "plan": {
      if (!v.dump) throw new Error(`${command} needs --dump FILE`);
      const meta = await verifiedMeta(v.dump);
      const s = store();
      try {
        const windowStartMs = meta.startedAtMs - margin;
        if (command === "coverage") {
          const c = assessCoverage({ windows: await s.windows(), windowStartMs,
            ...(serviceEnd === undefined ? {} : { serviceEndedAtMs: serviceEnd }), ...(retired === undefined ? {} : { storeRetiredAtMs: retired }) });
          print(c);
          return c.complete ? 0 : 2;
        }
        const kc = operator();
        const r = await kc.json<{ id: string; maxFailureWaitSeconds?: number }>(realm(), "");
        const events = (await s.eventsFrom(r.id, windowStartMs)).filter((e) => e.timeMs <= (serviceEnd ?? Number.POSITIVE_INFINITY));
        const p = plan({ events, realmId: r.id, backupStartedAtMs: meta.startedAtMs, backupFinishedAtMs: meta.finishedAtMs, nowMs: Date.now(),
          maxFailureWaitSeconds: r.maxFailureWaitSeconds ?? 900, restoreClientIds: await kc.identities() });
        print(p);
        return p.findings.some((f) => f.severity === "blocking") ? 2 : 0;
      } finally { await s.close(); }
    }

    case "restore": {
      if (!v.dump) throw new Error("restore needs --dump FILE");
      if (!v["restore-id"]) throw new Error("restore needs --restore-id ID (reuse it to rerun the same restore)");
      const meta = await verifiedMeta(v.dump);
      const s = store();
      const kc = operator();
      try {
        const report: RestoreReport = await restore({
          kc, realm: realm(), store: s, restoreId: v["restore-id"], backup: meta, marginMs: margin,
          ...(serviceEnd === undefined ? {} : { serviceEndedAtMs: serviceEnd }), ...(retired === undefined ? {} : { storeRetiredAtMs: retired }),
          memberClients: (v["member-clients"] ?? "").split(",").filter(Boolean), restoreClientIds: await kc.identities(),
          reopen: v.reopen === true, allowReplayLockouts: v["allow-replay-lockouts"] === true, now: Date.now,
        });
        print(report);
        return report.outcome === "refused" ? 3 : report.blocking.length > 0 ? 2 : 0;
      } finally { await s.close(); }
    }

    case "release-temp-locks": {
      const s = store();
      const kc = operator();
      try {
        const out = await releaseTempLocks({ kc, realm: realm(), store: s, restoreClientIds: await kc.identities(), now: Date.now });
        print(out);
        return 0;
      } finally { await s.close(); }
    }

    default:
      process.stderr.write("usage: cli.ts install-schema | record | backup | coverage | plan | restore | release-temp-locks\n");
      return 1;
  }
}

main(process.argv.slice(2)).then((code) => { process.exitCode = code; }, (e: unknown) => {
  process.stderr.write(`${(e as Error).stack ?? String(e)}\n`);
  process.exitCode = 1;
});
