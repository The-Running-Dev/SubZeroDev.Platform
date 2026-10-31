import { render, screen, within } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import RoadmapApp, { RoadmapView } from "./RoadmapApp";
import {
  betweenEfforts,
  lastArchivedLedger,
  nonGoals as activeNonGoals,
  slices as activeSlices,
  type NonGoal,
  type Slice,
} from "./roadmapData";

const repo = "https://github.com/The-Running-Dev/SubZeroDev.Platform";

function slice(id: string, status: Slice["status"]): Slice {
  return {
    id,
    number: Number(id.slice(1)),
    title: `Title of ${id}`,
    status,
    dependsOn: "none",
  };
}

// A fixture ledger, so these tests do not change meaning the day the active
// ledger does (or disappears between efforts).
const fixtureSlices: readonly Slice[] = [
  slice("S1", "shipped"),
  slice("S2", "shipped"),
  slice("S3", "in-progress"),
  slice("S4", "queued"),
  slice("S5", "queued"),
];
const fixtureNonGoals: readonly NonGoal[] = [
  { title: "A thing not done", reason: "Closed. Because." },
  { title: "Another thing not done", reason: "Deferred. Later." },
];

describe("roadmap page — an active ledger", () => {
  const view = () =>
    render(<RoadmapView slices={fixtureSlices} nonGoals={fixtureNonGoals} />);

  it("renders a truthful headline derived from the parsed inventory", () => {
    view();
    expect(screen.getByRole("heading", { level: 1 })).toHaveTextContent(
      "2 of 5 Slices Resolved.",
    );
  });

  it("has four named landmark sections for RESOLVED, ONGOING, SCHEDULED, WON'T FIX", () => {
    view();
    const regions = screen.getAllByRole("region");
    const names = regions.map((r) => r.getAttribute("aria-labelledby"));
    for (const id of [
      "resolved-title",
      "ongoing-title",
      "scheduled-title",
      "wontfix-title",
    ]) {
      expect(names).toContain(id);
    }
  });

  it("lists shipped slices newest-first in the RESOLVED section", () => {
    view();
    const resolved = screen
      .getByRole("heading", { name: "Closed Incidents" })
      .closest("section")!;
    const rows = within(resolved).getAllByRole("row").slice(1); // drop header row
    expect(
      rows.map((r) => within(r).getAllByRole("cell")[0].textContent),
    ).toEqual(["S2", "S1"]);
  });

  it("shows the current slice in ONGOING", () => {
    view();
    const ongoing = screen
      .getByRole("heading", { name: "Open Incident" })
      .closest("section")!;
    expect(within(ongoing).getByText("S3")).toBeInTheDocument();
  });

  it("lists every queued slice in SCHEDULED, in dependency order", () => {
    view();
    const scheduled = screen
      .getByRole("heading", { name: "Scheduled Maintenance" })
      .closest("section")!;
    const rows = within(scheduled).getAllByRole("row").slice(1);
    expect(
      rows.map((r) => within(r).getAllByRole("cell")[0].textContent),
    ).toEqual(["S4", "S5"]);
  });

  it("closes every brief non-goal as WON'T FIX with its reason", () => {
    view();
    const wontFix = screen
      .getByRole("heading", { name: "Closed As Not Planned" })
      .closest("section")!;
    for (const goal of fixtureNonGoals) {
      expect(within(wontFix).getByText(goal.title)).toBeInTheDocument();
      expect(within(wontFix).getByText(goal.reason)).toBeInTheDocument();
    }
  });

  it("links the active ledger, not an archived one", () => {
    view();
    expect(
      screen.getByRole("link", { name: /Read the slice ledger/ }),
    ).toHaveAttribute("href", `${repo}/blob/main/design/30-slices.md`);
  });

  it("renders no hard-coded slice or package count: the headline tracks the ledger it is given", () => {
    render(
      <RoadmapView
        slices={[slice("S1", "shipped"), slice("S2", "queued")]}
        nonGoals={[]}
      />,
    );
    // If this ever drifts, it means someone typed a literal number in
    // RoadmapApp.tsx instead of counting the slices it was handed.
    expect(screen.getByText("1 of 2 Slices Resolved.")).toBeInTheDocument();
  });
});

describe("roadmap page — between efforts (no active design/30-slices.md)", () => {
  // Non-goals are passed deliberately: an empty ledger must not show them
  // even if a caller hands some over, because no brief is active.
  const view = () =>
    render(<RoadmapView slices={[]} nonGoals={fixtureNonGoals} />);

  it("says nothing is in progress instead of '0 of 0 Slices Resolved.'", () => {
    view();
    const h1 = screen.getByRole("heading", { level: 1 });
    expect(h1).toHaveTextContent("Nothing In Progress.");
    expect(h1).not.toHaveTextContent(/0 of 0/);
  });

  it("keeps the four named sections, each saying there is nothing to show", () => {
    view();
    const regions = screen.getAllByRole("region");
    const names = regions.map((r) => r.getAttribute("aria-labelledby"));
    for (const id of [
      "resolved-title",
      "ongoing-title",
      "scheduled-title",
      "wontfix-title",
    ]) {
      expect(names).toContain(id);
    }
    const ongoing = screen
      .getByRole("heading", { name: "Open Incident" })
      .closest("section")!;
    expect(
      within(ongoing).getByText(/Nothing currently in progress/),
    ).toBeInTheDocument();
    const scheduled = screen
      .getByRole("heading", { name: "Scheduled Maintenance" })
      .closest("section")!;
    expect(
      within(scheduled).getByText(/Nothing scheduled/),
    ).toBeInTheDocument();
    expect(screen.queryAllByRole("row")).toHaveLength(0);
  });

  it("links the last archived ledger, from the resolved section and from the footer links", () => {
    view();
    const href = `${repo}/blob/main/${lastArchivedLedger.path}`;
    const resolved = screen
      .getByRole("heading", { name: "Closed Incidents" })
      .closest("section")!;
    expect(
      within(resolved).getByRole("link", { name: /archived D5 ledger/ }),
    ).toHaveAttribute("href", href);
    expect(
      screen.getByRole("link", { name: /Read the last slice ledger/ }),
    ).toHaveAttribute("href", href);
    expect(
      screen.queryByRole("link", { name: /^Read the slice ledger$/ }),
    ).toBeNull();
  });

  it("shows no non-goals: with no active brief none is in force", () => {
    view();
    const wontFix = screen
      .getByRole("heading", { name: "Closed As Not Planned" })
      .closest("section")!;
    expect(within(wontFix).queryByRole("list")).toBeNull();
    for (const goal of fixtureNonGoals) {
      expect(within(wontFix).queryByText(goal.title)).toBeNull();
    }
    expect(within(wontFix).getByText(/No brief is active/)).toBeInTheDocument();
  });
});

describe("the default export renders whatever ledger the build found", () => {
  it("is the between-efforts page exactly when roadmapData reports no active ledger", () => {
    render(<RoadmapApp />);
    const h1 = screen.getByRole("heading", { level: 1 });
    if (betweenEfforts) {
      expect(activeSlices).toHaveLength(0);
      expect(activeNonGoals).toHaveLength(0);
      expect(h1).toHaveTextContent("Nothing In Progress.");
    } else {
      expect(h1).toHaveTextContent(/\d+ of \d+ Slices Resolved\./);
    }
  });
});
