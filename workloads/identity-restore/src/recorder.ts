/**
 * The recorder: copies Keycloak's stored user and admin events into the off-host record while Keycloak is
 * live, one closed window per cycle, and says for each window whether the copy is provably complete.
 *
 * A cycle, with `now` the recorder's clock:
 *  1. Request a token as the recorder client. That is a `CLIENT_LOGIN` in the realm: the canary. If the
 *     token is refused (Keycloak down, the realm held for a restore) the cycle writes nothing; the next
 *     successful cycle starts where the last window ended, so nothing is skipped.
 *  2. Read the realm's event configuration, and the admin-event expiry from the realm's attributes
 *     (Keycloak keeps it there; `/events/config` does not report it). Events off, admin events or their
 *     details off, a required event type not enabled, or an expiry shorter than the minimum: the window
 *     is a gap.
 *  3. Read `[from, through]` with `from` = the last window's end + 1 and `through` = now − settle. Also
 *     re-read `[from − lookback, from − 1]`; an event there not already held is a late arrival, which means
 *     the settle delay is too short.
 *  4. Look for the canary in Keycloak's store at a time after `through`. Found: storage was on and had
 *     committed past the window. Not found: the window is a gap.
 *  5. If `from` is older than either expiry, user or admin, Keycloak may already have deleted events in
 *     it: gap. An admin expiry the recorder cannot read is a gap too.
 *  6. Write the events, then the window.
 *
 * The windows tile time with no overlap (both ends inclusive, like Keycloak's `dateFrom` and `dateTo`), so
 * the gap detector can check the chain.
 */
import type { Keycloak } from "./keycloak.ts";
import { REQUIRED_USER_EVENT_TYPES } from "./plan.ts";
import type { NewWindow, RecorderStore } from "./store.ts";
import type { KcAdminEvent, KcUserEvent, StoredEvent } from "./types.ts";

export interface RecorderOptions {
  readonly realm: string;
  readonly recorderName: string;
  /** How long after an event Keycloak is assumed to have committed it. Must exceed clock skew. */
  readonly settleMs: number;
  readonly lookbackMs: number;
  /** The shortest event expiry the record accepts, in seconds; Keycloak's `null` (never) always passes. */
  readonly minExpirationSeconds: number;
  readonly pageMax: number;
  /** The largest page asked for when one millisecond alone holds more events than `pageMax`. */
  readonly pageCap: number;
  readonly canaryAttempts: number;
  readonly canaryDelayMs: number;
  readonly now: () => number;
  readonly sleep: (ms: number) => Promise<void>;
}

export const DEFAULT_RECORDER: Omit<RecorderOptions, "realm" | "recorderName" | "now" | "sleep"> = {
  settleMs: 10_000,
  lookbackMs: 120_000,
  minExpirationSeconds: 7 * 24 * 3600,
  pageMax: 1000,
  pageCap: 100_000,
  canaryAttempts: 3,
  canaryDelayMs: 1000,
};

interface EventsConfig {
  eventsEnabled?: boolean;
  eventsExpiration?: number | null;
  enabledEventTypes?: string[];
  adminEventsEnabled?: boolean;
  adminEventsDetailsEnabled?: boolean;
  adminEventsExpiration?: number | null;
}

/** The admin-event expiry in seconds from the realm's attributes: `null` when absent or 0 (never expires),
 *  `undefined` when it is present but not a whole number of seconds. */
export function adminExpirySeconds(realm: { attributes?: Record<string, unknown> }): number | null | undefined {
  const raw = realm.attributes?.["adminEventsExpiration"];
  if (raw === undefined || raw === null || raw === "") return null;
  const n = typeof raw === "number" ? raw : /^\s*\d+\s*$/.test(String(raw)) ? Number(raw) : Number.NaN;
  if (!Number.isSafeInteger(n) || n < 0) return undefined;
  return n === 0 ? null : n;
}

export type CycleResult =
  | { readonly wrote: false; readonly why: string }
  | { readonly wrote: true; readonly window: NewWindow };

export function configViolations(c: EventsConfig, minExpirationSeconds: number): string[] {
  const v: string[] = [];
  if (c.eventsEnabled !== true) v.push("user events are not being stored (eventsEnabled is off)");
  if (c.adminEventsEnabled !== true) v.push("admin events are not being stored (adminEventsEnabled is off)");
  if (c.adminEventsDetailsEnabled !== true) v.push("admin event representations are not being stored (adminEventsDetailsEnabled is off)");
  const types = c.enabledEventTypes ?? [];
  // An empty list stores every type in Keycloak, but nothing then stops a later edit from narrowing it
  // without the record noticing; the record requires the types it needs to be listed explicitly.
  if (types.length === 0) v.push("enabledEventTypes is empty: the required types must be listed explicitly");
  const missing = REQUIRED_USER_EVENT_TYPES.filter((t) => !types.includes(t));
  if (types.length > 0 && missing.length > 0) v.push(`required event types not enabled: ${missing.join(", ")}`);
  for (const [k, s] of [["eventsExpiration", c.eventsExpiration], ["adminEventsExpiration", c.adminEventsExpiration]] as const) {
    if (s !== undefined && s !== null && s > 0 && s < minExpirationSeconds) v.push(`${k} is ${s}s, shorter than the ${minExpirationSeconds}s minimum`);
  }
  return v;
}

const qs = (o: Record<string, string | number>) => new URLSearchParams(Object.entries(o).map(([k, v]): [string, string] => [k, String(v)])).toString();

/** Reads every event of one kind in `[a, b]`. A full page is split in two rather than paged with `first`,
 *  so events committed while the read is in progress cannot shift a page boundary and be skipped. */
export async function fetchRange(kc: Keycloak, realm: string, kind: "user" | "admin", a: number, b: number,
  opts: Pick<RecorderOptions, "pageMax" | "pageCap">): Promise<StoredEvent[]> {
  const path = kind === "user" ? "/events" : "/admin-events";
  let max = opts.pageMax;
  for (;;) {
    const page = await kc.json<Array<KcUserEvent | KcAdminEvent>>(realm, `${path}?${qs({ dateFrom: a, dateTo: b, first: 0, max, direction: "asc" })}`);
    if (page.length < max) {
      return page.map((e) => (kind === "user"
        ? { kind: "user", id: e.id, timeMs: e.time, body: e as KcUserEvent }
        : { kind: "admin", id: e.id, timeMs: e.time, body: e as KcAdminEvent }));
    }
    if (a < b) {
      const m = Math.floor((a + b) / 2);
      return [...await fetchRange(kc, realm, kind, a, m, opts), ...await fetchRange(kc, realm, kind, m + 1, b, opts)];
    }
    if (max >= opts.pageCap) throw new Error(`more than ${opts.pageCap} ${kind} events in the single millisecond ${a}; raise the page cap`);
    max = Math.min(opts.pageCap, max * 10);
  }
}

async function findCanary(kc: Keycloak, o: RecorderOptions, afterMs: number): Promise<KcUserEvent | undefined> {
  for (let i = 0; i < o.canaryAttempts; i++) {
    if (i > 0) await o.sleep(o.canaryDelayMs);
    const found = await kc.json<KcUserEvent[]>(o.realm, `/events?${qs({ type: "CLIENT_LOGIN", client: kc.clientId, dateFrom: afterMs + 1, first: 0, max: 1, direction: "asc" })}`);
    if (found[0]) return found[0];
  }
  return undefined;
}

export async function runCycle(kc: Keycloak, store: RecorderStore, o: RecorderOptions): Promise<CycleResult> {
  const startedAt = o.now();
  try {
    await kc.freshToken();
  } catch (e) {
    return { wrote: false, why: `canary token refused, nothing recorded this cycle: ${(e as Error).message}` };
  }
  const realm = await kc.json<{ attributes?: Record<string, unknown> }>(o.realm, "");
  const adminExpiry = adminExpirySeconds(realm);
  const config: EventsConfig = { ...await kc.json<EventsConfig>(o.realm, "/events/config"),
    ...(adminExpiry === undefined ? {} : { adminEventsExpiration: adminExpiry }) };
  const through = startedAt - o.settleMs;
  const last = await store.lastWindow();

  if (!last) {
    // Nothing before this instant is claimed; the restore's gap detector reports the start.
    const window: NewWindow = { fromMs: through, throughMs: through, status: "gap", reason: "bootstrap: the record starts here",
      lateCount: 0, insertedCount: 0, canaryId: null, canaryMs: null, recorder: o.recorderName };
    await store.insertWindow(window);
    return { wrote: true, window };
  }
  const from = last.throughMs + 1;
  if (through < from) return { wrote: false, why: "the settle delay has not passed since the last window" };

  const reasons = configViolations(config, o.minExpirationSeconds);
  const lookFrom = Math.max(0, from - o.lookbackMs);
  const [users, admins, oldUsers, oldAdmins] = [
    await fetchRange(kc, o.realm, "user", from, through, o),
    await fetchRange(kc, o.realm, "admin", from, through, o),
    lookFrom < from ? await fetchRange(kc, o.realm, "user", lookFrom, from - 1, o) : [],
    lookFrom < from ? await fetchRange(kc, o.realm, "admin", lookFrom, from - 1, o) : [],
  ];

  const canary = await findCanary(kc, o, through);
  if (!canary) reasons.push(`the recorder's canary login was not found in Keycloak's store after ${through}: event storage may be off, or Keycloak's clock is more than the settle delay behind`);

  const userExpiry = config.eventsExpiration ?? null;
  if (userExpiry !== null && userExpiry > 0 && from < startedAt - userExpiry * 1000) reasons.push(`the window starts before Keycloak's ${userExpiry}s user event expiry: user events in it may already have been deleted`);
  if (adminExpiry === undefined) reasons.push(`the realm's adminEventsExpiration attribute is ${JSON.stringify(realm.attributes?.["adminEventsExpiration"])}, not a number of seconds: admin events in the window may already have been deleted`);
  else if (adminExpiry !== null && from < startedAt - adminExpiry * 1000) reasons.push(`the window starts before Keycloak's ${adminExpiry}s admin event expiry: admin events in it may already have been deleted`);

  // Late arrivals count only where an earlier window claimed coverage; before the record began they are
  // simply events the record never claimed.
  const claimed = (await store.windowsOverlapping(lookFrom, from - 1)).filter((w) => w.status === "covered");
  const inClaimed = (e: StoredEvent) => claimed.some((w) => e.timeMs >= w.fromMs && e.timeMs <= w.throughMs);
  const old = [...oldUsers, ...oldAdmins];
  const lateCount = await store.insertEvents(old.filter(inClaimed));
  await store.insertEvents(old.filter((e) => !inClaimed(e)));
  const insertedCount = await store.insertEvents([...users, ...admins]);

  const window: NewWindow = {
    fromMs: from, throughMs: through,
    status: reasons.length === 0 ? "covered" : "gap",
    reason: reasons.length === 0 ? null : reasons.join("; "),
    lateCount, insertedCount,
    canaryId: canary?.id ?? null, canaryMs: canary?.time ?? null,
    recorder: o.recorderName,
  };
  await store.insertWindow(window);
  return { wrote: true, window };
}
