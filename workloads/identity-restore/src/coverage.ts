/**
 * Missing-event detection. Given every window the recorder wrote, decide whether the record provably holds
 * every event from the replay window's start to the moment the old service stopped. Pure: no I/O.
 *
 * A millisecond counts as recorded only if a `covered` window contains it and no `gap` window does. The
 * answer is one of:
 *  - complete: every millisecond from `windowStartMs` to `serviceEndedAtMs` is recorded;
 *  - a list of gaps, each blocking. Which gaps, if any, may be accepted is an owner policy this module does
 *    not decide; the restore fails closed on any of them.
 */
import type { RecordWindow } from "./types.ts";

export type GapKind = "start" | "interior" | "tail" | "late-arrival" | "read-source";

export interface Gap {
  readonly kind: GapKind;
  readonly fromMs: number;
  /** `null` when the gap has no known end: the old service's end time was not given. */
  readonly throughMs: number | null;
  readonly reasons: readonly string[];
}

export interface Coverage {
  readonly complete: boolean;
  readonly windowStartMs: number;
  /** The last millisecond of the recorded run that starts at `windowStartMs`, or `null` if none does. */
  readonly recordedThroughMs: number | null;
  readonly serviceEndedAtMs: number | null;
  readonly gaps: readonly Gap[];
}

interface Span { from: number; through: number }

function merge(spans: Span[]): Span[] {
  const sorted = [...spans].sort((a, b) => a.from - b.from || a.through - b.through);
  const out: Span[] = [];
  for (const s of sorted) {
    const last = out[out.length - 1];
    if (last && s.from <= last.through + 1) last.through = Math.max(last.through, s.through);
    else out.push({ ...s });
  }
  return out;
}

function subtract(spans: Span[], holes: Span[]): Span[] {
  let out = spans.map((s) => ({ ...s }));
  for (const h of holes) {
    const next: Span[] = [];
    for (const s of out) {
      if (h.through < s.from || h.from > s.through) { next.push(s); continue; }
      if (s.from < h.from) next.push({ from: s.from, through: h.from - 1 });
      if (s.through > h.through) next.push({ from: h.through + 1, through: s.through });
    }
    out = next;
  }
  return out;
}

function reasonsFor(windows: readonly RecordWindow[], from: number, through: number | null): string[] {
  const hits = windows.filter((w) => w.status === "gap" && w.throughMs >= from && (through === null || w.fromMs <= through));
  const reasons = [...new Set(hits.map((w) => w.reason ?? "gap"))];
  return reasons.length > 0 ? reasons : ["no window recorded for this range (the recorder did not run, or its writes were lost)"];
}

export function assessCoverage(input: {
  readonly windows: readonly RecordWindow[];
  readonly windowStartMs: number;
  /** When the old service stopped accepting requests. Asserted by the operator; omitted when unknown. */
  readonly serviceEndedAtMs?: number;
  /** When the old Keycloak and its database were stopped, before the restore. A window the recorder wrote
   *  later may have been read from the restored database, which lacks every event after the backup but
   *  still holds the recorder's older canaries, so it proves nothing and is ignored. Asserted by the
   *  operator; when omitted, no window can be trusted not to be such a read, and the result is a gap. */
  readonly storeRetiredAtMs?: number;
}): Coverage {
  const { windowStartMs: start } = input;
  const end = input.serviceEndedAtMs ?? null;
  const retired = input.storeRetiredAtMs ?? null;
  const windows = retired === null ? input.windows : input.windows.filter((w) => w.polledAtMs <= retired);
  const covered = merge(windows.filter((w) => w.status === "covered").map((w) => ({ from: w.fromMs, through: w.throughMs })));
  const holes = windows.filter((w) => w.status === "gap").map((w) => ({ from: w.fromMs, through: w.throughMs }));
  const recorded = subtract(covered, holes).filter((s) => s.through >= start);

  const gaps: Gap[] = [];
  const first = recorded[0];
  let recordedThroughMs: number | null = null;

  if (!first || first.from > start) {
    const through = first ? first.from - 1 : end;
    gaps.push({ kind: "start", fromMs: start, throughMs: through, reasons: reasonsFor(windows, start, through) });
  }
  for (let i = 0; i < recorded.length; i++) {
    const s = recorded[i]!;
    recordedThroughMs = s.through;
    if (end !== null && s.through >= end) break;
    const next = recorded[i + 1];
    if (!next || (end !== null && next.from > end)) break;
    gaps.push({ kind: "interior", fromMs: s.through + 1, throughMs: next.from - 1, reasons: reasonsFor(windows, s.through + 1, next.from - 1) });
  }
  if (recordedThroughMs !== null && (end === null || recordedThroughMs < end)) {
    const reasons = end === null
      ? ["the old service's end time is not known: events after the last recorded window may be missing"]
      : ["events between the last recorded window and the end of service were not recorded"];
    gaps.push({ kind: "tail", fromMs: recordedThroughMs + 1, throughMs: end, reasons });
  }

  const relevant = (w: RecordWindow) => w.throughMs >= start && (end === null || w.fromMs <= end);
  for (const w of windows) {
    if (w.lateCount > 0 && relevant(w)) {
      gaps.push({
        kind: "late-arrival",
        fromMs: w.fromMs,
        throughMs: w.throughMs,
        reasons: [`${w.lateCount} event(s) arrived after their window was recorded: the settle delay is shorter than Keycloak's commit delay, so an event later than the look-back could be missing`],
      });
    }
  }

  if (retired === null) {
    gaps.push({ kind: "read-source", fromMs: start, throughMs: end,
      reasons: ["the time the old Keycloak store was retired is not given: a window read from the restored store cannot be told apart"] });
  } else if (end !== null && retired < end) {
    gaps.push({ kind: "read-source", fromMs: retired, throughMs: end,
      reasons: ["the old store is said to have been retired before the service ended; the two times are inconsistent"] });
  }

  return {
    complete: gaps.length === 0,
    windowStartMs: start,
    recordedThroughMs: first && first.from <= start ? recordedThroughMs : null,
    serviceEndedAtMs: end,
    gaps,
  };
}
