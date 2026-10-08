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
const LANDED_ROW_RE = /^\|\s*\*\*(S(\d+))\*\*\s*\|\s*([^|]+?)\s*\|/gm;
const LANDED_BULLET_RE = /^- \*\*(S(\d+)) — (.+?)\*\* — shipped:/gm;
const BULLET_PR_RE = /\bvia\s+\[#(\d+)]\((https:\/\/\S+?)\)/;

/**
 * Parses design/30-slices.md, counting every slice once from whichever of
 * the ledger's three forms carries it: an outstanding slice's `## S<n> — `
 * heading with its `**Status:**` and `Depends on:` lines, or a retired
 * slice's row in the `## Landed` table or bullet under `## Landed` — both of
 * which are shipped and carry no dependency line. Throws rather than
 * returning an empty or partial result on any malformed input — an empty
 * roadmap, a silently-SCHEDULED slice or a slice counted twice is exactly
 * the failure mode this function exists to make impossible. See the
 * archived design/d3/40-site.md, "Derived content".
 */
export function parseSlices(raw: string): Slice[] {
  const headingMatches = [...raw.matchAll(HEADING_RE)];
  if (headingMatches.length === 0) {
    throw new Error(
      "30-slices.md: no '## ' headings found — is this the right document?",
    );
  }

  const slices: Slice[] = [];
  const formOf = new Map<string, string>();
  const add = (slice: Slice, form: string) => {
    const earlier = formOf.get(slice.id);
    if (earlier) {
      throw new Error(
        `30-slices.md: ${slice.id} appears twice — as a ${earlier} and as a ${form}`,
      );
    }
    formOf.set(slice.id, form);
    slices.push(slice);
  };

  for (let i = 0; i < headingMatches.length; i++) {
    const match = headingMatches[i];
    const contentStart = match.index + match[0].length;
    const contentEnd = headingMatches[i + 1]?.index ?? raw.length;
    const body = raw.slice(contentStart, contentEnd);

    if (match[1].trim() === "Landed") {
      for (const row of body.matchAll(LANDED_ROW_RE)) {
        add(
          {
            id: row[1],
            number: Number(row[2]),
            title: row[3],
            status: "shipped",
            dependsOn: "",
          },
          "Landed row",
        );
      }
      const bullets = [...body.matchAll(LANDED_BULLET_RE)];
      for (let j = 0; j < bullets.length; j++) {
        const bullet = bullets[j];
        const text = body.slice(
          bullet.index,
          bullets[j + 1]?.index ?? body.length,
        );
        const prMatch = BULLET_PR_RE.exec(text);
        add(
          {
            id: bullet[1],
            number: Number(bullet[2]),
            title: bullet[3],
            status: "shipped",
            pr: prMatch ? { number: prMatch[1], url: prMatch[2] } : undefined,
            dependsOn: "",
          },
          "Landed bullet",
        );
      }
      continue;
    }

    const sliceMatch = SLICE_HEADING_RE.exec(match[1]);
    if (!sliceMatch) continue;

    const [, id, numberText, title] = sliceMatch;

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

    add(
      {
        id,
        number: Number(numberText),
        title,
        status: parseStatus(id, statusText),
        pr: prMatch ? { number: prMatch[1], url: prMatch[2] } : undefined,
        dependsOn: dependsMatch[1].trim(),
      },
      "heading",
    );
  }

  if (slices.length === 0) {
    throw new Error(
      "30-slices.md: no 'S<n> — ' slice headings and no '## Landed' entries found",
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
 * also checks against the same design/30-slices.md: exactly one
 * 'in progress' slice whenever any slice is unshipped, and every queued slice
 * ordered after every shipped one. Exported so the app, its tests, and the
 * active-ledger test can assert it without duplicating the rule.
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
 * is not machine-parseable the way 30-slices.md's headings are, and the list
 * is closed — closing a non-goal is a brief amendment, not something a slice
 * merge changes.
 */
export const nonGoals: readonly NonGoal[] = [
  {
    title: "Every capability in the platform specification",
    reason:
      "Closed by the brief. D5 builds a bounded subset; the rest waits for a later brief.",
  },
  {
    title: "Migrating the Automator, GEaaS, BarStrad or SkyNet HR",
    reason: "Closed. They supply evidence; the sample supplies the proof.",
  },
  {
    title: "Deploying or operating the sample",
    reason:
      "Closed. It is a CI proof, not a hosted environment or an endpoint.",
  },
  {
    title: "Real payment-provider integration",
    reason:
      "Closed. A provider-neutral seam and a test provider. No checkout, no invoices, no live credentials, and no money moves.",
  },
  {
    title: "Metering, quotas or rate limiting",
    reason:
      "Closed. Execution minutes and playtime stay rejected; nothing is enforced by usage.",
  },
  {
    title: "Machine activation, seats, trials or online revocation",
    reason:
      "Closed. Licensing exposes claims and an optional revocation seam; the policies are the product's.",
  },
  {
    title: "Federation or a shared user directory",
    reason:
      "Closed. Each product keeps its own identity store; the optional account store links sign-ins within one host only.",
  },
  {
    title: "Choosing or operating an identity provider",
    reason:
      "Closed. The seams are provider-neutral; the provider is a deployment's choice.",
  },
  {
    title: "A complete shared administration product",
    reason:
      "Closed. The shell is a proof. Settings pages, notifications, API keys, payments, plugins and feature flags stay out of it.",
  },
  {
    title: "Redesigning tenant identifiers or their storage",
    reason:
      "Closed. The tenant identifier, primary keys and implicit-tenant storage are settled.",
  },
  {
    title: "Marketplace, event bus or enterprise tenancy",
    reason: "Deferred by the platform specification, and still deferred.",
  },
  {
    title: "Performance or capacity targets",
    reason:
      "Closed. D5 proves correctness under the stated concurrency and sets no SLO.",
  },
  {
    title: "Hardening licensing against piracy",
    reason:
      "Closed. The threat is casual over-use, not an operator who controls the clock.",
  },
  {
    title: "Settling module tiers in the brief",
    reason:
      "Closed. The design records each capability's tier; none stays Undecided when D5 finishes.",
  },
  {
    title: "Requiring the commercial modules in local mode",
    reason:
      "Closed. Identity, Organizations, Billing and Licensing are absent there, not registered checks that always pass.",
  },
];
