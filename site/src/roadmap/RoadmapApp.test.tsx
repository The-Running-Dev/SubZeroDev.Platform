import { render, screen, within } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import RoadmapApp from "./RoadmapApp";
import {
  currentSlice,
  nonGoals,
  queuedSlices,
  shippedCount,
  shippedSlices,
  totalCount,
} from "./roadmapData";

describe("roadmap page", () => {
  it("renders a truthful headline derived from the parsed inventory", () => {
    render(<RoadmapApp />);
    expect(screen.getByRole("heading", { level: 1 })).toHaveTextContent(
      `${shippedCount} of ${totalCount} Slices Resolved.`,
    );
  });

  it("has four named landmark sections for RESOLVED, ONGOING, SCHEDULED, WON'T FIX", () => {
    render(<RoadmapApp />);
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
    render(<RoadmapApp />);
    const resolved = screen
      .getByRole("heading", {
        name: "Closed Incidents",
      })
      .closest("section")!;
    if (shippedSlices.length > 0) {
      const rows = within(resolved).getAllByRole("row").slice(1); // drop header row
      const expectedOrder = [...shippedSlices].reverse().map((s) => s.id);
      expect(
        rows.map((r) => within(r).getAllByRole("cell")[0].textContent),
      ).toEqual(expectedOrder);
    }
  });

  it("shows the current slice in ONGOING when one exists", () => {
    render(<RoadmapApp />);
    const ongoing = screen
      .getByRole("heading", { name: "Open Incident" })
      .closest("section")!;
    if (currentSlice) {
      expect(within(ongoing).getByText(currentSlice.id)).toBeInTheDocument();
    } else {
      expect(
        within(ongoing).getByText(/Nothing currently in progress/),
      ).toBeInTheDocument();
    }
  });

  it("lists every queued slice in SCHEDULED, in dependency order", () => {
    render(<RoadmapApp />);
    const scheduled = screen
      .getByRole("heading", {
        name: "Scheduled Maintenance",
      })
      .closest("section")!;
    if (queuedSlices.length > 0) {
      const rows = within(scheduled).getAllByRole("row").slice(1);
      expect(
        rows.map((r) => within(r).getAllByRole("cell")[0].textContent),
      ).toEqual(queuedSlices.map((s) => s.id));
    }
  });

  it("closes every brief non-goal as WON'T FIX with its reason", () => {
    render(<RoadmapApp />);
    const wontFix = screen
      .getByRole("heading", {
        name: "Closed As Not Planned",
      })
      .closest("section")!;
    for (const goal of nonGoals) {
      expect(within(wontFix).getByText(goal.title)).toBeInTheDocument();
      expect(within(wontFix).getByText(goal.reason)).toBeInTheDocument();
    }
  });

  it("renders no hard-coded slice or package count: the headline tracks the module's own numbers", () => {
    render(<RoadmapApp />);
    // If this ever drifts, it means someone typed a literal number in
    // RoadmapApp.tsx instead of rendering shippedCount/totalCount.
    expect(
      screen.getByText(`${shippedCount} of ${totalCount} Slices Resolved.`),
    ).toBeInTheDocument();
  });
});
