/**
 * TEST-ONLY. Runs the identity-restore workload's recorder and dump check (workloads/identity-restore)
 * against this workload's realm, as a separate process. Nothing from that workload is copied here;
 * its source is imported at run time only, so this workload has no build or package dependency on it.
 *
 * Protocol, one JSON line each way:
 *   stdout {"step":"bootstrapped","window":...}   after the first cycle
 *   stdin  any line                                the parent has signed a member in
 *   stdout {"step":"done","bootstrap":...,"window":...,"inserted":[...],"meta":...}
 *
 * Environment: RB_BASE_URL, RB_REALM, RB_SECRET (the recorder client's secret),
 * RB_DUMP (a dump written by this workload's `backup`), RB_SETTLE_MS.
 */
import { createInterface } from "node:readline";

const need = (k: string): string => {
  const v = process.env[k];
  if (!v) throw new Error(`${k} is not set`);
  return v;
};
const src = (file: string) => new URL(`../../../identity-restore/src/${file}`, import.meta.url).href;

interface Window { fromMs: number; throughMs: number; status: string; reason: string | null; lateCount: number; canaryId: string | null; canaryMs: number | null }
interface Stored { kind: "user" | "admin"; id: string; timeMs: number; body: Record<string, unknown> }

const { runCycle, DEFAULT_RECORDER } = await import(src("recorder.ts"));
const { Keycloak } = await import(src("keycloak.ts"));
const { verifiedMeta } = await import(src("backup.ts"));

/** The recorder's store interface, held in memory. */
const windows: (Window & { seq: number; polledAtMs: number })[] = [];
const events = new Map<string, Stored>();
const store = {
  async insertEvents(list: readonly Stored[]): Promise<number> {
    let n = 0;
    for (const e of list) if (!events.has(`${e.kind}:${e.id}`)) { events.set(`${e.kind}:${e.id}`, e); n++; }
    return n;
  },
  async insertWindow(w: Window): Promise<void> { windows.push({ ...w, seq: windows.length + 1, polledAtMs: Date.now() }); },
  async lastWindow() { return windows.at(-1); },
  async windowsOverlapping(a: number, b: number) { return windows.filter((w) => w.fromMs <= b && w.throughMs >= a); },
};

const realm = need("RB_REALM");
const kc = new Keycloak({ baseUrl: need("RB_BASE_URL"), tokenRealm: realm, clientId: "identity-recorder", clientSecret: need("RB_SECRET") });
const settleMs = Number(need("RB_SETTLE_MS"));
const options = { ...DEFAULT_RECORDER, settleMs, realm, recorderName: "identity-provider-boundary-test", now: Date.now, sleep: (ms: number) => new Promise((r) => setTimeout(r, ms)) };

const bootstrap = await runCycle(kc, store, options);
const say = (o: unknown) => process.stdout.write(JSON.stringify(o) + "\n");
say({ step: "bootstrapped", window: bootstrap.window ?? null, why: bootstrap.why ?? null });

const lines = createInterface({ input: process.stdin });
await new Promise<void>((done) => lines.once("line", () => done()));
lines.close();
await options.sleep(settleMs + 500);

const second = await runCycle(kc, store, options);
const inserted = [...events.values()].filter((e) => e.kind === "user").map((e) => ({ type: e.body["type"], userId: e.body["userId"] ?? null, clientId: e.body["clientId"] ?? null }));
const meta = await verifiedMeta(need("RB_DUMP"));
say({ step: "done", bootstrap: bootstrap.window ?? null, window: second.window ?? null, why: second.why ?? null, inserted, meta });
