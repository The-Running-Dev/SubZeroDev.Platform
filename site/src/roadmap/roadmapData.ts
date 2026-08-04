import slicesRaw from "../../../design/30-slices.md?raw";

export type SliceStatus = "shipped" | "in-progress" | "queued";

export type Slice = {
  id: string;
  number: number;
  title: string;
  status: SliceStatus;
  pr?: { number: string; url: string };
  dependsOn: string;
};

const HEADING_RE = /^## (.+)$/gm;
const SLICE_HEADING_RE = /^(S(\d+)) — (.+)$/;
const STATUS_RE = /^\*\*Status:\*\*\s*(.+)$/m;
const DEPENDS_RE = /^Depends on:\s*(.+)$/m;
const PR_LINK_RE = /\[#(\d+)]\((https:\/\/\S+?)\)/;

/**
 * Parses design/30-slices.md's own headings and Status lines. Throws rather
 * than returning an empty or partial result on any malformed input — an
 * empty roadmap or a silently-SCHEDULED slice is exactly the failure mode
 * this function exists to make impossible. See design/40-site.md, "Derived
 * content".
 */
export function parseSlices(raw: string): Slice[] {
  const headingMatches = [...raw.matchAll(HEADING_RE)];
  if (headingMatches.length === 0) {
    throw new Error(
      "30-slices.md: no '## ' headings found — is this the right document?",
    );
  }

  const slices: Slice[] = [];
  for (let i = 0; i < headingMatches.length; i++) {
    const match = headingMatches[i];
    const sliceMatch = SLICE_HEADING_RE.exec(match[1]);
    if (!sliceMatch) continue;

    const [, id, numberText, title] = sliceMatch;
    const contentStart = match.index + match[0].length;
    const contentEnd = headingMatches[i + 1]?.index ?? raw.length;
    const body = raw.slice(contentStart, contentEnd);

    const statusMatch = STATUS_RE.exec(body);
    if (!statusMatch) {
      throw new Error(`30-slices.md: ${id} has no '**Status:**' line`);
    }
    const statusText = statusMatch[1].trim();

    const dependsMatch = DEPENDS_RE.exec(body);
    if (!dependsMatch) {
      throw new Error(`30-slices.md: ${id} has no 'Depends on:' line`);
    }

    const prMatch = PR_LINK_RE.exec(statusText);

    slices.push({
      id,
      number: Number(numberText),
      title,
      status: parseStatus(id, statusText),
      pr: prMatch ? { number: prMatch[1], url: prMatch[2] } : undefined,
      dependsOn: dependsMatch[1].trim(),
    });
  }

  if (slices.length === 0) {
    throw new Error(
      "30-slices.md: no 'S<n> — ' slice headings found among its '## ' headings",
    );
  }

  return slices.sort((a, b) => a.number - b.number);
}

function parseStatus(id: string, text: string): SliceStatus {
  if (text.startsWith("shipped")) return "shipped";
  if (text.startsWith("in progress")) return "in-progress";
  if (text.startsWith("queued")) return "queued";
  throw new Error(`30-slices.md: ${id} has an unrecognised status: "${text}"`);
}

/**
 * Fails the internal-consistency invariants build/Test-SliceStatusMarkers.ps1
 * also checks at the repository level: exactly one 'in progress' slice
 * whenever any slice is unshipped, and every queued slice ordered after
 * every shipped one. Exported so both the app and its tests can assert it
 * without duplicating the rule.
 */
export function assertConsistent(slices: readonly Slice[]): void {
  const inProgress = slices.filter((s) => s.status === "in-progress");
  const hasQueued = slices.some((s) => s.status === "queued");

  if (inProgress.length > 1) {
    throw new Error(
      `30-slices.md: more than one slice marked 'in progress': ${inProgress.map((s) => s.id).join(", ")}`,
    );
  }
  if (inProgress.length === 0 && hasQueued) {
    throw new Error(
      "30-slices.md: no slice is marked 'in progress' while a 'queued' slice exists",
    );
  }

  let seenQueued = false;
  for (const slice of slices) {
    if (slice.status === "queued") seenQueued = true;
    if (slice.status === "shipped" && seenQueued) {
      throw new Error(
        `30-slices.md: ${slice.id} is 'shipped' but ordered after a 'queued' slice`,
      );
    }
  }
}

export const slices: readonly Slice[] = parseSlices(slicesRaw);
assertConsistent(slices);

export const shippedSlices = slices.filter((s) => s.status === "shipped");
export const currentSlice = slices.find((s) => s.status === "in-progress");
export const queuedSlices = slices.filter((s) => s.status === "queued");

export const shippedCount = shippedSlices.length;
export const totalCount = slices.length;

export type NonGoal = {
  title: string;
  reason: string;
};

/**
 * design/00-brief.md's Non-goals section, hand-authored: the brief's prose
 * is not machine-parseable the way 30-slices.md's headings are, and these
 * four are a closed, stable list — closing a non-goal is a brief amendment,
 * not something a slice merge changes.
 */
export const nonGoals: readonly NonGoal[] = [
  {
    title: "Tenant query filters",
    reason:
      "Scheduled for D5. The column ships now; the filter is cheap later.",
  },
  {
    title: "Hosted multi-tenant SaaS deployment",
    reason: "Closed by design. Self-host only, per the brief's Environment.",
  },
  {
    title: "Adopting an application framework",
    reason: "Closed by ADR-004. ABP is a reference, never a dependency.",
  },
  {
    title: "Any runtime dependency on outbound network",
    reason:
      "Closed aggressively. Licensing verifies locally; nothing calls out.",
  },
];
